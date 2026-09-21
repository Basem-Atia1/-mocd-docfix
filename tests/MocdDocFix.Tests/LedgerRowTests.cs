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
    // Legacy. Sheets written before skip left the vocabulary carry the word in hundreds of
    // cells; if it stopped parsing, every one would read as an unrecognised typo.
    [InlineData("skip", RowVerdict.Done)]
    [InlineData("ignore", RowVerdict.Ignore)]
    [InlineData("redo", RowVerdict.Redo)]
    [InlineData("done", RowVerdict.Done)]
    public void Every_verdict_the_operator_may_type_is_understood(string cell, RowVerdict expected) =>
        Assert.Equal(expected, RowVerdicts.Parse(cell));

    /// <summary>
    /// The safety rule: a typo must never be read as the value it nearly is. "fixx" acting as
    /// "fix" would upload a file the operator had tried to exclude.
    /// </summary>
    [Theory]
    [InlineData("fixx")]
    [InlineData("f")]
    [InlineData("finished")]
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

    // ---- naming a row in a message ----

    private static LedgerRow Named() => new()
    {
        Row = 1,
        DocId = Guid.Parse("2cd3b04c-391c-f111-b119-005056010908"),
        DocFileName = "Application Summary.png"
    };

    [Fact]
    public void A_settled_row_is_named_with_its_number_document_and_file()
    {
        var text = Named().Ref();

        Assert.Contains("row 1", text);
        Assert.Contains("2cd3b04c", text);
        Assert.Contains("Application Summary.png", text);
    }

    /// <summary>The tab helps somebody find the row, while the row is really on it.</summary>
    [Fact]
    public void A_row_awaiting_its_delete_is_named_with_the_tab_it_sits_on()
    {
        var row = Named();
        row.FinalState = RowStates.Text(RowState.Corrected);

        Assert.Contains("on the corrected tab", row.Ref());
    }

    /// <summary>
    /// The bug this closes. The two halves of Ref are not as of the same moment: the tab comes
    /// from the row's state right now, the number from the last write. A row whose state has
    /// just been changed therefore reads as "row 1 on the corrected tab" — where 1 was its
    /// number on the ledger tab, and the corrected tab is somewhere it has not been written to.
    /// That sends the reader to the wrong sheet to look for a row that is not there.
    /// </summary>
    [Fact]
    public void Named_leaves_the_tab_out_so_a_row_just_changed_is_not_placed_where_it_is_not_yet()
    {
        var row = Named();
        row.FinalState = RowStates.Text(RowState.Corrected);

        var text = row.Named();

        Assert.DoesNotContain("tab", text);
        Assert.Contains("row 1", text);
        Assert.Contains("2cd3b04c", text);
        Assert.Contains("Application Summary.png", text);
    }
}
