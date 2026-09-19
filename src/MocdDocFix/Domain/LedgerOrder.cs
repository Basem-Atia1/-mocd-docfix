namespace MocdDocFix.Domain;

/// <summary>
/// The order rows appear in, and the numbers they carry.
///
/// Sorted by verdict so the work is at the top: fix and skip are the two big populations and
/// sit together, then the rows wanting a decision, then the ones deliberately left alone.
/// Within a verdict the scan's own order is kept, so two documents of the same kind stay
/// neighbours run after run.
///
/// Numbering is positional — the sheet always reads 1, 2, 3 down the page. That means a
/// document's number changes when its verdict does, so nothing should ever be identified by
/// its row number: <see cref="LedgerRow.DocId"/> is the identity.
/// </summary>
public static class LedgerOrder
{
    /// <summary>
    /// Whether the work on this row is over, whatever its verdict happens to say.
    ///
    /// Sorted on before the verdict is, because the two can disagree: a row corrected before the
    /// tool wrote "done", then deleted, still reads "fix" — and ranking on the verdict alone put
    /// that finished row at the very top of the sheet, among the work still outstanding. The
    /// final state is the record of what happened; the verdict is only an instruction.
    ///
    /// It asks <see cref="LedgerTabs"/> rather than testing the state itself, so the rule that
    /// sorts a row and the rule that decides which tab it is written to are one rule. Two
    /// definitions of "finished" would drift apart, and the one that drifted would put a
    /// corrected document back among the outstanding work.
    /// </summary>
    private static bool Finished(LedgerRow row) => LedgerTabs.Of(row) != LedgerTab.Ledger;

    /// <summary>
    /// Where each verdict sits. Unrecognised goes last but above nothing — a typo must be
    /// visible, and burying it among hundreds of skipped rows is how it would be missed.
    /// </summary>
    private static int Rank(RowVerdict verdict) => verdict switch
    {
        RowVerdict.Fix => 0,
        RowVerdict.Skip => 1,
        RowVerdict.Review => 2,
        RowVerdict.Redo => 3,
        RowVerdict.Ignore => 4,
        RowVerdict.Unrecognised => 5,

        // Last. A finished row is the one thing nobody needs to look at again, and leaving it
        // among the outstanding work is what made a corrected document still read as "to do".
        RowVerdict.Done => 6,
        _ => 5
    };

    /// <summary>
    /// A new list in display order, with every row renumbered to its position. The rows
    /// themselves are the same objects — this reorders and renumbers, it never copies.
    /// </summary>
    public static IReadOnlyList<LedgerRow> Sorted(IEnumerable<LedgerRow> rows)
    {
        var ordered = rows
            .Select((row, at) => (row, at))
            .OrderBy(x => Finished(x.row) ? 1 : 0)
            .ThenBy(x => Rank(x.row.Verdict2()))
            .ThenBy(x => x.at)
            .Select(x => x.row)
            .ToList();

        for (var i = 0; i < ordered.Count; i++) ordered[i].Row = i + 1;

        return ordered;
    }
}
