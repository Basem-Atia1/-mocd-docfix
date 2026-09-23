using MocdDocFix.Commands;
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Rows whose document CRM no longer returns. Nothing will ever act on one again, so a clean one
/// goes — but deleting the document in CRM does not remove the files, and a row that records
/// work is the only thing that knows where they are.
/// </summary>
public class DropDeletedDocumentsTests
{
    private static readonly Guid Ours = Guid.Parse("3ff27d73-653e-f111-b119-005056010908");
    private static readonly Guid Theirs = Guid.Parse("9ee8941a-8870-f111-b119-005056010908");

    private static readonly IReadOnlySet<string> InScope =
        new HashSet<string>(new[] { Ours.ToString() }, StringComparer.OrdinalIgnoreCase);

    private static LedgerRow Row(Guid? catalogue = null, string finalState = "") => new()
    {
        Row = 1,
        DocId = Guid.NewGuid(),
        DocFileName = "a.pdf",
        OldFilePath = @"DigitalServices\Document\20260305\a.pdf",
        Verdict = RowVerdicts.Fix,
        FinalState = finalState,
        ServiceCatalogueId = (catalogue ?? Ours).ToString()
    };

    [Fact]
    public void A_row_whose_document_is_gone_and_owed_nothing_is_taken_out()
    {
        var gone = Row();

        var result = DropDeletedDocuments.From(new[] { gone }, new[] { gone }, InScope);

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Kept);
    }

    [Fact]
    public void A_row_the_scan_saw_is_untouched()
    {
        var here = Row();

        var result = DropDeletedDocuments.From(
            new[] { here }, Array.Empty<LedgerRow>(), InScope);

        Assert.Single(result.Rows);
        Assert.Equal(0, result.Removed);
    }

    /// <summary>
    /// Out of scope is not gone. Its service simply was not read this run — it is not missing,
    /// it was not looked for, and narrowing the scope would otherwise empty the other file.
    /// </summary>
    [Fact]
    public void A_row_of_a_service_this_run_did_not_scan_is_left_alone()
    {
        var elsewhere = Row(Theirs);

        var result = DropDeletedDocuments.From(
            new[] { elsewhere }, new[] { elsewhere }, InScope);

        Assert.Single(result.Rows);
        Assert.Equal(0, result.Removed);
        Assert.Equal(string.Empty, result.Rows[0].Notes);
    }

    /// <summary>
    /// The one that must not be lost. Its old file is still on the server waiting for the delete
    /// step, and the row is the only record of where it is.
    /// </summary>
    [Fact]
    public void A_row_awaiting_its_delete_is_kept_and_marked()
    {
        var owed = Row(finalState: RowStates.Corrected);

        var result = DropDeletedDocuments.From(new[] { owed }, new[] { owed }, InScope);

        Assert.Single(result.Rows);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Kept);
        Assert.Contains(DropDeletedDocuments.Mark, owed.Notes);
    }

    /// <summary>A copy left on the file server is an orphan the row is the only pointer to.</summary>
    [Fact]
    public void A_row_carrying_a_superseded_path_is_kept_and_marked()
    {
        var orphan = Row();
        orphan.SupersededPaths = @"DigitalServices\cd97bf8d\20260921\4c240b4b.png";

        var result = DropDeletedDocuments.From(new[] { orphan }, new[] { orphan }, InScope);

        Assert.Single(result.Rows);
        Assert.Contains(DropDeletedDocuments.Mark, orphan.Notes);
    }

    /// <summary>
    /// This runs on every scan. A mark added each time would fill the cell with the same
    /// sentence, which is how the journal recovery note grew to 2,604 characters.
    /// </summary>
    [Fact]
    public void Scanning_again_and_again_marks_the_row_once()
    {
        var owed = Row(finalState: RowStates.Corrected);

        DropDeletedDocuments.From(new[] { owed }, new[] { owed }, InScope);
        var afterFirst = owed.Notes;

        for (var i = 0; i < 5; i++)
            DropDeletedDocuments.From(new[] { owed }, new[] { owed }, InScope);

        Assert.Equal(afterFirst, owed.Notes);
    }

    /// <summary>A blank service catalogue counts as in scope, or it would be kept for ever.</summary>
    [Fact]
    public void A_row_with_no_service_catalogue_is_taken_out_like_any_other()
    {
        var gone = Row();
        gone.ServiceCatalogueId = string.Empty;

        var result = DropDeletedDocuments.From(new[] { gone }, new[] { gone }, InScope);

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void A_scan_that_saw_everything_changes_nothing()
    {
        var rows = new[] { Row(), Row() };

        var result = DropDeletedDocuments.From(rows, Array.Empty<LedgerRow>(), InScope);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Kept);
    }
}
