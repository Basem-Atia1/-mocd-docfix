using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class DeleteOldFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-del-" + Guid.NewGuid());

    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");
    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

    private const string OldPath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260915\b2c3.jpg";

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakePrompts _prompts = new();
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;

    public DeleteOldFilesTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new ChangeJournal(Path.Combine(_dir, "changes-dev.jsonl"));
        _ledger = new LedgerStore(Path.Combine(_dir, "repair-dev.csv"));

        _files.Files[OldPath] = ("AQID", "9f86d081");

        // CRM says the record now holds the new path — the correction landed.
        _read.RawRecords[$"mocd_documentfiles:{Record}"] =
            $$"""{"mocd_documentfileid":"{{Record}}","mocd_filepath":"{{NewPath.Replace("\\", "\\\\")}}"}""";
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static LedgerRow Row() => new()
    {
        Row = 12,
        DocId = Doc,
        DocFileId = Record,
        DocFileName = "cert.jpg",
        OldFilePath = OldPath,
        NewFilePath = NewPath,
        Verdict = RowVerdicts.Fix,
        FinalState = RowStates.Text(RowState.Corrected)
    };

    private DeleteOldFiles Subject() => new(_files, _read, _journal, _ledger, _prompts);

    private Task<DeleteSummary> Run(params LedgerRow[] rows)
    {
        _prompts.YesNoResponse = true;
        return Subject().RunAsync(rows, isProduction: false, CancellationToken.None);
    }

    [Fact]
    public async Task An_eligible_row_has_its_old_file_removed_and_is_marked_deleted()
    {
        var row = Row();

        var summary = await Run(row);

        Assert.Equal(1, summary.Deleted);
        Assert.Contains(OldPath, _files.Deleted);
        Assert.Equal(RowState.Deleted, row.State());
    }

    /// <summary>
    /// The correction updated the record rather than replacing it, so there is no orphaned CRM
    /// row to remove. This step is given no write client at all — proven by construction.
    /// </summary>
    [Fact]
    public async Task Only_the_file_goes_and_only_once()
    {
        await Run(Row());

        Assert.Single(_files.Deleted);
    }

    [Theory]
    [InlineData("")]
    [InlineData(RowStates.Deleted)]
    [InlineData(RowStates.Ignore)]
    [InlineData(RowStates.Failed)]
    [InlineData("nearly done")]
    public async Task Only_the_one_final_state_is_eligible(string state)
    {
        var row = Row();
        row.FinalState = state;

        var summary = await Run(row);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(_files.Deleted);
    }

    /// <summary>
    /// The ledger's belief is not evidence. CRM is asked, live, whether the record really does
    /// hold the new path — deleting the old file otherwise leaves a document that opens nothing.
    /// </summary>
    [Fact]
    public async Task A_record_that_still_names_the_old_path_is_refused()
    {
        _read.RawRecords[$"mocd_documentfiles:{Record}"] =
            $$"""{"mocd_documentfileid":"{{Record}}","mocd_filepath":"{{OldPath.Replace("\\", "\\\\")}}"}""";

        var summary = await Run(Row());

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
        Assert.Contains(summary.Reasons, r => r.Contains("still", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One file referenced by two records is rare and real. Deleting it breaks the other.</summary>
    [Fact]
    public async Task A_file_another_record_also_points_at_is_refused()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { Record, Guid.NewGuid() };

        var summary = await Run(Row());

        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
        Assert.Contains(summary.Reasons, r => r.Contains("another", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The row's own record referencing it is expected, not a reason to refuse.</summary>
    [Fact]
    public async Task The_rows_own_record_referencing_the_old_path_is_not_a_second_reference()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { Record };

        var summary = await Run(Row());

        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task A_delete_the_server_refuses_leaves_the_row_alone()
    {
        _files.DeleteRefusal = "423 Locked";
        var row = Row();

        var summary = await Run(row);

        Assert.Equal(1, summary.Refused);
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Contains(summary.Reasons, r => r.Contains("423"));
    }

    [Fact]
    public async Task The_deletion_is_journalled()
    {
        await Run(Row());

        var entry = Assert.Single(_journal.Read());
        Assert.Equal(ChangeActions.Deleted, entry.Action);
        Assert.Equal(OldPath, entry.Old!.Path);
    }

    [Fact]
    public async Task Nothing_is_deleted_until_the_operator_agrees()
    {
        _prompts.YesNoResponse = false;

        var summary = await Subject().RunAsync(new[] { Row() }, isProduction: false, CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Production_is_confirmed_by_typing_the_word()
    {
        _prompts.YesNoResponse = true;
        _prompts.TypedWordResponse = "no";

        var summary = await Subject().RunAsync(new[] { Row() }, isProduction: true, CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task The_ledger_is_written_after_each_deletion()
    {
        await Run(Row());

        Assert.Equal(RowState.Deleted, _ledger.Read()[0].State());
    }
}
