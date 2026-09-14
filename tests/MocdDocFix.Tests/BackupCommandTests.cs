using System.Text;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class BackupCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-bk-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public BackupCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private const string Path1 = @"DigitalServices\goodConductCertificate\20260330\a.jpg";
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");
    private static readonly string Base64 = Convert.ToBase64String(Content);

    private static ScanRow Row(string path, string fileName = "cert.jpg") => new(
        Guid.NewGuid(), Guid.NewGuid(), fileName, "Certificate of Good Conduct",
        Guid.NewGuid(), "Employee Appointment Request", path, "goodConductCertificate", null,
        Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), nameof(Verdict.Fix),
        3, "path holds a name instead of an id", "reason", "solution",
        null, "https://crm/x");

    private BackupCommand Command(Reporter? reporter = null) =>
        new(_files, _read, Backups(), States(),
            reporter ?? new Reporter(System.IO.Path.Combine(_root, "reports")),
            hashLookup: _ => "VENDORHASH");

    [Fact]
    public async Task Saves_the_file_and_records_the_manifest()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Saved);
        Assert.Equal(0, summary.Quarantined);
        Assert.Equal(Content.Length, summary.TotalBytes);

        var entry = Assert.Single(Backups().LoadManifest());
        Assert.Equal(row.DocumentId, entry.DocumentId);
        Assert.Equal(Path1, entry.OldFilePath);
        Assert.Equal("VENDORHASH", entry.OldVendorHash);
        Assert.True(File.Exists(entry.LocalPath));
        Assert.Equal(Content, File.ReadAllBytes(entry.LocalPath));
    }

    [Fact]
    public async Task Marks_the_document_BackedUp_in_the_state_store()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.True(States().IsAtLeast(row.DocumentId, MigrationState.BackedUp));
    }

    [Fact]
    public async Task A_hash_disagreement_quarantines_the_file_and_does_not_save_it()
    {
        _files.Files[Path1] = (Base64, "A-DIFFERENT-HASH");
        var row = Row(Path1);

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Saved);
        Assert.Equal(1, summary.Quarantined);
        Assert.Empty(Backups().LoadManifest());
        Assert.False(States().IsAtLeast(row.DocumentId, MigrationState.BackedUp));
        Assert.Single(summary.QuarantinedRows);
    }

    [Fact]
    public async Task A_download_failure_quarantines_rather_than_throwing()
    {
        var summary = await Command().RunAsync("dev", new[] { Row(@"DigitalServices\missing\x\y.jpg") },
            CancellationToken.None);

        Assert.Equal(0, summary.Saved);
        Assert.Equal(1, summary.Quarantined);
    }

    [Fact]
    public async Task A_re_run_keeps_one_restore_record_per_document()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Single(Backups().LoadManifest());       // refreshed, not duplicated
    }

    [Fact]
    public async Task A_quarantine_report_is_written_when_anything_is_quarantined()
    {
        _files.Files[Path1] = (Base64, "WRONG");
        var reportsDir = System.IO.Path.Combine(_root, "reports");

        await Command(new Reporter(reportsDir)).RunAsync("dev", new[] { Row(Path1) }, CancellationToken.None);

        Assert.Single(Directory.GetFiles(reportsDir, "quarantine-dev-*.csv"));
    }

    [Fact]
    public async Task The_manifest_snapshots_both_crm_records_and_recovers_the_media_type()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        _read.RawRecords[$"mocd_documentfiles:{row.DocumentFileId}"] =
            """{"mocd_documentfileid":"x","mocd_mediatype":"image/jpeg","mocd_filesize":"350208","mocd_extension":".jpg"}""";
        _read.RawRecords[$"mocd_documents:{row.DocumentId}"] =
            """{"mocd_documentid":"y","_mocd_documentfile_value":"z","statuscode":1}""";

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var entry = Assert.Single(Backups().LoadManifest());
        Assert.Contains("mocd_filesize", entry.DocumentFileSnapshotJson!);
        Assert.Contains("_mocd_documentfile_value", entry.DocumentSnapshotJson!);
        Assert.Equal("image/jpeg", entry.MediaType);          // recovered from the snapshot
        Assert.Equal("Certificate of Good Conduct", entry.DocumentTypeName);
    }

    [Fact]
    public async Task Unparseable_snapshot_json_is_still_stored_verbatim_and_media_type_is_just_absent()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        _read.RawRecords[$"mocd_documentfiles:{row.DocumentFileId}"] = "<html>not json</html>";

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Saved);
        Assert.Null(Backups().LoadManifest()[0].MediaType);
        Assert.Contains("not json", Backups().LoadManifest()[0].DocumentFileSnapshotJson!);
    }

    // --- backup completeness: a full image, or nothing (spec section 8.2) ---

    [Fact]
    public async Task A_complete_image_is_written_as_a_readable_sidecar_next_to_the_file()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        _read.RawRecords[$"mocd_documentfiles:{row.DocumentFileId}"] =
            """{"mocd_documentfileid":"x","mocd_mediatype":"image/jpeg","mocd_filesize":"350208"}""";
        _read.RawRecords[$"mocd_documents:{row.DocumentId}"] =
            """{"mocd_documentid":"y","_mocd_relationship_value":"z","_mocd_relationship_value@Microsoft.Dynamics.CRM.lookuplogicalname":"mocd_nporelationship"}""";
        _read.Annotations[row.DocumentId] = """{"value":[{"annotationid":"a1","filename":"logo.png"}]}""";

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var entry = Assert.Single(Backups().LoadManifest());
        Assert.False(string.IsNullOrWhiteSpace(entry.CrmImagePath));
        Assert.True(File.Exists(entry.CrmImagePath));

        var image = File.ReadAllText(entry.CrmImagePath!);
        Assert.Contains("mocd_documentfileid", image);          // the documentfile record
        Assert.Contains("mocd_documentid", image);              // the document record
        Assert.Contains("lookuplogicalname", image);            // lookup types survived
        Assert.Contains("logo.png", image);                     // annotations
        Assert.Contains(row.DocumentId.ToString(), image);

        // The bytes sit beside it.
        Assert.True(File.Exists(entry.LocalPath));
    }

    [Fact]
    public async Task A_missing_document_snapshot_quarantines_and_saves_nothing()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        _read.RawRecords[$"mocd_documents:{row.DocumentId}"] = null!;   // read failed

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Saved);
        Assert.Equal(1, summary.Quarantined);
        Assert.Empty(Backups().LoadManifest());
        Assert.False(States().IsAtLeast(row.DocumentId, MigrationState.BackedUp));
    }

    [Fact]
    public async Task A_missing_documentfile_snapshot_quarantines_and_saves_nothing()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        _read.RawRecords[$"mocd_documentfiles:{row.DocumentFileId}"] = null!;

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Saved);
        Assert.Equal(1, summary.Quarantined);
        Assert.Empty(Backups().LoadManifest());
    }

    [Fact]
    public async Task A_failed_annotations_query_quarantines_because_the_image_would_be_incomplete()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        _read.Annotations[row.DocumentId] = null;   // query failed, not "none exist"

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Saved);
        Assert.Equal(1, summary.Quarantined);
    }

    [Fact]
    public async Task A_document_with_no_annotations_backs_up_normally()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        _read.Annotations[row.DocumentId] = """{"value":[]}""";

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Saved);
    }

    // ---- one folder per document ----

    [Fact]
    public async Task Everything_for_a_document_lands_in_its_own_folder()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var folder = Backups().Folder(row.DocumentId);

        Assert.True(File.Exists(folder.SummaryPath), "document.txt");
        Assert.True(Directory.Exists(folder.OldDir), "old\\");
        Assert.True(File.Exists(System.IO.Path.Combine(folder.OldDir, "crm.json")), "old\\crm.json");

        var bytes = Directory.GetFiles(folder.OldDir).Single(f => f.EndsWith(".jpg", StringComparison.Ordinal));
        Assert.Equal(Content, File.ReadAllBytes(bytes));
        Assert.Contains(row.DocumentFileId.ToString(), bytes);
    }

    [Fact]
    public async Task The_document_record_explains_the_file_and_what_was_saved()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var text = File.ReadAllText(Backups().Folder(row.DocumentId).SummaryPath);

        Assert.Contains(row.DocumentId.ToString(), text);
        Assert.Contains("cert.jpg", text);
        Assert.Contains("THE OLD FILE", text);
        Assert.Contains("VENDORHASH", text);
        Assert.Contains(Path1, text);
        Assert.Contains("bytes", text);
    }

    [Fact]
    public async Task Two_documents_do_not_share_a_folder()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var a = Row(Path1);
        var b = Row(Path1, "other.jpg");

        await Command().RunAsync("dev", new[] { a, b }, CancellationToken.None);

        Assert.NotEqual(Backups().Folder(a.DocumentId).Root, Backups().Folder(b.DocumentId).Root);
        Assert.True(File.Exists(Backups().Folder(a.DocumentId).SummaryPath));
        Assert.True(File.Exists(Backups().Folder(b.DocumentId).SummaryPath));
    }

    // ---- a failure has to say why ----

    [Fact]
    public async Task A_quarantined_document_records_why_in_its_own_folder()
    {
        // No entry in _files.Files, so the download fails.
        var row = Row(Path1);

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Quarantined);

        var text = File.ReadAllText(Backups().Folder(row.DocumentId).SummaryPath);

        Assert.Contains("NOT BACKED UP", text);
        Assert.Contains("Download failed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the state file is a claim about the disk, not the truth ----

    [Fact]
    public async Task A_backup_whose_bytes_have_gone_is_downloaded_again_rather_than_skipped()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        // Someone deletes the saved copy. The state file still says BackedUp.
        File.Delete(Backups().LoadManifest().Single().LocalPath);
        Assert.True(States().IsAtLeast(row.DocumentId, MigrationState.BackedUp));

        var second = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, second.Saved);
        Assert.Equal(0, second.Skipped);
        Assert.True(File.Exists(Backups().LoadManifest().Single().LocalPath));
    }

    [Fact]
    public async Task A_backup_whose_crm_image_has_gone_is_downloaded_again()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        File.Delete(Backups().LoadManifest().Single().CrmImagePath!);

        var second = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, second.Saved);
    }

    [Fact]
    public async Task A_backup_whose_restore_record_has_gone_is_downloaded_again()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        File.Delete(System.IO.Path.Combine(Backups().Folder(row.DocumentId).Root, "restore.json"));

        var second = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, second.Saved);
    }

    [Fact]
    public async Task Re_downloading_a_lost_backup_says_so_rather_than_doing_it_silently()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        var said = new List<string>();

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);
        File.Delete(Backups().LoadManifest().Single().LocalPath);

        await new BackupCommand(_files, _read, Backups(), States(),
                new Reporter(System.IO.Path.Combine(_root, "reports")),
                _ => "VENDORHASH", (m, _) => said.Add(m))
            .RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Contains(said, m => m.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Asking_for_a_document_again_backs_it_up_again()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var second = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, second.Saved);
        Assert.Equal(0, second.Skipped);
    }

    [Fact]
    public async Task Backing_up_again_replaces_an_identical_copy_without_leaving_clutter()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);
        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var saved = Directory.GetFiles(Backups().Folder(row.DocumentId).OldDir, "*.jpg");
        Assert.Single(saved);
        Assert.Equal(Content, File.ReadAllBytes(saved[0]));
    }

    [Fact]
    public async Task A_file_that_has_changed_on_the_server_never_overwrites_the_earlier_copy()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        // The server's copy is now different. Its hash moves with it, so check 1 still passes.
        var changed = Encoding.UTF8.GetBytes("a DIFFERENT certificate");
        _files.Files[Path1] = (Convert.ToBase64String(changed), "VENDORHASH");

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var oldDir = Backups().Folder(row.DocumentId).OldDir;
        Assert.Single(Directory.GetFiles(oldDir, "*.superseded-*.jpg"));
        Assert.Equal(changed, File.ReadAllBytes(Backups().LoadManifest().Single().LocalPath));

        var kept = Directory.GetFiles(oldDir, "*.superseded-*.jpg").Single();
        Assert.Equal(Content, File.ReadAllBytes(kept));
    }

    [Fact]
    public async Task A_changed_file_is_reported_loudly_rather_than_silently()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        var said = new List<string>();

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);
        _files.Files[Path1] = (Convert.ToBase64String(Encoding.UTF8.GetBytes("different")), "VENDORHASH");

        await new BackupCommand(_files, _read, Backups(), States(),
                new Reporter(System.IO.Path.Combine(_root, "reports")), _ => "VENDORHASH", (m, _) => said.Add(m))
            .RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Contains(said, m => m.Contains("CHANGED", StringComparison.Ordinal));
        Assert.Contains("superseded", File.ReadAllText(Backups().Folder(row.DocumentId).SummaryPath));
    }

    [Fact]
    public async Task A_quarantined_document_leaves_no_half_saved_bytes_behind()
    {
        var row = Row(Path1);

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var folder = Backups().Folder(row.DocumentId);
        Assert.True(File.Exists(folder.SummaryPath));
        Assert.False(Directory.Exists(folder.OldDir));
    }
}
