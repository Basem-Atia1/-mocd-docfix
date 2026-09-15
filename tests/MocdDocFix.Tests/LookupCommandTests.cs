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
[Collection(LedgerCollection.Name)]
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
    private LedgerStore Ledger() => new(Path.Combine(_root, "repair-dev.csv"));

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
        new(_files, _read, Backups(), _prompts, Ledger());

    /// <summary>
    /// One look-up, through the same entry point the menu uses — so every test covers what is
    /// printed as well as what is found.
    /// </summary>
    private async Task<LookupReport> Look(string asked) =>
        (await Command().RunAsync(new[] { asked }, CancellationToken.None))[0];

    /// <summary>A document this tool backed up and corrected, so its own notes have something.</summary>
    private void WeHaveBeenHere(RowState state)
    {
        var backups = Backups();
        var saved = backups.Save(DocumentId, OldFileId, ".png", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH",
            "Screenshot.png", "image/png", ".png", null, Correct, saved.LocalPath, saved.Bytes,
            Verifier.OurHash(Content), DateTimeOffset.UtcNow));

        Ledger().Write(new[]
        {
            new LedgerRow
            {
                Row = 1,
                DocId = DocumentId,
                DocFileId = OldFileId,
                DocFileName = "Screenshot.png",
                OldFilePath = OldPath,
                NewFilePath = NewPath,
                Verdict = RowVerdicts.Fix,
                FinalState = RowStates.Text(state)
            }
        });
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
        WeHaveBeenHere(RowState.Deleted);
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

    /// <summary>
    /// What this tool knows is now the backup folder alone — how far a document got is the
    /// ledger's business, and it is read there rather than repeated here.
    /// </summary>
    [Fact]
    public async Task The_backup_is_reported_beside_the_two_systems()
    {
        WeHaveBeenHere(RowState.Deleted);

        var report = await Look(OldPath);

        Assert.Equal(DocumentId, report.Backup?.DocumentId);
        Assert.Contains("backed up", string.Join("\n", _prompts.Messages));
    }

    /// <summary>
    /// Asking about the file a document uses NOW is as reasonable as asking about the old one,
    /// and the answer should still say which document it belongs to.
    /// </summary>
    [Fact]
    public async Task The_new_path_of_a_migrated_document_is_recognised_too()
    {
        WeHaveBeenHere(RowState.Corrected);
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
        WeHaveBeenHere(RowState.Corrected);

        await Command().RunAsync(new[] { OldPath, OldFileId.ToString() }, CancellationToken.None);

        Assert.Empty(_files.Deleted);
        Assert.Empty(_files.Uploads);

        // The ledger is read, never written — a look-up must not advance anything.
        Assert.Equal(RowState.Corrected, Ledger().Read()[0].State());
    }

    [Fact]
    public async Task Several_can_be_asked_about_at_once()
    {
        var reports = await Command().RunAsync(
            new[] { OldPath, OldFileId.ToString() }, CancellationToken.None);

        Assert.Equal(2, reports.Count);
    }

    // ---- when a system cannot be asked at all ----

    /// <summary>
    /// The failure that started this: an id typed in, and the connection dropped underneath the
    /// CRM call. It used to abandon the whole look-up with a stack of transport exceptions. What
    /// it must never do instead is quietly report the record as deleted — nobody answered.
    /// </summary>
    [Fact]
    public async Task An_id_crm_could_not_be_asked_about_is_never_called_gone()
    {
        _read.UnreachableRecords[OldFileId] =
            "An existing connection was forcibly closed by the remote host.";

        var report = await Look(OldFileId.ToString());

        Assert.False(report.RecordIsGone);
        Assert.NotNull(report.CrmProblem);

        var said = string.Join("\n", _prompts.Messages);
        Assert.Contains("CRM could not be asked", said);
        Assert.DoesNotContain("GONE", said);
    }

    [Fact]
    public async Task A_file_server_that_drops_the_connection_is_said_plainly_not_read_as_deleted()
    {
        _files.DownloadThrows[OldPath] = "An existing connection was forcibly closed by the remote host.";

        var report = await Look(OldPath);

        Assert.Null(report.OnTheServer);
        Assert.NotNull(report.ServerProblem);

        var said = string.Join("\n", _prompts.Messages);
        Assert.Contains("NOT ANSWERED", said);
        Assert.DoesNotContain("GONE", said);
    }

    /// <summary>
    /// The GUID an operator has to hand is usually the one written on the file — the file
    /// server's id, which is not the CRM record's id. Looked up as a record it is simply absent,
    /// which reads as "deleted" for a record that is in fact perfectly well.
    /// </summary>
    [Fact]
    public async Task A_file_id_off_the_path_finds_the_record_that_uses_it()
    {
        var fileId = Guid.Parse("35687738-986f-413d-8846-6dc1a720a1ec");
        _read.MissingRecords.Add($"mocd_documentfiles:{fileId}");
        _read.FilesByFileId[fileId] = new List<Guid> { OldFileId };

        var report = await Look(fileId.ToString());

        Assert.Equal(OldPath, report.Path);
        Assert.True(report.OnTheServer);
        Assert.Contains("file server's file id", string.Join("\n", _prompts.Messages));
    }

    [Fact]
    public async Task One_identifier_that_cannot_be_looked_up_does_not_stop_the_rest()
    {
        var other = Guid.Parse("11111111-2222-3333-4444-555555555555");
        _read.MissingRecords.Add($"mocd_documentfiles:{other}");
        _files.DownloadThrows[OldPath] = "boom";

        var reports = await Command().RunAsync(
            new[] { OldPath, other.ToString() }, CancellationToken.None);

        Assert.Equal(2, reports.Count);
    }
}
