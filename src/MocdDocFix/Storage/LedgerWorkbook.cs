using ClosedXML.Excel;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// The ledger as a workbook, written beside the CSV for reading and filtering.
///
/// The CSV is the file the tool reads and writes; this is regenerated from it and is **not**
/// read back. Anything typed into the workbook is lost the next time a row finishes, and the
/// sheet says so in its first cell comment — the alternative, reading both, is two sources of
/// truth for one ledger.
///
/// It exists because three of the things asked for cannot live in a CSV at all: a dropdown on
/// the two edited columns, column widths fitted to the contents, and a frozen header.
/// </summary>
public sealed class LedgerWorkbook
{
    /// <summary>Anything longer than this is still stored whole — only the column is capped.</summary>
    private const double WidestColumn = 60;

    private const string SheetName = "ledger";

    public LedgerWorkbook(string path) => Path = path;

    public string Path { get; }

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
            "Regenerated from the CSV every time a row finishes. Edits made here are lost — " +
            "edit the .csv beside it.");

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
