using System.Text;
using MocdDocFix.Storage;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

public class BackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-backup-" + Guid.NewGuid());

    private BackupStore Store() =>
        new(Path.Combine(_root, "backup"), Path.Combine(_root, "restore-manifest.jsonl"));

    public BackupStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("pretend this is a certificate");

    [Fact]
    public void Save_writes_the_file_named_by_the_old_file_id()
    {
        var id = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");

        var result = Store().Save(Guid.NewGuid(), id, ".jpg", Bytes);

        Assert.True(File.Exists(result.LocalPath));
        Assert.EndsWith("5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg", result.LocalPath);
        Assert.Equal(Bytes.Length, result.Bytes);
        Assert.Equal(Verifier.OurHash(Bytes), result.OurHash);
        Assert.Equal(Bytes, File.ReadAllBytes(result.LocalPath));
    }

    [Fact]
    public void Save_creates_the_directory_if_it_is_missing()
    {
        var result = Store().Save(Guid.NewGuid(), Guid.NewGuid(), ".pdf", Bytes);

        Assert.True(Directory.Exists(Path.GetDirectoryName(result.LocalPath)));
    }

    [Fact]
    public void Save_tolerates_a_missing_extension()
    {
        var result = Store().Save(Guid.NewGuid(), Guid.NewGuid(), "", Bytes);

        Assert.True(File.Exists(result.LocalPath));
    }

    [Fact]
    public void Read_returns_exactly_what_was_saved()
    {
        var store = Store();
        var result = store.Save(Guid.NewGuid(), Guid.NewGuid(), ".jpg", Bytes);

        Assert.Equal(Bytes, store.Read(result.LocalPath));
    }

    [Fact]
    public void Manifest_round_trips_every_field_needed_to_re_upload()
    {
        var store = Store();
        var entry = new ManifestEntry(
            DocumentId: Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"),
            OldFileId: Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187"),
            OldFilePath: @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg",
            OldVendorHash: "e57d1555e2197c964daa9fd57e197b7b",
            FileName: "cert.jpg",
            MediaType: "image/jpeg",
            Extension: ".jpg",
            OldCategory: "goodConductCertificate",
            CorrectCatalogueId: Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
            LocalPath: @"C:\tmp\5b05398a.jpg",
            Bytes: 350208,
            OurHash: "abc",
            At: DateTimeOffset.UtcNow);

        store.AppendManifest(entry);
        var loaded = Assert.Single(store.LoadManifest());

        Assert.Equal(entry.OldFilePath, loaded.OldFilePath);
        Assert.Equal(entry.OldVendorHash, loaded.OldVendorHash);
        Assert.Equal(entry.MediaType, loaded.MediaType);
        Assert.Equal(entry.OldCategory, loaded.OldCategory);
        Assert.Equal(entry.CorrectCatalogueId, loaded.CorrectCatalogueId);
        Assert.Equal(350208, loaded.Bytes);
    }

    [Fact]
    public void LoadManifest_is_empty_before_anything_is_written()
        => Assert.Empty(Store().LoadManifest());

    [Fact]
    public void Backslashes_in_the_stored_path_survive_json_round_tripping()
    {
        var store = Store();
        const string path = @"DigitalServices\0\20260423\c91918d7-5698-4d08-b227-0005d35e76db.png";
        store.AppendManifest(new ManifestEntry(Guid.NewGuid(), Guid.NewGuid(), path, "h", "a.png",
            "image/png", ".png", "0", Guid.NewGuid(), "local", 1, "h", DateTimeOffset.UtcNow));

        Assert.Equal(path, store.LoadManifest()[0].OldFilePath);
    }

    [Fact]
    public void FreeSpaceBytes_reports_something_positive_for_the_temp_drive()
        => Assert.True(BackupStore.FreeSpaceBytes(Path.GetTempPath()) > 0);
}
