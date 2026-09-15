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
/// because member order is an accident of editing and this order was asked for: what identifies
/// a document and what is read or edited in the first seventeen, the remaining machine detail
/// after them.
/// </summary>
public sealed class LedgerRow
{
    // ---- what it is ----

    [Index(0), Name("row")] public int Row { get; set; }

    /// <summary>
    /// The identity. The row number is positional and changes whenever a verdict does, so this
    /// is the only thing that names a document across runs — which is why it sits second rather
    /// than out among the other GUIDs.
    /// </summary>
    [Index(1), Name("doc id")] public Guid DocId { get; set; }

    [Index(2), Name("doc name")] public string DocName { get; set; } = string.Empty;
    [Index(3), Name("doc type name")] public string DocTypeName { get; set; } = string.Empty;
    [Index(4), Name("doc file name")] public string DocFileName { get; set; } = string.Empty;

    // ---- where the file is, and where it is going ----

    [Index(5), Name("old file path")] public string OldFilePath { get; set; } = string.Empty;
    [Index(6), Name("new file path predicted")] public string NewFilePathPredicted { get; set; } = string.Empty;
    [Index(7), Name("new file path")] public string NewFilePath { get; set; } = string.Empty;
    [Index(8), Name("service catalogue name")] public string ServiceCatalogueName { get; set; } = string.Empty;

    /// <summary>
    /// mocd_category as the old record holds it — very often the junk that is the bug. It sits
    /// between the two service names on purpose, so the row reads as one sentence: filed under
    /// this, the record's category says that, and it should be the third.
    ///
    /// It is also one of the four values a revert writes back, so a blank cell means the
    /// original record held nothing there — not that it was never recorded.
    /// </summary>
    [Index(9), Name("old category")] public string OldCategory { get; set; } = string.Empty;

    [Index(10), Name("correct service catalogue name")] public string CorrectServiceCatalogueName { get; set; } = string.Empty;

    // ---- the two the operator edits, and what explains them ----

    [Index(11), Name("verdict")] public string Verdict { get; set; } = string.Empty;
    [Index(12), Name("final state")] public string FinalState { get; set; } = string.Empty;
    [Index(13), Name("group")] public int Group { get; set; }

    /// <summary>portal or plugin — which code path created the record, from FileRecordCopier.StyleOf.</summary>
    [Index(14), Name("way of upload")] public string WayOfUpload { get; set; } = string.Empty;

    [Index(15), Name("error")] public string Error { get; set; } = string.Empty;

    /// <summary>Filled once the row has been backed up, so the bytes are one click away.</summary>
    [Index(16), Name("backup path")] public string BackupPath { get; set; } = string.Empty;

    [Index(17), Name("reason of bug")] public string ReasonOfBug { get; set; } = string.Empty;
    [Index(18), Name("solution")] public string Solution { get; set; } = string.Empty;
    [Index(19), Name("notes")] public string Notes { get; set; } = string.Empty;

    // ---- links ----

    [Index(20), Name("crm link of doc")] public string CrmLinkOfDoc { get; set; } = string.Empty;
    [Index(21), Name("crm link of doc file")] public string CrmLinkOfDocFile { get; set; } = string.Empty;

    // ---- the remaining machine detail, kept to the right ----

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

    /// <summary>
    /// How this row is named in a message.
    ///
    /// The row number alone is not an identity. It is positional — the sheet is sorted by
    /// verdict and renumbered on every write — so a document that was row 1 this morning can be
    /// row 408 this afternoon, and "row 99" in yesterday's message points at somebody else
    /// today. The head of the document id does not move, so it travels with the number.
    /// </summary>
    public string Ref() =>
        $"row {Row} · doc {DocId.ToString()[..8]}" +
        (DocFileName.Length == 0 ? string.Empty : $" ({DocFileName})");
}
