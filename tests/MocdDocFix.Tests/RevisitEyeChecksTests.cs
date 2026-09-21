using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Rows a previous run stopped at the compare question. They were invisible: the row still said
/// fix, so the next run walked up to the same question and asked it again, with no reminder that
/// it had been asked, what was answered, or whether the two files differ at all.
/// </summary>
public class RevisitEyeChecksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-eye-" + Guid.NewGuid());

    private static readonly Guid Doc = Guid.Parse("ebb9210c-a6b5-f111-b11a-005056010908");
    private static readonly Guid NewId = Guid.Parse("1cb3f82f-b1b8-4325-86f8-b271f505969f");

    private static readonly string NewPath =
        $@"DigitalServices\3cb8d7fa-7bb2-f111-b119-005056010908\20260922\{NewId}.pdf";

    private readonly BackupStore _backups;
    private readonly FakePrompts _prompts = new();

    public RevisitEyeChecksTests()
    {
        Directory.CreateDirectory(_dir);
        _backups = new BackupStore(Path.Combine(_dir, "backup"));
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static LedgerRow Row(string mark) => new()
    {
        Row = 12,
        DocId = Doc,
        DocFileId = Guid.Parse("6c01a923-a6b5-f111-b11a-005056010908"),
        DocFileName = "board-decision.pdf",
        OldFilePath = @"DigitalServices\Document\20260305\6c01a923.pdf",
        Verdict = RowVerdicts.Fix,
        SupersededPaths = NewPath,
        Notes = $"not corrected 2026-09-21 23:19 {mark}"
    };

    /// <summary>Puts both copies in the backup folder, the same bytes or not.</summary>
    private void Backup(byte[] old, byte[] fresh)
    {
        _backups.Save(Doc, Guid.NewGuid(), ".pdf", old, "board-decision.pdf");
        _backups.SaveNew(Doc, NewId, ".pdf", fresh);
    }

    private RevisitEyeChecks Subject() => new(_backups, _prompts);

    // ---- what it finds ----

    [Fact]
    public void A_skipped_row_is_found_and_its_copies_compared()
    {
        Backup(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 });

        var found = Assert.Single(Subject().Find(new[] { Row(RepairOneRow.Skipped) }));

        Assert.True(found.Skipped);
        Assert.True(found.Identical);
    }

    [Fact]
    public void A_row_whose_copies_differ_says_so()
    {
        Backup(new byte[] { 1, 2, 3 }, new byte[] { 9, 9, 9 });

        var found = Assert.Single(Subject().Find(new[] { Row(RepairOneRow.NotMatched) }));

        Assert.False(found.Skipped);
        Assert.False(found.Identical);
    }

    /// <summary>
    /// Not knowing is a third answer. Saying "not identical" would send somebody to redo work
    /// over a file that was never there to compare.
    /// </summary>
    [Fact]
    public void A_row_with_no_backup_to_compare_is_neither_identical_nor_not()
    {
        Assert.Null(Assert.Single(Subject().Find(new[] { Row(RepairOneRow.Skipped) })).Identical);
    }

    [Fact]
    public void A_row_with_no_mark_is_not_offered()
    {
        var row = Row(RepairOneRow.Skipped);
        row.Notes = "nothing in particular";

        Assert.Empty(Subject().Find(new[] { row }));
    }

    /// <summary>A verdict somebody typed since is their answer, not a question to ask again.</summary>
    [Fact]
    public void A_row_closed_by_hand_since_is_not_offered()
    {
        var row = Row(RepairOneRow.Skipped);
        row.Verdict = RowVerdicts.Ignore;

        Assert.Empty(Subject().Find(new[] { row }));
    }

    // ---- what it asks, and what the answers do ----

    [Fact]
    public void Identical_copies_offer_to_finish_it_without_asking_again()
    {
        Backup(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 });
        _prompts.ReadLineResponse = "1";

        var row = Row(RepairOneRow.Skipped);
        var result = Subject().Ask(new[] { row });

        Assert.Contains(Doc, result.FinishWithoutAsking);
        Assert.DoesNotContain(RepairOneRow.Skipped, row.Notes);
    }

    [Fact]
    public void Working_it_again_leaves_the_row_at_fix_and_asks_nothing_of_the_run()
    {
        Backup(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 });
        _prompts.ReadLineResponse = "2";

        var row = Row(RepairOneRow.Skipped);
        var result = Subject().Ask(new[] { row });

        Assert.Empty(result.FinishWithoutAsking);
        Assert.Equal(1, result.Again);
        Assert.Equal(RowVerdict.Fix, row.Verdict2());
    }

    /// <summary>
    /// A pair that differ is not something to wave through, so finishing it is not on offer.
    /// </summary>
    [Fact]
    public void Copies_that_differ_are_never_offered_a_finish_without_looking()
    {
        Backup(new byte[] { 1, 2, 3 }, new byte[] { 9, 9, 9 });
        _prompts.ReadLineResponse = "1";

        var result = Subject().Ask(new[] { Row(RepairOneRow.NotMatched) });

        Assert.Empty(result.FinishWithoutAsking);
        Assert.Equal(1, result.Again);
    }

    [Fact]
    public void Copies_that_differ_can_be_kept_for_review_instead()
    {
        Backup(new byte[] { 1, 2, 3 }, new byte[] { 9, 9, 9 });
        _prompts.ReadLineResponse = "2";

        var row = Row(RepairOneRow.NotMatched);
        var result = Subject().Ask(new[] { row });

        Assert.Equal(1, result.Reviewed);
        Assert.Equal(RowVerdict.Review, row.Verdict2());
        Assert.DoesNotContain(RepairOneRow.NotMatched, row.Notes);
    }

    /// <summary>The mark is what makes the next run offer it; answering is what clears it.</summary>
    [Fact]
    public void A_row_left_undecided_keeps_its_mark_for_the_next_run()
    {
        Backup(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 });
        _prompts.ReadLineResponse = "q";

        var row = Row(RepairOneRow.Skipped);
        Subject().Ask(new[] { row });

        Assert.Contains(RepairOneRow.Skipped, row.Notes);
    }

    [Fact]
    public void Nothing_is_asked_when_no_row_carries_a_mark()
    {
        var row = Row(RepairOneRow.Skipped);
        row.Notes = string.Empty;

        var result = Subject().Ask(new[] { row });

        Assert.Empty(result.FinishWithoutAsking);
        Assert.Empty(_prompts.Questions);
    }

    /// <summary>The line says which row, what was answered, and whether the bytes differ.</summary>
    [Fact]
    public void The_line_says_what_was_answered_and_whether_the_bytes_differ()
    {
        Backup(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 });
        _prompts.ReadLineResponse = "1";

        Subject().Ask(new[] { Row(RepairOneRow.Skipped) });

        // Joined and squeezed: a paragraph is wrapped across several lines on the way to the
        // screen, and each one is indented, so the runs of spaces are the wrapping and not text.
        var said = System.Text.RegularExpressions.Regex.Replace(
            string.Join(" ", _prompts.Messages), @"\s+", " ");

        Assert.Contains("row 12", said);
        Assert.Contains("skipped at the compare question", said);
        Assert.Contains("identical in bytes", said);
    }
}
