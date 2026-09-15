using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class CheckItAllTests
{
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");
    private const string OldPath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg";
    private const string NewPath = @"DigitalServices\7c20a1f4\20260915\b2c3.jpg";

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();

    private CheckItAll Subject() => new(_files, _read);

    private static LedgerRow Row(string finalState) => new()
    {
        Row = 12,
        DocId = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001"),
        DocFileId = Record,
        DocFileName = "cert.jpg",
        OldFilePath = OldPath,
        NewFilePath = NewPath,
        FinalState = finalState
    };

    [Fact]
    public async Task A_deleted_row_whose_old_file_is_really_gone_is_as_expected()
    {
        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Deleted) }, CancellationToken.None);

        Assert.Equal(1, summary.Checked);
        Assert.Equal(1, summary.AsExpected);
        Assert.Empty(summary.Problems);
    }

    /// <summary>The whole point of this mode: a file the ledger says is gone, and is not.</summary>
    [Fact]
    public async Task A_deleted_row_whose_old_file_is_still_there_is_reported()
    {
        _files.Files[OldPath] = ("AQID", "9f86d081");

        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Deleted) }, CancellationToken.None);

        Assert.Equal(1, summary.NotAsExpected);
        Assert.Contains(summary.Problems, p =>
            p.Contains("still on the file server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_deleted_row_something_in_crm_still_refers_to_is_reported()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { Guid.NewGuid() };

        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Deleted) }, CancellationToken.None);

        Assert.Equal(1, summary.NotAsExpected);
        Assert.Contains(summary.Problems, p =>
            p.Contains("still refer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_corrected_row_whose_old_file_is_still_waiting_is_as_expected()
    {
        _files.Files[OldPath] = ("AQID", "9f86d081");

        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Corrected) }, CancellationToken.None);

        Assert.Equal(1, summary.AsExpected);
        Assert.Empty(summary.Problems);
    }

    /// <summary>
    /// A row awaiting deletion whose old file has already gone means somebody deleted it outside
    /// this tool — worth knowing, because Redo can no longer help that row.
    /// </summary>
    [Fact]
    public async Task A_corrected_row_whose_old_file_has_vanished_is_reported()
    {
        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Corrected) }, CancellationToken.None);

        Assert.Equal(1, summary.NotAsExpected);
        Assert.Contains(summary.Problems, p =>
            p.Contains("already gone", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData(RowStates.Ignore)]
    [InlineData(RowStates.Failed)]
    public async Task Rows_that_were_never_corrected_are_not_checked(string state)
    {
        var summary = await Subject().RunAsync(new[] { Row(state) }, CancellationToken.None);

        Assert.Equal(0, summary.Checked);
        Assert.Empty(summary.Problems);
    }

    [Fact]
    public async Task It_writes_nothing_anywhere()
    {
        _files.Files[OldPath] = ("AQID", "9f86d081");

        await Subject().RunAsync(
            new[] { Row(RowStates.Deleted), Row(RowStates.Corrected) }, CancellationToken.None);

        Assert.Empty(_files.Deleted);
        Assert.Empty(_files.Uploads);
    }

    [Fact]
    public async Task Counts_add_up_across_a_mixed_ledger()
    {
        _files.Files[OldPath] = ("AQID", "9f86d081");

        var summary = await Subject().RunAsync(new[]
        {
            Row(RowStates.Corrected),   // old file still there -- as expected
            Row(RowStates.Deleted),     // old file still there -- NOT as expected
            Row(RowStates.Ignore)       // not checked at all
        }, CancellationToken.None);

        Assert.Equal(2, summary.Checked);
        Assert.Equal(1, summary.AsExpected);
        Assert.Equal(1, summary.NotAsExpected);
    }
}
