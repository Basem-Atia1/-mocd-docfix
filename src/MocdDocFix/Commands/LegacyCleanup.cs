using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <param name="Removed">Rows taken out of the sheet because there was never anything to do.</param>
/// <param name="Kept">Rows that said skip but carried a record of work, and were rewritten.</param>
/// <param name="NoFile">
/// Rows taken out because the record names no file at all. Counted apart from
/// <paramref name="Removed"/>: those were documents that are fine, these are documents nothing
/// can be done about, and the two want different sentences on screen.
/// </param>
public sealed record CleanupResult(
    IReadOnlyList<LedgerRow> Rows, int Removed, int Kept, int NoFile = 0);

/// <summary>
/// Puts a sheet written by an earlier build in order, once.
///
/// Correct documents no longer enter the ledger, but a sheet already on disk is full of them —
/// 96 of the dev ledger's 410 rows. A scan that simply stops producing them cannot un-write the
/// ones already there, so they are cleared out here.
///
/// **A row with a final state is never deleted.** The final state is the record that something
/// was really done to that document: a file uploaded, a record overwritten, an old file removed.
/// The verdict is the column a hand can change in a second; the final state is not. So where the
/// two disagree the verdict is rewritten to agree with the final state, and the row stays.
///
/// It runs *before* "skip" is read as "done", which is why it looks at the raw cell rather than
/// at <see cref="LedgerRow.Verdict2"/>. Read as done first, every untouched row would land on
/// the finished tab and never be cleared at all.
///
/// After the first run there is nothing left saying skip, and this does nothing for ever after.
/// </summary>
public static class LegacyCleanup
{
    private const string LegacySkip = "skip";

    /// <param name="scanned">
    /// The fresh read of CRM. Its verdicts decide what is still broken — the stored cell cannot,
    /// because the whole point is that the stored cell is out of date.
    /// </param>
    public static CleanupResult Apply(
        IReadOnlyList<LedgerRow> existing, IReadOnlyList<LedgerRow> scanned)
    {
        var stillBroken = scanned
            .Where(s => s.Verdict.Length > 0)
            .Select(s => s.DocId)
            .ToHashSet();

        var kept = new List<LedgerRow>(existing.Count);
        int removed = 0, rewritten = 0, noFile = 0;

        foreach (var row in existing)
        {
            // A record naming no file. These no longer enter the sheet at all, and an earlier
            // build wrote them in as review — thirty-three of them in the dev ledger. Same rule
            // as everything else here: untouched, unremarked-upon, and no longer wanted by the
            // scan, or it stays. Somebody may have attached a file since, and then it is work.
            if (row.Group == 8 &&
                row.OldFilePath.Length == 0 &&
                row.State() == RowState.NotStarted &&
                row.Notes.Length == 0 &&
                !stillBroken.Contains(row.DocId))
            {
                noFile++;
                continue;
            }

            if (!row.Verdict.Trim().Equals(LegacySkip, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(row);
                continue;
            }

            // Only a row that is correct, untouched and unremarked-upon goes. Anything the scan
            // still has an opinion about, anything somebody left a note on, and anything with a
            // record of work against it stays.
            if (row.State() == RowState.NotStarted &&
                row.Notes.Length == 0 &&
                !stillBroken.Contains(row.DocId))
            {
                removed++;
                continue;
            }

            row.Verdict = VerdictFor(row.State());
            rewritten++;
            kept.Add(row);
        }

        return new CleanupResult(kept, removed, rewritten, noFile);
    }

    /// <summary>
    /// What the verdict should have said, given what really happened to the row. The final state
    /// is the authority here, not the word somebody typed over the top of it.
    /// </summary>
    private static string VerdictFor(RowState state) => state switch
    {
        RowState.Corrected or RowState.Deleted => RowVerdicts.Done,

        // It failed. That is not finished and it is not nothing — somebody has to look.
        RowState.Failed => RowVerdicts.Review,

        RowState.Ignore => RowVerdicts.Ignore,

        // Untouched, but the scan still wants it or a note was left on it. Review, because the
        // one thing certainly untrue now is that there is nothing to do.
        _ => RowVerdicts.Review
    };
}
