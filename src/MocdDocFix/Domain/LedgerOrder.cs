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
            .OrderBy(x => Rank(x.row.Verdict2()))
            .ThenBy(x => x.at)
            .Select(x => x.row)
            .ToList();

        for (var i = 0; i < ordered.Count; i++) ordered[i].Row = i + 1;

        return ordered;
    }
}
