using System.Text.Json;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

[Collection(LedgerCollection.Name)]
public class RedoRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-redo-" + Guid.NewGuid());

    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");
    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

    private const string OldPath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260915\b2c3.jpg";

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmWriteClient _write = new();
    private readonly FakePrompts _prompts = new();
    private readonly BackupStore _backups;
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;

    public RedoRunTests()
    {
        Directory.CreateDirectory(_dir);
        _backups = new BackupStore(Path.Combine(_dir, "backup"));
        _journal = new ChangeJournal(Path.Combine(_dir, "changes-dev.jsonl"));
        _ledger = new LedgerStore(Path.Combine(_dir, "repair-dev.xlsx"));

        // The old file is still on the server — which is what makes a revert possible at all.
        _files.Files[OldPath] = (Convert.ToBase64String(new byte[] { 1, 2, 3 }), "9f86d081");
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>Writes the crm.json the backup step would have written, holding the old record.</summary>
    private void Snapshot(string category = "docTypeCatalogue", string? fileId = null,
        string? fileName = "a3f1.jpg", string hash = "9f86d081")
    {
        var record = new Dictionary<string, object?>
        {
            ["mocd_documentfileid"] = Record.ToString(),
            ["mocd_filepath"] = OldPath,
            ["mocd_category"] = category,
            ["mocd_hash"] = hash
        };
        if (fileId is not null) record["mocd_fileid"] = fileId;
        if (fileName is not null) record["mocd_filename"] = fileName;

        _backups.SaveCrmImage(Record, Doc, OldPath, "dev",
            documentRaw: "{}",
            documentFileRaw: JsonSerializer.Serialize(record),
            annotationsRaw: """{"value":[]}""");
    }

    private LedgerRow Row() => new()
    {
        Row = 12,
        DocId = Doc,
        DocName = "Board of Director's Decision",
        DocFileId = Record,
        DocFileName = "cert.jpg",
        BackupPath = _backups.Folder(Doc, "cert.jpg").Root,
        OldFilePath = OldPath,
        NewFilePath = NewPath,
        CorrectServiceCatalogueId = Correct.ToString(),
        OldCategory = "docTypeCatalogue",
        OldHash = "9f86d081",
        OldFileName = "a3f1.jpg",
        OldFileId = string.Empty,
        Verdict = RowVerdicts.Redo,
        FinalState = RowStates.Text(RowState.Corrected),
        WayOfUpload = "portal"
    };

    private RedoRun Subject() => new(_files, _write, _backups, _journal, _ledger, _prompts);

    [Fact]
    public async Task The_record_goes_back_to_the_values_the_snapshot_holds()
    {
        Snapshot();
        var row = Row();

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Reverted);
        var update = Assert.Single(_write.UpdatedFiles);
        Assert.Equal(Record, update.RecordId);
        Assert.Equal(OldPath, update.FilePath);
        Assert.Equal("docTypeCatalogue", update.Category);
        Assert.Equal("9f86d081", update.Hash);
    }

    /// <summary>Back to the start: queued to be corrected again, with the history kept.</summary>
    [Fact]
    public async Task The_row_returns_to_fix_with_a_blank_final_state_and_a_note()
    {
        Snapshot();
        var row = Row();

        await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(RowVerdict.Fix, row.Verdict2());
        Assert.Equal(RowState.NotStarted, row.State());
        Assert.Equal(string.Empty, row.NewFilePath);
        Assert.Contains("reverted", row.Notes, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The abandoned copy is not deleted and not forgotten: nothing in CRM points at it, so its
    /// path is the only way anyone will ever find it again.
    /// </summary>
    [Fact]
    public async Task The_abandoned_copy_moves_into_superseded_paths_and_is_not_deleted()
    {
        Snapshot();
        var row = Row();

        await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Contains(NewPath, row.SupersededPaths);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Reverting_twice_keeps_both_abandoned_paths()
    {
        Snapshot();
        var row = Row();
        row.SupersededPaths = @"DigitalServices\x\20260901\first.jpg";

        await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Contains("first.jpg", row.SupersededPaths);
        Assert.Contains(NewPath, row.SupersededPaths);
    }

    /// <summary>
    /// The guard. Once the old file is off the server there is nothing to go back to, and
    /// repointing CRM at a path that no longer exists would break the document for good.
    /// </summary>
    [Fact]
    public async Task A_row_whose_old_file_is_gone_from_the_server_is_refused()
    {
        Snapshot();
        _files.Files.Remove(OldPath);
        var row = Row();

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Reverted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Contains(summary.Reasons, r => r.Contains("no longer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_row_whose_old_files_are_already_deleted_is_refused_without_asking_the_server()
    {
        Snapshot();
        var row = Row();
        row.FinalState = RowStates.Text(RowState.Deleted);

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Refused);
        Assert.Empty(_write.UpdatedFiles);
    }

    [Theory]
    [InlineData(RowVerdicts.Fix)]
    [InlineData(RowVerdicts.Review)]
    [InlineData(RowVerdicts.Done)]
    [InlineData(RowVerdicts.Ignore)]
    public async Task Only_redo_rows_are_acted_on(string verdict)
    {
        Snapshot();
        var row = Row();
        row.Verdict = verdict;

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Reverted);
        Assert.Equal(0, summary.Refused);
        Assert.Empty(_write.UpdatedFiles);
    }

    /// <summary>
    /// The snapshot is the record as it actually was, so it wins. The operator is told their
    /// edit was not used rather than left to assume it was.
    /// </summary>
    [Fact]
    public async Task Where_the_ledger_and_the_snapshot_disagree_the_snapshot_is_written_and_it_is_said()
    {
        Snapshot(category: "docTypeCatalogue");
        var row = Row();
        row.OldCategory = "something-the-operator-typed";

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Reverted);
        Assert.Equal("docTypeCatalogue", Assert.Single(_write.UpdatedFiles).Category);
        Assert.Contains(summary.Reasons, r =>
            r.Contains("old category", StringComparison.OrdinalIgnoreCase) &&
            r.Contains("something-the-operator-typed"));
    }

    /// <summary>A revert writes back exactly the fields a correction overwrote — no more.</summary>
    [Fact]
    public async Task A_portal_record_gets_back_no_file_id_because_it_never_had_one()
    {
        Snapshot(fileId: null);

        await Subject().RunAsync(new[] { Row() }, CancellationToken.None);

        Assert.False(Assert.Single(_write.UpdatedFiles).Wrote("mocd_fileid"));
    }

    [Fact]
    public async Task A_plugin_record_gets_its_own_file_id_back()
    {
        Snapshot(fileId: "11111111-0000-0000-0000-000000000001");
        var row = Row();
        row.OldFileId = "11111111-0000-0000-0000-000000000001";

        await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal("11111111-0000-0000-0000-000000000001",
            Assert.Single(_write.UpdatedFiles).FileId);
    }

    [Fact]
    public async Task A_row_with_no_snapshot_on_disk_is_refused_rather_than_guessed_at()
    {
        var summary = await Subject().RunAsync(new[] { Row() }, CancellationToken.None);

        Assert.Equal(1, summary.Refused);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Contains(summary.Reasons, r => r.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_revert_is_journalled()
    {
        Snapshot();

        await Subject().RunAsync(new[] { Row() }, CancellationToken.None);

        var entry = Assert.Single(_journal.Read());
        Assert.Equal(ChangeActions.Reverted, entry.Action);
        Assert.Equal(NewPath, entry.Old!.Path);
        Assert.Equal(OldPath, entry.New!.Path);
    }

    [Fact]
    public async Task The_ledger_is_written_after_each_row()
    {
        Snapshot();

        await Subject().RunAsync(new[] { Row() }, CancellationToken.None);

        Assert.Equal(RowVerdict.Fix, _ledger.Read()[0].Verdict2());
    }

    /// <summary>
    /// Redo used to walk the sheet silently and print only what it happened to act on, so the
    /// operator could not tell whether it had found one row or four hundred.
    /// </summary>
    [Fact]
    public async Task How_many_rows_say_redo_is_said_before_any_of_them_is_touched()
    {
        Snapshot();

        var wanted = Row();
        wanted.Verdict = RowVerdicts.Redo;

        var ignored = Row();
        ignored.DocId = Guid.NewGuid();
        ignored.Verdict = RowVerdicts.Fix;

        await Subject().RunAsync(new[] { wanted, ignored }, CancellationToken.None);

        var said = string.Join("\n", _prompts.Messages);

        Assert.Contains("1 row(s) say redo", said);
        Assert.Contains("[ 1/1 ]", said);
    }

    [Fact]
    public async Task A_ledger_with_nothing_to_redo_says_so_rather_than_saying_nothing()
    {
        var nothingToDo = Row();
        nothingToDo.Verdict = RowVerdicts.Fix;

        var summary = await Subject().RunAsync(new[] { nothingToDo }, CancellationToken.None);

        Assert.Equal(0, summary.Reverted);
        Assert.Contains(summary.Reasons, r => r.Contains("nothing to put back"));
    }
}
