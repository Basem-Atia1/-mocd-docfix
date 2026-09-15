using ClosedXML.Excel;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The workbook is the ledger: it is what the operator edits, with dropdowns on the two columns
/// they set, and it is what every mode reads. The CSV beside it is a copy.
/// </summary>
public class LedgerWorkbookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-xlsx-" + Guid.NewGuid());

    public LedgerWorkbookTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string LedgerPath => Path.Combine(_dir, "repair-dev.xlsx");
    private LedgerStore Store() => new(LedgerPath);

    private static LedgerRow Row(string verdict, string name, string? longPath = null) => new()
    {
        DocId = Guid.NewGuid(),
        DocName = name,
        DocFileName = "cert.jpg",
        Verdict = verdict,
        OldFilePath = longPath ?? @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg"
    };

    [Fact]
    public void The_ledger_is_the_workbook_and_the_csv_is_written_beside_it()
    {
        var store = Store();

        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        Assert.EndsWith(".xlsx", store.Path);
        Assert.EndsWith(".csv", store.CsvPath);
        Assert.True(File.Exists(store.Path));
        Assert.True(File.Exists(store.CsvPath));
    }

    /// <summary>
    /// The whole point of the dropdown: what the operator picks in Excel is what the next run
    /// acts on. If this fails, the option-set is decoration.
    /// </summary>
    [Fact]
    public void A_verdict_chosen_in_the_workbook_is_what_the_tool_reads_back()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        // Exactly what Excel does when the operator picks "ignore" from the list and saves.
        using (var workbook = new XLWorkbook(store.Path))
        {
            workbook.Worksheet(1).Cell(2, 10).Value = RowVerdicts.Ignore;
            workbook.Save();
        }

        var back = Assert.Single(store.Read());
        Assert.Equal(RowVerdict.Ignore, back.Verdict2());
    }

    [Fact]
    public void A_final_state_chosen_in_the_workbook_is_read_back_too()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using (var workbook = new XLWorkbook(store.Path))
        {
            workbook.Worksheet(1).Cell(2, 11).Value = RowStates.Ignore;
            workbook.Save();
        }

        Assert.Equal(RowState.Ignore, Assert.Single(store.Read()).State());
    }

    [Fact]
    public void Every_column_round_trips_through_the_workbook()
    {
        var store = Store();
        var row = Row(RowVerdicts.Fix, "Board Decision");
        row.OldHash = "9f86d081";
        row.Group = 2;
        row.WayOfUpload = "plugin";
        row.ReasonOfBug = "Path has 'docTypeCatalogue'";
        store.Write(new[] { row });

        var back = Assert.Single(store.Read());

        Assert.Equal(row.DocId, back.DocId);
        Assert.Equal("Board Decision", back.DocName);
        Assert.Equal(row.OldFilePath, back.OldFilePath);
        Assert.Equal("9f86d081", back.OldHash);
        Assert.Equal(2, back.Group);
        Assert.Equal("plugin", back.WayOfUpload);
        Assert.Equal("Path has 'docTypeCatalogue'", back.ReasonOfBug);
    }

    /// <summary>Columns are found by header, so dragging one in Excel does not break the read.</summary>
    [Fact]
    public void A_column_the_operator_has_moved_is_still_found_by_its_header()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using (var workbook = new XLWorkbook(store.Path))
        {
            // Everything shifts one to the right, so nothing is where the writer put it.
            workbook.Worksheet(1).Column(1).InsertColumnsBefore(1);
            workbook.Save();
        }

        var back = Assert.Single(store.Read());
        Assert.Equal(RowVerdict.Fix, back.Verdict2());
        Assert.Equal("Board Decision", back.DocName);
    }

    [Fact]
    public void A_blank_line_left_behind_in_excel_is_not_read_as_a_document()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using (var workbook = new XLWorkbook(store.Path))
        {
            workbook.Worksheet(1).Cell(3, 2).Value = "   ";
            workbook.Save();
        }

        Assert.Single(store.Read());
    }

    [Fact]
    public void The_headers_are_in_the_order_that_was_asked_for()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.Path);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("row", sheet.Cell(1, 1).GetString());
        Assert.Equal("old file path", sheet.Cell(1, 5).GetString());
        Assert.Equal("verdict", sheet.Cell(1, 10).GetString());
        Assert.Equal("final state", sheet.Cell(1, 11).GetString());
        Assert.Equal("backup path", sheet.Cell(1, 15).GetString());
    }

    /// <summary>The thing a CSV cannot do: a dropdown of exactly the values the tool understands.</summary>
    [Fact]
    public void The_verdict_column_carries_a_dropdown_of_the_five_values()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.Path);
        var validation = workbook.Worksheet(1).Cell(2, 10).GetDataValidation();

        Assert.NotNull(validation);
        foreach (var value in RowVerdicts.All)
            Assert.Contains(value, validation!.Value);
    }

    [Fact]
    public void The_final_state_column_carries_its_own_dropdown()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.Path);
        var validation = workbook.Worksheet(1).Cell(2, 11).GetDataValidation();

        Assert.NotNull(validation);
        Assert.Contains(RowStates.Corrected, validation!.Value);
        Assert.Contains(RowStates.Deleted, validation.Value);
    }

    /// <summary>
    /// A wrong value is warned about, not refused. The tool treats an unknown verdict as "leave
    /// this row alone" and reports it, so blocking the keystroke would be stricter than the rule.
    /// </summary>
    [Fact]
    public void A_value_outside_the_list_is_warned_about_rather_than_refused()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.Path);

        Assert.Equal(XLErrorStyle.Warning,
            workbook.Worksheet(1).Cell(2, 10).GetDataValidation()!.ErrorStyle);
    }

    [Fact]
    public void The_header_is_frozen_so_four_hundred_rows_stay_readable()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.Path);

        Assert.Equal(1, workbook.Worksheet(1).SheetView.SplitRow);
    }

    /// <summary>
    /// Fitted to the contents, but capped: one 300-character path must not push every other
    /// column off the screen.
    /// </summary>
    [Fact]
    public void A_very_long_value_does_not_make_its_column_unusably_wide()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision", new string('x', 300)) });

        using var workbook = new XLWorkbook(store.Path);

        Assert.True(workbook.Worksheet(1).Column(5).Width <= 60);
    }

    [Fact]
    public void The_rows_are_sorted_by_verdict()
    {
        var store = Store();

        store.Write(new[]
        {
            Row(RowVerdicts.Ignore, "excluded"),
            Row(RowVerdicts.Fix, "work"),
            Row(RowVerdicts.Review, "decide")
        });

        Assert.Equal(new[] { "work", "decide", "excluded" }, store.Read().Select(r => r.DocName));
    }

    /// <summary>
    /// Excel holds an exclusive lock on an open workbook. Knowing that before a run starts is
    /// what lets the tool say "close it" instead of failing after the first upload.
    /// </summary>
    [Fact]
    public void A_workbook_open_in_excel_is_reported_as_unwritable()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        Assert.True(store.CanWrite());

        using (File.Open(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(store.CanWrite());
        }

        Assert.True(store.CanWrite());
    }

    [Fact]
    public void A_ledger_that_is_not_there_yet_reads_as_nothing_and_can_be_written()
    {
        var store = Store();

        Assert.False(store.Exists);
        Assert.Empty(store.Read());
        Assert.True(store.CanWrite());
    }

    [Fact]
    public void Starting_a_new_ledger_keeps_both_old_files()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        var kept = store.StartNewKeepingOld();

        Assert.True(File.Exists(kept));
        Assert.False(store.Exists);
        Assert.False(File.Exists(store.CsvPath));
        Assert.Single(new LedgerStore(kept).Read());
    }
}
