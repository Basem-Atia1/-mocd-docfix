using MocdDocFix.Commands;
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// There is one ledger per environment for its whole life, so a re-scan has to merge into it
/// rather than replace it. Who wins is decided per column: CRM owns the facts, the operator owns
/// the verdict, and a row already acted on owns its own history.
/// </summary>
public class LedgerMergeTests
{
    private static readonly Guid One = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Two = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000002");

    private static LedgerRow Row(Guid id, string verdict = RowVerdicts.Fix,
        string finalState = "", string oldPath = @"DigitalServices\docType\20250509\a.jpg") => new()
    {
        Row = 1,
        DocId = id,
        DocName = "Board Decision",
        DocFileName = "cert.jpg",
        OldFilePath = oldPath,
        OldCategory = "docType",
        OldHash = "9f86d081",
        ServiceCatalogueName = "Employee Appointment Request",
        Verdict = verdict,
        FinalState = finalState,
        Group = 2,
        ReasonOfBug = "the old reason"
    };

    [Fact]
    public void A_document_crm_has_and_the_ledger_does_not_is_added()
    {
        var merged = LedgerMerge.Into(new[] { Row(One) }, new[] { Row(One), Row(Two) });

        Assert.Equal(1, merged.Added);
        Assert.Equal(2, merged.Rows.Count);
        Assert.Contains(merged.Rows, r => r.DocId == Two);
    }

    [Fact]
    public void A_row_not_yet_worked_on_takes_crms_current_facts()
    {
        var existing = Row(One);
        var scanned = Row(One);
        scanned.DocName = "renamed in CRM";
        scanned.ReasonOfBug = "the new reason";
        scanned.Group = 3;
        scanned.OldFilePath = @"DigitalServices\somethingElse\20250509\a.jpg";

        LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal("renamed in CRM", existing.DocName);
        Assert.Equal("the new reason", existing.ReasonOfBug);
        Assert.Equal(3, existing.Group);
        Assert.Equal(@"DigitalServices\somethingElse\20250509\a.jpg", existing.OldFilePath);
    }

    /// <summary>
    /// The sharp edge. After a correction CRM's mocd_filepath holds the NEW path, so refreshing
    /// this row would overwrite the only record of where the file used to be — which is what
    /// Redo and the delete step run on.
    /// </summary>
    [Fact]
    public void A_corrected_row_keeps_its_history_even_though_crm_now_says_otherwise()
    {
        var existing = Row(One, finalState: RowStates.Text(RowState.Corrected));
        existing.NewFilePath = @"DigitalServices\correct\20260915\b.jpg";

        // What a fresh scan would see now: the record points at the corrected file.
        var scanned = Row(One, oldPath: @"DigitalServices\correct\20260915\b.jpg");
        scanned.OldCategory = "correct";
        scanned.OldHash = "5e884898";

        var merged = LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal(@"DigitalServices\docType\20250509\a.jpg", existing.OldFilePath);
        Assert.Equal("docType", existing.OldCategory);
        Assert.Equal("9f86d081", existing.OldHash);
        Assert.Equal(RowState.Corrected, existing.State());
        Assert.Equal(1, merged.Protected);
    }

    [Fact]
    public void A_corrected_row_still_takes_a_new_display_name_and_links()
    {
        var existing = Row(One, finalState: RowStates.Text(RowState.Corrected));
        var scanned = Row(One);
        scanned.DocName = "renamed in CRM";
        scanned.CrmLinkOfDoc = "https://crm.example/new";

        LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal("renamed in CRM", existing.DocName);
        Assert.Equal("https://crm.example/new", existing.CrmLinkOfDoc);
    }

    /// <summary>
    /// A rescan resetting a finished row to "fix" would offer work that is not outstanding —
    /// and the operator would then watch the tool upload a file it had already corrected.
    /// </summary>
    [Fact]
    public void A_finished_row_is_not_put_back_to_fix_by_a_rescan()
    {
        var existing = Row(One, verdict: RowVerdicts.Done,
            finalState: RowStates.Text(RowState.Corrected));

        LedgerMerge.Into(new[] { existing }, new[] { Row(One, verdict: RowVerdicts.Fix) });

        Assert.Equal(RowVerdict.Done, existing.Verdict2());
    }

    [Theory]
    [InlineData(RowVerdicts.Ignore)]
    [InlineData(RowVerdicts.Redo)]
    [InlineData(RowVerdicts.Done)]
    public void The_words_a_scan_could_not_have_written_are_never_overwritten(string mine)
    {
        var existing = Row(One, verdict: mine);
        var scanned = Row(One, verdict: RowVerdicts.Fix);

        LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal(mine, existing.Verdict);
    }

    /// <summary>
    /// A document somebody has fixed in CRM since the last scan stops being work to do, and the
    /// ledger should say so rather than keep offering it.
    /// </summary>
    [Fact]
    public void A_row_crm_now_calls_correct_takes_the_scans_verdict()
    {
        var existing = Row(One, verdict: RowVerdicts.Fix);
        var scanned = Row(One, verdict: RowVerdicts.Skip);

        LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal(RowVerdict.Skip, existing.Verdict2());
    }

    [Fact]
    public void A_final_state_is_never_overwritten_by_a_scan()
    {
        var existing = Row(One, finalState: RowStates.Text(RowState.Corrected));

        LedgerMerge.Into(new[] { existing }, new[] { Row(One) });

        Assert.Equal(RowState.Corrected, existing.State());
    }

    [Fact]
    public void A_failed_row_is_refreshed_because_nothing_succeeded_on_it()
    {
        var existing = Row(One, finalState: RowStates.Text(RowState.Failed));
        var scanned = Row(One);
        scanned.ReasonOfBug = "the new reason";

        var merged = LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal("the new reason", existing.ReasonOfBug);
        Assert.Equal(1, merged.Refreshed);
    }

    /// <summary>
    /// A document out of scope, deleted, or moved to a service this tool does not cover. Losing
    /// the row would lose the record of a correction it may already have had.
    /// </summary>
    [Fact]
    public void A_document_crm_no_longer_returns_is_kept_and_reported()
    {
        var gone = Row(Two, finalState: RowStates.Text(RowState.Corrected));

        var merged = LedgerMerge.Into(new[] { Row(One), gone }, new[] { Row(One) });

        Assert.Equal(2, merged.Rows.Count);
        Assert.Contains(merged.Rows, r => r.DocId == Two);
        Assert.Contains(merged.Notes, n => n.Contains("no longer"));
    }

    [Fact]
    public void Merging_an_empty_ledger_is_just_the_scan()
    {
        var merged = LedgerMerge.Into(Array.Empty<LedgerRow>(), new[] { Row(One), Row(Two) });

        Assert.Equal(2, merged.Added);
        Assert.Equal(2, merged.Rows.Count);
    }

    [Fact]
    public void Nothing_is_duplicated_when_the_scan_finds_the_same_documents()
    {
        var merged = LedgerMerge.Into(new[] { Row(One), Row(Two) }, new[] { Row(One), Row(Two) });

        Assert.Equal(2, merged.Rows.Count);
        Assert.Equal(0, merged.Added);
        Assert.Equal(2, merged.Refreshed);
    }
}
