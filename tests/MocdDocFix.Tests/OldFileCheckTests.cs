using System.Text;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The closing check of a run: the old file, asked about by name, in both systems. It is the
/// menu's own look-up run for every document, so what it must get right is the difference
/// between "we recorded a delete" and "it is actually gone".
/// </summary>
public class OldFileCheckTests : IDisposable
{
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");
    private static readonly Guid OldFileId = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");
    private static readonly Guid NewFileId = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");

    private const string OldPath = @"DigitalServices\goodConductCertificate\20260330\5b05398a.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260914\a41c0b77.jpg";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-old-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakePrompts _prompts = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));
    private DocumentReportStore Reports() => new(Path.Combine(_root, "reports"));

    public OldFileCheckTests()
    {
        Directory.CreateDirectory(_root);

        var backups = Backups();
        var saved = backups.Save(DocumentId, OldFileId, ".jpg", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH", "cert.jpg",
            "image/jpeg", ".jpg", "goodConductCertificate", Correct, saved.LocalPath, saved.Bytes,
            Verifier.OurHash(Content), DateTimeOffset.UtcNow));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private void GotTo(MigrationState state) =>
        States().Append(new StateRecord(DocumentId, state, DateTimeOffset.UtcNow,
            NewFileId, NewPath, null));

    /// <summary>The old file is on the server and a record still refers to it.</summary>
    private void TheOldFileIsStillThere()
    {
        _files.Files[OldPath] = (Convert.ToBase64String(Content), "VHASH");
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId };
    }

    private void TheOldFileHasGone()
    {
        _files.Files.Remove(OldPath);
        _read.FilesByPath[OldPath] = new List<Guid>();
        _read.MissingRecords.Add($"mocd_documentfiles:{OldFileId}");
    }

    private Task<OldFileSummary> Run() =>
        new OldFileCheckCommand(
                new LookupCommand(_files, _read, Backups(), States(), _prompts),
                Backups(), States(), _prompts, Reports())
            .RunAsync(CancellationToken.None);

    // ---- what the run hoped for ----

    [Fact]
    public async Task A_deleted_document_whose_old_file_has_gone_from_both_is_as_expected()
    {
        GotTo(MigrationState.Deleted);
        TheOldFileHasGone();

        var summary = await Run();

        var result = Assert.Single(summary.Results);
        Assert.True(result.AsExpected);
        Assert.Equal(1, summary.AsExpected);
        Assert.Contains("GONE", result.Verdict);
    }

    /// <summary>
    /// The one the whole check exists for: the tool wrote down a delete, and the file is there.
    /// </summary>
    [Fact]
    public async Task A_deleted_document_whose_file_is_still_on_the_server_is_called_out()
    {
        GotTo(MigrationState.Deleted);
        TheOldFileIsStillThere();

        var summary = await Run();

        var result = Assert.Single(summary.Results);
        Assert.False(result.AsExpected);
        Assert.Equal(1, summary.NotAsExpected);
        Assert.Contains("STILL THERE", result.Verdict);
        Assert.Contains("still on the server", result.Verdict);
    }

    [Fact]
    public async Task A_deleted_document_whose_crm_record_survived_is_called_out()
    {
        GotTo(MigrationState.Deleted);
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId };     // the row is still there

        var summary = await Run();

        var result = Assert.Single(summary.Results);
        Assert.False(result.AsExpected);
        Assert.True(result.InCrm);
    }

    // ---- a document that was repointed but not yet deleted ----

    [Fact]
    public async Task A_repointed_document_still_holding_its_old_file_is_as_expected()
    {
        GotTo(MigrationState.Repointed);
        TheOldFileIsStillThere();

        var summary = await Run();

        var result = Assert.Single(summary.Results);
        Assert.True(result.AsExpected);
        Assert.Contains("repointed, not deleted", result.Verdict);
    }

    [Fact]
    public async Task A_repointed_document_whose_old_file_vanished_is_called_out()
    {
        GotTo(MigrationState.Repointed);
        TheOldFileHasGone();

        var summary = await Run();

        var result = Assert.Single(summary.Results);
        Assert.False(result.AsExpected);
        Assert.Contains("MISSING", result.Verdict);
    }

    // ---- honesty about what could not be asked ----

    [Fact]
    public async Task A_file_server_that_cannot_be_asked_is_never_read_as_a_successful_delete()
    {
        GotTo(MigrationState.Deleted);
        _read.MissingRecords.Add($"mocd_documentfiles:{OldFileId}");
        _read.FilesByPath[OldPath] = new List<Guid>();
        _files.DownloadThrows[OldPath] = "An existing connection was forcibly closed by the remote host.";

        var summary = await Run();

        var result = Assert.Single(summary.Results);
        Assert.False(result.Answered);
        Assert.False(result.AsExpected);
        Assert.Contains("NOT ANSWERED", result.Verdict);
    }

    // ---- where the answer is written down ----

    [Fact]
    public async Task Each_documents_answer_lands_in_its_own_folder()
    {
        GotTo(MigrationState.Deleted);
        TheOldFileHasGone();

        await Run();

        var path = Path.Combine(Reports().FolderFor(DocumentId, "cert.jpg"), "06-old-file.txt");
        Assert.True(File.Exists(path), path);

        var text = File.ReadAllText(path);
        Assert.Contains(OldPath, text);
        Assert.Contains("GONE", text);
    }

    [Fact]
    public async Task A_document_that_never_got_a_new_file_is_not_asked_about()
    {
        GotTo(MigrationState.BackedUp);
        TheOldFileIsStillThere();

        var summary = await Run();

        Assert.Equal(0, summary.Checked);
        Assert.Empty(summary.Results);
    }
}
