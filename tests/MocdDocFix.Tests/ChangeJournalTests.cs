using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class ChangeJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-journal-" + Guid.NewGuid());

    public ChangeJournalTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string JournalPath => Path.Combine(_dir, "changes-dev.jsonl");

    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

    private static ChangeEntry Corrected() => new(
        DateTimeOffset.UtcNow, Doc, Record, ChangeActions.Corrected,
        new RecordValues(@"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg",
            "docTypeCatalogue", "9f86d081", "a3f1.jpg", null),
        new RecordValues(@"DigitalServices\7c20a1f4\20260915\b2c3.jpg",
            "7c20a1f4", "5e884898", "b2c3.jpg", null));

    [Fact]
    public void A_correction_round_trips()
    {
        var journal = new ChangeJournal(JournalPath);
        journal.Append(Corrected());

        var back = journal.Read();

        Assert.Single(back);
        Assert.Equal(ChangeActions.Corrected, back[0].Action);
        Assert.Equal(Doc, back[0].Doc);
        Assert.Equal("docTypeCatalogue", back[0].Old!.Category);
        Assert.Equal(@"DigitalServices\7c20a1f4\20260915\b2c3.jpg", back[0].New!.Path);
        Assert.Null(back[0].Old!.FileId);
    }

    [Fact]
    public void Appending_never_rewrites_what_is_already_there()
    {
        var journal = new ChangeJournal(JournalPath);
        journal.Append(Corrected());
        journal.Append(Corrected() with { Action = ChangeActions.Reverted });
        journal.Append(Corrected() with { Action = ChangeActions.Deleted });

        Assert.Equal(
            new[] { ChangeActions.Corrected, ChangeActions.Reverted, ChangeActions.Deleted },
            journal.Read().Select(e => e.Action));
    }

    /// <summary>
    /// The reason this file exists rather than trusting the CSV: a process killed mid-write
    /// leaves a half line, and everything before it must still be readable.
    /// </summary>
    [Fact]
    public void A_torn_last_line_costs_that_line_and_nothing_else()
    {
        var journal = new ChangeJournal(JournalPath);
        journal.Append(Corrected());
        journal.Append(Corrected() with { Action = ChangeActions.Reverted });
        File.AppendAllText(JournalPath, "{\"At\":\"2026-09-15T10:32:0");

        var back = journal.Read();

        Assert.Equal(2, back.Count);
        Assert.Equal(ChangeActions.Reverted, back[1].Action);
    }

    [Fact]
    public void Reading_a_journal_that_is_not_there_gives_nothing() =>
        Assert.Empty(new ChangeJournal(JournalPath).Read());

    [Fact]
    public void The_error_log_says_which_row_which_step_and_why()
    {
        var log = new ErrorLog(Path.Combine(_dir, "errors-dev.txt"));

        log.Append(14, 431,
            new LedgerRow { DocId = Doc, DocName = "Good Conduct Certificate", DocFileName = "id.png" },
            "upload", "POST /api/File/Upload -> 413 Payload Too Large");

        var text = File.ReadAllText(log.Path);

        Assert.Contains("[ 14/431 ]", text);
        Assert.Contains("id.png", text);
        Assert.Contains(Doc.ToString(), text);
        Assert.Contains("step: upload", text);
        Assert.Contains("413", text);
    }

    [Fact]
    public void The_error_log_keeps_every_failure_rather_than_the_last()
    {
        var log = new ErrorLog(Path.Combine(_dir, "errors-dev.txt"));
        var row = new LedgerRow { DocId = Doc, DocFileName = "id.png" };

        log.Append(1, 2, row, "upload", "first");
        log.Append(2, 2, row, "record update", "second");

        var text = File.ReadAllText(log.Path);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }
}
