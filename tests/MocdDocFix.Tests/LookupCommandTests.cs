using System.Text;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The look-up answers one question — is this file still there? — and it is the only part of the
/// tool that can be pointed at a single file without also wanting to change something. It has to
/// be right about "gone", because that is the answer people act on.
/// </summary>
public class LookupCommandTests : IDisposable
{
    private static readonly Guid OldFileId = Guid.Parse("2a1c51a3-e330-f111-b119-005056010908");
    private static readonly Guid NewFileId = Guid.Parse("99d7dbea-19b0-f111-b119-005056010908");
    private static readonly Guid DocumentId = Guid.Parse("c0c2a661-e330-f111-b119-005056010908");
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");

    private const string OldPath = @"DigitalServices\20260405\35687738-986f-413d-8846-6dc1a720a1ec.png";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260914\78efd6b2.png";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("the bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-look-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakePrompts _prompts = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public LookupCommandTests()
    {
        Directory.CreateDirectory(_root);

        _files.Files[OldPath] = (Convert.ToBase64String(Content), "VHASH");
        _read.RawRecords[$"mocd_documentfiles:{OldFileId}"] =
            "{\"mocd_filepath\":\"" + OldPath.Replace("\\", "\\\\") + "\"," +
            "\"mocd_name\":\"Screenshot.png\",\"mocd_category\":\"" + Correct + "\"}";
        _read.FilesByPath[OldPath] = new List<Guid> { OldFileId };
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private LookupCommand Command() =>
        new(_files, _read, Backups(), States(), _prompts);

    /// <summary>
    /// One look-up, through the same entry point the menu uses — so every test covers what is
    /// printed as well as what is found.
    /// </summary>
    private async Task<LookupReport> Look(string asked) =>
        (await Command().RunAsync(new[] { asked }, CancellationToken.None))[0];

    /// <summary>A document this tool backed up and migrated, so its own notes have something.</summary>
    private void WeHaveBeenHere(MigrationState state)
    {
        var backups = Backups();
        var saved = backups.Save(DocumentId, OldFileId, ".png", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH",
            "Screenshot.png", "image/png", ".png", null, Correct, saved.LocalPath, saved.Bytes,
            Verifier.OurHash(Content), DateTimeOffset.UtcNow));

        States().Append(new StateRecord(DocumentId, state, DateTimeOffset.UtcNow,
            NewFileId, NewPath, "Deleted the old file and its record."));
    }

    // ---- asked by path ----

    [Fact]
    public async Task A_path_that_is_on_the_server_and_in_crm_says_both()
    {
        var report = await Look(OldPath);

        Assert.True(report.OnTheServer);
        Assert.Contains(OldFileId, report.PointingAtThePath);
        Assert.Contains("BOTH", string.Join("\n", _prompts.Messages));
    }

    [Fact]
    public async Task A_path_the_server_no_longer_has_is_said_plainly()
    {
        _files.Files.Remove(OldPath);

        var report = await Look(OldPath);

        Assert.False(report.OnTheServer);
        Assert.Contains("CRM ONLY", string.Join("\n", _prompts.Messages));
    }

    /// <summary>
    /// The state everyone wants to be sure of after a delete: nothing left in either place.
    /// </summary>
    [Fact]
    public async Task A_file_gone_from_both_places_says_gone()
    {
        _files.Files.Remove(OldPath);
        _read.FilesByPath[OldPath] = new List<Guid>();

        var report = await Look(OldPath);

        Assert.False(report.OnTheServer);
        Assert.Empty(report.PointingAtThePath);
        Assert.Contains("GONE", string.Join("\n", _prompts.Messages));
    }

    [Fact]
    public async Task A_file_on_disk_that_nothing_refers_to_is_called_out()
    {
        _read.FilesByPath[OldPath] = new List<Guid>();

        await Look(OldPath);

        Assert.Contains("FILE ONLY", string.Join("\n", _prompts.Messages));
    }

    /// <summary>
    /// The path is copied off a screen, so it arrives with a UNC prefix, forward slashes or
    /// quotes around it. Any of those would otherwise look like a different file.
    /// </summary>
    [Fact]
    public async Task A_path_pasted_in_any_of_its_usual_forms_finds_the_same_file()
    {
        foreach (var typed in new[]
                 {
                     OldPath,
                     @"\\mocdfs01\share\" + OldPath,
                     OldPath.Replace('\\', '/'),
                     "\"" + OldPath + "\""
                 })
        {
            var report = await Look(typed);

            Assert.True(report.OnTheServer, $"'{typed}' was not recognised");
        }
    }

    // ---- asked by documentfile id ----

    [Fact]
    public async Task An_id_still_in_crm_reports_the_record_and_checks_its_path()
    {
        var report = await Look(OldFileId.ToString());

        Assert.False(report.RecordIsGone);
        Assert.Equal(OldPath, report.Record?.FilePath);
        Assert.Equal("Screenshot.png", report.Record?.Name);
        Assert.True(report.OnTheServer);
    }

    /// <summary>
    /// A record that has gone takes its path with it. Without falling back to the backup there
    /// would be nothing to ask the file server about — which is exactly the case that matters,
    /// because "is the file gone too?" is the question after a delete.
    /// </summary>
    [Fact]
    public async Task An_id_crm_no_longer_has_is_still_checked_against_the_backup_path()
    {
        WeHaveBeenHere(MigrationState.Deleted);
        _read.RawRecords.Remove($"mocd_documentfiles:{OldFileId}");
        _read.MissingRecords.Add($"mocd_documentfiles:{OldFileId}");

        var report = await Look(OldFileId.ToString());

        Assert.True(report.RecordIsGone);
        Assert.Equal(OldPath, report.Path);
        Assert.True(report.OnTheServer);           // the row went, the file did not
        Assert.NotNull(report.Backup);
    }

    [Fact]
    public async Task An_id_that_is_nowhere_says_so_rather_than_pretending_to_check()
    {
        var unknown = Guid.Parse("11111111-2222-3333-4444-555555555555");
        _read.MissingRecords.Add($"mocd_documentfiles:{unknown}");

        var report = await Look(unknown.ToString());

        Assert.True(report.RecordIsGone);
        Assert.Null(report.Path);
        Assert.Null(report.OnTheServer);
        Assert.Contains("no path to ask the file server about",
            string.Join("\n", _prompts.Messages));
    }

    // ---- what this tool itself knows ----

    [Fact]
    public async Task The_backup_and_the_state_are_reported_beside_the_two_systems()
    {
        WeHaveBeenHere(MigrationState.Deleted);

        var report = await Look(OldPath);

        Assert.Equal(DocumentId, report.Backup?.DocumentId);
        Assert.Equal(MigrationState.Deleted, report.State?.State);

        var said = string.Join("\n", _prompts.Messages);
        Assert.Contains("backed up", said);
        Assert.Contains("Deleted", said);
    }

    /// <summary>
    /// Asking about the file a document uses NOW is as reasonable as asking about the old one,
    /// and the answer should still say which document it belongs to.
    /// </summary>
    [Fact]
    public async Task The_new_path_of_a_migrated_document_is_recognised_too()
    {
        WeHaveBeenHere(MigrationState.Repointed);
        _files.Files[NewPath] = (Convert.ToBase64String(Content), "VHASH");

        var report = await Look(NewPath);

        Assert.True(report.OnTheServer);
        Assert.Equal(DocumentId, report.Backup?.DocumentId);
    }

    [Fact]
    public async Task A_file_this_tool_has_never_seen_says_that_much()
    {
        await Look(OldPath);

        Assert.Contains("never been through this tool", string.Join("\n", _prompts.Messages));
    }

    // ---- it never writes ----

    [Fact]
    public async Task Nothing_is_deleted_uploaded_or_written()
    {
        WeHaveBeenHere(MigrationState.Repointed);

        await Command().RunAsync(new[] { OldPath, OldFileId.ToString() }, CancellationToken.None);

        Assert.Empty(_files.Deleted);
        Assert.Empty(_files.Uploads);
        Assert.Equal(MigrationState.Repointed, States().LoadLatest()[DocumentId].State);
    }

    [Fact]
    public async Task Several_can_be_asked_about_at_once()
    {
        var reports = await Command().RunAsync(
            new[] { OldPath, OldFileId.ToString() }, CancellationToken.None);

        Assert.Equal(2, reports.Count);
    }
}
