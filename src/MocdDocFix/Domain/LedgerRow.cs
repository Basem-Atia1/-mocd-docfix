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
/// </summary>
public sealed class LedgerRow
{
    [Name("row")] public int Row { get; set; }
    [Name("doc id")] public Guid DocId { get; set; }
    [Name("doc name")] public string DocName { get; set; } = string.Empty;

    /// <summary>Filled once the row has been backed up, so the bytes are one click away.</summary>
    [Name("backup path")] public string BackupPath { get; set; } = string.Empty;

    [Name("doc file id")] public Guid DocFileId { get; set; }
    [Name("doc file name")] public string DocFileName { get; set; } = string.Empty;
    [Name("doc type name")] public string DocTypeName { get; set; } = string.Empty;
    [Name("service catalogue id")] public string ServiceCatalogueId { get; set; } = string.Empty;
    [Name("service catalogue name")] public string ServiceCatalogueName { get; set; } = string.Empty;
    [Name("correct service catalogue id")] public string CorrectServiceCatalogueId { get; set; } = string.Empty;
    [Name("correct service catalogue name")] public string CorrectServiceCatalogueName { get; set; } = string.Empty;
    [Name("old file path")] public string OldFilePath { get; set; } = string.Empty;
    [Name("new file path predicted")] public string NewFilePathPredicted { get; set; } = string.Empty;
    [Name("new file path")] public string NewFilePath { get; set; } = string.Empty;

    /// <summary>Corrected copies a revert has abandoned, semicolon-separated. Nothing deletes these.</summary>
    [Name("superseded paths")] public string SupersededPaths { get; set; } = string.Empty;

    // The four fields a correction overwrites, as they were. A blank cell means the original
    // record held nothing there — it does not mean "not recorded".
    [Name("old category")] public string OldCategory { get; set; } = string.Empty;
    [Name("old hash")] public string OldHash { get; set; } = string.Empty;
    [Name("old file name")] public string OldFileName { get; set; } = string.Empty;
    [Name("old file id")] public string OldFileId { get; set; } = string.Empty;

    [Name("verdict")] public string Verdict { get; set; } = string.Empty;
    [Name("group")] public int Group { get; set; }
    [Name("reason of bug")] public string ReasonOfBug { get; set; } = string.Empty;
    [Name("solution")] public string Solution { get; set; } = string.Empty;
    [Name("final state")] public string FinalState { get; set; } = string.Empty;
    [Name("error")] public string Error { get; set; } = string.Empty;
    [Name("notes")] public string Notes { get; set; } = string.Empty;
    [Name("crm link of doc")] public string CrmLinkOfDoc { get; set; } = string.Empty;
    [Name("crm link of doc file")] public string CrmLinkOfDocFile { get; set; } = string.Empty;

    /// <summary>portal or plugin — which code path created the record, from FileRecordCopier.StyleOf.</summary>
    [Name("way of upload")] public string WayOfUpload { get; set; } = string.Empty;

    /// <summary>
    /// The verdict cell, parsed. A method rather than a property because the column itself is
    /// called Verdict, and CsvHelper maps by property — which is also why neither needs [Ignore].
    /// </summary>
    public RowVerdict Verdict2() => RowVerdicts.Parse(Verdict);

    public RowState State() => RowStates.Parse(FinalState);
}
