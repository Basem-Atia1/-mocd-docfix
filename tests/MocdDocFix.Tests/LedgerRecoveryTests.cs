using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The gap this closes is real and was hit in anger: a document was corrected, the ledger write
/// found the workbook open in Excel, and the run died. The work stood; nothing recorded it.
/// </summary>
public class LedgerRecoveryTests
{
    private static readonly Guid Doc = Guid.Parse("8b7c5517-6018-f111-b119-005056010908");
    private static readonly Guid Record = Guid.Parse("b454acc3-1641-49c1-a8f4-8e3004f9a48f");

    private const string OldPath = @"DigitalServices\Document\20260305\b454acc3.png";
    private const string NewPath = @"DigitalServices\cd97bf8d\20260915\1181cb78.png";

    private static LedgerRow Row(string verdict = RowVerdicts.Fix, string finalState = "") => new()
    {
        Row = 1,
        DocId = Doc,
        DocFileId = Record,
        DocName = "A Copy of Board of Director's Decision",
        DocFileName = "cert.png",
        OldFilePath = OldPath,
        Verdict = verdict,
        FinalState = finalState
    };

    private static ChangeEntry Entry(string action, DateTimeOffset? at = null) => new(
        at ?? DateTimeOffset.UtcNow, Doc, Record, action,
        new RecordValues(OldPath, "Document", "9f86d081", null, null),
        new RecordValues(NewPath, "cd97bf8d", "5e884898", null, null));

    [Fact]
    public void A_correction_the_ledger_never_recorded_is_filled_in()
    {
        var row = Row();

        var recovered = LedgerRecovery.Apply(new[] { row }, new[] { Entry(ChangeActions.Corrected) });

        Assert.Equal(1, recovered.Rows);
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Equal(NewPath, row.NewFilePath);
        Assert.Contains("journal", row.Notes);
        Assert.Contains(recovered.Notes, n => n.Contains("never recorded"));
    }

    /// <summary>
    /// The danger it removes: without this the row still reads as work to do, so the next run
    /// uploads the same file again and orphans the copy it made last time.
    /// </summary>
    [Fact]
    public void After_recovery_the_row_is_no_longer_something_a_repair_run_would_redo()
    {
        var row = Row();

        LedgerRecovery.Apply(new[] { row }, new[] { Entry(ChangeActions.Corrected) });

        Assert.True(row.State() is RowState.Corrected or RowState.Deleted);
    }

    [Fact]
    public void A_row_the_ledger_already_recorded_is_left_alone()
    {
        var row = Row(finalState: RowStates.Text(RowState.Corrected));
        row.Notes = "untouched";

        var recovered = LedgerRecovery.Apply(new[] { row }, new[] { Entry(ChangeActions.Corrected) });

        Assert.Equal(0, recovered.Rows);
        Assert.Equal("untouched", row.Notes);
    }

    /// <summary>A failed row is a gap too — the failure was recorded, the success that followed was not.</summary>
    [Fact]
    public void A_row_left_marked_failed_is_corrected_by_the_journal()
    {
        var row = Row(finalState: RowStates.Text(RowState.Failed));
        row.Error = "could not write the ledger";

        LedgerRecovery.Apply(new[] { row }, new[] { Entry(ChangeActions.Corrected) });

        Assert.Equal(RowState.Corrected, row.State());
        Assert.Equal(string.Empty, row.Error);
    }

    [Fact]
    public void A_deletion_the_ledger_never_recorded_is_filled_in()
    {
        var row = Row(finalState: RowStates.Text(RowState.Corrected));

        LedgerRecovery.Apply(new[] { row }, new[] { Entry(ChangeActions.Deleted) });

        Assert.Equal(RowState.Deleted, row.State());
    }

    [Fact]
    public void A_revert_the_ledger_never_recorded_puts_the_row_back_to_fix()
    {
        var row = Row(finalState: RowStates.Text(RowState.Corrected));
        row.NewFilePath = NewPath;

        LedgerRecovery.Apply(new[] { row }, new[] { Entry(ChangeActions.Reverted) });

        Assert.Equal(RowVerdict.Fix, row.Verdict2());
        Assert.Equal(RowState.NotStarted, row.State());
        Assert.Equal(string.Empty, row.NewFilePath);
        Assert.Contains(NewPath, row.SupersededPaths);
    }

    /// <summary>
    /// Corrected, reverted, corrected again. Replaying out of order would leave the row saying
    /// the opposite of what is true.
    /// </summary>
    [Fact]
    public void Entries_are_replayed_in_the_order_they_happened()
    {
        var row = Row();
        var start = DateTimeOffset.UtcNow.AddHours(-3);

        LedgerRecovery.Apply(new[] { row }, new[]
        {
            Entry(ChangeActions.Reverted, start.AddHours(1)),
            Entry(ChangeActions.Corrected, start.AddHours(2)),
            Entry(ChangeActions.Corrected, start)
        });

        Assert.Equal(RowState.Corrected, row.State());
    }

    [Fact]
    public void A_journal_entry_for_a_document_not_in_the_ledger_is_ignored()
    {
        var row = Row();
        var stranger = new ChangeEntry(DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(),
            ChangeActions.Corrected, null, new RecordValues(NewPath, null, null, null, null));

        var recovered = LedgerRecovery.Apply(new[] { row }, new[] { stranger });

        Assert.Equal(0, recovered.Rows);
        Assert.Equal(RowState.NotStarted, row.State());
    }

    [Fact]
    public void An_empty_journal_changes_nothing()
    {
        var row = Row();

        Assert.Equal(0, LedgerRecovery.Apply(new[] { row }, Array.Empty<ChangeEntry>()).Rows);
        Assert.Equal(RowState.NotStarted, row.State());
    }

    /// <summary>Two entries for one document count as one row put right, not two.</summary>
    [Fact]
    public void A_row_mended_twice_is_counted_once()
    {
        var row = Row();
        var start = DateTimeOffset.UtcNow.AddHours(-1);

        var recovered = LedgerRecovery.Apply(new[] { row }, new[]
        {
            Entry(ChangeActions.Corrected, start),
            Entry(ChangeActions.Deleted, start.AddMinutes(10))
        });

        Assert.Equal(1, recovered.Rows);
        Assert.Equal(RowState.Deleted, row.State());
    }
}
