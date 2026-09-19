using ClosedXML.Excel;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// The ledger itself: the file the operator edits and every mode reads.
///
/// It is a workbook rather than plain text because three of the things asked for cannot live in
/// a comma-separated file at all — a dropdown on the two edited columns, set column widths, and
/// a frozen header. A dropdown on a file nothing reads back would be decoration, so the file
/// carrying it has to be the authority.
///
/// It is also the only file written. A plain-text copy used to be kept beside it; nothing ever
/// read it, and one falling behind looked current while showing fewer corrections than had
/// really happened.
/// </summary>
public sealed class LedgerWorkbook
{
    /// <summary>
    /// How wide each column is, by header.
    ///
    /// Fixed rather than measured. AdjustToContents walks every cell of every column to fit the
    /// widths, and on a 29-column sheet that was 97% of the cost of a write — 15.7 seconds at
    /// 2,000 rows against 0.57 without it — paid after every single corrected document. Nobody
    /// was ever going to notice a column two characters wider than its widest value.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> Widths =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = 6,
            ["group"] = 7,
            ["verdict"] = 12,
            ["final state"] = 38,
            ["way of upload"] = 14,
            ["doc name"] = 30,
            ["doc type name"] = 30,
            ["doc file name"] = 26,
            ["service catalogue name"] = 30,
            ["correct service catalogue name"] = 30,
            ["old category"] = 22,
        };

    /// <summary>Anything not named above: every path, link, reason and note.</summary>
    private const double DefaultColumn = 40;

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

        // A .tmp left behind by a killed process is rubbish: a partial file with no reader.
        // Cleared here rather than at start-up, so the rule sits beside the code that makes it.
        var leftOver = Path + ".tmp";
        if (File.Exists(leftOver)) File.Delete(leftOver);

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

        SetWidths(sheet);

        sheet.Cell(1, 1).CreateComment().AddText(
            "This is the ledger. Edit verdict and final state here, save, and close it before " +
            "running docfix — the tool rewrites this file after every document.");

        // Built in memory, written beside the ledger, and moved over it — rather than saved
        // straight onto it. A process killed mid-save (the console window closed, the machine
        // shut down) otherwise leaves a half-written workbook, and half a workbook opens as
        // nothing at all. A move within one folder is atomic on NTFS, so what is on disk is
        // always one whole version or the other.
        //
        // Through a stream because ClosedXML picks its format from the file extension and
        // refuses to save to anything but .xlsx — and a temporary file named .xlsx is a file
        // somebody opens by mistake, which is the very thing the previous\ folder exists to stop.
        using var built = new MemoryStream();
        workbook.SaveAs(built);

        var temporary = Path + ".tmp";

        File.WriteAllBytes(temporary, built.ToArray());
        File.Move(temporary, Path, overwrite: true);
    }

    /// <summary>The fixed width for each column, by header, with a default for the rest.</summary>
    private static void SetWidths(IXLWorksheet sheet)
    {
        var columns = LedgerColumns.All;

        for (var c = 0; c < columns.Count; c++)
            sheet.Column(c + 1).Width =
                Widths.TryGetValue(columns[c].Header, out var width) ? width : DefaultColumn;
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
