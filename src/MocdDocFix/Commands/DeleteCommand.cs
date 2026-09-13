using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;
using MocdDocFix.Verification;

namespace MocdDocFix.Commands;

public sealed record DeleteSummary(int Deleted, int Skipped, int Refused, bool Aborted, string? AbortReason);

/// <summary>
/// Phase 5 — the only irreversible step. Every candidate is re-verified against live state
/// immediately before its delete, because a report written an hour ago is a stale fact.
/// Note the vendor's delete is a GET (spec section 3.4), so it is called exactly once per file
/// and never retried automatically.
/// </summary>
public sealed class DeleteCommand
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly IPrompts _prompts;

    public DeleteCommand(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write,
        BackupStore backups, StateStore state, IPrompts prompts)
    {
        _files = files;
        _read = read;
        _write = write;
        _backups = backups;
        _state = state;
        _prompts = prompts;
    }

    public async Task<DeleteSummary> RunAsync(string env, bool isProduction, CancellationToken ct)
    {
        var manifest = _backups.LoadManifest().ToDictionary(m => m.DocumentId);
        var latest = _state.LoadLatest();

        var candidates = latest.Values
            .Where(r => r.State == MigrationState.Repointed && manifest.ContainsKey(r.DocumentId))
            .ToList();

        if (candidates.Count == 0)
        {
            _prompts.Info("Nothing is awaiting deletion.");
            return new DeleteSummary(0, 0, 0, false, null);
        }

        _prompts.Info("");
        _prompts.Info($"About to permanently delete {candidates.Count} old file(s) from {env}:");
        foreach (var c in candidates.Take(20))
            _prompts.Info($"  {manifest[c.DocumentId].OldFilePath}");
        if (candidates.Count > 20) _prompts.Info($"  … and {candidates.Count - 20} more");
        _prompts.Info("");
        _prompts.Info("This cannot be undone. Local backups keep the bytes, but a restored file");
        _prompts.Info("cannot return to its original path.");

        if (!_prompts.TypedWord($"Delete {candidates.Count} old file(s) from {env}?", "DELETE"))
            return new DeleteSummary(0, 0, 0, true, "Operator did not type DELETE.");

        if (isProduction &&
            !_prompts.TypedWord($"PRODUCTION. Confirm the number of files to delete ({candidates.Count})",
                candidates.Count.ToString()))
        {
            return new DeleteSummary(0, 0, 0, true, "Operator did not confirm the production count.");
        }

        int deleted = 0, refused = 0, skipped = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var entry = manifest[candidate.DocumentId];
            if (_state.IsAtLeast(candidate.DocumentId, MigrationState.Deleted)) { skipped++; continue; }

            var refusal = await WhyNotSafeAsync(candidate, entry, ct);
            if (refusal is not null)
            {
                _prompts.Info($"  REFUSED {entry.OldFilePath} — {refusal}");
                _state.Append(new StateRecord(candidate.DocumentId, MigrationState.Failed,
                    DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                    $"Delete refused: {refusal}"));
                refused++;
                continue;
            }

            var fileDelete = await _files.DeleteAsync(entry.OldFilePath, ct);
            if (!fileDelete.Success)
            {
                _prompts.Info($"  FAILED to delete {entry.OldFilePath} — {fileDelete.Message}");
                refused++;
                continue;
            }

            await _write.DeleteDocumentFileAsync(entry.OldFileId, ct);

            _state.Append(new StateRecord(candidate.DocumentId, MigrationState.Deleted,
                DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                $"Deleted {entry.OldFilePath} and documentfile {entry.OldFileId}."));
            deleted++;
        }

        return new DeleteSummary(deleted, skipped, refused, false, null);
    }

    /// <summary>
    /// Deletes one document's old file, re-running the same safety checks as the bulk step.
    /// Used by the migrate step when the operator chooses to remove a file straight after
    /// repointing it, so there is one implementation of "is this safe to delete", not two.
    /// </summary>
    /// <returns>Null when it was deleted, otherwise the reason it was refused.</returns>
    public async Task<string?> DeleteOneAsync(Guid documentId, CancellationToken ct)
    {
        if (_state.IsAtLeast(documentId, MigrationState.Deleted)) return null;

        var entry = _backups.LoadManifest().FirstOrDefault(m => m.DocumentId == documentId);
        if (entry is null) return "no backup record for this document";

        if (!_state.LoadLatest().TryGetValue(documentId, out var candidate) ||
            candidate.State != MigrationState.Repointed)
        {
            return "the document is not in the repointed state";
        }

        var refusal = await WhyNotSafeAsync(candidate, entry, ct);
        if (refusal is not null)
        {
            _state.Append(new StateRecord(documentId, MigrationState.Failed,
                DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                $"Delete refused: {refusal}"));
            return refusal;
        }

        var fileDelete = await _files.DeleteAsync(entry.OldFilePath, ct);
        if (!fileDelete.Success) return $"the file server refused: {fileDelete.Message}";

        await _write.DeleteDocumentFileAsync(entry.OldFileId, ct);

        _state.Append(new StateRecord(documentId, MigrationState.Deleted,
            DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
            $"Deleted {entry.OldFilePath} and documentfile {entry.OldFileId}."));

        return null;
    }

    /// <summary>Re-runs the safety checks against live state. Null means safe to delete.</summary>
    private async Task<string?> WhyNotSafeAsync(StateRecord candidate, ManifestEntry entry, CancellationToken ct)
    {
        if (candidate.NewFileId is null || string.IsNullOrWhiteSpace(candidate.NewFilePath))
            return "no new file recorded";

        var download = await _files.DownloadAsync(candidate.NewFilePath, ct);
        if (!download.Success || download.Data?.File is null)
            return $"the new file no longer downloads ({download.Message})";

        byte[] newBytes;
        try { newBytes = Convert.FromBase64String(download.Data.File); }
        catch (FormatException) { return "the new file did not come back as valid base64"; }

        if (!string.Equals(Verifier.OurHash(newBytes), entry.OurHash, StringComparison.OrdinalIgnoreCase))
            return "the new file's content no longer matches the backup";

        var linked = await _write.GetDocumentFileLinkAsync(entry.DocumentId, ct);
        if (linked != candidate.NewFileId)
            return $"the document points at '{linked?.ToString() ?? "null"}', not the new file";

        // And the record it points at must itself name the new file. Without this, a record whose
        // mocd_filepath still holds the old path passes every other check — and deleting the old
        // file then leaves the document pointing at something that no longer exists.
        var recordPath = ReadFilePath(
            await _read.GetRawRecordAsync("mocd_documentfiles", candidate.NewFileId.Value, ct));

        var pathCheck = Verifier.FileRecordPointsAtTheNewFile(candidate.NewFilePath!, recordPath);
        if (!pathCheck.Passed) return pathCheck.Detail;

        return null;
    }

    private static string? ReadFilePath(string? recordJson)
    {
        if (string.IsNullOrWhiteSpace(recordJson)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(recordJson);
            return json.RootElement.TryGetProperty("mocd_filepath", out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
