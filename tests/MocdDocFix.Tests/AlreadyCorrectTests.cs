using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Before a repair run uploads anything it asks CRM which rows are already right. Getting this
/// wrong costs a second copy of a file on the server, so every branch is pinned here.
/// </summary>
public class AlreadyCorrectTests
{
    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000002");
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");
    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

    private const string OldPath = @"DigitalServices\boardDecision\20250509\a3f1.jpg";
    private static readonly string RightPath = $@"DigitalServices\{Correct}\20260916\b2c3.jpg";

    private readonly FakeCrmReadClient _read = new();
    private readonly FakeFileServiceClient _files = new();

    private static LedgerRow Row(Guid id, string oldPath = OldPath, string finalState = "") => new()
    {
        Row = 99,
        DocId = id,
        DocFileId = Record,
        DocFileName = "a.png",
        OldFilePath = oldPath,
        CorrectServiceCatalogueId = Correct.ToString(),
        Verdict = RowVerdicts.Fix,
        FinalState = finalState
    };

    private void CrmHolds(string path) =>
        _read.RawRecords[$"mocd_documentfiles:{Record}"] =
            $$"""{"mocd_documentfileid":"{{Record}}","mocd_filepath":"{{path.Replace("\\", "\\\\")}}"}""";

    private Task<AlreadyCorrectScan> Scan(params LedgerRow[] rows) =>
        new AlreadyCorrect(_read, _files).FindAsync(rows, CancellationToken.None);

    [Fact]
    public async Task A_row_crm_still_files_wrongly_is_left_for_the_run()
    {
        CrmHolds(OldPath);

        var scan = await Scan(Row(Doc));

        Assert.Empty(scan.Settled);
        Assert.Empty(scan.MissingFile);
    }

    [Fact]
    public async Task A_row_whose_path_never_changed_becomes_a_skip()
    {
        CrmHolds(RightPath);
        _files.Files[RightPath] = ("AQID", "9f86d081");

        var row = Row(Doc, oldPath: RightPath);
        var scan = await Scan(row);

        Assert.Equal(SettleAs.AlwaysRight, Assert.Single(scan.Settled).As);

        AlreadyCorrect.Apply(scan.Settled);

        Assert.Equal(RowVerdict.Skip, row.Verdict2());
        Assert.Equal(RowState.NotStarted, row.State());
    }

    [Fact]
    public async Task A_row_corrected_by_something_else_keeps_the_delete_outstanding()
    {
        CrmHolds(RightPath);
        _files.Files[RightPath] = ("AQID", "9f86d081");
        _files.Files[OldPath] = ("AQID", "9f86d081");

        var row = Row(Doc);
        var scan = await Scan(row);

        Assert.Equal(SettleAs.PendingDelete, Assert.Single(scan.Settled).As);

        AlreadyCorrect.Apply(scan.Settled);

        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Equal(RightPath, row.NewFilePath);
    }

    [Fact]
    public async Task A_row_whose_old_file_has_gone_is_finished()
    {
        CrmHolds(RightPath);
        _files.Files[RightPath] = ("AQID", "9f86d081");

        var row = Row(Doc);
        var scan = await Scan(row);

        AlreadyCorrect.Apply(scan.Settled);

        Assert.Equal(SettleAs.Finished, scan.Settled[0].As);
        Assert.Equal(RowState.Deleted, row.State());
    }

    /// <summary>
    /// Two documents, one mocd_documentfile. The sibling owns the old file and its deletion, so
    /// this row must finish without a final state — a second row saying "pending the delete"
    /// would queue the same physical file twice.
    /// </summary>
    [Fact]
    public async Task A_row_corrected_by_a_sibling_finishes_without_queueing_a_delete()
    {
        CrmHolds(RightPath);
        _files.Files[RightPath] = ("AQID", "9f86d081");
        _files.Files[OldPath] = ("AQID", "9f86d081");

        var row = Row(Doc);
        var sibling = Row(Other, finalState: RowStates.Text(RowState.Corrected));
        sibling.Row = 408;

        var scan = await Scan(row, sibling);
        var settled = Assert.Single(scan.Settled);

        Assert.Equal(SettleAs.BySibling, settled.As);
        Assert.Equal(408, settled.Sibling!.Row);

        AlreadyCorrect.Apply(scan.Settled);

        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(string.Empty, row.FinalState);
        Assert.Contains("row 408", row.Notes);
    }

    /// <summary>
    /// The catalogue in the path is right and the file is not there. The record is still broken,
    /// so the row keeps its verdict and the run puts the file back.
    /// </summary>
    [Fact]
    public async Task A_row_filed_right_with_no_file_on_the_server_is_not_settled()
    {
        CrmHolds(RightPath);

        var row = Row(Doc);
        var scan = await Scan(row);

        Assert.Empty(scan.Settled);
        Assert.Equal(RightPath, Assert.Single(scan.MissingFile).NowAt);
        Assert.Equal(RowVerdict.Fix, row.Verdict2());
    }

    [Fact]
    public async Task Rows_that_are_not_marked_fix_are_never_touched()
    {
        CrmHolds(RightPath);
        _files.Files[RightPath] = ("AQID", "9f86d081");

        var row = Row(Doc);
        row.Verdict = RowVerdicts.Skip;

        var scan = await Scan(row);

        Assert.Empty(scan.Settled);
        Assert.Empty(scan.MissingFile);
    }
}
