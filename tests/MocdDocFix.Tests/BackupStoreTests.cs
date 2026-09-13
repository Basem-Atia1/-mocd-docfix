using System.Text;
using MocdDocFix.Storage;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

public class BackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-backup-" + Guid.NewGuid());

    private BackupStore Store() => new(Path.Combine(_root, "backup"));

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

    // ---- the restore record lives with its document ----

    [Fact]
    public void The_restore_record_is_written_inside_the_document_folder()
    {
        var store = Store();
        var documentId = Guid.NewGuid();

        store.AppendManifest(Entry(documentId));

        var expected = Path.Combine(store.Folder(documentId).Root, "restore.json");
        Assert.True(File.Exists(expected), expected);
    }

    [Fact]
    public void Nothing_is_written_beside_the_document_folders()
    {
        var store = Store();
        store.AppendManifest(Entry(Guid.NewGuid()));

        // Only per-document folders at the root — no shared manifest file alongside them.
        Assert.Empty(Directory.GetFiles(store.Root));
    }

    [Fact]
    public void Each_document_keeps_its_own_record()
    {
        var store = Store();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        store.AppendManifest(Entry(a));
        store.AppendManifest(Entry(b));

        Assert.Equal(2, store.LoadManifest().Count);
        Assert.True(File.Exists(Path.Combine(store.Folder(a).Root, "restore.json")));
        Assert.True(File.Exists(Path.Combine(store.Folder(b).Root, "restore.json")));
    }

    [Fact]
    public void Backing_the_same_document_up_again_replaces_its_record_rather_than_adding_one()
    {
        var store = Store();
        var documentId = Guid.NewGuid();

        store.AppendManifest(Entry(documentId) with { Bytes = 1 });
        store.AppendManifest(Entry(documentId) with { Bytes = 2 });

        var loaded = Assert.Single(store.LoadManifest());
        Assert.Equal(2, loaded.Bytes);
    }

    [Fact]
    public void The_record_is_readable_rather_than_one_long_line()
    {
        var store = Store();
        var documentId = Guid.NewGuid();
        store.AppendManifest(Entry(documentId));

        var text = File.ReadAllText(Path.Combine(store.Folder(documentId).Root, "restore.json"));

        Assert.Contains(Environment.NewLine, text);
        Assert.Contains("\"OldFilePath\"", text);
    }

    [Fact]
    public void A_folder_with_no_restore_record_is_skipped_rather_than_failing_the_load()
    {
        var store = Store();
        store.AppendManifest(Entry(Guid.NewGuid()));
        Directory.CreateDirectory(Path.Combine(store.Root, Guid.NewGuid().ToString()));

        Assert.Single(store.LoadManifest());
    }

    [Fact]
    public void A_torn_restore_record_is_skipped_rather_than_aborting_the_run()
    {
        var store = Store();
        store.AppendManifest(Entry(Guid.NewGuid()));

        var broken = Path.Combine(store.Root, Guid.NewGuid().ToString());
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "restore.json"), "{ not json");

        Assert.Single(store.LoadManifest());
    }

    [Fact]
    public void Records_come_back_in_the_order_they_were_written()
    {
        var store = Store();
        var first = Entry(Guid.NewGuid()) with { At = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var second = Entry(Guid.NewGuid()) with { At = DateTimeOffset.UtcNow };

        store.AppendManifest(second);
        store.AppendManifest(first);

        Assert.Equal(new[] { first.DocumentId, second.DocumentId },
            store.LoadManifest().Select(m => m.DocumentId));
    }

    private static ManifestEntry Entry(Guid documentId) => new(
        DocumentId: documentId,
        OldFileId: Guid.NewGuid(),
        OldFilePath: @"DigitalServices\cat\20260330\a.jpg",
        OldVendorHash: "h",
        FileName: "cert.jpg",
        MediaType: "image/jpeg",
        Extension: ".jpg",
        OldCategory: "cat",
        CorrectCatalogueId: Guid.NewGuid(),
        LocalPath: @"C:\tmp\a.jpg",
        Bytes: 10,
        OurHash: "abc",
        At: DateTimeOffset.UtcNow);

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
