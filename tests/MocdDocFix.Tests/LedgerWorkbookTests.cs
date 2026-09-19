using ClosedXML.Excel;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The workbook is the ledger: it is what the operator edits, with dropdowns on the two columns
/// they set, and it is what every mode reads. The CSV beside it is a copy.
/// </summary>
[Collection(LedgerCollection.Name)]
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
            workbook.Worksheet(1).Cell(2, 12).Value = RowVerdicts.Ignore;
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
            workbook.Worksheet(1).Cell(2, 13).Value = RowStates.Ignore;
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
        Assert.Equal("doc id", sheet.Cell(1, 2).GetString());
        Assert.Equal("old file path", sheet.Cell(1, 6).GetString());

        // The sentence: filed under this, the record says that, it should be the third.
        Assert.Equal("service catalogue name", sheet.Cell(1, 9).GetString());
        Assert.Equal("old category", sheet.Cell(1, 10).GetString());
        Assert.Equal("correct service catalogue name", sheet.Cell(1, 11).GetString());

        Assert.Equal("verdict", sheet.Cell(1, 12).GetString());
        Assert.Equal("final state", sheet.Cell(1, 13).GetString());
        Assert.Equal("backup path", sheet.Cell(1, 17).GetString());
    }

    /// <summary>The thing a CSV cannot do: a dropdown of exactly the values the tool understands.</summary>
    [Fact]
    public void The_verdict_column_carries_a_dropdown_of_the_five_values()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var workbook = new XLWorkbook(store.Path);
        var validation = workbook.Worksheet(1).Cell(2, 12).GetDataValidation();

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
        var validation = workbook.Worksheet(1).Cell(2, 13).GetDataValidation();

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
            workbook.Worksheet(1).Cell(2, 12).GetDataValidation()!.ErrorStyle);
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

        Assert.True(workbook.Worksheet(1).Column(6).Width <= 60);
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

    /// <summary>
    /// The case that matters most: Excel is opened mid-run, after a document has already been
    /// uploaded and its CRM record changed. Failing here would strand that work with nothing on
    /// disk recording it, so the write waits and is tried again.
    /// </summary>
    [Fact]
    public void A_locked_workbook_is_written_after_the_operator_closes_excel()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        var held = File.Open(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var asked = 0;

        // Answering yes is the operator closing Excel, so the lock goes with the answer.
        store.AskToRetry = _ => { asked++; held.Dispose(); return true; };

        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision"), Row(RowVerdicts.Fix, "Second") });

        Assert.Equal(1, asked);
        Assert.Equal(2, store.Read().Count);
    }

    [Fact]
    public void Answering_no_lets_the_failure_through_rather_than_pretending_it_worked()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });
        store.AskToRetry = _ => false;

        using var held = File.Open(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() =>
            store.Write(new[] { Row(RowVerdicts.Fix, "Second") }));
    }

    /// <summary>Without anyone to ask — the direct commands, and tests — it throws as before.</summary>
    [Fact]
    public void With_nobody_to_ask_a_locked_workbook_still_throws()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var held = File.Open(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() =>
            store.Write(new[] { Row(RowVerdicts.Fix, "Second") }));
    }

    [Fact]
    public void A_ledger_that_is_not_there_yet_reads_as_nothing_and_can_be_written()
    {
        var store = Store();

        Assert.False(store.Exists);
        Assert.Empty(store.Read());
        Assert.True(store.CanWrite());
    }

    /// <summary>A copy kept aside must still be a readable ledger in its own right.</summary>
    [Fact]
    public void A_backup_copy_can_itself_be_opened_as_a_ledger()
    {
        var store = Store();
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });
        store.Write(new[] { Row(RowVerdicts.Fix, "Board Decision"), Row(RowVerdicts.Skip, "Passport") });

        var kept = Directory.GetFiles(store.PreviousDirectory, "*.xlsx").Single();

        Assert.Single(new LedgerStore(kept).Read());
    }

    /// <summary>
    /// A process killed during a save — the console window closed, the machine shut down —
    /// used to leave a half-written workbook, and half a workbook opens as nothing at all.
    /// </summary>
    [Fact]
    public void A_stray_temporary_file_is_cleared_by_the_next_write()
    {
        var stray = LedgerPath + ".tmp";
        File.WriteAllText(stray, "half a workbook");

        new LedgerWorkbook(LedgerPath).Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        Assert.True(File.Exists(LedgerPath));
        Assert.False(File.Exists(stray));
    }

    /// <summary>
    /// The widths are fixed, not measured.
    ///
    /// AdjustToContents walked every cell of every column to fit them, and on a 29-column sheet
    /// that was 97% of the cost of a write — 15.7 seconds at 2,000 rows against 0.57 without
    /// it — paid after every corrected document. So a long value in a cell must make no
    /// difference at all to the column carrying it.
    /// </summary>
    [Fact]
    public void A_long_value_does_not_widen_its_column()
    {
        new LedgerWorkbook(LedgerPath).Write(new[] { Row(RowVerdicts.Fix, new string('x', 200)) });

        using var book = new XLWorkbook(LedgerPath);
        var sheet = book.Worksheet("ledger");

        var docName = LedgerColumns.All.ToList().FindIndex(c => c.Header == "doc name") + 1;

        Assert.Equal(30, sheet.Column(docName).Width);
    }

    /// <summary>Every column gets one, and none is left absurdly wide.</summary>
    [Fact]
    public void Every_column_gets_a_width_and_none_is_wider_than_the_cap()
    {
        new LedgerWorkbook(LedgerPath).Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

        using var book = new XLWorkbook(LedgerPath);
        var sheet = book.Worksheet("ledger");

        for (var c = 1; c <= LedgerColumns.All.Count; c++)
        {
            Assert.True(sheet.Column(c).Width >= 6, $"column {c} is too narrow");
            Assert.True(sheet.Column(c).Width <= 60, $"column {c} is wider than the cap");
        }
    }
}
