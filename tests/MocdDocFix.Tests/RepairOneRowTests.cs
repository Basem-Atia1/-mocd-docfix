using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

[Collection(LedgerCollection.Name)]
public class RepairOneRowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-one-" + Guid.NewGuid());

    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");
    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");
    private static readonly Guid NewFileId = Guid.Parse("b2c3d4e5-0000-0000-0000-000000000001");

    private const string OldPath =
        @"DigitalServices\docTypeCatalogue\20250509\a3f10000-0000-0000-0000-000000000001.jpg";

    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260915\{NewFileId}.jpg";

    private static readonly byte[] Bytes = { 1, 2, 3, 4, 5 };
    private static readonly string Base64 = Convert.ToBase64String(Bytes);

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();
    private readonly FakePrompts _prompts = new();
    private readonly RecordingOpener _opener = new();
    private readonly BackupStore _backups;
    private readonly ChangeJournal _journal;

    public RepairOneRowTests()
    {
        Directory.CreateDirectory(_dir);
        _backups = new BackupStore(Path.Combine(_dir, "backup"));
        _journal = new ChangeJournal(Path.Combine(_dir, "changes-dev.jsonl"));

        _files.Files[OldPath] = (Base64, "9f86d081");
        _files.Files[NewPath] = (Base64, "9f86d081");
        _files.UploadResponder = _ => new ApiResponse<FileData>(true, null,
            new FileData(NewFileId, NewPath, "9f86d081", $"{NewFileId}.jpg", "image/jpeg", null), null);

        // A portal record: mocd_fileid absent, which is how portal and plugin are told apart.
        _read.RawRecords[$"mocd_documentfiles:{Record}"] = PortalRecord(OldPath);

        // CRM would hold what was just written; the fakes are independent, so mirror it.
        _write.OnUpdated = (id, attributes) =>
            _read.RawRecords[$"mocd_documentfiles:{id}"] =
                Escaped($$"""{"mocd_documentfileid":"{{id}}","mocd_filepath":"@@"}""",
                    (string?)attributes["mocd_filepath"] ?? "");
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static string PortalRecord(string path) =>
        Escaped($$"""
            {"mocd_documentfileid":"{{Record}}","mocd_filepath":"@@","mocd_hash":"9f86d081",
             "mocd_mediatype":"image/jpeg","mocd_category":"docTypeCatalogue","mocd_name":"cert.jpg"}
            """, path);

    /// <summary>Backslashes in a Windows path have to survive being embedded in JSON.</summary>
    private static string Escaped(string template, string path) =>
        template.Replace("@@", path.Replace("\\", "\\\\"));

    private sealed class RecordingOpener : IFileOpener
    {
        public List<string> Opened { get; } = new();
        public void Open(string path) => Opened.Add(path);
    }

    private static LedgerRow Row() => new()
    {
        Row = 12,
        DocId = Doc,
        DocName = "Board of Director's Decision",
        DocFileId = Record,
        DocFileName = "cert.jpg",
        CorrectServiceCatalogueId = Correct.ToString(),
        OldFilePath = OldPath,
        OldCategory = "docTypeCatalogue",
        OldHash = "9f86d081",
        OldFileName = "a3f10000-0000-0000-0000-000000000001.jpg",
        OldFileId = string.Empty,
        Verdict = RowVerdicts.Fix,
        WayOfUpload = "portal"
    };

    private RepairOneRow Subject(WatchMode mode = WatchMode.Quiet) =>
        new(_files, _read, _write, _backups, _journal, _prompts, _opener,
            new RunProgress(_prompts, mode), "dev");

    /// <summary>The happy path, end to end: backed up, uploaded, checked, record updated in place.</summary>
    [Fact]
    public async Task A_clean_row_is_corrected_and_the_record_is_updated_in_place()
    {
        _prompts.Answer(ConfirmChoice.Yes);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.Corrected);
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Equal(NewPath, row.NewFilePath);
        Assert.NotEqual(string.Empty, row.BackupPath);
        Assert.Equal(string.Empty, row.Error);

        // A finished row must not go on reading "fix", which says the opposite of the truth.
        Assert.Equal(RowVerdict.Done, row.Verdict2());

        var update = Assert.Single(_write.UpdatedFiles);
        Assert.Equal(Record, update.RecordId);
        Assert.Equal(NewPath, update.FilePath);
        Assert.Equal(Correct.ToString(), update.Category);
    }

    /// <summary>
    /// The thing that makes this design work: nothing is created and nothing is repointed, so a
    /// document keeps the record it already had.
    /// </summary>
    [Fact]
    public async Task Nothing_is_created_and_nothing_is_repointed()
    {
        _prompts.Answer(ConfirmChoice.Yes);

        await Subject().RunAsync(Row(), CancellationToken.None);

        Assert.Empty(_write.CreatedFiles);
        Assert.Empty(_write.Links);
    }

    /// <summary>
    /// mocd_fileid and mocd_filename are written only where the old record used them. Adding
    /// mocd_fileid to a portal record would change its shape, which is the thing the copier
    /// exists to avoid.
    /// </summary>
    [Fact]
    public async Task A_portal_record_does_not_gain_a_file_id_it_never_had()
    {
        _prompts.Answer(ConfirmChoice.Yes);

        await Subject().RunAsync(Row(), CancellationToken.None);

        var update = Assert.Single(_write.UpdatedFiles);
        Assert.False(update.Wrote("mocd_fileid"));
        Assert.True(update.Wrote("mocd_filepath"));
        Assert.True(update.Wrote("mocd_category"));
        Assert.True(update.Wrote("mocd_hash"));
    }

    [Fact]
    public async Task A_plugin_record_keeps_its_file_id_and_gets_the_new_one()
    {
        _read.RawRecords[$"mocd_documentfiles:{Record}"] =
            $$"""{"mocd_documentfileid":"{{Record}}","mocd_fileid":"11111111-0000-0000-0000-000000000001","mocd_filename":"a3f1.jpg","mocd_mediatype":"image/jpeg","mocd_category":"docTypeCatalogue"}""";

        _prompts.Answer(ConfirmChoice.Yes);
        var row = Row();
        row.OldFileId = "11111111-0000-0000-0000-000000000001";
        row.WayOfUpload = "plugin";

        await Subject().RunAsync(row, CancellationToken.None);

        var update = Assert.Single(_write.UpdatedFiles);
        Assert.Equal(NewFileId.ToString(), update.FileId);
        Assert.True(update.Wrote("mocd_filename"));
    }

    /// <summary>A no to the eye-check must leave CRM exactly as it was.</summary>
    [Fact]
    public async Task Saying_the_copies_do_not_match_writes_nothing_to_crm()
    {
        _prompts.Answer(ConfirmChoice.No);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.False(outcome.Corrected);
        Assert.False(outcome.Failed);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Equal(RowState.NotStarted, row.State());
        Assert.NotEqual(string.Empty, row.Notes);
    }

    /// <summary>
    /// Quit at the eye-check is not "they look wrong". It is also where a q pressed during the
    /// upload lands, because the keystroke waits in the buffer for the next prompt to read —
    /// and calling that a rejected copy would put words in the operator's mouth and then carry
    /// on regardless.
    /// </summary>
    [Fact]
    public async Task Quitting_at_the_eye_check_asks_to_stop_rather_than_rejecting_the_copy()
    {
        _prompts.Answer(ConfirmChoice.Quit);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.StopAsked);
        Assert.False(outcome.Corrected);
        Assert.False(outcome.Failed);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Equal(RowState.NotStarted, row.State());

        Assert.Contains("stopped", row.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not match", row.Notes);
    }

    /// <summary>Saying no is still saying no, and must not be mistaken for asking to stop.</summary>
    [Fact]
    public async Task Saying_no_is_not_asking_to_stop()
    {
        _prompts.Answer(ConfirmChoice.No);

        var outcome = await Subject().RunAsync(Row(), CancellationToken.None);

        Assert.False(outcome.StopAsked);
        Assert.False(outcome.Corrected);
    }

    [Fact]
    public async Task Both_copies_are_opened_before_the_operator_is_asked()
    {
        _prompts.Answer(ConfirmChoice.Yes);

        await Subject().RunAsync(Row(), CancellationToken.None);

        Assert.Equal(2, _opener.Opened.Count);
    }

    /// <summary>Unattended asks nothing and opens nothing; the four checks decide on their own.</summary>
    [Fact]
    public async Task Unattended_corrects_the_row_without_asking_or_opening_anything()
    {
        var row = Row();

        var outcome = await Subject(WatchMode.Unattended).RunAsync(row, CancellationToken.None);

        Assert.True(outcome.Corrected);
        Assert.Empty(_prompts.Questions);
        Assert.Empty(_opener.Opened);
        Assert.Single(_write.UpdatedFiles);
    }

    [Fact]
    public async Task An_upload_the_server_refuses_fails_the_row_and_writes_nothing_to_crm()
    {
        _files.UploadResponder = _ => ApiResponse<FileData>.Fail("413 Payload Too Large");

        var outcome = await Subject().RunAsync(Row(), CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal("upload", outcome.FailedStep);
        Assert.Contains("413", outcome.Failure);
        Assert.Empty(_write.UpdatedFiles);
    }

    /// <summary>
    /// The decisive check. A corrupted round trip means the copy on the server is not the file
    /// we backed up, so CRM must not be pointed at it.
    /// </summary>
    [Fact]
    public async Task A_new_copy_that_does_not_come_back_identical_fails_the_row()
    {
        _files.Files[NewPath] = (Convert.ToBase64String(new byte[] { 9, 9, 9 }), "9f86d081");
        _prompts.Answer(ConfirmChoice.Yes);

        var outcome = await Subject().RunAsync(Row(), CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Empty(_write.UpdatedFiles);
    }

    /// <summary>
    /// CRM accepting a PATCH is not the same as CRM having stored it. The record is read back,
    /// and only then is the row called corrected.
    /// </summary>
    [Fact]
    public async Task A_record_that_does_not_hold_the_new_path_afterwards_fails_the_row()
    {
        _write.OnUpdated = null;      // CRM silently does not take the write
        _prompts.Answer(ConfirmChoice.Yes);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal("read the record back", outcome.FailedStep);
        Assert.Equal(RowState.NotStarted, row.State());
    }

    [Fact]
    public async Task The_correction_is_journalled_with_the_values_before_and_after()
    {
        _prompts.Answer(ConfirmChoice.Yes);

        await Subject().RunAsync(Row(), CancellationToken.None);

        var entry = Assert.Single(_journal.Read());
        Assert.Equal(ChangeActions.Corrected, entry.Action);
        Assert.Equal(Doc, entry.Doc);
        Assert.Equal(Record, entry.Record);
        Assert.Equal(OldPath, entry.Old!.Path);
        Assert.Equal("docTypeCatalogue", entry.Old.Category);
        Assert.Equal(NewPath, entry.New!.Path);
    }

    [Fact]
    public async Task A_file_the_server_does_not_have_fails_at_the_backup_step()
    {
        _files.Files.Remove(OldPath);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal("backup", outcome.FailedStep);
        Assert.Equal(string.Empty, row.NewFilePath);
        Assert.Empty(_files.Uploads);
    }

    // ---- already right in CRM ----
    //
    // Somebody corrects a document in CRM by hand, or an earlier run finished the upload and the
    // ledger never learned of it. The row still says fix. Uploading a second copy would be wrong
    // twice over: it would orphan a file and it would tell the operator work was done that was
    // already done. So CRM is asked first, and the row is settled from what it says.

    /// <summary>
    /// Corrected elsewhere, old file still on the server. The correction is not owed; the
    /// deletion still is, and the row has to say so or the old file is stranded forever.
    /// </summary>
    [Fact]
    public async Task A_row_crm_already_corrected_is_marked_done_and_left_for_the_delete_step()
    {
        _read.RawRecords[$"mocd_documentfiles:{Record}"] = PortalRecord(NewPath);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.WasAlreadyRight);
        Assert.False(outcome.Corrected);
        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Equal(NewPath, row.NewFilePath);
        Assert.Contains("still on the server", row.Notes);

        Assert.Empty(_files.Uploads);
        Assert.Empty(_write.UpdatedFiles);
    }

    /// <summary>Corrected elsewhere and the old file already gone: nothing is outstanding.</summary>
    [Fact]
    public async Task A_row_crm_corrected_whose_old_file_has_gone_is_finished()
    {
        _read.RawRecords[$"mocd_documentfiles:{Record}"] = PortalRecord(NewPath);
        _files.Files.Remove(OldPath);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.WasAlreadyRight);
        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(RowState.Deleted, row.State());
        Assert.Contains("no longer", row.Notes);
        Assert.Empty(_files.Uploads);
    }

    /// <summary>
    /// Filed correctly all along — the path CRM holds is the one the ledger calls old. There is
    /// no second copy anywhere, so there is nothing to delete and the row is a skip, not a done.
    /// </summary>
    [Fact]
    public async Task A_row_that_was_always_right_becomes_a_skip_with_nothing_to_delete()
    {
        var alreadyRight = $@"DigitalServices\{Correct}\20250509\a3f10000-0000-0000-0000-000000000001.jpg";
        _read.RawRecords[$"mocd_documentfiles:{Record}"] = PortalRecord(alreadyRight);

        var row = Row();
        row.OldFilePath = alreadyRight;

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.WasAlreadyRight);
        Assert.Equal(RowVerdict.Skip, row.Verdict2());
        Assert.Equal(RowState.NotStarted, row.State());
        Assert.Contains("nothing to delete", row.Notes);
        Assert.Empty(_files.Uploads);
    }

    /// <summary>The check reads CRM and nothing else. A row still filed wrongly is corrected.</summary>
    [Fact]
    public async Task A_row_crm_still_files_wrongly_is_corrected_as_usual()
    {
        _prompts.Answer(ConfirmChoice.Yes);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.False(outcome.WasAlreadyRight);
        Assert.True(outcome.Corrected);
        Assert.Single(_files.Uploads);
    }
}
