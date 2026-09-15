using ClosedXML.Excel;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class LedgerWorkbookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-xlsx-" + Guid.NewGuid());

    public LedgerWorkbookTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string CsvPath => Path.Combine(_dir, "repair-dev.csv");

    private static LedgerRow Row(string verdict, string name, string? longPath = null) => new()
    {
        DocId = Guid.NewGuid(),
        DocName = name,
        DocFileName = "cert.jpg",
        Verdict = verdict,
        OldFilePath = longPath ?? @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg"
    };

    [Fact]
    public void Writing_the_ledger_writes_the_workbook_beside_it()
    {
        var store = new LedgerStore(CsvPath);

        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        Assert.True(File.Exists(store.Path));
        Assert.True(File.Exists(store.WorkbookPath));
        Assert.EndsWith(".xlsx", store.WorkbookPath);
    }

    [Fact]
    public void The_workbook_holds_the_same_headers_and_rows_as_the_csv()
    {
        var store = new LedgerStore(CsvPath);
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision"), Row(RowVerdicts.Skip, "Passport") });

        using var workbook = new XLWorkbook(store.WorkbookPath);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("row", sheet.Cell(1, 1).GetString());
        Assert.Equal("doc name", sheet.Cell(1, 2).GetString());
        Assert.Equal("verdict", sheet.Cell(1, 10).GetString());
        Assert.Equal("final state", sheet.Cell(1, 11).GetString());

        Assert.Equal("Board Decision", sheet.Cell(2, 2).GetString());
        Assert.Equal("Passport", sheet.Cell(3, 2).GetString());
    }

    /// <summary>The thing a CSV cannot do: a dropdown of exactly the values the tool understands.</summary>
    [Fact]
    public void The_verdict_column_carries_a_dropdown_of_the_five_values()
    {
        var store = new LedgerStore(CsvPath);
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.WorkbookPath);
        var validation = workbook.Worksheet(1).Cell(2, 10).GetDataValidation();

        Assert.NotNull(validation);
        foreach (var value in RowVerdicts.All)
            Assert.Contains(value, validation!.Value);
    }

    [Fact]
    public void The_final_state_column_carries_its_own_dropdown()
    {
        var store = new LedgerStore(CsvPath);
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.WorkbookPath);
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
        var store = new LedgerStore(CsvPath);
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.WorkbookPath);

        Assert.Equal(XLErrorStyle.Warning,
            workbook.Worksheet(1).Cell(2, 10).GetDataValidation()!.ErrorStyle);
    }

    [Fact]
    public void The_header_is_frozen_so_four_hundred_rows_stay_readable()
    {
        var store = new LedgerStore(CsvPath);
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.WorkbookPath);

        Assert.Equal(1, workbook.Worksheet(1).SheetView.SplitRow);
    }

    /// <summary>
    /// Fitted to the contents, but capped: one 200-character path must not push every other
    /// column off the screen.
    /// </summary>
    [Fact]
    public void A_very_long_value_does_not_make_its_column_unusably_wide()
    {
        var store = new LedgerStore(CsvPath);
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision", new string('x', 300)) });

        using var workbook = new XLWorkbook(store.WorkbookPath);

        Assert.True(workbook.Worksheet(1).Column(5).Width <= 60);
    }

    [Fact]
    public void The_rows_are_sorted_by_verdict_in_the_workbook_too()
    {
        var store = new LedgerStore(CsvPath);

        store.Write(new[]
        {
            Row(RowVerdicts.Ignore, "excluded"),
            Row(RowVerdicts.Fix, "work"),
            Row(RowVerdicts.Review, "decide")
        });

        using var workbook = new XLWorkbook(store.WorkbookPath);
        var sheet = workbook.Worksheet(1);

        Assert.Equal("work", sheet.Cell(2, 2).GetString());
        Assert.Equal("decide", sheet.Cell(3, 2).GetString());
        Assert.Equal("excluded", sheet.Cell(4, 2).GetString());
    }

    /// <summary>
    /// Excel holds the workbook open while the operator reads it. That must not fail a run —
    /// the CSV is the file that matters and it is written first.
    /// </summary>
    [Fact]
    public void A_workbook_that_cannot_be_written_does_not_lose_the_csv()
    {
        var store = new LedgerStore(CsvPath);
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using (File.Open(store.WorkbookPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision"), Row(RowVerdicts.Fix, "Second") });
        }

        Assert.Equal(2, store.Read().Count);
        Assert.NotNull(store.LastWorkbookProblem);
        Assert.Contains("Excel", store.LastWorkbookProblem);
    }
}
