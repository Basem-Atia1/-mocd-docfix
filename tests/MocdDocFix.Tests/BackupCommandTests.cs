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

    private BackupStore Backups() =>
        new(Path.Combine(_root, "backup"), Path.Combine(_root, "restore-manifest.jsonl"));
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
    public async Task Already_backed_up_documents_are_skipped_on_a_re_run()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var second = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(0, second.Saved);
        Assert.Equal(1, second.Skipped);
        Assert.Single(Backups().LoadManifest());       // not duplicated
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
}
