using System.Text;
using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class MigrateCommandTests : IDisposable
{
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid NewFileId = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid OldFileId = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");

    private const string OldPath = @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg";
    private static string NewPath(Guid id) => $@"DigitalServices\{Correct}\20260910\{id}.jpg";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-mig-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();
    private readonly NullFileOpener _opener = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public MigrateCommandTests()
    {
        Directory.CreateDirectory(_root);

        // A backed-up document, ready to migrate.
        var backups = Backups();
        var saved = backups.Save(DocumentId, OldFileId, ".jpg", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH", "cert.jpg",
            "image/jpeg", ".jpg", "goodConductCertificate", Correct, saved.LocalPath,
            saved.Bytes, saved.OurHash, DateTimeOffset.UtcNow));
        States().Append(new StateRecord(DocumentId, MigrationState.BackedUp, DateTimeOffset.UtcNow, null, null, null));

        // The vendor accepts the upload and serves the new file back identically.
        _files.UploadResponder = _ =>
        {
            var path = NewPath(NewFileId);
            _files.Files[path] = (Convert.ToBase64String(Content), "VHASH");
            _read.RawRecords[$"mocd_documentfiles:{NewFileId}"] =
                "{\"mocd_filepath\":\"" + path.Replace("\\", "\\\\") + "\"}";
            return new ApiResponse<FileData>(true, null,
                new FileData(NewFileId, path, "VHASH", "cert.jpg", "image/jpeg", null), null);
        };

        // CRM stores the new row under whatever key it ended up with — the vendor's file id for a
        // portal-shaped record, its own generated key for a plugin-shaped one. Registering it only
        // under the vendor id let a read-back by the real key silently find nothing.
        _write.OnCreated = (id, attributes) =>
            _read.RawRecords[$"mocd_documentfiles:{id}"] =
                "{\"mocd_filepath\":\"" +
                ((string?)attributes["mocd_filepath"] ?? "").Replace("\\", "\\\\") + "\"}";
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private MigrateCommand Command(FakePrompts prompts,
        Func<Guid, CancellationToken, Task<string?>>? deleteOld = null) =>
        new(_files, _read, _write, Backups(), States(), new Reporter(Path.Combine(_root, "reports")),
            prompts, _opener, "https://crm/MoCD", deleteOld);

    [Fact]
    public async Task Happy_path_uploads_verifies_creates_and_repoints()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        Assert.False(summary.Halted);

        var upload = Assert.Single(_files.Uploads);
        Assert.Equal(Correct.ToString(), upload.Category);          // the whole point
        Assert.Equal("cert.jpg", upload.FileName);
        Assert.Equal(Convert.ToBase64String(Content), upload.File);

        var created = Assert.Single(_write.CreatedFiles);
        Assert.Equal(NewFileId, created.FileId);
        Assert.Equal(Correct.ToString(), created.Category);
        Assert.Equal(NewFileId, _write.Links[DocumentId]);
        Assert.True(States().IsAtLeast(DocumentId, MigrationState.Repointed));
    }

    [Fact]
    public async Task Both_files_are_opened_for_the_operator_before_the_question()
    {
        await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes)).RunAsync("dev", CancellationToken.None);

        Assert.Equal(2, _opener.Opened.Count);
        Assert.Contains(_opener.Opened, p => p.Contains(OldFileId.ToString()));
    }

    [Fact]
    public async Task Answering_no_writes_nothing_to_crm()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.No))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Empty(_write.CreatedFiles);
        Assert.Empty(_write.Links);
        Assert.False(States().IsAtLeast(DocumentId, MigrationState.Repointed));
    }

    [Fact]
    public async Task Quit_stops_the_run_without_writing()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Quit))
            .RunAsync("dev", CancellationToken.None);

        Assert.Empty(_write.Links);
        Assert.Equal(0, summary.Migrated);
    }

    [Fact]
    public async Task A_corrupted_round_trip_stops_the_file_and_writes_nothing_to_crm()
    {
        _files.UploadResponder = _ =>
        {
            var path = NewPath(NewFileId);
            _files.Files[path] = (Convert.ToBase64String(Encoding.UTF8.GetBytes("DIFFERENT")), "VHASH");
            return new ApiResponse<FileData>(true, null,
                new FileData(NewFileId, path, "VHASH", "cert.jpg", "image/jpeg", null), null);
        };

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Equal(1, summary.Failed);
        Assert.Empty(_write.CreatedFiles);
    }

    [Fact]
    public async Task The_operator_is_never_asked_when_verification_already_failed()
    {
        _files.UploadResponder = _ => ApiResponse<FileData>.Fail("vendor exploded");
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        Assert.DoesNotContain(prompts.Questions, q => q.Contains("Repoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Deduplication_halts_the_entire_run()
    {
        _files.UploadResponder = _ =>
        {
            _files.Files[OldPath] = (Convert.ToBase64String(Content), "VHASH");
            return new ApiResponse<FileData>(true, null,
                new FileData(OldFileId, OldPath, "VHASH", "cert.jpg", "image/jpeg", null), null);
        };

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(summary.Halted);
        Assert.Contains("deduplicat", summary.HaltReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_write.CreatedFiles);
    }

    [Fact]
    public async Task A_failed_crm_read_back_halts_the_run()
    {
        _write.ForceLinkReadback = OldFileId;      // the repoint did not stick

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(summary.Halted);
        Assert.Contains("crm-repointed", summary.HaltReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_document_edited_since_the_scan_is_skipped()
    {
        _read.ModifiedOn = DateTimeOffset.UtcNow.AddYears(1);

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Empty(_write.Links);
    }

    [Fact]
    public async Task Already_repointed_documents_are_skipped_on_a_re_run()
    {
        await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes)).RunAsync("dev", CancellationToken.None);
        _files.Uploads.Clear();

        var second = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, second.Migrated);
        Assert.Equal(1, second.Skipped);
        Assert.Empty(_files.Uploads);              // no second upload
    }

    [Fact]
    public async Task A_migration_report_records_both_sides()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(File.Exists(summary.ReportPath));
        var row = Assert.Single(summary.Rows);
        Assert.Equal(OldFileId, row.OldFileId);
        Assert.Equal(NewFileId, row.NewFileId);
        Assert.Contains($"id={DocumentId}", row.OldCrmLink);
    }

    // ---- a failure is stated plainly, with what it cost ----

    [Fact]
    public async Task A_failed_verification_says_what_was_expected_and_what_was_skipped()
    {
        _files.UploadResponder = _ =>
        {
            var path = NewPath(NewFileId);
            _files.Files[path] = (Convert.ToBase64String(Encoding.UTF8.GetBytes("DIFFERENT")), "VHASH");
            return new ApiResponse<FileData>(true, null,
                new FileData(NewFileId, path, "VHASH", "cert.jpg", "image/jpeg", null), null);
        };

        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        var said = string.Join("|", prompts.Messages);
        Assert.Contains("SOMETHING WENT WRONG", said);
        Assert.Contains("Expected", said);
        Assert.Contains("Actually", said);
        Assert.Contains("NOT done because of this", said);
        Assert.Contains("the old file and its CRM record were NOT deleted", said);
        Assert.Contains("the document was NOT repointed", said);
    }

    [Fact]
    public async Task A_failed_upload_says_the_old_file_is_untouched()
    {
        _files.UploadResponder = _ => ApiResponse<FileData>.Fail("vendor exploded");
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        var said = string.Join("|", prompts.Messages);
        Assert.Contains("SOMETHING WENT WRONG", said);
        Assert.Contains("vendor exploded", said);
        Assert.Contains("both are untouched", said);
    }

    [Fact]
    public async Task A_record_left_holding_the_wrong_path_explains_why_nothing_was_deleted()
    {
        CrmKeepsTheOldPathOnTheNewRecord();

        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        var said = string.Join("|", prompts.Messages);
        Assert.Contains("does not hold the new path", said);
        Assert.Contains("would then have nothing that opens", said);
    }

    // ---- creating the record and repointing are separate decisions ----

    [Fact]
    public async Task Refusing_to_create_the_record_writes_nothing()
    {
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        var summary = await Command(prompts).RunAsync("dev", CancellationToken.None);

        Assert.Contains(prompts.Questions, q => q.Contains("Create the new documentfile", StringComparison.Ordinal));
        Assert.Empty(_write.CreatedFiles);
        Assert.Empty(_write.Links);
        Assert.Equal(0, summary.Migrated);
    }

    [Fact]
    public async Task The_record_can_be_created_without_repointing_the_document()
    {
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        var summary = await Command(prompts).RunAsync("dev", CancellationToken.None);

        Assert.Single(_write.CreatedFiles);          // the row exists
        Assert.Empty(_write.Links);                  // but the document has not moved
        Assert.Equal(0, summary.Migrated);
        Assert.False(States().IsAtLeast(DocumentId, MigrationState.Repointed));
    }

    [Fact]
    public async Task The_operator_is_told_the_document_has_not_moved_yet()
    {
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        var said = string.Join("|", prompts.Messages);
        Assert.Contains("still points at the OLD one", said);
        Assert.Contains("has NOT moved yet", said);
    }

    // ---- a document that is already correct is left alone ----

    [Fact]
    public async Task A_document_already_pointing_at_a_correctly_filed_record_is_not_migrated_again()
    {
        // As if an earlier run finished the work and then failed a later check.
        var already = Guid.NewGuid();
        _write.Links[DocumentId] = already;
        _read.RawRecords[$"mocd_documentfiles:{already}"] =
            "{\"mocd_filepath\":\"" + NewPath(NewFileId).Replace("\\", "\\\\") + "\"}";

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes)).RunAsync("dev", CancellationToken.None);

        Assert.Empty(_files.Uploads);                // nothing re-uploaded
        Assert.Empty(_write.CreatedFiles);           // no duplicate row
        Assert.Equal(1, summary.Skipped);
        Assert.True(States().IsAtLeast(DocumentId, MigrationState.Repointed));
    }

    // ---- a plugin-created record stays plugin-shaped ----

    private const string PluginRecordJson = """
        {"mocd_documentfileid":"5b05398a-b0bc-4c26-a6a6-40b7e0ece187",
         "mocd_name":"cert.jpg",
         "mocd_fileid":"5b05398a-b0bc-4c26-a6a6-40b7e0ece187",
         "mocd_filename":"5b05398a.jpg",
         "mocd_mediatype":"image/jpeg",
         "mocd_extension":"jpg",
         "mocd_applicationid":"2c9d5572-a77b-f111-b10f-00505601095a",
         "mocd_filesize":"4684",
         "mocd_hash":"VHASH",
         "mocd_ismigrated":false}
        """;

    private void BackedUpAsPluginRecord()
    {
        var backups = Backups();
        var saved = backups.Save(DocumentId, OldFileId, ".jpg", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH", "cert.jpg",
            "image/jpeg", ".jpg", "goodConductCertificate", Correct, saved.LocalPath,
            saved.Bytes, saved.OurHash, DateTimeOffset.UtcNow,
            DocumentFileSnapshotJson: PluginRecordJson));
    }

    [Fact]
    public async Task A_plugin_created_record_keeps_every_column_it_had()
    {
        BackedUpAsPluginRecord();

        await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        var created = Assert.Single(_write.CreatedFiles);
        Assert.Equal("cert.jpg", created.Attributes["mocd_name"]);
        Assert.Equal("jpg", created.Attributes["mocd_extension"]);
        Assert.Equal("4684", created.Attributes["mocd_filesize"]);
        Assert.Equal("2c9d5572-a77b-f111-b10f-00505601095a", created.Attributes["mocd_applicationid"]);
        Assert.Equal(false, created.Attributes["mocd_ismigrated"]);
    }

    [Fact]
    public async Task A_plugin_created_record_lets_crm_choose_the_key_and_stores_the_vendor_id()
    {
        BackedUpAsPluginRecord();

        await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        var created = Assert.Single(_write.CreatedFiles);
        Assert.Null(created.ExplicitId);                                  // CRM generates it
        Assert.Equal(NewFileId.ToString(), created.Attributes["mocd_fileid"]);
        Assert.Equal(_write.GeneratedId, _write.Links[DocumentId]);        // and that key is linked
    }

    [Fact]
    public async Task A_plugin_created_record_uploads_with_the_document_id_and_a_dotless_extension()
    {
        BackedUpAsPluginRecord();

        await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        var upload = Assert.Single(_files.Uploads);
        Assert.Equal("jpg", upload.Extension);
        Assert.Equal(DocumentId, upload.ApplicationId);
    }

    /// <summary>
    /// The state file names the new mocd_documentfile, and for a plugin-shaped record that is the
    /// key CRM generated, not the vendor's file id. Recording the vendor id instead made the
    /// delete step and the final check look up a record that does not exist, so a document that
    /// had migrated perfectly was reported as broken and its old file could never be removed.
    /// </summary>
    [Fact]
    public async Task A_plugin_created_record_is_recorded_under_the_key_crm_generated()
    {
        BackedUpAsPluginRecord();

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        Assert.False(summary.Halted);

        var record = States().LoadLatest()[DocumentId];
        Assert.Equal(MigrationState.Repointed, record.State);
        Assert.Equal(_write.GeneratedId, record.NewFileId);      // the CRM key
        Assert.NotEqual(NewFileId, record.NewFileId);            // not the vendor's file id
        Assert.Equal(NewPath(NewFileId), record.NewFilePath);
    }

    [Fact]
    public async Task The_crm_link_offered_after_repointing_is_the_record_that_exists()
    {
        BackedUpAsPluginRecord();
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        var summary = await Command(prompts).RunAsync("dev", CancellationToken.None);

        Assert.Equal(_write.GeneratedId, Assert.Single(summary.Rows).NewFileId);
        Assert.Contains(_write.GeneratedId.ToString(), string.Join("|", prompts.Messages));
    }

    [Fact]
    public async Task A_portal_created_record_still_uses_the_vendor_id_as_its_key()
    {
        // The default setup has no snapshot at all, which reads as the portal shape.
        await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        var created = Assert.Single(_write.CreatedFiles);
        Assert.Equal(NewFileId, created.ExplicitId);
        Assert.False(created.Attributes.ContainsKey("mocd_fileid"));
        Assert.Equal(Guid.Empty, Assert.Single(_files.Uploads).ApplicationId);
    }

    // ---- repointing never deletes anything ----

    [Fact]
    public async Task Repointing_leaves_the_old_file_and_its_record_alone()
    {
        // No per-document deleter is supplied, so the run ends right after the repoint.
        var summary = await Command(new FakePrompts().Answer(
                ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        Assert.Empty(_write.DeletedFiles);                 // the old CRM record is untouched
        Assert.Empty(_files.Deleted);                      // and so is the old file
    }

    [Fact]
    public async Task Declining_the_delete_removes_neither_the_file_nor_the_record()
    {
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        await Command(prompts, (_, _) => Task.FromResult<string?>(null))
            .RunAsync("dev", CancellationToken.None);

        Assert.Empty(_write.DeletedFiles);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task The_delete_question_says_it_removes_the_crm_record_too()
    {
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        await Command(prompts, (_, _) => Task.FromResult<string?>(null))
            .RunAsync("dev", CancellationToken.None);

        var said = string.Join("\n", prompts.Messages);
        Assert.Contains("mocd_documentfile", said);
        Assert.Contains("both still there", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(prompts.Questions, q => q.Contains("AND its CRM record", StringComparison.Ordinal));
    }

    // ---- the new file is written down where you can find it ----

    [Fact]
    public async Task The_run_level_repointed_file_is_only_an_index()
    {
        var reports = new DocumentReportStore(Path.Combine(_root, "per-doc"));

        var summary = await new MigrateCommand(_files, _read, _write, Backups(), States(),
                new Reporter(Path.Combine(_root, "reports")),
                new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes),
                _opener, "https://crm/MoCD", null,
                new RepointedListWriter(Path.Combine(_root, "reports")), reports)
            .RunAsync("dev", CancellationToken.None);

        Assert.True(File.Exists(summary.RepointedPath), summary.RepointedPath);
        Assert.Contains("repointed-index-", Path.GetFileName(summary.RepointedPath));

        var text = File.ReadAllText(summary.RepointedPath);

        Assert.Contains("An index", text);
        Assert.Contains(NewFileId.ToString(), text);            // the new documentfile id
        Assert.Contains("etn=mocd_documentfile", text);         // a link straight to it
        Assert.Contains(DocumentId.ToString(), text);
        Assert.Contains("03-upload-repoint.txt", text);         // and where the rest of it lives
    }

    [Fact]
    public async Task The_upload_detail_lives_in_the_documents_own_folder()
    {
        var reports = new DocumentReportStore(Path.Combine(_root, "per-doc"));

        await new MigrateCommand(_files, _read, _write, Backups(), States(),
                new Reporter(Path.Combine(_root, "reports")),
                new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes),
                _opener, "https://crm/MoCD", null, null, reports)
            .RunAsync("dev", CancellationToken.None);

        var path = Path.Combine(reports.FolderFor(DocumentId, "cert.jpg"), "03-upload-repoint.txt");
        Assert.True(File.Exists(path), path);

        var text = File.ReadAllText(path);
        Assert.Contains(NewPath(NewFileId), text);
        Assert.Contains(NewFileId.ToString(), text);
        Assert.Contains("still on the server", text);           // the old side is still there
    }

    // ---- check 7: the record must point at the new file ----

    [Fact]
    public async Task A_documentfile_left_pointing_at_the_old_path_halts_the_run()
    {
        CrmKeepsTheOldPathOnTheNewRecord();

        var summary = await Command(new FakePrompts().Answer(
                ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(summary.Halted);
        Assert.Contains("file-record-path", summary.HaltReason!);
        Assert.Empty(_files.Deleted);              // and nothing was deleted
    }

    /// <summary>The new row is created, but its mocd_filepath still names the old file.</summary>
    private void CrmKeepsTheOldPathOnTheNewRecord() =>
        _write.OnCreated = (id, _) =>
            _read.RawRecords[$"mocd_documentfiles:{id}"] =
                "{\"mocd_filepath\":\"" + OldPath.Replace("\\", "\\\\") + "\"}";

    /// <summary>
    /// A halt must not erase what the run already learned. Blanking the new record's id and path
    /// left nothing to recover from: the row existed in CRM, but the tool's own notes no longer
    /// said where it was, so no later step could find it.
    /// </summary>
    [Fact]
    public async Task A_halt_keeps_the_new_record_id_and_path_it_already_knew()
    {
        CrmKeepsTheOldPathOnTheNewRecord();

        await Command(new FakePrompts().Answer(
                ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        var record = States().LoadLatest()[DocumentId];
        Assert.Equal(MigrationState.Failed, record.State);
        Assert.Equal(NewFileId, record.NewFileId);           // portal shape: key = vendor file id
        Assert.Equal(NewPath(NewFileId), record.NewFilePath);
    }

    // ---- one question per decision ----

    [Fact]
    public async Task The_operator_is_told_the_new_catalogue_and_the_new_path_shape_before_uploading()
    {
        var prompts = new FakePrompts().Answer(ConfirmChoice.No);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        var said = string.Join("\n", prompts.Messages);

        Assert.Contains(Correct.ToString(), said);                      // the catalogue it will use
        Assert.Contains($@"DigitalServices\{Correct}\", said);          // the shape of the new path
        Assert.Contains("<new-file-id>", said);                         // and that the id is theirs
        Assert.Contains("old file is not touched", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refusing_the_upload_uploads_nothing_at_all()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.No))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Equal(1, summary.Skipped);
        Assert.Empty(_write.Links);
    }

    [Fact]
    public async Task The_comparison_is_shown_before_the_operator_is_asked_about_it()
    {
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        var said = string.Join("\n", prompts.Messages);

        Assert.Contains("MY COMPARISON", said);
        Assert.Contains("opened both files", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(prompts.Questions, q => q.Contains("look the same", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Saying_the_files_do_not_match_writes_nothing_to_crm()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No))
            .RunAsync("dev", CancellationToken.None);

        Assert.Empty(_write.CreatedFiles);
        Assert.Empty(_write.Links);
        Assert.Equal(0, summary.Migrated);
        Assert.False(States().IsAtLeast(DocumentId, MigrationState.Repointed));
    }

    [Fact]
    public async Task Repointing_is_a_separate_question_from_the_comparison()
    {
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        var summary = await Command(prompts).RunAsync("dev", CancellationToken.None);

        Assert.Contains(prompts.Questions, q => q.Contains("Repoint", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, summary.Migrated);
        Assert.Empty(_write.Links);
    }

    [Fact]
    public async Task After_repointing_the_operator_is_asked_about_the_old_file()
    {
        var deleted = new List<Guid>();
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        await Command(prompts, (id, _) => { deleted.Add(id); return Task.FromResult<string?>(null); })
            .RunAsync("dev", CancellationToken.None);

        Assert.Contains(prompts.Questions,
            q => q.Contains("Delete the old file AND its CRM record", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { DocumentId }, deleted);
    }

    [Fact]
    public async Task Declining_the_delete_leaves_the_old_file_alone()
    {
        var deleted = new List<Guid>();
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        var summary = await Command(prompts, (id, _) => { deleted.Add(id); return Task.FromResult<string?>(null); })
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);      // the repoint still happened
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task A_refused_delete_is_reported_and_does_not_fail_the_migration()
    {
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        var summary = await Command(prompts,
                (_, _) => Task.FromResult<string?>("the document points somewhere else"))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        Assert.Contains(prompts.Messages,
            m => m.Contains("REFUSED", StringComparison.Ordinal) &&
                 m.Contains("points somewhere else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_deleter_the_old_file_question_is_not_asked_at_all()
    {
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        Assert.DoesNotContain(prompts.Questions,
            q => q.Contains("Delete the old file AND its CRM record", StringComparison.OrdinalIgnoreCase));
    }

    // ---- why, not just how many ----

    /// <summary>
    /// "5 skipped" is the same number whether the work was finished last week or the operator
    /// said no to all of it, and the two are not the same news. The reason travels with the count.
    /// </summary>
    [Fact]
    public async Task A_document_finished_in_an_earlier_run_is_skipped_with_that_said()
    {
        States().Append(new StateRecord(DocumentId, MigrationState.Repointed,
            DateTimeOffset.UtcNow, NewFileId, NewPath(NewFileId), null));

        var summary = await Command(new FakePrompts()).RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        var skip = Assert.Single(summary.Skips!);
        Assert.Equal(1, skip.Count);
        Assert.Contains("earlier run", skip.Why);
    }

    [Fact]
    public async Task Declining_the_upload_is_recorded_as_the_operators_choice_not_as_a_gap()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.No))
            .RunAsync("dev", CancellationToken.None);

        var skip = Assert.Single(summary.Skips!);
        Assert.Contains("you chose not to upload it", skip.Why);
    }

    /// <summary>
    /// What lets a run stop claiming "the old files are still in place" after removing them.
    /// </summary>
    [Fact]
    public async Task Removing_the_old_file_as_it_goes_is_counted()
    {
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        var summary = await Command(prompts, (_, _) => Task.FromResult<string?>(null))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        Assert.Equal(1, summary.OldFilesRemoved);
    }

    [Fact]
    public async Task An_old_file_left_alone_is_not_counted_as_removed()
    {
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        var summary = await Command(prompts, (_, _) => Task.FromResult<string?>(null))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.OldFilesRemoved);
    }
}
