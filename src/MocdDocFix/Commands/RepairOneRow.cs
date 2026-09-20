using System.Text.Json;
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;
using MocdDocFix.Verification;

namespace MocdDocFix.Commands;

/// <param name="FailedStep">Which of the six broke, for the error log and the ledger.</param>
/// <param name="StopAsked">
/// The operator answered the eye-check with quit. That is a different thing from saying the
/// copies do not match, and it must end the run rather than only this document — pressing q
/// during an upload leaves the keystroke in the buffer for the next prompt to swallow, and
/// reading it as "they look wrong" would annotate the row with something untrue and then carry
/// on regardless.
/// </param>
/// <param name="WasAlreadyRight">
/// CRM had this document filed correctly before the run reached it. Counted apart from a
/// correction because nothing was uploaded and nothing was written — the row was settled by
/// asking, which is worth telling the operator about separately.
/// </param>
public sealed record RowOutcome(bool Corrected, string? FailedStep, string? Failure,
    bool StopAsked = false, bool WasAlreadyRight = false)
{
    public static RowOutcome Ok() => new(true, null, null);

    /// <summary>The operator said the two copies do not match. Not a failure — a decision.</summary>
    public static RowOutcome Declined() => new(false, null, null);

    /// <summary>The operator asked to stop. Nothing was written to CRM for this document.</summary>
    public static RowOutcome Stopped() => new(false, null, null, StopAsked: true);

    /// <summary>
    /// CRM already had it right, so nothing was uploaded and nothing was changed. Not a
    /// correction — the row has been settled by looking, not by working.
    /// </summary>
    public static RowOutcome AlreadyRight() => new(false, null, null, WasAlreadyRight: true);

    public static RowOutcome Broke(string step, string why) => new(false, step, why);

    public bool Failed => Failure is not null;
}

/// <summary>
/// One document, corrected. Download and back up, upload under the right catalogue, check the
/// copy four ways, show the operator, update the record the document already points at, and
/// read it back.
///
/// The record is updated in place: nothing is created and nothing is repointed. That is what
/// makes the change journal and the ledger the only route back, so every field this overwrites
/// is journalled with the value it had.
///
/// It mutates the row it is given and returns. Writing the ledger to disk is the loop's job.
/// </summary>
public sealed class RepairOneRow
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly ChangeJournal _journal;
    private readonly IPrompts _prompts;
    private readonly IFileOpener _opener;
    private readonly RunProgress _progress;
    private readonly string _env;

    public RepairOneRow(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write,
        BackupStore backups, ChangeJournal journal, IPrompts prompts, IFileOpener opener,
        RunProgress progress, string env)
    {
        _files = files;
        _read = read;
        _write = write;
        _backups = backups;
        _journal = journal;
        _prompts = prompts;
        _opener = opener;
        _progress = progress;
        _env = env;
    }

    public async Task<RowOutcome> RunAsync(LedgerRow row, CancellationToken ct)
    {
        if (!Guid.TryParse(row.CorrectServiceCatalogueId, out var correct))
            return RowOutcome.Broke("check", "No correct service catalogue on this row.");

        // ---- 0. is there anything to do at all? ----
        //
        // A row can say fix and already be right: somebody corrected it in CRM, or an earlier
        // run did the work and was cut short before recording it. Uploading again would put a
        // third copy of the file on the server and point CRM at it for nothing.
        if (await AlreadyRightAsync(row, correct, ct)) return RowOutcome.AlreadyRight();

        // ---- 1. back up ----

        var download = await _files.DownloadAsync(row.OldFilePath, ct);
        if (!download.Success || download.Data?.File is null)
            return RowOutcome.Broke("backup", $"The old file could not be downloaded: {download.Message}");

        var bytes = Convert.FromBase64String(download.Data.File);
        var extension = Path.GetExtension(row.OldFilePath);

        var oldRecordJson = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocFileId, ct);

        var saved = _backups.Save(row.DocId, row.DocFileId, extension, bytes, row.DocFileName);
        _backups.SaveCrmImage(row.DocFileId, row.DocId, row.OldFilePath, _env,
            await _read.GetRawRecordAsync("mocd_documents", row.DocId, ct) ?? "",
            oldRecordJson ?? "",
            await _read.GetDocumentAnnotationsAsync(row.DocId, ct) ?? "");

        row.BackupPath = _backups.Folder(row.DocId, row.DocFileName).Root;
        _progress.Step("backing up", row.BackupPath);

        // ---- 2. upload — unless an earlier run already put this very file there ----
        //
        // Stopping at the eye check leaves a complete, verified file on the server with nothing
        // pointing at it. Uploading a second one and abandoning that too is how a row ends up
        // with three copies of itself on disk, so the one already there is used instead.

        var reused = await ReusableCopyAsync(row, correct, bytes, ct);
        FileData newFile;

        if (reused is not null)
        {
            newFile = reused;
            _progress.Step("reusing the copy an earlier run left", newFile.FilePath);
        }
        else
        {
            var style = FileRecordCopier.StyleOf(oldRecordJson);

            var upload = await _files.UploadAsync(new UploadRequest(
                Category: correct.ToString(),
                FileName: row.DocFileName.Length > 0 ? row.DocFileName : $"{row.DocFileId}{extension}",
                File: Convert.ToBase64String(bytes),
                MediaType: ReadString(oldRecordJson, "mocd_mediatype") ?? "application/octet-stream",
                Extension: FileRecordCopier.ExtensionFor(oldRecordJson, extension),
                ApplicationId: FileRecordCopier.ApplicationIdFor(style, row.DocId)), ct);

            if (!upload.Success || upload.Data is null)
                return RowOutcome.Broke("upload", upload.Message ?? "the file server gave no reason");

            newFile = upload.Data;
            _progress.Step("uploading", newFile.FilePath);
        }

        // ---- 3. the four checks ----

        // The vendor's id for the OLD file is the stem of its path, not the CRM record's key —
        // they are equal only on portal-created records.
        var oldVendorId = Guid.TryParse(FilePathParser.Parse(row.OldFilePath).FileStem, out var stem)
            ? stem : Guid.Empty;

        var checks = new List<CheckResult>
        {
            // A reused copy has already been compared byte for byte against the backup, which is
            // a stronger statement than two hashes agreeing — and the vendor does not always
            // return a hash on a download, so asking for one here would fail a good file.
            reused is null
                ? Verifier.UploadHashMatches(row.OldHash, newFile.Hash)
                : new CheckResult("bytes", true,
                    "the copy an earlier run left is byte for byte the file just backed up", false),

            Verifier.IsGenuinelyNew(row.OldFilePath, newFile.FilePath, oldVendorId, newFile.FileId),
            Verifier.PathIsFixed(FilePathParser.Parse(newFile.FilePath), correct, newFile.FileId)
        };

        if (checks.All(c => c.Passed))
        {
            var back = await _files.DownloadAsync(newFile.FilePath, ct);
            checks.Add(!back.Success || back.Data?.File is null
                ? new CheckResult("round-trip", false,
                    $"The new file could not be downloaded back: {back.Message}", false)
                : Verifier.RoundTrip(bytes, Convert.FromBase64String(back.Data.File)));
        }

        var report = new VerificationReport(checks);
        if (!report.AllPassed)
            return RowOutcome.Broke("verify",
                string.Join(" | ", report.Failures.Select(f => $"{f.Name}: {f.Detail}")));

        _progress.Step("checked", string.Join(", ", checks.Select(c => c.Name)));

        // ---- 4. the operator's own eyes, unless nobody is watching ----

        var staged = _backups.SaveNew(row.DocId, newFile.FileId, extension, bytes);

        if (_progress.AsksTheEyeCheck)
        {
            _opener.Open(saved.LocalPath);
            _opener.Open(staged.LocalPath);

            // A q pressed while this document was being uploaded is still in the buffer and
            // would otherwise be read as the answer to the question below. Claim it first: it
            // meant "stop when this document is done", and it is remembered until then.
            _progress.NoticeStopRequest();

            var looksRight = _prompts.Confirm("Do these two files look the same?");

            // Quit is not "they look wrong". It is also where a q pressed during the upload
            // lands, because the keystroke waits in the buffer for the next prompt to read.
            // Either way it means stop, and saying the operator rejected the copy would be
            // putting words in their mouth.
            if (looksRight == ConfirmChoice.Quit)
            {
                Abandon(row, newFile.FilePath);
                row.Notes = Note(row.Notes,
                    $"stopped {DateTimeOffset.Now:yyyy-MM-dd HH:mm} before this document was " +
                    $"corrected — nothing in CRM was changed; the uploaded copy is at " +
                    $"{newFile.FilePath} and nothing points at it");
                return RowOutcome.Stopped();
            }

            if (looksRight != ConfirmChoice.Yes)
            {
                Abandon(row, newFile.FilePath);
                row.Notes = Note(row.Notes,
                    $"not corrected {DateTimeOffset.Now:yyyy-MM-dd HH:mm} — you said the copies " +
                    $"did not match; the uploaded copy is at {newFile.FilePath}");
                return RowOutcome.Declined();
            }
        }

        // ---- 5. update the record the document already points at ----

        var before = new RecordValues(row.OldFilePath, row.OldCategory, row.OldHash,
            Blank(row.OldFileName), Blank(row.OldFileId));

        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mocd_filepath"] = newFile.FilePath,
            ["mocd_category"] = correct.ToString(),
            ["mocd_hash"] = newFile.Hash
        };

        // Only where the old record used them. Adding either to a portal record would change
        // its shape, which is the one thing a correction must not do.
        if (ReadString(oldRecordJson, "mocd_fileid") is not null)
            attributes["mocd_fileid"] = newFile.FileId.ToString();

        if (ReadString(oldRecordJson, "mocd_filename") is not null)
            attributes["mocd_filename"] = LeafOf(newFile.FilePath) ?? newFile.FileName;

        try
        {
            await _write.UpdateDocumentFileAsync(row.DocFileId, attributes, ct);
        }
        catch (Exception problem)
        {
            return RowOutcome.Broke("record update", problem.Message);
        }

        _progress.Step("updating the record", row.DocFileId.ToString());

        // ---- 6. read it back. CRM accepting a PATCH is not CRM having stored it ----

        var after = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocFileId, ct);
        var stored = ReadString(after, "mocd_filepath");

        if (!FilePaths.Same(stored, newFile.FilePath))
            return RowOutcome.Broke("read the record back",
                $"mocd_filepath on {row.DocFileId} is '{stored ?? "null"}', not {newFile.FilePath}");

        _progress.Step("reading it back");

        // CRM points at it now, so it is the live file rather than an orphan. Left in the
        // abandoned list it would be reported as one on every future run.
        if (reused is not null)
        {
            NoLongerAbandoned(row, reused.FilePath);
            row.Notes = Note(row.Notes,
                $"corrected {Now()} using the copy an earlier run had already uploaded to " +
                $"{reused.FilePath}; nothing new was uploaded");
        }

        // ---- done ----

        _journal.Append(new ChangeEntry(
            DateTimeOffset.UtcNow, row.DocId, row.DocFileId, ChangeActions.Corrected,
            before,
            new RecordValues(newFile.FilePath, correct.ToString(), newFile.Hash,
                attributes.TryGetValue("mocd_filename", out var n) ? n as string : null,
                attributes.TryGetValue("mocd_fileid", out var f) ? f as string : null)));

        row.NewFilePath = newFile.FilePath;
        row.FinalState = RowStates.Text(RowState.Corrected);

        // The verdict is the column the eye lands on first, and a finished row still reading
        // "fix" says the opposite of the truth. Nothing acts on this value afterwards — the
        // delete step and Check it all both run on the final state — so it is safe to say so.
        row.Verdict = RowVerdicts.Done;
        row.Error = string.Empty;

        return RowOutcome.Ok();
    }

    /// <summary>
    /// Whether CRM already has this document filed correctly, and settles the row if so.
    ///
    /// Asks CRM what the record holds now rather than trusting the ledger, then asks the file
    /// server whether the old file is still there — because those are two different questions
    /// and the answer to the second decides whether anything is still owed:
    ///
    /// - the record was always right, and the old path is the current one: nothing to do, ever;
    /// - the record has been corrected and the old file is still on the server: the deletion is
    ///   still owed, so the row is marked corrected and pending it;
    /// - the record has been corrected and the old file has gone: the work is complete.
    ///
    /// Only reads. Whatever it finds, nothing is uploaded and nothing in CRM is changed.
    /// </summary>
    private async Task<bool> AlreadyRightAsync(LedgerRow row, Guid correct, CancellationToken ct)
    {
        var record = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocFileId, ct);
        var now = ReadString(record, "mocd_filepath");

        if (now is null) return false;

        var filed = FilePathParser.Parse(now).CategorySegment;
        if (!Guid.TryParse(filed, out var under) || under != correct) return false;

        var wasAlwaysRight = FilePaths.Same(now, row.OldFilePath);

        // The catalogue in the path is right, but that is not the same as the document working.
        // A record naming a file nobody can fetch is still broken, and correcting the row is
        // what puts the file back — so this is not a row to settle.
        if (!wasAlwaysRight && !(await _files.DownloadAsync(now, ct)).Success) return false;

        _progress.Step("already correct in CRM", now);

        var oldFileStillThere = !wasAlwaysRight &&
                                (await _files.DownloadAsync(row.OldFilePath, ct)).Success;

        if (wasAlwaysRight)
        {
            // Done, not skip: skip no longer exists, and nothing was ever outstanding here.
            row.Verdict = RowVerdicts.Done;
            row.Notes = Note(row.Notes,
                $"checked {Now()} — CRM already files this under the right catalogue and the " +
                "path has not changed, so there is nothing to correct and nothing to delete");
        }
        else
        {
            row.NewFilePath = now;
            row.Verdict = RowVerdicts.Done;

            if (oldFileStillThere)
            {
                row.FinalState = RowStates.Text(RowState.Corrected);
                row.Notes = Note(row.Notes,
                    $"checked {Now()} — CRM was already corrected by something other than this " +
                    $"run, and the old file is still on the server at {row.OldFilePath}, so the " +
                    "delete step still has work to do here");
            }
            else
            {
                row.FinalState = RowStates.Text(RowState.Deleted);
                row.Notes = Note(row.Notes,
                    $"checked {Now()} — CRM was already corrected and the old file is no longer " +
                    "on the server, so nothing is outstanding");
            }
        }

        row.Error = string.Empty;
        return true;
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// A copy an earlier run uploaded and then left behind, if one can safely be used again.
    ///
    /// The upload happens before the two files are shown, so stopping at that question leaves a
    /// complete file on the server that nothing points at. Working the row again would upload a
    /// second one and abandon that too — a copy per attempt, none of them deleted by anything.
    ///
    /// Three things have to hold before one is used, and all three are checked against the
    /// server rather than against the ledger: it must be filed under the catalogue this row is
    /// being corrected to, it must still be there, and it must be byte for byte the file just
    /// backed up. Anything less and CRM would be pointed at a file nobody has looked at.
    /// </summary>
    /// <returns>The file as the server describes it, or null to upload a fresh one.</returns>
    private async Task<FileData?> ReusableCopyAsync(
        LedgerRow row, Guid correct, byte[] bytes, CancellationToken ct)
    {
        var abandoned = row.SupersededPaths
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse()                           // the most recent attempt first
            .ToList();

        foreach (var path in abandoned)
        {
            // The correct catalogue can change between runs — a document type moved in CRM —
            // and a copy filed under yesterday's answer is no better than the original.
            var filed = FilePathParser.Parse(path).CategorySegment;
            if (!Guid.TryParse(filed, out var under) || under != correct) continue;

            var back = await _files.DownloadAsync(path, ct);
            if (!back.Success || back.Data?.File is null) continue;

            if (!Verifier.RoundTrip(bytes, Convert.FromBase64String(back.Data.File)).Passed)
                continue;

            // Built here rather than taken from the response: the vendor does not reliably echo
            // the path or the id on a download, and those two are what CRM is about to be given.
            var stem = FilePathParser.Parse(path).FileStem;

            return new FileData(
                FileId: Guid.TryParse(stem, out var id) ? id : Guid.Empty,
                FilePath: path,
                Hash: back.Data.Hash,
                FileName: back.Data.FileName,
                MediaType: back.Data.MediaType,
                File: back.Data.File);
        }

        return null;
    }

    /// <summary>
    /// Takes a path out of the abandoned list, because it is not abandoned any more — CRM now
    /// points at it. Leaving it there would report a live file as an orphan for ever.
    /// </summary>
    private static void NoLongerAbandoned(LedgerRow row, string path) =>
        row.SupersededPaths = string.Join(';', row.SupersededPaths
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !FilePaths.Same(p, path)));

    /// <summary>
    /// Records a copy that was uploaded and then left behind, in the column a machine can read.
    ///
    /// The upload happens before the two files are shown, so stopping or rejecting at that
    /// question leaves a complete file on the server with nothing in CRM pointing at it. The
    /// note said so in prose, which is no use to the next run: the row still says fix, so it
    /// uploads a second copy and abandons that one too. Written here as well, the count is
    /// visible and the run can say what is accumulating.
    /// </summary>
    private static void Abandon(LedgerRow row, string path) =>
        row.SupersededPaths = row.SupersededPaths.Length == 0
            ? path
            : $"{row.SupersededPaths};{path}";

    /// <summary>Appends to the notes cell without discarding what is already in it.</summary>
    private static string Note(string existing, string addition) =>
        existing.Length == 0 ? addition : $"{existing}; {addition}";

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    private static string? LeafOf(string path)
    {
        var leaf = path.Replace('/', '\\').Split('\\').LastOrDefault();
        return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
    }

    private static string? ReadString(string? json, string attribute)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(attribute, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
