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

        LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal("renamed in CRM", existing.DocName);
        Assert.Equal("the new reason", existing.ReasonOfBug);
        Assert.Equal(3, existing.Group);
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
    /// The flaw this replaced: an operator who read the reason and moved a row from review to
    /// fix — the whole point of the column — watched the next scan put it straight back. A
    /// column the tool silently overrules is not a column anybody can edit.
    /// </summary>
    [Fact]
    public void A_verdict_typed_by_hand_is_not_overwritten_by_the_scan()
    {
        var existing = Row(One, verdict: RowVerdicts.Fix);      // the operator upgraded it
        var scanned = Row(One, verdict: RowVerdicts.Review);    // the scan still says review

        LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal(RowVerdict.Fix, existing.Verdict2());
    }

    /// <summary>
    /// But a disagreement is not swallowed either — the operator is shown it and decides, since
    /// a scan can be the newer of the two when somebody has fixed a document type since.
    /// </summary>
    [Fact]
    public void A_disagreement_is_reported_so_it_can_be_asked_about()
    {
        var existing = Row(One, verdict: RowVerdicts.Fix);
        var scanned = Row(One, verdict: RowVerdicts.Review);

        var merged = LedgerMerge.Into(new[] { existing }, new[] { scanned });

        var disagreement = Assert.Single(merged.Disagreements);
        Assert.Equal(RowVerdicts.Review, disagreement.ScanSays);
        Assert.Same(existing, disagreement.Row);
    }

    [Fact]
    public void Agreement_is_not_reported_as_a_disagreement()
    {
        var merged = LedgerMerge.Into(
            new[] { Row(One, verdict: RowVerdicts.Fix) },
            new[] { Row(One, verdict: RowVerdicts.Fix) });

        Assert.Empty(merged.Disagreements);
    }

    /// <summary>Answering "take what CRM says" applies the scan's answers, and only to those rows.</summary>
    [Fact]
    public void Taking_the_scans_verdicts_replaces_exactly_the_rows_that_differed()
    {
        var disputed = Row(One, verdict: RowVerdicts.Fix);
        var agreed = Row(Two, verdict: RowVerdicts.Skip);

        var merged = LedgerMerge.Into(
            new[] { disputed, agreed },
            new[] { Row(One, verdict: RowVerdicts.Review), Row(Two, verdict: RowVerdicts.Skip) });

        LedgerMerge.TakeScanVerdicts(merged.Disagreements);

        Assert.Equal(RowVerdict.Review, disputed.Verdict2());
        Assert.Equal(RowVerdict.Skip, agreed.Verdict2());
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
    /// A document somebody has fixed in CRM since the ledger was built stops being work to do.
    /// The ledger is not changed behind the operator's back, but the disagreement is put in
    /// front of them — and the reason and solution beside it are refreshed, so the row itself
    /// says why CRM now thinks differently.
    /// </summary>
    [Fact]
    public void A_row_crm_now_calls_correct_is_raised_rather_than_quietly_changed()
    {
        var existing = Row(One, verdict: RowVerdicts.Fix);
        var scanned = Row(One, verdict: RowVerdicts.Skip);
        scanned.ReasonOfBug = "Path already correct";

        var merged = LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal(RowVerdict.Fix, existing.Verdict2());          // untouched
        Assert.Equal("Path already correct", existing.ReasonOfBug); // but the row says why
        Assert.Contains(merged.Disagreements, d => d.ScanSays == RowVerdicts.Skip);
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

    /// <summary>
    /// One fill-down in Excel sets four hundred cells to ignore in a second, and the merge is
    /// deliberately unable to overrule an excluded verdict. So the rows are reported instead —
    /// that report is the only way back that does not mean editing every cell by hand.
    /// </summary>
    [Fact]
    public void A_row_excluded_by_hand_and_never_worked_on_is_reported()
    {
        var existing = Row(One, verdict: RowVerdicts.Ignore);
        var scanned = Row(One, verdict: RowVerdicts.Fix);

        var merged = LedgerMerge.Into(new[] { existing }, new[] { scanned });

        var reported = Assert.Single(merged.Excluded);
        Assert.Same(existing, reported.Row);
        Assert.Equal(RowVerdicts.Fix, reported.ScanSays);
        Assert.Equal(RowVerdict.Ignore, existing.Verdict2());   // still not touched
    }

    /// <summary>
    /// Excluded and already acted on is a different thing: the row has a history to protect, and
    /// offering to undo the exclusion would offer to redo the work.
    /// </summary>
    [Theory]
    [InlineData(RowStates.Corrected)]
    [InlineData(RowStates.Deleted)]
    [InlineData(RowStates.Ignore)]
    public void An_excluded_row_that_has_been_acted_on_is_not_offered_back(string state)
    {
        var existing = Row(One, verdict: RowVerdicts.Ignore, finalState: state);

        var merged = LedgerMerge.Into(new[] { existing }, new[] { Row(One) });

        Assert.Empty(merged.Excluded);
    }

    [Fact]
    public void A_row_nobody_excluded_is_not_offered_back()
    {
        var merged = LedgerMerge.Into(new[] { Row(One, verdict: RowVerdicts.Skip) },
            new[] { Row(One, verdict: RowVerdicts.Fix) });

        Assert.Empty(merged.Excluded);
    }

    [Fact]
    public void Taking_the_scans_verdict_undoes_an_exclusion()
    {
        var existing = Row(One, verdict: RowVerdicts.Ignore);
        var merged = LedgerMerge.Into(new[] { existing }, new[] { Row(One, verdict: RowVerdicts.Fix) });

        LedgerMerge.TakeScanVerdicts(merged.Excluded);

        Assert.Equal(RowVerdict.Fix, existing.Verdict2());
    }

    /// <summary>
    /// The other way back: the operator wants to decide each row themselves, so they are put
    /// where a person has to look and no run will act on them in the meantime.
    /// </summary>
    [Fact]
    public void Marking_for_review_hands_the_rows_back_to_the_operator()
    {
        var existing = Row(One, verdict: RowVerdicts.Ignore);
        var merged = LedgerMerge.Into(new[] { existing }, new[] { Row(One, verdict: RowVerdicts.Fix) });

        LedgerMerge.MarkForReview(merged.Excluded);

        Assert.Equal(RowVerdict.Review, existing.Verdict2());
    }

    /// <summary>
    /// One ledger row is one mocd_document, but a correction writes to the mocd_documentfile
    /// record, and several documents can share one of those. So a row nothing has touched can
    /// still find its file somewhere new — and its old path is then the only record of where
    /// the file was, which is what the delete step needs.
    /// </summary>
    [Fact]
    public void A_row_whose_file_moved_keeps_the_path_it_recorded()
    {
        var existing = Row(One);
        var scanned = Row(One, oldPath: @"DigitalServices\cd97bf8d\20260916\new.jpg");
        scanned.OldCategory = "cd97bf8d";

        var merged = LedgerMerge.Into(new[] { existing }, new[] { scanned });

        Assert.Equal(@"DigitalServices\docType\20250509\a.jpg", existing.OldFilePath);
        Assert.Equal("docType", existing.OldCategory);
        Assert.Single(merged.Moved);
        Assert.Equal(@"DigitalServices\cd97bf8d\20260916\new.jpg", merged.Moved[0].NowAt);
    }

    /// <summary>The usual explanation, and the one the operator needs by name.</summary>
    [Fact]
    public void A_file_moved_by_a_sibling_row_names_the_row_that_did_it()
    {
        var file = Guid.Parse("a3f1b2c4-0000-0000-0000-0000000000ff");

        var untouched = Row(One);
        untouched.Row = 99;
        untouched.DocFileId = file;

        var sibling = Row(Two, finalState: RowStates.Corrected);
        sibling.Row = 408;
        sibling.DocFileId = file;

        var scanned = Row(One, oldPath: @"DigitalServices\cd97bf8d\20260916\new.jpg");

        var merged = LedgerMerge.Into(new[] { untouched, sibling }, new[] { scanned });

        Assert.Equal(408, Assert.Single(merged.Moved).CorrectedBy?.Row);
    }

    [Fact]
    public void A_file_moved_by_nobody_in_the_ledger_says_so()
    {
        var existing = Row(One);
        existing.DocFileId = Guid.Parse("a3f1b2c4-0000-0000-0000-0000000000ff");

        var merged = LedgerMerge.Into(new[] { existing },
            new[] { Row(One, oldPath: @"DigitalServices\cd97bf8d\20260916\new.jpg") });

        Assert.Null(Assert.Single(merged.Moved).CorrectedBy);
    }

    [Fact]
    public void A_row_whose_file_is_where_it_was_is_not_reported_as_moved()
    {
        var merged = LedgerMerge.Into(new[] { Row(One) }, new[] { Row(One) });

        Assert.Empty(merged.Moved);
    }

    /// <summary>
    /// "CRM says skip" answers nothing on its own — skip covers already-correct, no-file-path
    /// and no-catalogue alike. The reason travels with the disagreement so the operator can see
    /// which one they are being asked about.
    /// </summary>
    [Fact]
    public void A_disagreement_carries_the_scans_own_reason()
    {
        var scanned = Row(One, verdict: RowVerdicts.Skip);
        scanned.ReasonOfBug = "Path already correct — matches the document type's catalogue.";

        var merged = LedgerMerge.Into(new[] { Row(One, verdict: RowVerdicts.Fix) }, new[] { scanned });

        Assert.Equal("Path already correct — matches the document type's catalogue.",
            Assert.Single(merged.Disagreements).ScanReason);
    }
}
