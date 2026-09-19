using MocdDocFix.Commands;
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The one-time tidy of a sheet written before correct documents stopped entering it.
///
/// Getting this wrong throws away the record of work that was really done, so every branch is
/// pinned here.
/// </summary>
public class LegacyCleanupTests
{
    private static readonly Guid Doc = Guid.Parse("11111111-0000-0000-0000-000000000001");

    private static LedgerRow Row(string verdict, string finalState = "", string notes = "") => new()
    {
        Row = 1,
        DocId = Doc,
        DocFileName = "a.pdf",
        Verdict = verdict,
        FinalState = finalState,
        Notes = notes
    };

    /// <summary>What the scan returns for a document it finds nothing wrong with.</summary>
    private static LedgerRow Correct() => new() { DocId = Doc, Verdict = string.Empty };

    [Fact]
    public void A_correct_row_with_no_final_state_is_removed()
    {
        var result = LegacyCleanup.Apply(new[] { Row("skip") }, new[] { Correct() });

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.Removed);
    }

    /// <summary>
    /// The one that must not be got wrong. Its old file is still on the server and the delete
    /// has not run, so it belongs with the outstanding deletes — not on a tab called finished.
    /// </summary>
    [Fact]
    public void A_row_awaiting_its_delete_is_kept_and_reads_done()
    {
        var row = Row("skip", RowStates.Corrected);

        var result = LegacyCleanup.Apply(new[] { row }, new[] { Correct() });

        Assert.Single(result.Rows);
        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(LedgerTab.Corrected, LedgerTabs.Of(row));
    }

    [Fact]
    public void A_row_whose_old_file_has_gone_is_kept_and_is_finished()
    {
        var row = Row("skip", RowStates.Deleted);

        LegacyCleanup.Apply(new[] { row }, new[] { Correct() });

        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(LedgerTab.Finished, LedgerTabs.Of(row));
    }

    /// <summary>It failed. That is neither finished nor nothing — somebody has to look.</summary>
    [Fact]
    public void A_failed_row_is_kept_for_a_human()
    {
        var row = Row("skip", RowStates.Failed);

        LegacyCleanup.Apply(new[] { row }, new[] { Correct() });

        Assert.Equal(RowVerdict.Review, row.Verdict2());
    }

    [Fact]
    public void A_row_closed_by_hand_stays_closed()
    {
        var row = Row("skip", RowStates.Ignore);

        LegacyCleanup.Apply(new[] { row }, new[] { Correct() });

        Assert.Equal(RowVerdict.Ignore, row.Verdict2());
    }

    /// <summary>
    /// The test is the scan's own classification, not the cell. Somebody who typed fix on a row
    /// has said they want it, whatever CRM makes of it.
    /// </summary>
    [Fact]
    public void A_row_the_scan_still_calls_broken_is_never_removed()
    {
        var result = LegacyCleanup.Apply(
            new[] { Row("skip") },
            new[] { new LedgerRow { DocId = Doc, Verdict = RowVerdicts.Fix } });

        Assert.Single(result.Rows);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void A_row_with_notes_on_it_is_never_removed()
    {
        var result = LegacyCleanup.Apply(
            new[] { Row("skip", notes: "checked 2026-09-16 — already right") },
            new[] { Correct() });

        Assert.Single(result.Rows);
    }

    /// <summary>Anything that never said skip is not this pass's business at all.</summary>
    [Fact]
    public void Rows_with_any_other_verdict_are_left_exactly_as_they_are()
    {
        var fix = Row(RowVerdicts.Fix);
        var review = Row(RowVerdicts.Review);

        var result = LegacyCleanup.Apply(new[] { fix, review }, Array.Empty<LedgerRow>());

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Kept);
        Assert.Equal(RowVerdicts.Fix, fix.Verdict);
    }

    /// <summary>A sheet with nothing legacy in it must do nothing and say nothing.</summary>
    [Fact]
    public void A_sheet_already_in_order_is_untouched()
    {
        var result = LegacyCleanup.Apply(
            new[] { Row(RowVerdicts.Fix) }, new[] { new LedgerRow { DocId = Doc, Verdict = RowVerdicts.Fix } });

        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Kept);
    }
}
