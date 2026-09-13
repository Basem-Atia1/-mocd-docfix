using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Verification;

namespace MocdDocFix.Commands;

public sealed record BackupSummary(
    int Saved,
    int Quarantined,
    int Skipped,
    long TotalBytes,
    string BackupRoot,
    IReadOnlyList<ScanRow> QuarantinedRows);

/// <summary>
/// Phase 2. Downloads and verifies every FIX file, then stores it locally with a manifest
/// that also snapshots the CRM records. Reads only — nothing is written to CRM or the file
/// service.
/// </summary>
public sealed class BackupCommand
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly Reporter _reporter;
    private readonly Func<ScanRow, string?> _hashLookup;
    private readonly Action<string>? _prompts;
    private readonly DocumentReportStore? _reports;

    /// <param name="hashLookup">
    /// Supplies the mocd_hash CRM holds for a row. Injected because ScanRow does not carry it.
    /// </param>
    /// <param name="say">Optional progress line, for things the operator should know about.</param>
    public BackupCommand(IFileServiceClient files, ICrmReadClient read, BackupStore backups,
        StateStore state, Reporter reporter, Func<ScanRow, string?> hashLookup,
        Action<string>? say = null, DocumentReportStore? reports = null)
    {
        _files = files;
        _read = read;
        _backups = backups;
        _state = state;
        _reporter = reporter;
        _hashLookup = hashLookup;
        _prompts = say;
        _reports = reports;
    }

    /// <summary>
    /// True when what the state claims is actually on disk: the restore record, the saved bytes
    /// it names, and the CRM image beside them.
    /// </summary>
    private bool BackupIsIntact(Guid documentId)
    {
        var entry = _backups.LoadManifest().FirstOrDefault(m => m.DocumentId == documentId);
        if (entry is null) return false;
        if (!File.Exists(entry.LocalPath)) return false;

        return string.IsNullOrWhiteSpace(entry.CrmImagePath) || File.Exists(entry.CrmImagePath);
    }

    public async Task<BackupSummary> RunAsync(string env, IReadOnlyList<ScanRow> fixRows, CancellationToken ct)
    {
        int saved = 0, skipped = 0;
        long totalBytes = 0;
        var quarantined = new List<ScanRow>();

        foreach (var row in fixRows)
        {
            ct.ThrowIfCancellationRequested();

            // A document reaching this loop was asked for, so it is backed up again rather than
            // skipped on the strength of an earlier run. A backup is a snapshot: taking a fresh
            // one is the point, and the state file is only a claim about the disk anyway.
            if (_state.IsAtLeast(row.DocumentId, MigrationState.BackedUp))
            {
                _prompts?.Invoke(BackupIsIntact(row.DocumentId)
                    ? $"  {row.FileName}: backed up before — taking a fresh copy anyway."
                    : $"  {row.FileName}: the state says this was backed up, but the saved copy " +
                      "is missing. Downloading it again.");
            }

            // The document's folder and its record exist before anything is attempted, so even a
            // failure leaves a readable account of what was tried and why it did not work.
            DocumentRecord.EnsureHeader(_backups, row, _reports);

            if (string.IsNullOrWhiteSpace(row.OldFilePath))
            {
                Quarantine(row, "No file path.");
                quarantined.Add(row);
                continue;
            }

            var download = await _files.DownloadAsync(row.OldFilePath, ct);
            if (!download.Success || download.Data?.File is null)
            {
                Quarantine(row, $"Download failed: {download.Message}");
                quarantined.Add(row);
                continue;
            }

            // Check 1 — CRM's stored hash must agree with what the server reports today.
            var integrity = Verifier.BackupIntegrity(_hashLookup(row), download.Data.Hash);
            if (!integrity.Passed)
            {
                Quarantine(row, integrity.Detail);
                quarantined.Add(row);
                continue;
            }

            byte[] bytes;
            try { bytes = Convert.FromBase64String(download.Data.File); }
            catch (FormatException ex)
            {
                Quarantine(row, $"Response was not valid base64: {ex.Message}");
                quarantined.Add(row);
                continue;
            }

            // Snapshot the CRM side BEFORE saving anything, so the backup is a complete image
            // of the document — bytes AND the CRM records about it (spec section 8.2).
            // A partial backup that looks complete is worse than no backup, so any gap here
            // quarantines the file instead of producing one.
            var fileSnapshot = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocumentFileId, ct);
            var documentSnapshot = await _read.GetRawRecordAsync("mocd_documents", row.DocumentId, ct);
            var annotations = await _read.GetDocumentAnnotationsAsync(row.DocumentId, ct);

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(documentSnapshot)) missing.Add("mocd_document record");
            if (string.IsNullOrWhiteSpace(fileSnapshot)) missing.Add("mocd_documentfile record");
            if (annotations is null) missing.Add("annotations");

            if (missing.Count > 0)
            {
                var detail = "Backup would be incomplete — could not read " +
                             string.Join(", ", missing) + ". Nothing was saved for this document.";
                Quarantine(row, detail);
                quarantined.Add(row);
                continue;
            }

            var extension = Path.GetExtension(row.FileName ?? string.Empty);
            var result = _backups.Save(row.DocumentId, row.DocumentFileId, extension, bytes, row.FileName);

            var imagePath = _backups.SaveCrmImage(row.DocumentFileId, row.DocumentId, row.OldFilePath,
                env, documentSnapshot!, fileSnapshot!, annotations!);

            if (result.PreviousKeptAs is not null)
            {
                _prompts?.Invoke(
                    $"  {row.FileName}: WARNING — the file on the server has CHANGED since the " +
                    "last backup. The earlier copy was kept as " +
                    $"{Path.GetFileName(result.PreviousKeptAs)}.");
            }

            var backedUp = new (string, string?)[]
            {
                ("Path on server", row.OldFilePath),
                ("Earlier copy kept", result.PreviousKeptAs is null
                    ? null
                    : $"{Path.GetFileName(result.PreviousKeptAs)} — the server's copy had changed"),
                ("File record", row.DocumentFileId.ToString()),
                ("Folder it is in", row.CurrentSegment ?? "(none)"),
                ("Vendor hash", download.Data.Hash),
                ("Saved as", Path.Combine("old", Path.GetFileName(result.LocalPath))),
                ("Size", $"{result.Bytes:N0} bytes"),
                ("SHA-256 (ours)", result.OurHash),
                ("CRM records", Path.Combine("old", "crm.json")),
                ("Downloaded at", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            };

            _backups.Folder(row.DocumentId, row.FileName)
                .AppendSection("THE OLD FILE — backed up", backedUp);

            _reports?.Write(row.DocumentId, row.FileName, "02-backup",
                "STEP 2 — BACKUP: what was downloaded and saved", backedUp);

            _backups.AppendManifest(new ManifestEntry(
                DocumentId: row.DocumentId,
                OldFileId: row.DocumentFileId,
                OldFilePath: row.OldFilePath,
                OldVendorHash: download.Data.Hash,
                FileName: row.FileName,
                MediaType: ReadString(fileSnapshot, "mocd_mediatype"),
                Extension: extension,
                OldCategory: row.CurrentSegment,
                CorrectCatalogueId: row.CorrectCatalogueId ?? Guid.Empty,
                LocalPath: result.LocalPath,
                Bytes: result.Bytes,
                OurHash: result.OurHash,
                At: DateTimeOffset.UtcNow,
                DocumentFileSnapshotJson: fileSnapshot,
                DocumentSnapshotJson: documentSnapshot,
                DocumentTypeId: Guid.Empty,
                DocumentTypeName: row.DocumentTypeName,
                AnnotationsJson: annotations,
                CrmImagePath: imagePath));

            _state.Append(new StateRecord(row.DocumentId, MigrationState.BackedUp,
                DateTimeOffset.UtcNow, null, null, $"{result.Bytes} bytes → {result.LocalPath}"));

            saved++;
            totalBytes += result.Bytes;
        }

        if (quarantined.Count > 0) _reporter.WriteQuarantine(env, quarantined);

        return new BackupSummary(saved, quarantined.Count, skipped, totalBytes,
            _backups.Root, quarantined);
    }

    /// <summary>
    /// Records why a file was not backed up, in the document's own folder as well as the state
    /// file. The reason used to live only in state-&lt;env&gt;.jsonl, so the quarantine report showed
    /// the classification reason and nothing about the actual failure.
    /// </summary>
    private void Quarantine(ScanRow row, string detail)
    {
        _state.Append(new StateRecord(row.DocumentId, MigrationState.Quarantined,
            DateTimeOffset.UtcNow, null, null, detail));

        var failure = new (string, string?)[]
        {
            ("What happened", detail),
            ("What was saved", "nothing — a partial backup is worse than none"),
            ("At", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            ("What to do", "Fix the cause, then run the check and backup again for this document.")
        };

        _backups.Folder(row.DocumentId, row.FileName).AppendSection("NOT BACKED UP", failure);

        _reports?.Write(row.DocumentId, row.FileName, "02-backup",
            "STEP 2 — BACKUP: NOT DONE", failure);
    }

    /// <summary>Pulls one string attribute out of a raw record snapshot.</summary>
    private static string? ReadString(string? snapshotJson, string attribute)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(snapshotJson);
            return json.RootElement.TryGetProperty(attribute, out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
