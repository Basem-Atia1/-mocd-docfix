using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

public class LedgerRowTests
{
    [Theory]
    [InlineData("fix", RowVerdict.Fix)]
    [InlineData("FIX", RowVerdict.Fix)]
    [InlineData("  Fix  ", RowVerdict.Fix)]
    [InlineData("review", RowVerdict.Review)]
    [InlineData("skip", RowVerdict.Skip)]
    [InlineData("ignore", RowVerdict.Ignore)]
    [InlineData("redo", RowVerdict.Redo)]
    public void Every_verdict_the_operator_may_type_is_understood(string cell, RowVerdict expected) =>
        Assert.Equal(expected, RowVerdicts.Parse(cell));

    /// <summary>
    /// The safety rule: a typo must never be read as the value it nearly is. "fixx" acting as
    /// "fix" would upload a file the operator had tried to exclude.
    /// </summary>
    [Theory]
    [InlineData("fixx")]
    [InlineData("f")]
    [InlineData("done")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_unrecognised_rather_than_the_value_it_resembles(string? cell) =>
        Assert.Equal(RowVerdict.Unrecognised, RowVerdicts.Parse(cell));

    [Theory]
    [InlineData("corrected and pending the delete of old docs", RowState.Corrected)]
    [InlineData("CORRECTED AND PENDING THE DELETE OF OLD DOCS", RowState.Corrected)]
    [InlineData("old files deleted", RowState.Deleted)]
    [InlineData("ignore", RowState.Ignore)]
    [InlineData("failed", RowState.Failed)]
    public void Every_final_state_the_steps_write_is_understood(string cell, RowState expected) =>
        Assert.Equal(expected, RowStates.Parse(cell));

    /// <summary>Blank is a real value — it means the row has not been started.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_final_state_means_not_started(string? cell) =>
        Assert.Equal(RowState.NotStarted, RowStates.Parse(cell));

    [Fact]
    public void A_final_state_nobody_recognises_is_not_mistaken_for_not_started() =>
        Assert.Equal(RowState.Unrecognised, RowStates.Parse("nearly done"));

    /// <summary>Round-tripping matters: the steps write Text(), the operator reads it, Parse() sees it again.</summary>
    [Theory]
    [InlineData(RowState.Corrected)]
    [InlineData(RowState.Deleted)]
    [InlineData(RowState.Ignore)]
    [InlineData(RowState.Failed)]
    public void What_a_step_writes_is_what_the_next_run_reads(RowState state) =>
        Assert.Equal(state, RowStates.Parse(RowStates.Text(state)));

    [Fact]
    public void The_row_reads_its_own_two_cells()
    {
        var row = new LedgerRow { Verdict = "fix", FinalState = RowStates.Text(RowState.Corrected) };

        Assert.Equal(RowVerdict.Fix, row.Verdict2());
        Assert.Equal(RowState.Corrected, row.State());
    }

    [Fact]
    public void The_two_crm_links_point_at_the_right_entities()
    {
        var id = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");

        Assert.Equal(
            $"https://crm.example/main.aspx?etn=mocd_document&pagetype=entityrecord&id={id}",
            CrmLinks.Document("https://crm.example/", id));

        Assert.Equal(
            $"https://crm.example/main.aspx?etn=mocd_documentfile&pagetype=entityrecord&id={id}",
            CrmLinks.DocumentFile("https://crm.example", id));
    }
}
