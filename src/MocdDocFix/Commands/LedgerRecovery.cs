using MocdDocFix.Domain;
using MocdDocFix.Storage;

namespace MocdDocFix.Commands;

/// <param name="NowIs">
/// What the row says now, in the words of the final state column — or "back to fix" for a
/// revert, which clears it. The one thing worth reading about a row that has just been changed
/// underneath the operator is what it has been changed to.
/// </param>
public sealed record RecoveredRow(LedgerRow Row, string NowIs);

/// <param name="Changed">Every row put right, and what each one now says.</param>
/// <param name="MissingFromJournal">
/// Rows the ledger says were corrected or deleted that the journal has never heard of. The
/// journal is append-only and is written first, so it cannot fall behind on its own: anything
/// here means it was deleted, truncated or replaced.
/// </param>
public sealed record Recovered(
    int Rows, IReadOnlyList<RecoveredRow> Changed, int MissingFromJournal = 0);

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

        var changed = new Dictionary<Guid, RecoveredRow>();

        // In order: a document corrected, reverted and corrected again must end where it ended.
        // The dictionary is keyed on the document for the same reason — the last word wins, and
        // saying a row is corrected when a later entry put it back to fix would be worse than
        // saying nothing.
        foreach (var entry in journal.OrderBy(e => e.At))
        {
            if (!byDocument.TryGetValue(entry.Doc, out var row)) continue;

            var nowIs = entry.Action switch
            {
                ChangeActions.Corrected => Correct(row, entry),
                ChangeActions.Deleted => Delete(row),
                ChangeActions.Reverted => Revert(row),
                _ => null
            };

            if (nowIs is null) continue;

            changed[row.DocId] = new RecoveredRow(row, nowIs);
        }

        // The other direction. The journal is written before the ledger and only ever appended
        // to, so it can never legitimately know less than the ledger does. Where it does, it
        // has been deleted or replaced — and the route back for those rows is now the backup
        // folder alone, which is worth being told about rather than discovering during a revert.
        var known = journal.Select(e => e.Doc).ToHashSet();

        var missing = rows.Count(r =>
            r.State() is RowState.Corrected or RowState.Deleted && !known.Contains(r.DocId));

        return new Recovered(changed.Count, changed.Values.ToList(), missing);
    }

    /// <returns>What the row says now, or null when there was nothing to put right.</returns>
    private static string? Correct(LedgerRow row, ChangeEntry entry)
    {
        // Already recorded, by this run or an earlier one. Nothing to put right.
        if (row.State() is RowState.Corrected or RowState.Deleted) return null;

        row.NewFilePath = entry.New?.Path ?? row.NewFilePath;
        row.FinalState = RowStates.Text(RowState.Corrected);
        row.Verdict = RowVerdicts.Done;
        row.Error = string.Empty;
        row.Notes = Add(row.Notes,
            $"recovered from the change journal {Now()} — it was corrected at " +
            $"{entry.At.LocalDateTime:yyyy-MM-dd HH:mm} and the ledger never recorded it");

        return RowStates.Corrected;
    }

    private static string? Delete(LedgerRow row)
    {
        if (row.State() == RowState.Deleted) return null;

        row.FinalState = RowStates.Text(RowState.Deleted);
        row.Verdict = RowVerdicts.Done;
        row.Notes = Add(row.Notes,
            $"recovered from the change journal {Now()} — its old file was deleted and the " +
            "ledger never recorded it");

        return RowStates.Text(RowState.Deleted);
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
        row.Notes = Add(row.Notes,
            $"recovered from the change journal {Now()} — it was put back and the ledger never " +
            "recorded it");

        // No final state to name. What matters is that it is work again.
        return "back to fix — its record was put back";
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");

    private static string Add(string existing, string addition) =>
        existing.Length == 0 ? addition : $"{existing}; {addition}";
}
