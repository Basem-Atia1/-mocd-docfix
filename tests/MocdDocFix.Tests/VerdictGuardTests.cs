using MocdDocFix.Commands;
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Delete old files reads the final state column, never the verdict. So typing ignore on a
/// finished row stops nothing and the old file goes anyway — two columns, one of which looks
/// like a switch and is not. This is the guard on that.
/// </summary>
public class VerdictGuardTests
{
    private static LedgerRow Row(string verdict, string finalState) => new()
    {
        Row = 12,
        DocId = Guid.Parse("22222222-0000-0000-0000-000000000001"),
        DocFileName = "a.pdf",
        OldFilePath = @"DigitalServices\boardDecision\20250509\a.pdf",
        Verdict = verdict,
        FinalState = finalState
    };

    [Fact]
    public void Ignore_typed_over_a_pending_delete_is_caught()
    {
        var found = VerdictGuard.Find(new[] { Row(RowVerdicts.Ignore, RowStates.Corrected) });

        var stranded = Assert.Single(found);
        Assert.True(stranded.DeleteStillPending);
        Assert.False(stranded.WouldReRun);
    }

    [Fact]
    public void Fix_typed_over_a_corrected_row_is_caught_as_a_re_run()
    {
        var found = VerdictGuard.Find(new[] { Row(RowVerdicts.Fix, RowStates.Corrected) });

        Assert.True(Assert.Single(found).WouldReRun);
    }

    /// <summary>Redo is the mode built for exactly this row. Asking would be asking twice.</summary>
    [Fact]
    public void Redo_is_never_questioned()
    {
        Assert.Empty(VerdictGuard.Find(new[] { Row(RowVerdicts.Redo, RowStates.Corrected) }));
    }

    [Fact]
    public void A_row_that_already_says_done_is_not_questioned()
    {
        Assert.Empty(VerdictGuard.Find(new[] { Row(RowVerdicts.Done, RowStates.Corrected) }));
    }

    [Fact]
    public void An_untouched_row_is_not_questioned()
    {
        Assert.Empty(VerdictGuard.Find(new[] { Row(RowVerdicts.Ignore, string.Empty) }));
    }

    /// <summary>The file has gone. There is no delete left to stop, only a verdict to correct.</summary>
    [Fact]
    public void A_deleted_row_is_caught_but_has_no_delete_to_stop()
    {
        var found = VerdictGuard.Find(new[] { Row(RowVerdicts.Ignore, RowStates.Deleted) });

        Assert.False(Assert.Single(found).DeleteStillPending);
    }

    [Fact]
    public void Putting_it_back_leaves_the_final_state_alone()
    {
        var row = Row(RowVerdicts.Ignore, RowStates.Corrected);

        VerdictGuard.Apply(VerdictGuard.Find(new[] { row }), GuardChoice.BackToDone);

        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(RowState.Corrected, row.State());
    }

    /// <summary>The only answer that actually stops the file being deleted.</summary>
    [Fact]
    public void Stopping_the_delete_writes_ignore_into_both_columns()
    {
        var row = Row(RowVerdicts.Ignore, RowStates.Corrected);

        VerdictGuard.Apply(VerdictGuard.Find(new[] { row }), GuardChoice.StopTheDelete);

        Assert.Equal(RowVerdict.Ignore, row.Verdict2());
        Assert.Equal(RowState.Ignore, row.State());
        Assert.Contains("will not be deleted", row.Notes);
    }

    [Fact]
    public void Leaving_it_as_typed_changes_nothing_but_says_what_will_happen()
    {
        var row = Row(RowVerdicts.Ignore, RowStates.Corrected);

        VerdictGuard.Apply(VerdictGuard.Find(new[] { row }), GuardChoice.LeaveAsTyped);

        Assert.Equal(RowVerdict.Ignore, row.Verdict2());
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Contains("will still be deleted", row.Notes);
    }

    /// <summary>
    /// Asking to stop a delete that has already happened cannot un-delete the file. It says so
    /// rather than writing ignore over the record that it went.
    /// </summary>
    [Fact]
    public void Stopping_a_delete_that_already_happened_leaves_the_record_intact()
    {
        var row = Row(RowVerdicts.Ignore, RowStates.Deleted);

        VerdictGuard.Apply(VerdictGuard.Find(new[] { row }), GuardChoice.StopTheDelete);

        Assert.Equal(RowState.Deleted, row.State());
        Assert.Contains("already deleted", row.Notes);
    }
}
