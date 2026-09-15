using ClosedXML.Excel;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// The ledger itself: the file the operator edits and every mode reads.
///
/// It is a workbook rather than a CSV because three of the things asked for cannot live in
/// comma-separated text at all — a dropdown on the two edited columns, widths fitted to the
/// contents, and a frozen header. A dropdown on a file nothing reads back would be decoration,
/// so the file carrying it has to be the authority.
///
/// The CSV written beside it is a copy: plain text for grepping and diffing, and something
/// readable if the workbook is ever damaged. Nothing reads it.
/// </summary>
public sealed class LedgerWorkbook
{
    /// <summary>Anything longer than this is still stored whole — only the column is capped.</summary>
    private const double WidestColumn = 60;

    private const string SheetName = "ledger";

    public LedgerWorkbook(string path) => Path = path;

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    /// <summary>
    /// The rows as the operator left them. Columns are found by header, not by position, so a
    /// workbook whose columns have been dragged around still reads correctly — and one missing
    /// a column reads the rest rather than failing.
    /// </summary>
    public IReadOnlyList<LedgerRow> Read()
    {
        if (!Exists) return Array.Empty<LedgerRow>();

        using var workbook = new XLWorkbook(Path);
        var sheet = workbook.Worksheets.FirstOrDefault();
        var used = sheet?.RangeUsed();
        if (sheet is null || used is null || used.RowCount() < 2) return Array.Empty<LedgerRow>();

        var byHeader = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 1; c <= used.ColumnCount(); c++)
        {
            var header = sheet.Cell(1, c).GetString().Trim();
            if (header.Length > 0) byHeader[header] = c;
        }

        var rows = new List<LedgerRow>(used.RowCount() - 1);

        for (var r = 2; r <= used.RowCount(); r++)
        {
            var row = new LedgerRow();
            var anything = false;

            foreach (var column in LedgerColumns.All)
            {
                if (!byHeader.TryGetValue(column.Header, out var c)) continue;

                var text = sheet.Cell(r, c).GetString().Trim();
                column.Set(row, text);
                if (text.Length > 0) anything = true;
            }

            // A blank line left behind by Excel is not a document.
            if (anything && row.DocId != Guid.Empty) rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Whether the workbook could be written right now. Excel holds an exclusive lock while it
    /// is open, and finding that out before a run starts is far kinder than finding out after
    /// the first document has already been uploaded.
    /// </summary>
    public bool CanWrite()
    {
        if (!Exists) return true;

        try
        {
            using var _ = File.Open(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Write(IReadOnlyList<LedgerRow> rows)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName);

        var columns = LedgerColumns.All;

        for (var c = 0; c < columns.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = columns[c].Header;
            for (var r = 0; r < rows.Count; r++)
                sheet.Cell(r + 2, c + 1).Value = columns[c].Read(rows[r]);
        }

        var header = sheet.Row(1);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromArgb(0xEE, 0xEE, 0xEE);

        // Frozen and filtered, so four hundred rows stay navigable.
        sheet.SheetView.FreezeRows(1);
        sheet.RangeUsed()?.SetAutoFilter();

        Validate(sheet, ColumnOf(columns, "verdict"), rows.Count, RowVerdicts.All);
        Validate(sheet, ColumnOf(columns, "final state"), rows.Count, RowStates.All);

        // Fitted to the contents, then capped — a file path is 120 characters and one such
        // column pushes every other off the screen.
        sheet.Columns().AdjustToContents();
        foreach (var column in sheet.ColumnsUsed())
            if (column.Width > WidestColumn)
                column.Width = WidestColumn;

        sheet.Cell(1, 1).CreateComment().AddText(
            "This is the ledger. Edit verdict and final state here, save, and close it before " +
            "running docfix — the tool rewrites this file after every document. The .csv " +
            "beside it is a copy the tool maintains; editing that one changes nothing.");

        workbook.SaveAs(Path);
    }

    /// <summary>A dropdown of exactly the values the tool understands, on one whole column.</summary>
    private static void Validate(IXLWorksheet sheet, int column, int rows, IReadOnlyList<string> allowed)
    {
        if (column < 1 || rows == 0) return;

        var validation = sheet.Range(sheet.Cell(2, column), sheet.Cell(rows + 1, column))
            .CreateDataValidation();

        validation.List($"\"{string.Join(',', allowed)}\"", inCellDropdown: true);

        // A warning, not a refusal. The tool treats an unknown value as "leave this row alone"
        // and reports it, so blocking the keystroke would be stricter than the rule behind it.
        validation.ErrorStyle = XLErrorStyle.Warning;
        validation.ErrorTitle = "Not a value this tool understands";
        validation.ErrorMessage =
            "The run will leave this row alone and list it as unrecognised at the end.";
    }

    private static int ColumnOf(IReadOnlyList<LedgerColumn> columns, string header)
    {
        for (var i = 0; i < columns.Count; i++)
            if (columns[i].Header == header) return i + 1;

        return 0;
    }
}
