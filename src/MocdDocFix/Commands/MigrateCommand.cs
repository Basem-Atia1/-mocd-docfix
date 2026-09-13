using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;
using MocdDocFix.Verification;

namespace MocdDocFix.Commands;

public sealed record MigrateSummary(
    int Migrated,
    int Skipped,
    int Failed,
    bool Halted,
    string? HaltReason,
    string ReportPath,
    IReadOnlyList<MigrationRow> Rows,
    /// <summary>Readable list of what was repointed: new file id and its CRM link.</summary>
    string RepointedPath = "");

/// <summary>
/// Phase 3. Upload, verify, show the operator, ask, then write CRM — in that order, so the
/// worst case at any instant is two good copies. The old file is never touched here.
/// </summary>
public sealed class MigrateCommand
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly Reporter _reporter;
    private readonly IPrompts _prompts;
    private readonly IFileOpener _opener;
    private readonly string _crmUrl;
    private readonly Func<Guid, CancellationToken, Task<string?>>? _deleteOldAsync;
    private readonly RepointedListWriter? _repointed;

    /// <param name="deleteOldAsync">
    /// Deletes one document's old file, re-running the full safety check first. Returns null on
    /// success or the reason it refused. Optional: when it is not supplied the operator is not
    /// offered a per-document delete and the separate delete step handles them all.
    /// </param>
    public MigrateCommand(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write,
        BackupStore backups, StateStore state, Reporter reporter, IPrompts prompts,
        IFileOpener opener, string crmUrl,
        Func<Guid, CancellationToken, Task<string?>>? deleteOldAsync = null,
        RepointedListWriter? repointed = null)
    {
        _files = files;
        _read = read;
        _write = write;
        _backups = backups;
        _state = state;
        _reporter = reporter;
        _prompts = prompts;
        _opener = opener;
        _crmUrl = crmUrl;
        _deleteOldAsync = deleteOldAsync;
        _repointed = repointed;
    }

    public async Task<MigrateSummary> RunAsync(string env, CancellationToken ct)
    {
        var manifest = _backups.LoadManifest();
        var rows = new List<MigrationRow>();
        int migrated = 0, skipped = 0, failed = 0;
        string? haltReason = null;

        for (var i = 0; i < manifest.Count && haltReason is null; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = manifest[i];

            if (_state.IsAtLeast(entry.DocumentId, MigrationState.Repointed)) { skipped++; continue; }
            if (!_state.IsAtLeast(entry.DocumentId, MigrationState.BackedUp)) { skipped++; continue; }

            // Somebody else may have edited the record since the scan.
            var modifiedOn = await _read.GetDocumentModifiedOnAsync(entry.DocumentId, ct);
            if (modifiedOn is not null && modifiedOn > entry.At)
            {
                _prompts.Info($"  SKIP — document {entry.DocumentId} was modified at {modifiedOn} " +
                              $"after its backup at {entry.At}.");
                _state.Append(new StateRecord(entry.DocumentId, MigrationState.Failed,
                    DateTimeOffset.UtcNow, null, null, "Modified after backup — skipped."));
                skipped++;
                continue;
            }

            var oldBytes = _backups.Read(entry.LocalPath);

            // Say what is about to happen, in the operator's terms, BEFORE uploading: which
            // catalogue the new copy will be filed under, and what the new path will look like.
            var catalogueName = await _read.GetServiceCatalogueNameAsync(
                entry.CorrectCatalogueId.ToString(), ct);

            _prompts.Info(BuildUploadBriefing(i + 1, manifest.Count, entry, catalogueName, oldBytes.Length));

            var goAhead = _prompts.Confirm("Upload this corrected copy now?");
            if (goAhead == ConfirmChoice.Quit) break;
            if (goAhead is ConfirmChoice.No or ConfirmChoice.Skip)
            {
                _prompts.Info("  Not uploaded. Nothing was changed for this document.");
                skipped++;
                continue;
            }

            var upload = await _files.UploadAsync(new UploadRequest(
                Category: entry.CorrectCatalogueId.ToString(),
                FileName: entry.FileName ?? $"{entry.OldFileId}{entry.Extension}",
                File: Convert.ToBase64String(oldBytes),
                MediaType: entry.MediaType ?? "application/octet-stream",
                Extension: entry.Extension,
                ApplicationId: Guid.Empty), ct);

            if (!upload.Success || upload.Data is null)
            {
                Fail(entry, $"Upload failed: {upload.Message}");
                failed++;
                continue;
            }

            var newFile = upload.Data;
            _state.Append(new StateRecord(entry.DocumentId, MigrationState.Uploaded,
                DateTimeOffset.UtcNow, newFile.FileId, newFile.FilePath, null));

            // Checks 2, 5 and 4 before we spend a download.
            var checks = new List<CheckResult>
            {
                Verifier.UploadHashMatches(entry.OldVendorHash, newFile.Hash),
                Verifier.IsGenuinelyNew(entry.OldFilePath, newFile.FilePath, entry.OldFileId, newFile.FileId),
                Verifier.PathIsFixed(FilePathParser.Parse(newFile.FilePath), entry.CorrectCatalogueId, newFile.FileId)
            };

            // Check 3 — the decisive one. Only worth doing if nothing already failed hard.
            if (checks.All(c => c.Passed))
            {
                var download = await _files.DownloadAsync(newFile.FilePath, ct);
                if (!download.Success || download.Data?.File is null)
                {
                    checks.Add(new CheckResult("round-trip", false,
                        $"The new file could not be downloaded back: {download.Message}", false));
                }
                else
                {
                    checks.Add(Verifier.RoundTrip(oldBytes, Convert.FromBase64String(download.Data.File)));
                }
            }

            var report = new VerificationReport(checks);

            if (report.MustHalt)
            {
                haltReason = string.Join(" | ", report.Failures.Select(f => $"{f.Name}: {f.Detail}"));
                Fail(entry, haltReason);
                failed++;
                break;
            }

            if (!report.AllPassed)
            {
                var detail = string.Join(" | ", report.Failures.Select(f => $"{f.Name}: {f.Detail}"));
                _prompts.Info($"  FAILED verification — {detail}");
                Fail(entry, detail);
                failed++;
                continue;
            }

            _state.Append(new StateRecord(entry.DocumentId, MigrationState.Verified,
                DateTimeOffset.UtcNow, newFile.FileId, newFile.FilePath, null));

            // Stage the new copy in the document's own new\ folder, beside old\, so everything
            // about this document stays together and the operator can open both.
            var staged = _backups.SaveNew(entry.DocumentId, newFile.FileId, entry.Extension, oldBytes);
            var stagedPath = staged.LocalPath;

            _prompts.Info(BuildSummary(i + 1, manifest.Count, entry, newFile, oldBytes.Length, checks));
            _opener.Open(entry.LocalPath);
            _opener.Open(stagedPath);

            // Two separate questions, deliberately. The first asks only whether the operator's
            // own eyes agree with the checks above; the second asks whether to write to CRM.
            var looksRight = _prompts.Confirm("Do the two files look the same to you?");
            if (looksRight == ConfirmChoice.Quit) break;
            if (looksRight is ConfirmChoice.No or ConfirmChoice.Skip)
            {
                _prompts.Info("  Left alone. CRM still points at the old file, and the old file is");
                _prompts.Info("  untouched. The new copy stays on the server for you to inspect:");
                _prompts.Info($"     {newFile.FilePath}");
                _state.Append(new StateRecord(entry.DocumentId, MigrationState.Uploaded,
                    DateTimeOffset.UtcNow, newFile.FileId, newFile.FilePath,
                    "Operator did not confirm the two files match."));
                skipped++;
                continue;
            }

            var choice = _prompts.Confirm("Repoint the document to the new file?");
            if (choice == ConfirmChoice.Quit) break;
            if (choice is ConfirmChoice.No or ConfirmChoice.Skip) { skipped++; continue; }

            await _write.CreateDocumentFileAsync(newFile.FileId, newFile.FilePath, newFile.Hash,
                entry.FileName, entry.MediaType, entry.CorrectCatalogueId.ToString(), ct);
            await _write.RepointDocumentAsync(entry.DocumentId, newFile.FileId, ct);

            // Check 6 — read back rather than assume: the document points at the new record.
            var linked = await _write.GetDocumentFileLinkAsync(entry.DocumentId, ct);
            var tookIt = Verifier.CrmTookTheChange(newFile.FileId, linked);
            if (!tookIt.Passed)
            {
                haltReason = $"{tookIt.Name}: {tookIt.Detail}";
                Fail(entry, haltReason);
                failed++;
                break;
            }

            // Check 7 — and that record points at the new file. Check 6 alone would pass while
            // mocd_filepath still named the old file, which is invisible until the old file goes.
            var recordPath = ReadString(
                await _read.GetRawRecordAsync("mocd_documentfiles", newFile.FileId, ct), "mocd_filepath");
            var pathStuck = Verifier.FileRecordPointsAtTheNewFile(newFile.FilePath, recordPath);
            checks.Add(pathStuck);

            if (!pathStuck.Passed)
            {
                haltReason = $"{pathStuck.Name}: {pathStuck.Detail}";
                Fail(entry, haltReason);
                failed++;
                break;
            }

            _state.Append(new StateRecord(entry.DocumentId, MigrationState.Repointed,
                DateTimeOffset.UtcNow, newFile.FileId, newFile.FilePath, null));

            _backups.Folder(entry.DocumentId).AppendSection("THE NEW FILE — uploaded and repointed",
                new (string, string?)[]
                {
                    ("New path", newFile.FilePath),
                    ("New file record", newFile.FileId.ToString()),
                    ("Filed under", entry.CorrectCatalogueId.ToString()),
                    ("Vendor hash", newFile.Hash),
                    ("Saved as", Path.Combine("new", Path.GetFileName(stagedPath))),
                    ("Checks passed", string.Join(", ", checks.Select(c => c.Name))),
                    ("CRM now points at", newFile.FileId.ToString()),
                    ("Repointed at", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("The old file", "still on the server, untouched until the delete step")
                });

            // There is no way to view the new file from inside this tool, so hand over the links
            // that do let it be seen — the document, and the file record behind it. The View
            // button on the document reads the record's path, which check 7 has just confirmed.
            _prompts.Info("");
            _prompts.Info("  REPOINTED — open it in CRM to see the file:");
            _prompts.Info($"    document        {Reporter.CrmLink(_crmUrl, entry.DocumentId)}");
            _prompts.Info($"    new file record {RepointedListWriter.DocumentFileLink(_crmUrl, newFile.FileId)}");
            _prompts.Info($"    new file id     {newFile.FileId}");
            _prompts.Info("    The View button on the document now serves the corrected copy.");

            await OfferToDeleteOldAsync(entry, ct);

            rows.Add(new MigrationRow(
                DocumentId: entry.DocumentId,
                OldFileId: entry.OldFileId,
                NewFileId: newFile.FileId,
                OldFilePath: entry.OldFilePath,
                NewFilePath: newFile.FilePath,
                Bytes: oldBytes.Length,
                VendorHash: newFile.Hash,
                OurHash: Verifier.OurHash(oldBytes),
                ChecksPassed: string.Join(";", checks.Select(c => c.Name).Append(tookIt.Name)),
                OldCrmLink: Reporter.CrmLink(_crmUrl, entry.DocumentId),
                NewCrmLink: Reporter.CrmLink(_crmUrl, entry.DocumentId),
                State: nameof(MigrationState.Repointed),
                At: DateTimeOffset.UtcNow));

            migrated++;
        }

        var reportPath = _reporter.WriteMigration(env, rows);
        var repointedPath = _repointed?.Write(env, _crmUrl, rows) ?? string.Empty;

        return new MigrateSummary(migrated, skipped, failed, haltReason is not null, haltReason,
            reportPath, rows, repointedPath);
    }

    private void Fail(ManifestEntry entry, string detail) =>
        _state.Append(new StateRecord(entry.DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, null, null, detail));

    /// <summary>Pulls one string attribute out of a raw record snapshot.</summary>
    private static string? ReadString(string? recordJson, string attribute)
    {
        if (string.IsNullOrWhiteSpace(recordJson)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(recordJson);
            return json.RootElement.TryGetProperty(attribute, out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// What will be uploaded, where it will land, and what the new path will look like — said
    /// before the upload happens, so the answer is an informed one.
    /// </summary>
    private static string BuildUploadBriefing(int index, int total, ManifestEntry entry,
        string? catalogueName, int bytes)
    {
        var parts = FilePathParser.Parse(entry.OldFilePath);
        var extension = string.IsNullOrEmpty(entry.Extension) ? "" : entry.Extension;

        return string.Join(Environment.NewLine,
            "",
            "──────────────────────────────────────────────────────────────────────",
            $"  File {index} of {total}   {entry.FileName}",
            $"  Document       {entry.DocumentId}",
            $"  Document type  {entry.DocumentTypeName}",
            $"  Size           {bytes:N0} bytes",
            "",
            $"  Filed under now   {parts.CategorySegment ?? "(nothing)"}",
            $"  Should be under   {entry.CorrectCatalogueId}",
            $"                    {catalogueName ?? "(name not available)"}",
            "",
            "  Uploading creates a NEW file. The vendor assigns its id and its date",
            "  folder, so the new path will look like this:",
            "",
            $"      DigitalServices\\{entry.CorrectCatalogueId}\\{DateTime.Now:yyyyMMdd}\\<new-file-id>{extension}",
            "",
            "  The old file is not touched. Nothing is written to CRM yet — you will",
            "  see both files and be asked again before anything is repointed.",
            "");
    }

    /// <summary>
    /// Offers to remove this document's old file straight away, rather than leaving every
    /// deletion to the end. Declining is always safe: the separate delete step can still do it.
    /// </summary>
    private async Task OfferToDeleteOldAsync(ManifestEntry entry, CancellationToken ct)
    {
        if (_deleteOldAsync is null) return;

        _prompts.Info("");
        _prompts.Info("  CRM now points at the new file. Nothing about the old one has been");
        _prompts.Info("  touched — its file and its CRM record are both still there:");
        _prompts.Info($"     file on server    {entry.OldFilePath}");
        _prompts.Info($"     mocd_documentfile {entry.OldFileId}");
        _prompts.Info("");
        _prompts.Info("  Deleting removes BOTH, in that order, and cannot be undone. Saying no");
        _prompts.Info("  leaves both in place for the delete step, which can do them together.");

        if (_prompts.Confirm("Delete the old file AND its CRM record now?") != ConfirmChoice.Yes)
        {
            _prompts.Info("  Left in place.");
            return;
        }

        var refusal = await _deleteOldAsync(entry.DocumentId, ct);

        if (refusal is null)
        {
            _prompts.Info("  Deleted.");
            _backups.Folder(entry.DocumentId).AppendSection("THE OLD FILE — deleted",
                new (string, string?)[]
                {
                    ("Deleted from", entry.OldFilePath),
                    ("At", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("Recoverable", "the bytes are in old\\, but a restore lands on a new path")
                });
        }
        else
        {
            _prompts.Info($"  REFUSED — {refusal}");
            _prompts.Info("  The old file is still there. Nothing was lost.");
        }
    }

    private static string BuildSummary(int index, int total, ManifestEntry entry, FileData newFile,
        int bytes, IReadOnlyList<CheckResult> checks)
    {
        var lines = new List<string>
        {
            "",
            $"  UPLOADED — file {index} of {total}   {entry.FileName}",
            "",
            $"  OLD  {entry.OldFilePath}",
            $"  NEW  {newFile.FilePath}",
            "",
            "  MY COMPARISON",
            $"    size              {bytes:N0} bytes",
            $"    SHA-256 of backup {entry.OurHash}",
            $"    vendor hash old   {entry.OldVendorHash}",
            $"    vendor hash new   {newFile.Hash}",
            ""
        };

        lines.AddRange(checks.Select(c =>
            $"    {(c.Passed ? "PASS" : "FAIL")}  {c.Name,-16} {c.Detail}"));

        lines.Add("");
        lines.Add(checks.All(c => c.Passed)
            ? "    Every check passed. As far as I can tell the two files are identical."
            : "    SOMETHING DID NOT PASS — read the lines above before answering.");
        lines.Add("");
        lines.Add("  I have opened both files for you. Compare them yourself as well.");

        return string.Join(Environment.NewLine, lines);
    }
}
