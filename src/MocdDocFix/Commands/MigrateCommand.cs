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
    private readonly DocumentReportStore? _reports;

    /// <param name="deleteOldAsync">
    /// Deletes one document's old file, re-running the full safety check first. Returns null on
    /// success or the reason it refused. Optional: when it is not supplied the operator is not
    /// offered a per-document delete and the separate delete step handles them all.
    /// </param>
    public MigrateCommand(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write,
        BackupStore backups, StateStore state, Reporter reporter, IPrompts prompts,
        IFileOpener opener, string crmUrl,
        Func<Guid, CancellationToken, Task<string?>>? deleteOldAsync = null,
        RepointedListWriter? repointed = null, DocumentReportStore? reports = null)
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
        _reports = reports;
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

            // The document may already be correct: an earlier run can complete the work and then
            // fail a later check, and a hand-fix looks the same. Uploading again would create a
            // second file on the server and a second record in CRM, for nothing.
            if (await AlreadyCorrectAsync(entry, ct) is { } settled)
            {
                _prompts.Info($"  {entry.FileName}: already points at a correctly filed record " +
                              $"({settled.RecordId}). Nothing to do.");
                _prompts.Info($"    its path  {settled.FilePath}");
                _prompts.Info("    The old file is now eligible for deletion.");

                // The path matters as much as the id. Recording it as null passes every check
                // here and then fails the delete step with "no new file recorded", which reads
                // like the migration never happened.
                _state.Append(new StateRecord(entry.DocumentId, MigrationState.Repointed,
                    DateTimeOffset.UtcNow, settled.RecordId, settled.FilePath,
                    "Already correct — not migrated again."));
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

            // Upload the way this record was originally uploaded: the plugin sends a dotless
            // extension and the document's own id as applicationId, the portal sends neither.
            var style = FileRecordCopier.StyleOf(entry.DocumentFileSnapshotJson);

            var upload = await _files.UploadAsync(new UploadRequest(
                Category: entry.CorrectCatalogueId.ToString(),
                FileName: entry.FileName ?? $"{entry.OldFileId}{entry.Extension}",
                File: Convert.ToBase64String(oldBytes),
                MediaType: entry.MediaType ?? "application/octet-stream",
                Extension: FileRecordCopier.ExtensionFor(entry.DocumentFileSnapshotJson, entry.Extension),
                ApplicationId: FileRecordCopier.ApplicationIdFor(style, entry.DocumentId)), ct);

            if (!upload.Success || upload.Data is null)
            {
                Fail(entry, $"Upload failed: {upload.Message}");
                failed++;

                var afterUpload = ReportTrouble(entry,
                    "The file server would not accept the upload.",
                    "a new copy filed under the correct catalogue",
                    upload.Message ?? "no reason given",
                    "nothing was uploaded, so nothing downstream could happen",
                    new[]
                    {
                        "no documentfile record was created",
                        "the document was NOT repointed",
                        "the old file and its CRM record were NOT deleted — both are untouched"
                    });

                if (afterUpload == AfterTrouble.StopTheRun) break;
                continue;
            }

            var newFile = upload.Data;
            _state.Append(new StateRecord(entry.DocumentId, MigrationState.Uploaded,
                DateTimeOffset.UtcNow, null, newFile.FilePath, null));

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

            if (!report.AllPassed)
            {
                var detail = string.Join(" | ", report.Failures.Select(f => $"{f.Name}: {f.Detail}"));
                Fail(entry, detail, null, newFile.FilePath);
                failed++;

                var choice = ReportTrouble(entry,
                    "The new copy did not pass verification.",
                    "the uploaded copy to be identical to the backup, under the right catalogue",
                    detail,
                    "the new copy cannot be trusted, so CRM was left pointing at the old file",
                    new[]
                    {
                        "no documentfile record was created",
                        "the document was NOT repointed — it still uses the old file",
                        "the old file and its CRM record were NOT deleted",
                        $"the uploaded copy is on the server at {newFile.FilePath} and can be ignored"
                    });

                if (report.MustHalt || choice == AfterTrouble.StopTheRun)
                {
                    haltReason = detail;
                    break;
                }

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
                // No CRM record exists yet, so NewFileId stays null: it names the new
                // mocd_documentfile, and claiming one that was never created is what sent the
                // delete and final-check steps looking for a record that is not there.
                _state.Append(new StateRecord(entry.DocumentId, MigrationState.Uploaded,
                    DateTimeOffset.UtcNow, null, newFile.FilePath,
                    "Operator did not confirm the two files match."));
                skipped++;
                continue;
            }

            // Two writes, two questions. Creating the record changes nothing the document can
            // see; repointing is what actually moves it. Asking once for both would hide that.
            _prompts.Info("");
            _prompts.Info("  Next: create the new mocd_documentfile record in CRM.");
            _prompts.Info("  It is a new row. The document still points at the OLD one afterwards,");
            _prompts.Info("  so nothing changes for anyone until the step after this.");

            var createIt = _prompts.Confirm("Create the new documentfile record?");
            if (createIt == ConfirmChoice.Quit) break;
            if (createIt is ConfirmChoice.No or ConfirmChoice.Skip) { skipped++; continue; }

            // The new record is the old one with only the file's whereabouts replaced, created
            // with the same key convention. Anything the original code path filled in — and
            // anything we have not thought of — comes across untouched.
            var payload = FileRecordCopier.BuildPayload(
                entry.DocumentFileSnapshotJson, newFile.FileId, newFile.FilePath, newFile.Hash,
                entry.CorrectCatalogueId, newFile.FileName);

            var newRecordId = await _write.CreateDocumentFileAsync(
                FileRecordCopier.NewRecordId(style, newFile.FileId), payload, ct);

            _prompts.Info("");
            _prompts.Info($"  Created mocd_documentfile {newRecordId}");
            _prompts.Info($"    open it   {RepointedListWriter.DocumentFileLink(_crmUrl, newRecordId)}");
            _prompts.Info($"    its path  {newFile.FilePath}");
            _prompts.Info("  The document has NOT moved yet — it still points at the old record.");

            var repoint = _prompts.Confirm("Repoint the document to this new record?");
            if (repoint == ConfirmChoice.Quit) break;
            if (repoint is ConfirmChoice.No or ConfirmChoice.Skip)
            {
                _prompts.Info("  Left as it was. The new record exists but nothing points at it;");
                _prompts.Info("  delete it by hand if you do not want it.");
                _state.Append(new StateRecord(entry.DocumentId, MigrationState.Verified,
                    DateTimeOffset.UtcNow, newRecordId, newFile.FilePath,
                    $"Record {newRecordId} created; operator did not repoint."));
                skipped++;
                continue;
            }

            await _write.RepointDocumentAsync(entry.DocumentId, newRecordId, ct);

            // Check 6 — read back rather than assume: the document points at the new record.
            var linked = await _write.GetDocumentFileLinkAsync(entry.DocumentId, ct);
            var tookIt = Verifier.CrmTookTheChange(newRecordId, linked);
            if (!tookIt.Passed)
            {
                haltReason = $"{tookIt.Name}: {tookIt.Detail}";
                Fail(entry, haltReason, newRecordId, newFile.FilePath);
                failed++;

                ReportTrouble(entry,
                    "CRM did not accept the repoint.",
                    $"the document to point at {newRecordId}",
                    $"it points at {linked?.ToString() ?? "nothing"}",
                    "the document still uses its old file, so nothing has moved",
                    new[]
                    {
                        $"the new record {newRecordId} EXISTS but nothing points at it",
                        "the old file and its CRM record were NOT deleted",
                        "no further document will be attempted — this looks like a CRM problem"
                    });

                break;
            }

            // Check 7 — and that record points at the new file. Check 6 alone would pass while
            // mocd_filepath still named the old file, which is invisible until the old file goes.
            // By the record's own key, which is not the vendor's file id when CRM generated it.
            var recordPath = ReadString(
                await _read.GetRawRecordAsync("mocd_documentfiles", newRecordId, ct), "mocd_filepath");
            var pathStuck = Verifier.FileRecordPointsAtTheNewFile(newFile.FilePath, recordPath);
            checks.Add(pathStuck);

            if (!pathStuck.Passed)
            {
                haltReason = $"{pathStuck.Name}: {pathStuck.Detail}";
                Fail(entry, haltReason, newRecordId, newFile.FilePath);
                failed++;

                ReportTrouble(entry,
                    "The new record does not hold the new path.",
                    $"mocd_filepath on record {newRecordId} to be {newFile.FilePath}",
                    $"it is '{recordPath ?? "null"}'",
                    "the document points at a record that names the wrong file, so the View " +
                    "button would not open it",
                    new[]
                    {
                        "the old file and its CRM record were NOT deleted — deliberately, " +
                        "because the document would then have nothing that opens",
                        "no further document will be attempted until this is understood"
                    });

                break;
            }

            // newRecordId, not newFile.FileId. They are the same number only when the portal
            // created the original record; when the plugin did, CRM generated its own key and the
            // vendor's file id lives in mocd_fileid. Everything downstream — the delete step's
            // safety checks, the final check, the CRM links — looks the record up by this id, so
            // recording the vendor's id here makes a correctly migrated document fail all of them.
            _state.Append(new StateRecord(entry.DocumentId, MigrationState.Repointed,
                DateTimeOffset.UtcNow, newRecordId, newFile.FilePath, null));

            var repointedPoints = new (string, string?)[]
                {
                    ("New path", newFile.FilePath),
                    ("New file record", newRecordId.ToString()),
                    ("Vendor file id", newFile.FileId.ToString()),
                    ("Filed under", entry.CorrectCatalogueId.ToString()),
                    ("Vendor hash", newFile.Hash),
                    ("Saved as", Path.Combine("new", Path.GetFileName(stagedPath))),
                    ("Checks passed", string.Join(", ", checks.Select(c => c.Name))),
                    ("CRM now points at", newFile.FileId.ToString()),
                    ("Repointed at", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("The old file", "still on the server, untouched until the delete step")
                };

            _backups.Folder(entry.DocumentId, entry.FileName)
                .AppendSection("THE NEW FILE — uploaded and repointed", repointedPoints);

            _reports?.Write(entry.DocumentId, entry.FileName, "03-upload-repoint",
                "STEP 3 and 4 — UPLOAD, VERIFY and REPOINT", repointedPoints);

            // There is no way to view the new file from inside this tool, so hand over the links
            // that do let it be seen — the document, and the file record behind it. The View
            // button on the document reads the record's path, which check 7 has just confirmed.
            _prompts.Info("");
            _prompts.Info("  REPOINTED — open it in CRM to see the file:");
            _prompts.Info($"    document        {Reporter.CrmLink(_crmUrl, entry.DocumentId)}");
            _prompts.Info($"    new file record {RepointedListWriter.DocumentFileLink(_crmUrl, newRecordId)}");
            _prompts.Info($"    record id       {newRecordId}");
            _prompts.Info($"    vendor file id  {newFile.FileId}");
            _prompts.Info("    The View button on the document now serves the corrected copy.");

            await OfferToDeleteOldAsync(entry, ct);

            rows.Add(new MigrationRow(
                DocumentId: entry.DocumentId,
                OldFileId: entry.OldFileId,
                NewFileId: newRecordId,
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
        var repointedPath = _repointed?.Write(env, _crmUrl, rows,
            _reports is null ? null : id => _reports.FolderFor(id)) ?? string.Empty;

        return new MigrateSummary(migrated, skipped, failed, haltReason is not null, haltReason,
            reportPath, rows, repointedPath);
    }

    /// <summary>
    /// Records a failure without throwing away what the run already learned. A Failed record that
    /// blanks the new record's id and path leaves nothing to recover from: the work may have been
    /// done in CRM, but the tool's own notes no longer say where it went. Whatever was known when
    /// the failure happened is carried through.
    /// </summary>
    private void Fail(ManifestEntry entry, string detail,
        Guid? newRecordId = null, string? newFilePath = null) =>
        _state.Append(new StateRecord(entry.DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, newRecordId, newFilePath, detail));

    /// <summary>What the operator chose after something went wrong.</summary>
    private enum AfterTrouble { StopTheRun, SkipThisOne }

    /// <summary>
    /// Says plainly that something failed, what was expected, what happened instead, and — the
    /// part that is easy to leave out — which of the remaining steps did NOT run because of it.
    /// Then asks what to do, rather than deciding silently.
    ///
    /// Written after a real run halted on a failed check and reported it as one line inside a
    /// step summary. The operator could see the delete had not happened but not why.
    /// </summary>
    private AfterTrouble ReportTrouble(ManifestEntry entry, string what, string expected,
        string actual, string meaning, IEnumerable<string> notDone)
    {
        _prompts.Info("");
        _prompts.Info("  ──────────────────────────────────────────────────────────────────");
        _prompts.Info($"  SOMETHING WENT WRONG — {entry.FileName}");
        _prompts.Info($"    {what}");
        _prompts.Info("");
        _prompts.Info($"    Expected      {expected}");
        _prompts.Info($"    Actually      {actual}");
        _prompts.Info($"    Which means   {meaning}");
        _prompts.Info("");
        _prompts.Info("    NOT done because of this:");
        foreach (var line in notDone) _prompts.Info($"      - {line}");
        _prompts.Info("");
        _prompts.Info($"    document  {entry.DocumentId}");
        _prompts.Info($"    open it   {Reporter.CrmLink(_crmUrl, entry.DocumentId)}");
        _prompts.Info("  ──────────────────────────────────────────────────────────────────");

        var answer = new Asker(_prompts).Ask("What should happen now?", new[]
        {
            new Choice("Stop the run", "look at this before doing anything else",
                "Nothing further runs. Everything already done stays done, and the state file " +
                "records where each document got to, so the run can be picked up later."),
            new Choice("Skip it, carry on", "leave this one and continue with the others",
                "This document is left exactly as it is — including anything the failed step " +
                "left behind — and the next document is attempted.")
        }, defaultIndex: 0, allowBack: false);

        return answer.Kind == AnswerKind.Chosen && answer.Index == 1
            ? AfterTrouble.SkipThisOne
            : AfterTrouble.StopTheRun;
    }

    /// <summary>
    /// The id of the record this document already points at, when that record is filed under the
    /// right catalogue — meaning the work is done, whoever did it. Null means there is work to do.
    /// </summary>
    private async Task<SettledRecord?> AlreadyCorrectAsync(ManifestEntry entry, CancellationToken ct) =>
        await Reconciler.AlreadyCorrectAsync(_read, _write, entry, ct);

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
