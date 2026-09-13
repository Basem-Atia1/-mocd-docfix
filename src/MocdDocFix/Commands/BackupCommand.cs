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
    string ManifestPath,
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

    /// <param name="hashLookup">
    /// Supplies the mocd_hash CRM holds for a row. Injected because ScanRow does not carry it.
    /// </param>
    public BackupCommand(IFileServiceClient files, ICrmReadClient read, BackupStore backups,
        StateStore state, Reporter reporter, Func<ScanRow, string?> hashLookup)
    {
        _files = files;
        _read = read;
        _backups = backups;
        _state = state;
        _reporter = reporter;
        _hashLookup = hashLookup;
    }

    public async Task<BackupSummary> RunAsync(string env, IReadOnlyList<ScanRow> fixRows, CancellationToken ct)
    {
        int saved = 0, skipped = 0;
        long totalBytes = 0;
        var quarantined = new List<ScanRow>();

        foreach (var row in fixRows)
        {
            ct.ThrowIfCancellationRequested();

            if (_state.IsAtLeast(row.DocumentId, MigrationState.BackedUp)) { skipped++; continue; }

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
            var result = _backups.Save(row.DocumentFileId, extension, bytes);

            var imagePath = _backups.SaveCrmImage(row.DocumentFileId, row.DocumentId, row.OldFilePath,
                env, documentSnapshot!, fileSnapshot!, annotations!);

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
            _backups.ManifestPath, quarantined);
    }

    private void Quarantine(ScanRow row, string detail) =>
        _state.Append(new StateRecord(row.DocumentId, MigrationState.Quarantined,
            DateTimeOffset.UtcNow, null, null, detail));

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
