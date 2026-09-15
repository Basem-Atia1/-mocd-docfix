using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <param name="Added">Documents CRM has that the ledger did not.</param>
/// <param name="Refreshed">Rows whose facts were brought up to date from CRM.</param>
/// <param name="Protected">Rows already acted on, whose history was left alone.</param>
/// <param name="Vanished">Rows whose document CRM no longer returns.</param>
public sealed record Merged(
    IReadOnlyList<LedgerRow> Rows, int Added, int Refreshed, int Protected, int Vanished,
    IReadOnlyList<string> Notes);

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

            Refresh(row, scanned);
            refreshed++;
        }

        foreach (var row in existing.Where(r => !seen.Contains(r.DocId)))
        {
            notes.Add($"row {row.Row} ({row.DocFileName}): CRM no longer returns this document — " +
                      "kept in the ledger, but nothing will act on it");
        }

        return new Merged(rows, added, refreshed, protectedRows,
            existing.Count - seen.Count(id => byDocument.ContainsKey(id)), notes);
    }

    /// <summary>
    /// True once a run has changed something for this row, or the operator has excluded it.
    /// A failed row has not been acted on successfully, so it is still refreshed.
    /// </summary>
    private static bool HasBeenActedOn(LedgerRow row) =>
        row.State() is RowState.Corrected or RowState.Deleted or RowState.Ignore ||
        row.Verdict2() is RowVerdict.Ignore or RowVerdict.Redo;

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

        // The scan's verdict is CRM's current answer, so it wins — except where the operator
        // has used one of the two words only they write. Those are instructions, not findings.
        if (row.Verdict2() is not (RowVerdict.Ignore or RowVerdict.Redo))
            row.Verdict = scanned.Verdict;
    }
}
