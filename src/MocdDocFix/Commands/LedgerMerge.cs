using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <param name="Added">Documents CRM has that the ledger did not.</param>
/// <param name="Refreshed">Rows whose facts were brought up to date from CRM.</param>
/// <param name="Protected">Rows already acted on, whose history was left alone.</param>
/// <param name="Vanished">Rows whose document CRM no longer returns.</param>
/// <param name="Row">The row as the ledger holds it.</param>
/// <param name="ScanSays">What a fresh look at CRM would put in the verdict column.</param>
public sealed record VerdictDisagreement(LedgerRow Row, string ScanSays);

/// <param name="Disagreements">
/// Rows whose verdict differs from what the scan would write. Not acted on: the operator is
/// asked whether to keep their own answers or take CRM's, because either can be the right one
/// and the tool cannot tell which.
/// </param>
public sealed record Merged(
    IReadOnlyList<LedgerRow> Rows, int Added, int Refreshed, int Protected, int Vanished,
    IReadOnlyList<string> Notes, IReadOnlyList<VerdictDisagreement> Disagreements);

/// <summary>
/// Brings one ledger up to date from a fresh read of CRM, instead of starting a second one.
///
/// There is one ledger per environment for its whole life. Rebuilding would throw away every
/// verdict the operator has typed and every final state the runs have recorded; keeping a stale
/// one would hide documents added since. So the scan's answers and the ledger's answers are
/// merged, and the rule for who wins is decided per column rather than per file.
///
/// The sharp edge is history. Once a row has been corrected, CRM's mocd_filepath holds the NEW
/// path — so refreshing "old file path" from CRM on a corrected row would overwrite the only
/// record of where the file used to be, which is exactly what Redo and the delete step depend
/// on. Rows that have been acted on therefore keep everything about paths and old values, and
/// take only their display name and links.
/// </summary>
public static class LedgerMerge
{
    public static Merged Into(IReadOnlyList<LedgerRow> existing, IReadOnlyList<LedgerRow> fresh)
    {
        var byDocument = new Dictionary<Guid, LedgerRow>();
        foreach (var row in existing) byDocument[row.DocId] = row;

        var seen = new HashSet<Guid>();
        var rows = new List<LedgerRow>(existing);
        var notes = new List<string>();
        var disagreements = new List<VerdictDisagreement>();
        int added = 0, refreshed = 0, protectedRows = 0;

        foreach (var scanned in fresh)
        {
            seen.Add(scanned.DocId);

            if (!byDocument.TryGetValue(scanned.DocId, out var row))
            {
                rows.Add(scanned);
                added++;
                continue;
            }

            if (HasBeenActedOn(row))
            {
                // Name and links only. Everything else on this row is a record of what was,
                // and CRM now describes what is.
                row.DocName = scanned.DocName;
                row.DocTypeName = scanned.DocTypeName;
                row.CrmLinkOfDoc = scanned.CrmLinkOfDoc;
                row.CrmLinkOfDocFile = scanned.CrmLinkOfDocFile;
                protectedRows++;
                continue;
            }

            if (!string.Equals(row.Verdict.Trim(), scanned.Verdict,
                    StringComparison.OrdinalIgnoreCase))
                disagreements.Add(new VerdictDisagreement(row, scanned.Verdict));

            Refresh(row, scanned);
            refreshed++;
        }

        foreach (var row in existing.Where(r => !seen.Contains(r.DocId)))
        {
            notes.Add($"row {row.Row} ({row.DocFileName}): CRM no longer returns this document — " +
                      "kept in the ledger, but nothing will act on it");
        }

        return new Merged(rows, added, refreshed, protectedRows,
            existing.Count - seen.Count(id => byDocument.ContainsKey(id)), notes, disagreements);
    }

    /// <summary>
    /// Takes the scan's verdict for the rows where the two disagree — what the operator gets by
    /// answering "refresh from CRM" rather than "keep what I typed".
    /// </summary>
    public static void TakeScanVerdicts(IReadOnlyList<VerdictDisagreement> disagreements)
    {
        foreach (var (row, scanSays) in disagreements) row.Verdict = scanSays;
    }

    /// <summary>
    /// True once a run has changed something for this row, or the operator has excluded it.
    /// A failed row has not been acted on successfully, so it is still refreshed.
    /// </summary>
    private static bool HasBeenActedOn(LedgerRow row) =>
        row.State() is RowState.Corrected or RowState.Deleted or RowState.Ignore ||
        row.Verdict2() is RowVerdict.Ignore or RowVerdict.Redo or RowVerdict.Done;

    /// <summary>
    /// Everything CRM is the authority on. The four columns the operator owns — verdict, final
    /// state, notes — and everything a run records are deliberately absent.
    /// </summary>
    private static void Refresh(LedgerRow row, LedgerRow scanned)
    {
        row.DocName = scanned.DocName;
        row.DocTypeName = scanned.DocTypeName;
        row.DocFileName = scanned.DocFileName;
        row.DocFileId = scanned.DocFileId;

        row.OldFilePath = scanned.OldFilePath;
        row.NewFilePathPredicted = scanned.NewFilePathPredicted;

        row.ServiceCatalogueId = scanned.ServiceCatalogueId;
        row.ServiceCatalogueName = scanned.ServiceCatalogueName;
        row.CorrectServiceCatalogueId = scanned.CorrectServiceCatalogueId;
        row.CorrectServiceCatalogueName = scanned.CorrectServiceCatalogueName;

        row.OldCategory = scanned.OldCategory;
        row.OldHash = scanned.OldHash;
        row.OldFileName = scanned.OldFileName;
        row.OldFileId = scanned.OldFileId;

        row.Group = scanned.Group;
        row.ReasonOfBug = scanned.ReasonOfBug;
        row.Solution = scanned.Solution;
        row.WayOfUpload = scanned.WayOfUpload;

        row.CrmLinkOfDoc = scanned.CrmLinkOfDoc;
        row.CrmLinkOfDocFile = scanned.CrmLinkOfDocFile;

        // The verdict is not refreshed. It is written once, when the row is first created, and
        // belongs to the operator from then on.
        //
        // It used to be overwritten by the scan, on the reasoning that CRM holds the current
        // truth. That made the column unusable: an operator who moved a "review" or "skip" row
        // to "fix" — the whole point of reading the reason and deciding — watched the next run
        // put it back. A column the tool silently overrules is not a column anybody can edit.
        //
        // Where the scan now disagrees, the row says so: reason of bug, solution and group are
        // refreshed above, and the disagreement is reported to the operator rather than acted
        // on behind them.
    }
}
