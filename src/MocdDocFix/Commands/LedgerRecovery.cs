using MocdDocFix.Domain;
using MocdDocFix.Storage;

namespace MocdDocFix.Commands;

/// <param name="Notes">One line per row put right, for the operator to read.</param>
/// <param name="MissingFromJournal">
/// Rows the ledger says were corrected or deleted that the journal has never heard of. The
/// journal is append-only and is written first, so it cannot fall behind on its own: anything
/// here means it was deleted, truncated or replaced.
/// </param>
public sealed record Recovered(int Rows, IReadOnlyList<string> Notes, int MissingFromJournal = 0);

/// <summary>
/// Puts the ledger back in step with what actually happened, from the change journal.
///
/// The ledger is rewritten after every row, and until it is, the work is done but unrecorded:
/// the file is uploaded and the CRM record changed, with nothing on disk saying so. A crash, a
/// stopped run or a ledger locked in Excel all leave that gap. Left alone it is worse than
/// untidy — the row still reads as work to do, so the next run uploads the same file again and
/// orphans the copy it made last time, and the delete step never removes an old file nobody
/// recorded as superseded.
///
/// The journal is append-only and is written before the ledger, so it always knows at least as
/// much. Replaying it in order and filling in what the ledger is missing closes the gap without
/// asking CRM anything.
///
/// It only ever fills in blanks. A row the ledger already has an answer for is left alone —
/// this reconciles a gap, it does not overrule the operator.
/// </summary>
public static class LedgerRecovery
{
    public static Recovered Apply(IReadOnlyList<LedgerRow> rows, IReadOnlyList<ChangeEntry> journal)
    {
        var byDocument = new Dictionary<Guid, LedgerRow>();
        foreach (var row in rows) byDocument[row.DocId] = row;

        var notes = new List<string>();
        var touched = new HashSet<Guid>();

        // In order: a document corrected, reverted and corrected again must end where it ended.
        foreach (var entry in journal.OrderBy(e => e.At))
        {
            if (!byDocument.TryGetValue(entry.Doc, out var row)) continue;

            var note = entry.Action switch
            {
                ChangeActions.Corrected => Correct(row, entry),
                ChangeActions.Deleted => Delete(row),
                ChangeActions.Reverted => Revert(row),
                _ => null
            };

            if (note is null) continue;

            notes.Add($"row {row.Row} ({row.DocFileName}): {note}");
            touched.Add(row.DocId);
        }

        // The other direction. The journal is written before the ledger and only ever appended
        // to, so it can never legitimately know less than the ledger does. Where it does, it
        // has been deleted or replaced — and the route back for those rows is now the backup
        // folder alone, which is worth being told about rather than discovering during a revert.
        var known = journal.Select(e => e.Doc).ToHashSet();

        var missing = rows.Count(r =>
            r.State() is RowState.Corrected or RowState.Deleted && !known.Contains(r.DocId));

        return new Recovered(touched.Count, notes, missing);
    }

    private static string? Correct(LedgerRow row, ChangeEntry entry)
    {
        // Already recorded, by this run or an earlier one. Nothing to put right.
        if (row.State() is RowState.Corrected or RowState.Deleted) return null;

        row.NewFilePath = entry.New?.Path ?? row.NewFilePath;
        row.FinalState = RowStates.Text(RowState.Corrected);
        row.Error = string.Empty;
        row.Notes = Add(row.Notes, $"recovered from the change journal {Now()}");

        return $"was corrected at {entry.At.LocalDateTime:yyyy-MM-dd HH:mm} but the ledger never " +
               "recorded it — marked corrected and pending the delete of old docs";
    }

    private static string? Delete(LedgerRow row)
    {
        if (row.State() == RowState.Deleted) return null;

        row.FinalState = RowStates.Text(RowState.Deleted);
        row.Notes = Add(row.Notes, $"recovered from the change journal {Now()}");

        return "its old file was deleted but the ledger never recorded it — marked old files deleted";
    }

    private static string? Revert(LedgerRow row)
    {
        if (row.State() != RowState.Corrected) return null;

        if (row.NewFilePath.Length > 0)
            row.SupersededPaths = row.SupersededPaths.Length == 0
                ? row.NewFilePath
                : $"{row.SupersededPaths};{row.NewFilePath}";

        row.NewFilePath = string.Empty;
        row.FinalState = string.Empty;
        row.Verdict = RowVerdicts.Fix;
        row.Notes = Add(row.Notes, $"recovered from the change journal {Now()}");

        return "was reverted but the ledger never recorded it — put back to fix";
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");

    private static string Add(string existing, string addition) =>
        existing.Length == 0 ? addition : $"{existing}; {addition}";
}
