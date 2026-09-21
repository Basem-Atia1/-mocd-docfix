using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <param name="Removed">How many rows were taken out.</param>
public sealed record Dropped(IReadOnlyList<LedgerRow> Rows, int Removed);

/// <summary>
/// Takes rows whose record names no file out of the sheet, every time it is opened.
///
/// There is no path to diagnose and no file to move, so there is nothing this tool can do with
/// one — which is why the scan stopped writing them. But a scan that no longer produces them
/// cannot un-write the ones already on disk, and until now they were only cleared on a run that
/// went and read CRM. Somebody who opens the ledger, works from it as it is and never rescans
/// keeps them for ever.
///
/// **The file path is the authority, not the verdict.** They were written as "review" and a hand
/// can change that to anything; what makes the row impossible to act on is that there is no
/// file. Any row with a path has a group, a reason and a remedy, so an empty one cannot be
/// anything else.
///
/// A row with a final state is never taken out, and cannot be one of these anyway: a final state
/// is the record that something was done to the document, and nothing can be done to a record
/// naming no file. The test stays because that rule is worth being unconditional.
/// </summary>
public static class DropRowsWithNoFile
{
    public static Dropped From(IReadOnlyList<LedgerRow> rows)
    {
        var kept = rows.Where(row => !ShouldGo(row)).ToList();

        return new Dropped(kept, rows.Count - kept.Count);
    }

    private static bool ShouldGo(LedgerRow row) =>
        row.OldFilePath.Length == 0 && row.State() == RowState.NotStarted;
}
