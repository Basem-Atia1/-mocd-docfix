using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <param name="Added">Documents CRM has that the ledger did not.</param>
/// <param name="Refreshed">Rows whose facts were brought up to date from CRM.</param>
/// <param name="Protected">Rows already acted on, whose history was left alone.</param>
/// <param name="Vanished">Rows whose document CRM no longer returns.</param>
/// <param name="Row">The row as the ledger holds it.</param>
/// <param name="ScanSays">What a fresh look at CRM would put in the verdict column.</param>
public sealed record VerdictDisagreement(LedgerRow Row, string ScanSays)
{
    /// <summary>
    /// Why the scan says that, in its own words.
    ///
    /// Carried because "CRM says skip" on its own is unanswerable. Skip covers three different
    /// findings — the path is already correct, the record has no file path at all, the document
    /// type has no catalogue to write — and they want three different answers from the operator.
    /// Being asked to choose between two bare words is what makes a correct answer look wrong.
    /// </summary>
    public string ScanReason { get; init; } = string.Empty;
}

/// <summary>
/// A row whose file CRM now holds somewhere else, although no run in this ledger moved it.
/// </summary>
/// <param name="NowAt">The path CRM holds for it now.</param>
/// <param name="CorrectedBy">
/// The ledger row that corrected the same mocd_documentfile record, when one did — the usual
/// explanation. Null when nothing in this ledger accounts for the move, which means a person
/// or another tool changed it.
/// </param>
public sealed record PathMoved(LedgerRow Row, string NowAt, int? CorrectedBy);

/// <param name="Excluded">
/// Rows the operator has marked ignore and that no run has touched. Reported so a whole column
/// set to "ignore" by one careless fill in Excel can be undone from inside the tool; they are
/// still protected from the refresh like any other excluded row.
/// </param>
/// <param name="Disagreements">
/// Rows whose verdict differs from what the scan would write. Not acted on: the operator is
/// asked whether to keep their own answers or take CRM's, because either can be the right one
/// and the tool cannot tell which.
/// </param>
/// <param name="Moved">
/// Rows whose file has moved without this ledger moving it. Reported rather than absorbed,
/// because the row is now describing a file somebody else has already corrected.
/// </param>
public sealed record Merged(
    IReadOnlyList<LedgerRow> Rows, int Added, int Refreshed, int Protected, int Vanished,
    IReadOnlyList<string> Notes, IReadOnlyList<VerdictDisagreement> Disagreements,
    IReadOnlyList<VerdictDisagreement> Excluded, IReadOnlyList<PathMoved> Moved);

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
///
/// One row is not one file. A single mocd_documentfile can be the file of several mocd_document
/// records, so correcting one row moves the file under every row that shares it — rows this
/// merge has no record of acting on, and would therefore refresh into agreement with CRM,
/// losing the old path in the process. They are held back too, and reported.
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
        var excluded = new List<VerdictDisagreement>();
        var moved = new List<PathMoved>();
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

            // A row excluded by hand and never worked on. Kept out of the refresh like any other
            // protected row, but recorded — a whole column set to "ignore" by one careless fill
            // in Excel is otherwise impossible to undo from inside the tool.
            if (row.Verdict2() == RowVerdict.Ignore && row.State() == RowState.NotStarted)
                excluded.Add(Disagreement(row, scanned));

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

            // CRM holds a different path from the one this row recorded, and nothing here put it
            // there. Its old path is the only record of where the file was before somebody moved
            // it, so it survives the refresh; overwriting it would leave the row claiming the
            // file has always been where it now is, and nothing left to delete.
            var elsewhere = row.OldFilePath.Length > 0 &&
                            !FilePaths.Same(row.OldFilePath, scanned.OldFilePath);

            if (elsewhere)
            {
                var by = CorrectorOf(row, existing);
                moved.Add(new PathMoved(row, scanned.OldFilePath, by));

                row.Notes = Add(row.Notes, by is null
                    ? $"the file moved to {scanned.OldFilePath} without this row being worked " +
                      "on — nothing in this ledger did it"
                    : $"the file moved to {scanned.OldFilePath} when row {by} corrected the " +
                      "same document file record; the old path here is kept as it was");
            }

            if (!string.Equals(row.Verdict.Trim(), scanned.Verdict,
                    StringComparison.OrdinalIgnoreCase))
                disagreements.Add(Disagreement(row, scanned));

            Refresh(row, scanned, keepHistory: elsewhere);
            refreshed++;
        }

        foreach (var row in existing.Where(r => !seen.Contains(r.DocId)))
        {
            notes.Add($"row {row.Row} ({row.DocFileName}): CRM no longer returns this document — " +
                      "kept in the ledger, but nothing will act on it");
        }

        return new Merged(rows, added, refreshed, protectedRows,
            existing.Count - seen.Count(id => byDocument.ContainsKey(id)), notes, disagreements,
            excluded, moved);
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
    /// Sends the rows back to the operator to decide one at a time — what they get by answering
    /// "let me type them myself". Review is the verdict that means exactly that: a person must
    /// look. It sorts near the top of the sheet, and no run acts on the row until it changes.
    /// </summary>
    public static void MarkForReview(IReadOnlyList<VerdictDisagreement> rows)
    {
        foreach (var (row, _) in rows) row.Verdict = RowVerdicts.Review;
    }

    private static VerdictDisagreement Disagreement(LedgerRow row, LedgerRow scanned) =>
        new(row, scanned.Verdict) { ScanReason = scanned.ReasonOfBug };

    /// <summary>
    /// The row that corrected this row's file, if one did. Rows are matched on the document file
    /// record rather than on the document, because that record is what a correction writes to
    /// and what several documents can share.
    /// </summary>
    private static int? CorrectorOf(LedgerRow row, IReadOnlyList<LedgerRow> existing)
    {
        if (row.DocFileId == Guid.Empty) return null;

        var sibling = existing.FirstOrDefault(other =>
            other.DocId != row.DocId &&
            other.DocFileId == row.DocFileId &&
            other.State() is RowState.Corrected or RowState.Deleted);

        return sibling?.Row;
    }

    /// <summary>
    /// True once a run has changed something for this row, or the operator has excluded it.
    /// A failed row has not been acted on successfully, so it is still refreshed.
    /// </summary>
    private static bool HasBeenActedOn(LedgerRow row) =>
        row.State() is RowState.Corrected or RowState.Deleted or RowState.Ignore ||
        row.Verdict2() is RowVerdict.Ignore or RowVerdict.Redo or RowVerdict.Done;

    /// <summary>
    /// Everything CRM is the authority on. The columns the operator owns — verdict, final state,
    /// notes — and everything a run records are deliberately absent.
    /// </summary>
    /// <param name="keepHistory">
    /// Holds back the columns that say what the file was: its path, category, hash, name and id.
    /// Set when the file has moved under this row without this ledger moving it, so the record
    /// of where it was is not replaced by where somebody else has just put it.
    /// </param>
    private static void Refresh(LedgerRow row, LedgerRow scanned, bool keepHistory)
    {
        row.DocName = scanned.DocName;
        row.DocTypeName = scanned.DocTypeName;
        row.DocFileName = scanned.DocFileName;
        row.DocFileId = scanned.DocFileId;

        row.NewFilePathPredicted = scanned.NewFilePathPredicted;

        row.ServiceCatalogueId = scanned.ServiceCatalogueId;
        row.ServiceCatalogueName = scanned.ServiceCatalogueName;
        row.CorrectServiceCatalogueId = scanned.CorrectServiceCatalogueId;
        row.CorrectServiceCatalogueName = scanned.CorrectServiceCatalogueName;

        if (!keepHistory)
        {
            row.OldFilePath = scanned.OldFilePath;
            row.OldCategory = scanned.OldCategory;
            row.OldHash = scanned.OldHash;
            row.OldFileName = scanned.OldFileName;
            row.OldFileId = scanned.OldFileId;
        }

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

    private static string Add(string notes, string line) =>
        notes.Length == 0 ? line : $"{notes}; {line}";
}
