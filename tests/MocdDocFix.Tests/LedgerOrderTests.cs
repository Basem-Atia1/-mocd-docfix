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
    /// <summary>
    /// The one seen in the live ledger: row 1 read "fix" and "old files deleted" at once — a
    /// completely finished document sitting at the very top of the sheet among the work still
    /// to do, because it was corrected before the tool wrote "done" and the delete step left
    /// the verdict alone. Ranking on the verdict alone cannot catch that; the final state can.
    /// </summary>
    [Fact]
    public void A_finished_row_sinks_even_when_its_verdict_still_says_fix()
    {
        var legacy = Row(RowVerdicts.Fix, "finished long ago");
        legacy.FinalState = RowStates.Text(RowState.Deleted);

        var sorted = LedgerOrder.Sorted(new[]
        {
            legacy,
            Row(RowVerdicts.Fix, "actually to do"),
            Row(RowVerdicts.Ignore, "excluded")
        });

        Assert.Equal("actually to do", sorted[0].DocName);
        Assert.Equal("finished long ago", sorted[^1].DocName);
    }

    [Fact]
    public void A_corrected_row_sinks_too_even_before_its_old_file_goes()
    {
        var corrected = Row(RowVerdicts.Fix, "corrected");
        corrected.FinalState = RowStates.Text(RowState.Corrected);

        var sorted = LedgerOrder.Sorted(new[] { corrected, Row(RowVerdicts.Fix, "to do") });

        Assert.Equal("to do", sorted[0].DocName);
    }

    /// <summary>
    /// A row that failed is not finished — it is work outstanding, and must stay where it can
    /// be seen.
    /// </summary>
    [Fact]
    public void A_failed_row_stays_with_the_work()
    {
        var failed = Row(RowVerdicts.Fix, "failed");
        failed.FinalState = RowStates.Text(RowState.Failed);

        var sorted = LedgerOrder.Sorted(new[] { Row(RowVerdicts.Ignore, "excluded"), failed });

        Assert.Equal("failed", sorted[0].DocName);
    }

    /// <summary>
    /// A finished row is the one thing nobody needs to look at again. Leaving it among the
    /// outstanding work is what made a corrected document still read as "to do".
    /// </summary>
    [Fact]
    public void Finished_rows_sink_below_everything_still_outstanding()
    {
        var sorted = LedgerOrder.Sorted(new[]
        {
            Row(RowVerdicts.Done, "finished"),
            Row(RowVerdicts.Fix, "to do"),
            Row(RowVerdicts.Review, "decide"),
            Row(RowVerdicts.Ignore, "excluded")
        });

        Assert.Equal("finished", sorted[^1].DocName);
        Assert.Equal("to do", sorted[0].DocName);
    }

    [Fact]
    public void An_unrecognised_verdict_sorts_below_the_work_but_above_the_finished()
    {
        var sorted = LedgerOrder.Sorted(new[]
        {
            Row("fixx", "typo"),
            Row(RowVerdicts.Done, "finished"),
            Row(RowVerdicts.Ignore, "ignored"),
            Row(RowVerdicts.Fix, "work")
        });

        // A typo needs attention, so it must not be buried under the rows that do not.
        Assert.Equal(new[] { "work", "ignored", "typo", "finished" },
            sorted.Select(r => r.DocName));
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
            "row", "doc id", "doc name", "doc type name", "doc file name",
            "old file path", "new file path predicted", "new file path",

            // The three that read as one sentence: filed under this, the record says that,
            // it should be the third.
            "service catalogue name", "old category", "correct service catalogue name",

            "verdict", "final state", "group", "way of upload", "error", "backup path"
        }, headers.Take(17));

        // old category earns its place by sitting between the two names it disagrees with.
        Assert.Equal(headers.IndexOf("correct service catalogue name") - 1,
            headers.IndexOf("old category"));

        // The remaining machine detail stays to the right.
        Assert.True(headers.IndexOf("doc file id") > headers.IndexOf("backup path"));
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
