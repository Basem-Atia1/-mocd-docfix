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
            return new ApiResponse<FileData>(true, null,
                new FileData(NewFileId, path, "VHASH", "cert.jpg", "image/jpeg", null), null);
        };
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private MigrateCommand Command(FakePrompts prompts,
        Func<Guid, CancellationToken, Task<string?>>? deleteOld = null) =>
        new(_files, _read, _write, Backups(), States(), new Reporter(Path.Combine(_root, "reports")),
            prompts, _opener, "https://crm/MoCD", deleteOld);

    [Fact]
    public async Task Happy_path_uploads_verifies_creates_and_repoints()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
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
        await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes)).RunAsync("dev", CancellationToken.None);

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

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Equal(1, summary.Failed);
        Assert.Empty(_write.CreatedFiles);
    }

    [Fact]
    public async Task The_operator_is_never_asked_when_verification_already_failed()
    {
        _files.UploadResponder = _ => ApiResponse<FileData>.Fail("vendor exploded");
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

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

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(summary.Halted);
        Assert.Contains("deduplicat", summary.HaltReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_write.CreatedFiles);
    }

    [Fact]
    public async Task A_failed_crm_read_back_halts_the_run()
    {
        _write.ForceLinkReadback = OldFileId;      // the repoint did not stick

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(summary.Halted);
        Assert.Contains("crm-repointed", summary.HaltReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_document_edited_since_the_scan_is_skipped()
    {
        _read.ModifiedOn = DateTimeOffset.UtcNow.AddYears(1);

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Empty(_write.Links);
    }

    [Fact]
    public async Task Already_repointed_documents_are_skipped_on_a_re_run()
    {
        await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes)).RunAsync("dev", CancellationToken.None);
        _files.Uploads.Clear();

        var second = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, second.Migrated);
        Assert.Equal(1, second.Skipped);
        Assert.Empty(_files.Uploads);              // no second upload
    }

    [Fact]
    public async Task A_migration_report_records_both_sides()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(File.Exists(summary.ReportPath));
        var row = Assert.Single(summary.Rows);
        Assert.Equal(OldFileId, row.OldFileId);
        Assert.Equal(NewFileId, row.NewFileId);
        Assert.Contains($"id={DocumentId}", row.OldCrmLink);
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
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.No);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        var said = string.Join("\n", prompts.Messages);

        Assert.Contains("MY COMPARISON", said);
        Assert.Contains("opened both files", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(prompts.Questions, q => q.Contains("look the same", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Saying_the_files_do_not_match_writes_nothing_to_crm()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.No))
            .RunAsync("dev", CancellationToken.None);

        Assert.Empty(_write.CreatedFiles);
        Assert.Empty(_write.Links);
        Assert.Equal(0, summary.Migrated);
        Assert.False(States().IsAtLeast(DocumentId, MigrationState.Repointed));
    }

    [Fact]
    public async Task Repointing_is_a_separate_question_from_the_comparison()
    {
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

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
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        await Command(prompts, (id, _) => { deleted.Add(id); return Task.FromResult<string?>(null); })
            .RunAsync("dev", CancellationToken.None);

        Assert.Contains(prompts.Questions,
            q => q.Contains("Delete this old file", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { DocumentId }, deleted);
    }

    [Fact]
    public async Task Declining_the_delete_leaves_the_old_file_alone()
    {
        var deleted = new List<Guid>();
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.No);

        var summary = await Command(prompts, (id, _) => { deleted.Add(id); return Task.FromResult<string?>(null); })
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);      // the repoint still happened
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task A_refused_delete_is_reported_and_does_not_fail_the_migration()
    {
        var prompts = new FakePrompts().Answer(
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

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
            ConfirmChoice.Yes, ConfirmChoice.Yes, ConfirmChoice.Yes);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        Assert.DoesNotContain(prompts.Questions,
            q => q.Contains("Delete this old file", StringComparison.OrdinalIgnoreCase));
    }
}
