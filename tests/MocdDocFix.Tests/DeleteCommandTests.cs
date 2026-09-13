using System.Text;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

public class DeleteCommandTests : IDisposable
{
    private static readonly Guid Correct    = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid OldFileId  = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");
    private static readonly Guid NewFileId  = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");

    private const string OldPath = @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260910\{NewFileId}.jpg";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-del-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public DeleteCommandTests()
    {
        Directory.CreateDirectory(_root);

        var backups = Backups();
        var saved = backups.Save(DocumentId, OldFileId, ".jpg", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH", "cert.jpg",
            "image/jpeg", ".jpg", "goodConductCertificate", Correct, saved.LocalPath,
            saved.Bytes, Verifier.OurHash(Content), DateTimeOffset.UtcNow));

        States().Append(new StateRecord(DocumentId, MigrationState.Repointed,
            DateTimeOffset.UtcNow, NewFileId, NewPath, null));

        _files.Files[OldPath] = (Convert.ToBase64String(Content), "VHASH");
        _files.Files[NewPath] = (Convert.ToBase64String(Content), "VHASH");
        _write.Links[DocumentId] = NewFileId;

        // The new documentfile record, as CRM would return it after the repoint.
        _read.RawRecords[$"mocd_documentfiles:{NewFileId}"] =
            "{\"mocd_filepath\":\"" + NewPath.Replace("\\", "\\\\") + "\"}";
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private DeleteCommand Command(FakePrompts prompts) =>
        new(_files, _read, _write, Backups(), States(), prompts);

    private static FakePrompts Confirmed() => new() { TypedWordResponse = "DELETE" };

    [Fact]
    public async Task Deletes_the_old_vendor_file_and_the_old_documentfile_row()
    {
        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Deleted);
        Assert.Contains(OldPath, _files.Deleted);
        Assert.Contains(OldFileId, _write.DeletedFiles);
        Assert.DoesNotContain(NewFileId, _write.DeletedFiles);
        Assert.True(States().IsAtLeast(DocumentId, MigrationState.Deleted));
    }

    [Fact]
    public async Task Without_the_typed_word_nothing_is_deleted()
    {
        var prompts = new FakePrompts { TypedWordResponse = "yes" };

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
    }

    [Fact]
    public async Task Documents_not_yet_repointed_are_never_considered()
    {
        States().Append(new StateRecord(Guid.NewGuid(), MigrationState.BackedUp,
            DateTimeOffset.UtcNow, null, null, null));

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Deleted);          // only the repointed one
    }

    [Fact]
    public async Task A_new_file_that_no_longer_downloads_refuses_the_delete()
    {
        _files.Files.Remove(NewPath);

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task A_new_file_whose_content_drifted_refuses_the_delete()
    {
        _files.Files[NewPath] = (Convert.ToBase64String(Encoding.UTF8.GetBytes("DIFFERENT")), "VHASH");

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task A_document_no_longer_pointing_at_the_new_file_refuses_the_delete()
    {
        _write.Links[DocumentId] = OldFileId;      // someone repointed it back

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Production_additionally_requires_typing_the_exact_count()
    {
        var prompts = new FakePrompts { TypedWordResponse = "DELETE" };

        // TypedWordResponse only matches one word, so the count prompt fails and aborts.
        var summary = await Command(prompts).RunAsync("prod", isProduction: true, CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
        Assert.Contains(prompts.Questions, q => q.Contains("1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Already_deleted_documents_are_skipped_on_a_re_run()
    {
        await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);
        _files.Deleted.Clear();

        var second = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, second.Deleted);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Nothing_to_delete_is_reported_without_prompting()
    {
        File.Delete(Path.Combine(_root, "state.jsonl"));
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(prompts.Questions);
    }

    // ---- the delete step shows its working ----

    // ---- a file that more than one record points at ----

    [Fact]
    public async Task A_shared_file_is_never_deleted()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId, Guid.NewGuid() };
        var prompts = Confirmed();
        prompts.ReadLineQueue = new Queue<string>(new[] { "1" });   // leave everything

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
    }

    [Fact]
    public async Task A_shared_file_is_explained_before_the_question()
    {
        var other = Guid.NewGuid();
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId, other };
        var prompts = Confirmed();
        prompts.ReadLineQueue = new Queue<string>(new[] { "1" });

        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        var said = string.Join("|", prompts.Messages);
        Assert.Contains("PROBLEM", said);
        Assert.Contains(other.ToString(), said);
        Assert.Contains("would leave those records pointing at nothing", said);
    }

    [Fact]
    public async Task The_operator_can_remove_only_the_old_crm_record_and_keep_the_shared_file()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId, Guid.NewGuid() };
        var prompts = Confirmed();
        prompts.ReadLineQueue = new Queue<string>(new[] { "2" });   // CRM record only

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Empty(_files.Deleted);                    // the file is kept for the others
        Assert.Contains(OldFileId, _write.DeletedFiles); // our old row is gone
        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task A_file_nobody_else_points_at_is_deleted_as_usual()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId };   // only us

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Contains(OldPath, _files.Deleted);
        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task The_log_says_the_file_was_found_before_it_was_deleted()
    {
        var prompts = Confirmed();

        await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        var log = string.Join("|", prompts.Messages);
        Assert.Contains("before      found on the server", log);
        Assert.Contains("delete      the file server accepted", log);
        Assert.Contains("after       confirmed gone from the server", log);
        Assert.Contains("CRM         mocd_documentfile", log);
        Assert.Contains("RESULT      done", log);
    }

    [Fact]
    public async Task A_file_already_absent_is_said_so_and_the_crm_record_still_goes()
    {
        _files.Files.Remove(OldPath);
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Contains("before      NOT on the server", string.Join("|", prompts.Messages));
        Assert.Contains(OldFileId, _write.DeletedFiles);
        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task A_server_that_says_deleted_but_keeps_the_file_is_caught()
    {
        _files.PretendToDelete = true;
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Contains("STILL ON THE SERVER", string.Join("|", prompts.Messages));
        Assert.Empty(_write.DeletedFiles);      // the CRM record is kept, so the two agree
    }

    [Fact]
    public async Task A_refused_delete_leaves_the_crm_record_in_place()
    {
        _files.DeleteRefusal = "access denied";
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Contains("the file server refused: access denied", string.Join("|", prompts.Messages));
        Assert.Empty(_write.DeletedFiles);
    }
}
