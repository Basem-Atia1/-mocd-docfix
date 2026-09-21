using System.Text.Json;
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="Aborted">The operator did not agree to the step, so nothing was attempted.</param>
/// <param name="AlreadyGone">
/// Rows whose old file was not on the server to delete. Counted apart from deletions because
/// nothing was removed, and apart from refusals because nothing is outstanding either.
/// </param>
/// <param name="FoundAgain">
/// Rows that said the old file was deleted and still had one, found by the check after the run.
/// </param>
public sealed record DeleteSummary(
    int Deleted, int Refused, bool Aborted, IReadOnlyList<string> Reasons,
    int AlreadyGone = 0, int Checked = 0, int FoundAgain = 0);

/// <summary>
/// Removes the old file of every row the repair run finished, and nothing else.
///
/// Because a correction updated the record rather than replacing it, there is no orphaned CRM
/// row to delete — the record the document points at is the same one it always pointed at, now
/// naming the new file. So this step touches the file server only.
///
/// Two checks stand between a row and an irreversible deletion, and both ask a system rather
/// than the ledger: does the record really hold the new path now, and does any other record
/// still name the old one.
/// </summary>
public sealed class DeleteOldFiles
{
    /// <summary>
    /// Written into the notes of a row that said its old file was deleted and turned out to
    /// still have one. It is what lets the next run offer that row again: a note is the only
    /// column the tool writes here, because the final state is the operator's record of what
    /// they believe happened, and changing it behind them is exactly what this avoids.
    /// </summary>
    public const string Recheck = "[recheck]";

    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ChangeJournal _journal;
    private readonly ILedger _ledger;
    private readonly IPrompts _prompts;

    private readonly ErrorLog? _errors;

    /// <param name="errors">
    /// Where a refusal is written in full. The screen gets a sentence that fits a line; the
    /// reason a server would not delete something is usually longer than that and is exactly
    /// what somebody needs an hour later.
    /// </param>
    public DeleteOldFiles(IFileServiceClient files, ICrmReadClient read, ChangeJournal journal,
        ILedger ledger, IPrompts prompts, ErrorLog? errors = null)
    {
        _files = files;
        _read = read;
        _journal = journal;
        _ledger = ledger;
        _prompts = prompts;
        _errors = errors;
    }

    public async Task<DeleteSummary> RunAsync(
        IReadOnlyList<LedgerRow> rows, bool isProduction, CancellationToken ct)
    {
        // Corrected, and not marked to be put back.
        //
        // This step used to read the final state alone. A row corrected and then given the
        // verdict "redo" by hand was therefore deleted like any other — and deleting the old
        // file is exactly what makes a redo impossible, so the two steps in that order destroy
        // the route back and Redo then refuses the row for ever. The verdict is the operator
        // saying what they want to happen next, and it wins over the sheet's history here.
        var eligible = rows
            .Where(r => r.State() == RowState.Corrected && r.Verdict2() != RowVerdict.Redo)
            .ToList();

        var heldForRedo = rows.Count(r =>
            r.State() == RowState.Corrected && r.Verdict2() == RowVerdict.Redo);

        if (heldForRedo > 0)
            _prompts.Say($"{heldForRedo} corrected row(s) say redo, so their old files are left " +
                         "alone — deleting one is what makes a redo impossible.", Tone.Warn);

        var reasons = new List<string>();

        // Rows a previous check found still holding their old file. They say deleted, so nothing
        // would ever look at them again; this is where they are offered back.
        eligible.AddRange(OfferRechecked(rows).Where(r => !eligible.Contains(r)));

        if (eligible.Count == 0)
            return new DeleteSummary(0, 0, false,
                new[] { $"No row says \"{RowStates.Corrected}\", so there is nothing to delete." });

        if (!Agreed(eligible.Count, isProduction))
            return new DeleteSummary(0, 0, true, reasons);

        int deleted = 0, refused = 0, alreadyGone = 0, at = 0;
        var done = new List<LedgerRow>();

        foreach (var row in eligible)
        {
            ct.ThrowIfCancellationRequested();

            // Over the files this step will delete, not over the ledger. The sheet can hold
            // tens of thousands of rows that have nothing to do with deleting anything.
            var where = $"{++at}/{eligible.Count}";

            var outcome = await DeleteOneAsync(row, ct);

            if (outcome.Problem is null)
            {
                deleted++;
                done.Add(row);

                if (outcome.AlreadyGone)
                {
                    alreadyGone++;
                    _prompts.Info($"  [ {where} ]  {row.Ref()}  the old file was already gone",
                        Tone.Muted);
                }
                else
                {
                    _prompts.Info($"  [ {where} ]  {row.Ref()}  old file deleted", Tone.Good);
                }
            }
            else
            {
                refused++;
                reasons.Add($"{row.Ref()}: {Short(outcome.Problem)}");

                _errors?.Append(row.Row, eligible.Count, row, "delete the old file", outcome.Problem);
                _prompts.Info($"  [ {where} ]  {row.Ref()}  REFUSED — {Short(outcome.Problem)}",
                    Tone.Warn);
            }

            _ledger.Write(rows);
        }

        var (checkedRows, foundAgain) = await VerifyAsync(rows, done, ct);
        if (checkedRows > 0) _ledger.Write(rows);

        return new DeleteSummary(deleted, refused, false, reasons, alreadyGone,
            checkedRows, foundAgain);
    }

    /// <param name="AlreadyGone">
    /// True when nothing was removed because nothing was there. Still a success: the outcome the
    /// step exists to reach is that the old file is not on the server, and it is not.
    /// </param>
    private readonly record struct DeleteOutcome(string? Problem, bool AlreadyGone);

    private async Task<DeleteOutcome> DeleteOneAsync(LedgerRow row, CancellationToken ct)
    {
        if (row.OldFilePath.Length == 0) return new("the ledger has no old file path for it", false);
        if (row.NewFilePath.Length == 0) return new("the ledger has no new file path for it", false);

        // CRM, live. The ledger's belief that the correction landed is not evidence that it did.
        var record = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocFileId, ct);
        var holds = ReadString(record, "mocd_filepath");

        if (!FilePaths.Same(holds, row.NewFilePath))
            return new($"its record still names '{holds ?? "nothing"}', not the new file — the " +
                       "correction did not land, so the old file is still the one in use", false);

        // One file referenced by two records is rare and real, and deleting it breaks the other.
        var referencing = await _read.FindDocumentFilesByPathAsync(row.OldFilePath, ct);
        if (referencing.Any(id => id != row.DocFileId))
            return new("another mocd_documentfile still points at the old file", false);

        var gone = await _files.DeleteAsync(row.OldFilePath, ct);
        var alreadyGone = false;

        if (!gone.Success)
        {
            // "No" from the server is two different answers: the file is not there, which is the
            // outcome we wanted, and the server would not remove it, which is not. Asking for the
            // file settles which — and a row whose file was already gone used to be a refusal
            // that never cleared, reported again on every run for ever.
            if ((await _files.DownloadAsync(row.OldFilePath, ct)).Success)
                return new($"the file server would not delete it: {gone.Message}", false);

            alreadyGone = true;
        }

        _journal.Append(new ChangeEntry(
            DateTimeOffset.UtcNow, row.DocId, row.DocFileId, ChangeActions.Deleted,
            new RecordValues(row.OldFilePath, row.OldCategory, row.OldHash,
                row.OldFileName.Length == 0 ? null : row.OldFileName,
                row.OldFileId.Length == 0 ? null : row.OldFileId),
            null));

        row.FinalState = RowStates.Text(RowState.Deleted);

        // The verdict too: a finished row that still reads "fix" says the opposite of the truth,
        // and until now only the correction step wrote "done" — so a row corrected before that
        // existed, then deleted here, kept saying there was work to do on it.
        row.Verdict = RowVerdicts.Done;

        // Whatever brought the row here, it is settled now, so it must not be offered again.
        row.Notes = Note(Cleared(row.Notes), alreadyGone
            ? $"{Now()} — the old file was already gone from the server, so nothing was removed"
            : $"{Now()} — old file deleted");

        return new(null, alreadyGone);
    }

    /// <summary>
    /// Asks the file server whether the files this run removed are really gone, and offers to
    /// check the ones earlier runs removed as well.
    ///
    /// It writes nothing to the server and changes no final state. A row that still has its old
    /// file gets a note carrying <see cref="Recheck"/>, and the next run offers it back.
    /// </summary>
    private async Task<(int Checked, int FoundAgain)> VerifyAsync(
        IReadOnlyList<LedgerRow> all, IReadOnlyList<LedgerRow> justDone, CancellationToken ct)
    {
        var toCheck = new List<LedgerRow>();

        if (justDone.Count > 0)
        {
            _prompts.Blank();
            if (_prompts.YesNo($"  Check that those {justDone.Count} file(s) are really gone?",
                    defaultYes: true))
                toCheck.AddRange(justDone);
        }

        var older = all
            .Where(r => r.State() == RowState.Deleted && r.OldFilePath.Length > 0)
            .Where(r => !justDone.Contains(r))
            .ToList();

        if (older.Count > 0)
        {
            _prompts.Say($"  {older.Count} row(s) were deleted in earlier runs. Checking them " +
                         "asks the file server once per row, so it takes a while.", Tone.Muted);

            if (_prompts.YesNo($"  Also check those {older.Count}?", defaultYes: false))
                toCheck.AddRange(older);
        }

        if (toCheck.Count == 0) return (0, 0);

        var foundAgain = 0;

        foreach (var row in toCheck)
        {
            ct.ThrowIfCancellationRequested();

            if (!(await _files.DownloadAsync(row.OldFilePath, ct)).Success) continue;

            foundAgain++;
            row.Notes = Note(Cleared(row.Notes),
                $"{Now()} — said deleted, but the old file is still on the server at " +
                $"{row.OldFilePath} {Recheck}");

            _prompts.Info($"  [ {row.Ref()} ]  still on the server at {row.OldFilePath}", Tone.Warn);
        }

        return (toCheck.Count, foundAgain);
    }

    /// <summary>
    /// Rows a previous check found still holding their old file, offered back for this run.
    ///
    /// They say "deleted", so nothing else in the tool will ever look at them again — which is
    /// why the check leaves a mark instead of quietly moving them back to corrected.
    /// </summary>
    private IReadOnlyList<LedgerRow> OfferRechecked(IReadOnlyList<LedgerRow> rows)
    {
        var marked = rows.Where(r => r.Notes.Contains(Recheck, StringComparison.Ordinal)).ToList();
        if (marked.Count == 0) return Array.Empty<LedgerRow>();

        _prompts.Section($"{marked.Count} row(s) say their old file was deleted, but a check " +
                         "found it still on the server", Tone.Warn);

        foreach (var row in marked.Take(10))
            _prompts.Bullet($"{row.Ref()}: {row.OldFilePath}", Tone.Muted);

        if (marked.Count > 10)
            _prompts.Bullet($"… and {marked.Count - 10} more", Tone.Muted);

        _prompts.Blank();

        if (_prompts.YesNo("  Delete those old files too, in this run?", defaultYes: true))
            return marked;

        _prompts.Say("Left as they are. They will be offered again next time.", Tone.Muted);
        return Array.Empty<LedgerRow>();
    }

    private bool Agreed(int count, bool isProduction)
    {
        _prompts.Section("Delete old files", Tone.Danger);
        _prompts.Say($"{count} row(s) are queued. For each one, the OLD file is removed from the " +
                     "file server.");
        _prompts.Blank();
        _prompts.Warn("This cannot be undone.", Tone.Danger);
        _prompts.Blank();
        _prompts.Bullet("No CRM record is deleted. The correction updated the record the document " +
                        "already pointed at, so there is no orphan to remove.", Tone.Muted);
        _prompts.Bullet("Each row is re-checked against CRM immediately before its file goes.",
            Tone.Muted);
        _prompts.Bullet("Your local backup keeps the bytes, but a restored file gets a new id and " +
                        "today's date folder — it cannot go back to its old path.", Tone.Muted);
        _prompts.Blank();

        if (!_prompts.YesNo("  Delete the old files now?", defaultYes: false, Tone.Danger))
            return false;

        return !isProduction || _prompts.TypedWord(
            "  This is PRODUCTION. Type DELETE to confirm", "DELETE");
    }

    /// <summary>One line for the screen. The whole of it goes to the error log.</summary>
    private static string Short(string problem)
    {
        var firstLine = problem.Split('\n')[0].TrimEnd();
        return firstLine.Length <= 160 ? firstLine : firstLine[..157] + "…";
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");

    private static string Note(string notes, string line) =>
        notes.Length == 0 ? line : $"{notes}; {line}";

    /// <summary>
    /// Drops the recheck mark, leaving the sentence that carried it. The mark is an instruction
    /// to the next run, not history: once the row has been through the step again it must not be
    /// offered a third time, but what the check found is still worth reading.
    /// </summary>
    private static string Cleared(string notes) =>
        notes.Replace($" {Recheck}", string.Empty, StringComparison.Ordinal)
             .Replace(Recheck, string.Empty, StringComparison.Ordinal);

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
