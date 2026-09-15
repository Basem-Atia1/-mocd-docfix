using CsvHelper.Configuration.Attributes;

namespace MocdDocFix.Domain;

/// <summary>
/// One document, as one line of the ledger. A mutable class rather than a record: the whole
/// file is rewritten after every completed row, and the steps set cells on the instance they
/// were handed.
///
/// The header names are the operator's, not the code's — they are read in Excel. Changing one
/// silently orphans the column in every ledger already on disk, so do not rename without
/// migrating.
///
/// The order is pinned with <see cref="IndexAttribute"/> rather than left to member order,
/// because member order is an accident of editing and this order was asked for: everything read
/// or edited in the first fifteen, every GUID after them.
/// </summary>
public sealed class LedgerRow
{
    // ---- what it is ----

    [Index(0), Name("row")] public int Row { get; set; }
    [Index(1), Name("doc name")] public string DocName { get; set; } = string.Empty;
    [Index(2), Name("doc type name")] public string DocTypeName { get; set; } = string.Empty;
    [Index(3), Name("doc file name")] public string DocFileName { get; set; } = string.Empty;

    // ---- where the file is, and where it is going ----

    [Index(4), Name("old file path")] public string OldFilePath { get; set; } = string.Empty;
    [Index(5), Name("new file path predicted")] public string NewFilePathPredicted { get; set; } = string.Empty;
    [Index(6), Name("new file path")] public string NewFilePath { get; set; } = string.Empty;
    [Index(7), Name("service catalogue name")] public string ServiceCatalogueName { get; set; } = string.Empty;

    /// <summary>
    /// mocd_category as the old record holds it — very often the junk that is the bug. It sits
    /// between the two service names on purpose, so the row reads as one sentence: filed under
    /// this, the record's category says that, and it should be the third.
    ///
    /// It is also one of the four values a revert writes back, so a blank cell means the
    /// original record held nothing there — not that it was never recorded.
    /// </summary>
    [Index(8), Name("old category")] public string OldCategory { get; set; } = string.Empty;

    [Index(9), Name("correct service catalogue name")] public string CorrectServiceCatalogueName { get; set; } = string.Empty;

    // ---- the two the operator edits, and what explains them ----

    [Index(10), Name("verdict")] public string Verdict { get; set; } = string.Empty;
    [Index(11), Name("final state")] public string FinalState { get; set; } = string.Empty;
    [Index(12), Name("group")] public int Group { get; set; }

    /// <summary>portal or plugin — which code path created the record, from FileRecordCopier.StyleOf.</summary>
    [Index(13), Name("way of upload")] public string WayOfUpload { get; set; } = string.Empty;

    [Index(14), Name("error")] public string Error { get; set; } = string.Empty;

    /// <summary>Filled once the row has been backed up, so the bytes are one click away.</summary>
    [Index(15), Name("backup path")] public string BackupPath { get; set; } = string.Empty;

    [Index(16), Name("reason of bug")] public string ReasonOfBug { get; set; } = string.Empty;
    [Index(17), Name("solution")] public string Solution { get; set; } = string.Empty;
    [Index(18), Name("notes")] public string Notes { get; set; } = string.Empty;

    // ---- links ----

    [Index(19), Name("crm link of doc")] public string CrmLinkOfDoc { get; set; } = string.Empty;
    [Index(20), Name("crm link of doc file")] public string CrmLinkOfDocFile { get; set; } = string.Empty;

    // ---- ids and machine detail, kept to the right ----

    [Index(21), Name("doc id")] public Guid DocId { get; set; }
    [Index(22), Name("doc file id")] public Guid DocFileId { get; set; }
    [Index(23), Name("service catalogue id")] public string ServiceCatalogueId { get; set; } = string.Empty;
    [Index(24), Name("correct service catalogue id")] public string CorrectServiceCatalogueId { get; set; } = string.Empty;

    // The rest of what a correction overwrites, as it was. Blank means the record held nothing.
    [Index(25), Name("old hash")] public string OldHash { get; set; } = string.Empty;
    [Index(26), Name("old file name")] public string OldFileName { get; set; } = string.Empty;
    [Index(27), Name("old file id")] public string OldFileId { get; set; } = string.Empty;

    /// <summary>Corrected copies a revert has abandoned, semicolon-separated. Nothing deletes these.</summary>
    [Index(28), Name("superseded paths")] public string SupersededPaths { get; set; } = string.Empty;

    /// <summary>
    /// The verdict cell, parsed. A method rather than a property because the column itself is
    /// called Verdict, and CsvHelper maps by property — which is also why neither needs [Ignore].
    /// </summary>
    public RowVerdict Verdict2() => RowVerdicts.Parse(Verdict);

    public RowState State() => RowStates.Parse(FinalState);
}
