using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

public class LedgerOrderTests
{
    private static LedgerRow Row(string verdict, string name) =>
        new() { Verdict = verdict, DocName = name, DocId = Guid.NewGuid() };

    [Fact]
    public void Verdicts_sort_fix_skip_review_redo_ignore()
    {
        var sorted = LedgerOrder.Sorted(new[]
        {
            Row(RowVerdicts.Ignore, "e"),
            Row(RowVerdicts.Redo, "d"),
            Row(RowVerdicts.Review, "c"),
            Row(RowVerdicts.Skip, "b"),
            Row(RowVerdicts.Fix, "a")
        });

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, sorted.Select(r => r.DocName));
    }

    /// <summary>
    /// A typo must not be buried among hundreds of skipped rows — it goes last, where the eye
    /// ends up, rather than into the middle.
    /// </summary>
    [Fact]
    public void An_unrecognised_verdict_sorts_last()
    {
        var sorted = LedgerOrder.Sorted(new[]
        {
            Row("fixx", "typo"),
            Row(RowVerdicts.Ignore, "ignored"),
            Row(RowVerdicts.Fix, "work")
        });

        Assert.Equal("typo", sorted[^1].DocName);
    }

    /// <summary>Two documents of the same kind stay neighbours, run after run.</summary>
    [Fact]
    public void Within_one_verdict_the_scans_own_order_is_kept()
    {
        var sorted = LedgerOrder.Sorted(new[]
        {
            Row(RowVerdicts.Fix, "first"),
            Row(RowVerdicts.Fix, "second"),
            Row(RowVerdicts.Fix, "third")
        });

        Assert.Equal(new[] { "first", "second", "third" }, sorted.Select(r => r.DocName));
    }

    [Fact]
    public void The_sheet_reads_one_two_three_down_the_page()
    {
        var sorted = LedgerOrder.Sorted(new[]
        {
            Row(RowVerdicts.Ignore, "e"),
            Row(RowVerdicts.Fix, "a"),
            Row(RowVerdicts.Skip, "b")
        });

        Assert.Equal(new[] { 1, 2, 3 }, sorted.Select(r => r.Row));
    }

    /// <summary>
    /// Renumbering reorders and relabels; it must never lose a row or hand back copies, because
    /// the loop holds references to these same objects.
    /// </summary>
    [Fact]
    public void Every_row_survives_and_is_the_same_object()
    {
        var rows = new[] { Row(RowVerdicts.Ignore, "e"), Row(RowVerdicts.Fix, "a") };

        var sorted = LedgerOrder.Sorted(rows);

        Assert.Equal(2, sorted.Count);
        Assert.Same(rows[1], sorted[0]);
        Assert.Same(rows[0], sorted[1]);
    }

    [Fact]
    public void An_empty_ledger_sorts_to_nothing() =>
        Assert.Empty(LedgerOrder.Sorted(Array.Empty<LedgerRow>()));

    /// <summary>
    /// The workbook takes its columns from the same attributes the CSV does, so the two cannot
    /// drift. This pins the order that was asked for.
    /// </summary>
    [Fact]
    public void The_columns_read_in_the_order_the_operator_asked_for()
    {
        var headers = LedgerColumns.All.Select(c => c.Header).ToList();

        Assert.Equal(new[]
        {
            "row", "doc name", "doc type name", "doc file name",
            "old file path", "new file path predicted", "new file path",
            "service catalogue name", "correct service catalogue name",
            "verdict", "final state", "group", "way of upload", "error", "backup path"
        }, headers.Take(15));

        // And every GUID is kept to the right of them.
        Assert.True(headers.IndexOf("doc id") > headers.IndexOf("backup path"));
        Assert.Equal(29, headers.Count);
    }

    [Fact]
    public void Every_column_can_be_read_off_a_row()
    {
        var row = Row(RowVerdicts.Fix, "Board Decision");
        row.OldFilePath = @"DigitalServices\x\20250509\a.jpg";

        var cells = LedgerColumns.All.Select(c => c.Read(row)).ToList();

        Assert.Equal(29, cells.Count);
        Assert.Contains("Board Decision", cells);
        Assert.Contains(@"DigitalServices\x\20250509\a.jpg", cells);
    }
}
