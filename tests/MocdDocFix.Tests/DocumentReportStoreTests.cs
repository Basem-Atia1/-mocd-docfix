using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class DocumentReportStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-reports-" + Guid.NewGuid());
    private readonly Guid _id = Guid.Parse("28ef6a1c-cd1d-f111-b119-005056010908");

    public DocumentReportStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private DocumentReportStore Store() => new(_root);

    private static (string, string?)[] Points => new (string, string?)[]
    {
        ("Path on server", @"DigitalServices\9b1121f4-…\20260312\00350015-….png"),
        ("Size", "11,261 bytes")
    };

    [Fact]
    public void Each_document_gets_a_folder_named_for_it()
    {
        var path = Store().Write(_id, "Application Summary.png", "01-check", "STEP 1", Points);

        Assert.Equal(Path.Combine(_root, $"Application Summary__{_id}", "01-check.txt"), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Every_step_is_its_own_file_and_they_sort_in_order()
    {
        var store = Store();
        store.Write(_id, "a.png", "01-check", "STEP 1", Points);
        store.Write(_id, "a.png", "02-backup", "STEP 2", Points);
        store.Write(_id, "a.png", "04-delete", "STEP 5", Points);

        var files = Directory.GetFiles(store.FolderFor(_id)).Select(Path.GetFileName).Order().ToList();

        Assert.Equal(new[] { "01-check.txt", "02-backup.txt", "04-delete.txt" }, files);
    }

    [Fact]
    public void Running_a_step_again_replaces_that_step_and_leaves_the_others()
    {
        var store = Store();
        store.Write(_id, "a.png", "01-check", "STEP 1", Points);
        store.Write(_id, "a.png", "02-backup", "STEP 2", Points);
        store.Write(_id, "a.png", "02-backup", "STEP 2 AGAIN", Points);

        Assert.Equal(2, Directory.GetFiles(store.FolderFor(_id)).Length);
        Assert.Contains("STEP 2 AGAIN",
            File.ReadAllText(Path.Combine(store.FolderFor(_id), "02-backup.txt")));
    }

    [Fact]
    public void Each_report_identifies_its_document_without_being_opened_alongside_anything_else()
    {
        var path = Store().Write(_id, "Application Summary.png", "01-check", "STEP 1 — CHECK", Points);
        var text = File.ReadAllText(path);

        Assert.Contains("STEP 1 — CHECK", text);
        Assert.Contains(_id.ToString(), text);
        Assert.Contains("Application Summary.png", text);
        Assert.Contains("written", text);
    }

    [Fact]
    public void Extra_lines_are_written_after_the_points()
    {
        var path = Store().Write(_id, "a.png", "04-delete", "STEP 5", Points,
            new[] { "    before      found on the server", "    after       confirmed gone" });

        var text = File.ReadAllText(path);

        Assert.Contains("before      found on the server", text);
        Assert.True(text.IndexOf("Size", StringComparison.Ordinal)
                  < text.IndexOf("before", StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_folder_is_separate_from_the_backup_folder_for_the_same_document()
    {
        var reports = Path.Combine(_root, "reports");
        var backups = Path.Combine(_root, "backup");

        var reportPath = new DocumentReportStore(reports).Write(_id, "a.png", "01-check", "T", Points);
        var backupFolder = new DocumentFolder(backups, _id, "a.png");
        backupFolder.WriteHeader("DOCUMENT", Points);

        Assert.StartsWith(reports, reportPath);
        Assert.StartsWith(backups, backupFolder.SummaryPath);
        Assert.NotEqual(Path.GetDirectoryName(reportPath), backupFolder.Root);
    }

    [Fact]
    public void Arabic_names_survive_in_the_folder_name_and_the_text()
    {
        var path = Store().Write(_id, "القرار الوزاري.pdf", "01-check", "T", Points);

        Assert.Contains("القرار الوزاري", path);
        Assert.Contains("القرار الوزاري.pdf", File.ReadAllText(path));
    }
}
