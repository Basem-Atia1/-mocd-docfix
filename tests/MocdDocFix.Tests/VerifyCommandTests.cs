using System.Text;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class VerifyCommandTests : IDisposable
{
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");
    private static readonly Guid OldFileId = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");
    private static readonly Guid NewFileId = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");

    private const string OldPath = @"DigitalServices\goodConductCertificate\20260330\5b05398a.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260913\{NewFileId}.jpg";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-ver-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public VerifyCommandTests()
    {
        Directory.CreateDirectory(_root);

        var backups = Backups();
        var saved = backups.Save(DocumentId, OldFileId, ".jpg", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH", "cert.jpg",
            "image/jpeg", ".jpg", "goodConductCertificate", Correct, saved.LocalPath,
            saved.Bytes, saved.OurHash, DateTimeOffset.UtcNow));

        // The healthy end state: repointed, both sides still present.
        States().Append(new StateRecord(DocumentId, MigrationState.Repointed,
            DateTimeOffset.UtcNow, NewFileId, NewPath, null));

        _files.Files[OldPath] = (Convert.ToBase64String(Content), "VHASH");
        _files.Files[NewPath] = (Convert.ToBase64String(Content), "VHASH");
        _read.RawRecords[$"mocd_documentfiles:{OldFileId}"] = """{"mocd_documentfileid":"old"}""";
        _read.RawRecords[$"mocd_documentfiles:{NewFileId}"] =
            "{\"mocd_filepath\":\"" + NewPath.Replace("\\", "\\\\") + "\"}";
        _write.Links[DocumentId] = NewFileId;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private VerifyCommand Command() =>
        new(_files, _read, _write, Backups(), States(), "https://crm/MoCD",
            Path.Combine(_root, "reports"));

    private void MarkDeleted()
    {
        States().Append(new StateRecord(DocumentId, MigrationState.Deleted,
            DateTimeOffset.UtcNow, NewFileId, NewPath, null));
        _files.Files.Remove(OldPath);
        _read.MissingRecords.Add($"mocd_documentfiles:{OldFileId}");
    }

    [Fact]
    public async Task A_repointed_document_with_everything_in_place_is_ok()
    {
        var summary = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Checked);
        Assert.Equal(1, summary.Ok);
        Assert.Empty(Assert.Single(summary.Verdicts).Problems);
    }

    [Fact]
    public async Task A_deleted_document_is_ok_when_both_old_sides_have_gone()
    {
        MarkDeleted();

        var summary = await Command().RunAsync("dev", CancellationToken.None);

        var v = Assert.Single(summary.Verdicts);
        Assert.Empty(v.Problems);
        Assert.False(v.OldFileOnServer);
        Assert.False(v.OldRecordInCrm);
        Assert.True(v.NewFileOnServer);
        Assert.True(v.NewRecordInCrm);
    }

    // ---- the file server ----

    [Fact]
    public async Task An_old_file_still_on_the_server_after_a_delete_is_a_problem()
    {
        MarkDeleted();
        _files.Files[OldPath] = (Convert.ToBase64String(Content), "VHASH");   // it survived

        var v = Assert.Single((await Command().RunAsync("dev", CancellationToken.None)).Verdicts);

        Assert.Contains(v.Problems, p => p.Contains("still on the file server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_new_file_is_a_problem()
    {
        _files.Files.Remove(NewPath);

        var v = Assert.Single((await Command().RunAsync("dev", CancellationToken.None)).Verdicts);

        Assert.Contains(v.Problems, p => p.Contains("NEW file is not on the file server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_old_file_that_vanished_without_being_deleted_is_a_problem()
    {
        _files.Files.Remove(OldPath);

        var v = Assert.Single((await Command().RunAsync("dev", CancellationToken.None)).Verdicts);

        Assert.Contains(v.Problems, p => p.Contains("nothing recorded deleting it", StringComparison.Ordinal));
    }

    // ---- CRM ----

    [Fact]
    public async Task An_old_record_still_in_crm_after_a_delete_is_a_problem()
    {
        MarkDeleted();
        _read.MissingRecords.Remove($"mocd_documentfiles:{OldFileId}");   // the row survived

        var v = Assert.Single((await Command().RunAsync("dev", CancellationToken.None)).Verdicts);

        Assert.Contains(v.Problems, p => p.Contains("old mocd_documentfile record is still in CRM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_new_record_is_a_problem()
    {
        _read.MissingRecords.Add($"mocd_documentfiles:{NewFileId}");

        var v = Assert.Single((await Command().RunAsync("dev", CancellationToken.None)).Verdicts);

        Assert.Contains(v.Problems, p => p.Contains("NEW mocd_documentfile record does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_record_pointing_at_the_old_path_is_a_problem()
    {
        _read.RawRecords[$"mocd_documentfiles:{NewFileId}"] =
            "{\"mocd_filepath\":\"" + OldPath.Replace("\\", "\\\\") + "\"}";

        var v = Assert.Single((await Command().RunAsync("dev", CancellationToken.None)).Verdicts);

        Assert.Contains(v.Problems, p => p.Contains("mocd_filepath", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_document_pointing_somewhere_else_is_a_problem()
    {
        _write.Links[DocumentId] = OldFileId;

        var v = Assert.Single((await Command().RunAsync("dev", CancellationToken.None)).Verdicts);

        Assert.Contains(v.Problems, p => p.Contains("document points at", StringComparison.Ordinal));
    }

    // ---- what it checks, and what it does not ----

    [Fact]
    public async Task Documents_that_were_never_repointed_are_not_checked()
    {
        States().Append(new StateRecord(DocumentId, MigrationState.BackedUp,
            DateTimeOffset.UtcNow, null, null, null));

        Assert.Equal(0, (await Command().RunAsync("dev", CancellationToken.None)).Checked);
    }

    [Fact]
    public async Task It_repairs_nothing()
    {
        _write.Links[DocumentId] = OldFileId;

        await Command().RunAsync("dev", CancellationToken.None);

        Assert.Equal(OldFileId, _write.Links[DocumentId]);   // left exactly as found
        Assert.Empty(_write.CreatedFiles);
        Assert.Empty(_write.DeletedFiles);
        Assert.Empty(_files.Deleted);
    }

    // ---- the report ----

    [Fact]
    public async Task The_report_says_yes_or_no_for_each_side_and_links_to_the_document()
    {
        var summary = await Command().RunAsync("dev", CancellationToken.None);

        Assert.True(File.Exists(summary.ReportPath));
        var text = File.ReadAllText(summary.ReportPath);

        Assert.Contains("old file on server", text);
        Assert.Contains("old record in CRM", text);
        Assert.Contains("new file on server", text);
        Assert.Contains("new record in CRM", text);
        Assert.Contains($"id={DocumentId}", text);
        Assert.Contains("OK", text);
    }

    [Fact]
    public async Task The_report_names_every_problem_it_found()
    {
        _files.Files.Remove(NewPath);
        _write.Links[DocumentId] = OldFileId;

        var summary = await Command().RunAsync("dev", CancellationToken.None);
        var text = File.ReadAllText(summary.ReportPath);

        Assert.Contains("WRONG", text);
        Assert.Contains("PROBLEM", text);
        Assert.Equal(2, Assert.Single(summary.Verdicts).Problems.Count);
    }
}
