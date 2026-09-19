namespace MocdDocFix.Domain;

/// <summary>
/// A ledger column: where it sits, and what the operator reads at the top of it.
///
/// This was CsvHelper's [Index] and [Name], kept long after the CSV they were written for. The
/// workbook is the only ledger now, so the ordering belongs here rather than to a serialisation
/// library nothing else referenced.
///
/// The header is the operator's name for the column, read in Excel. Renaming one silently
/// orphans that column in every ledger already on disk, so do not rename without migrating.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnAttribute : Attribute
{
    public ColumnAttribute(int index, string header)
    {
        Index = index;
        Header = header;
    }

    /// <summary>Position in the sheet, left to right, from zero.</summary>
    public int Index { get; }

    /// <summary>The text in row 1.</summary>
    public string Header { get; }
}
