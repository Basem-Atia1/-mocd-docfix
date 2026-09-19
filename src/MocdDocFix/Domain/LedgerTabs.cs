namespace MocdDocFix.Domain;

/// <summary>Which sheet of the workbook a row is shown on.</summary>
public enum LedgerTab
{
    /// <summary>Documents still to fix. What the repair run reads.</summary>
    Ledger,

    /// <summary>Corrected, old file still on the server. What the delete step reads.</summary>
    Corrected,

    /// <summary>Nothing left to do. What "Check it all" verifies.</summary>
    Finished
}

/// <summary>
/// The one rule saying where a row is shown.
///
/// One tab per mode, so each has a single place to look instead of a filter over one long sheet.
///
/// It is presentation and nothing else. An .xlsx is a single zip archive and a tab is a folder
/// inside it, so writing any one tab rebuilds the whole file — three tabs cost exactly what one
/// costs. No mode is confined to its own tab either: <see cref="Storage.LedgerWorkbook"/> reads
/// all three back as one list, and every mode still sees every row.
/// </summary>
public static class LedgerTabs
{
    public static LedgerTab Of(LedgerRow row) => row.State() switch
    {
        // The old file is still on the server and the delete has not run. This must never be
        // called finished: a tab named finished holding a file that still needs deleting is how
        // an orphan gets left behind with nobody counting it.
        RowState.Corrected => LedgerTab.Corrected,

        RowState.Deleted => LedgerTab.Finished,

        // Settled by the pre-run check, or by a sibling row sharing the same document file
        // record: done, with no final state, because there was never a delete of its own.
        RowState.NotStarted when row.Verdict2() == RowVerdict.Done => LedgerTab.Finished,

        _ => LedgerTab.Ledger
    };

    public static string Name(LedgerTab tab) => tab switch
    {
        LedgerTab.Corrected => "corrected",
        LedgerTab.Finished => "finished",
        _ => "ledger"
    };

    /// <summary>In workbook order, left to right.</summary>
    public static readonly IReadOnlyList<LedgerTab> All =
        new[] { LedgerTab.Ledger, LedgerTab.Corrected, LedgerTab.Finished };
}
