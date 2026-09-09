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
    IReadOnlyList<MigrationRow> Rows);

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

    public MigrateCommand(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write,
        BackupStore backups, StateStore state, Reporter reporter, IPrompts prompts,
        IFileOpener opener, string crmUrl)
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

            // Stage the new copy next to the backup so the operator can open both.
            var stagedPath = Path.Combine(
                Path.GetDirectoryName(entry.LocalPath)!, $"NEW-{newFile.FileId}{entry.Extension}");
            File.WriteAllBytes(stagedPath, oldBytes);

            _prompts.Info(BuildSummary(i + 1, manifest.Count, entry, newFile, oldBytes.Length, checks));
            _opener.Open(entry.LocalPath);
            _opener.Open(stagedPath);

            var choice = _prompts.Confirm("Repoint document to the new file?");
            if (choice == ConfirmChoice.Quit) break;
            if (choice is ConfirmChoice.No or ConfirmChoice.Skip) { skipped++; continue; }

            await _write.CreateDocumentFileAsync(newFile.FileId, newFile.FilePath, newFile.Hash,
                entry.FileName, entry.MediaType, entry.CorrectCatalogueId.ToString(), ct);
            await _write.RepointDocumentAsync(entry.DocumentId, newFile.FileId, ct);

            // Check 6 — read back rather than assume.
            var linked = await _write.GetDocumentFileLinkAsync(entry.DocumentId, ct);
            var tookIt = Verifier.CrmTookTheChange(newFile.FileId, linked);
            if (!tookIt.Passed)
            {
                haltReason = $"{tookIt.Name}: {tookIt.Detail}";
                Fail(entry, haltReason);
                failed++;
                break;
            }

            _state.Append(new StateRecord(entry.DocumentId, MigrationState.Repointed,
                DateTimeOffset.UtcNow, newFile.FileId, newFile.FilePath, null));

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
        return new MigrateSummary(migrated, skipped, failed, haltReason is not null, haltReason, reportPath, rows);
    }

    private void Fail(ManifestEntry entry, string detail) =>
        _state.Append(new StateRecord(entry.DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, null, null, detail));

    private static string BuildSummary(int index, int total, ManifestEntry entry, FileData newFile,
        int bytes, IReadOnlyList<CheckResult> checks)
    {
        var lines = new List<string>
        {
            "",
            $"[{index} / {total}]  {entry.FileName}",
            $"  document  {entry.DocumentId}",
            $"  OLD  {entry.OldFilePath}",
            $"  NEW  {newFile.FilePath}",
            ""
        };
        lines.AddRange(checks.Select(c => $"  {c.Name,-18} {(c.Passed ? "OK" : "FAILED")}  {c.Detail}"));
        lines.Add($"  bytes              {bytes:N0}");
        return string.Join(Environment.NewLine, lines);
    }
}
