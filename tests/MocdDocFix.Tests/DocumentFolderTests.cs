using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class DocumentFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-folder-" + Guid.NewGuid());
    private readonly Guid _id = Guid.Parse("28ef6a1c-cd1d-f111-b119-005056010908");

    public DocumentFolderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private DocumentFolder Folder() => new(_root, _id);

    [Fact]
    public void Everything_for_one_document_sits_under_a_folder_named_for_it()
    {
        var folder = Folder();

        Assert.Equal(Path.Combine(_root, _id.ToString()), folder.Root);
        Assert.Equal(Path.Combine(folder.Root, "old"), folder.OldDir);
        Assert.Equal(Path.Combine(folder.Root, "new"), folder.NewDir);
        Assert.Equal(Path.Combine(folder.Root, "document.txt"), folder.SummaryPath);
    }

    [Fact]
    public void Nothing_is_created_until_it_is_needed()
    {
        Folder();

        Assert.False(Directory.Exists(Path.Combine(_root, _id.ToString())));
    }

    [Fact]
    public void The_old_and_new_folders_are_created_on_demand()
    {
        var folder = Folder();

        Assert.True(Directory.Exists(folder.EnsureOld()));
        Assert.True(Directory.Exists(folder.EnsureNew()));
    }

    [Fact]
    public void The_header_writes_a_readable_file_of_labelled_points()
    {
        Folder().WriteHeader("DOCUMENT  " + _id, new (string, string?)[]
        {
            ("File name", "Application Summary.png"),
            ("Document type", "A Medical Examination Certificate")
        });

        var text = File.ReadAllText(Path.Combine(_root, _id.ToString(), "document.txt"));

        Assert.Contains("DOCUMENT  " + _id, text);
        Assert.Contains("File name", text);
        Assert.Contains("Application Summary.png", text);
        Assert.Contains("A Medical Examination Certificate", text);
    }

    [Fact]
    public void Points_line_up_in_one_column_so_the_file_can_be_read_down_the_page()
    {
        Folder().WriteHeader("T", new (string, string?)[] { ("Short", "111"), ("A longer label", "222") });

        var lines = File.ReadAllLines(Path.Combine(_root, _id.ToString(), "document.txt"));
        var first = lines.Single(l => l.Contains("111", StringComparison.Ordinal));
        var second = lines.Single(l => l.Contains("222", StringComparison.Ordinal));

        Assert.Equal(first.IndexOf("111", StringComparison.Ordinal),
                     second.IndexOf("222", StringComparison.Ordinal));
    }

    [Fact]
    public void A_point_with_no_value_is_left_out_rather_than_shown_blank()
    {
        Folder().WriteHeader("T", new (string, string?)[]
        {
            ("Present", "yes"), ("Absent", null), ("Empty", "   ")
        });

        var text = File.ReadAllText(Path.Combine(_root, _id.ToString(), "document.txt"));

        Assert.Contains("Present", text);
        Assert.DoesNotContain("Absent", text);
        Assert.DoesNotContain("Empty", text);
    }

    [Fact]
    public void A_long_value_wraps_under_its_label_instead_of_running_off_the_page()
    {
        var sentence = string.Join(" ", Enumerable.Repeat("word", 40));
        Folder().WriteHeader("T", new (string, string?)[] { ("Reason", sentence) });

        var lines = File.ReadAllLines(Path.Combine(_root, _id.ToString(), "document.txt"));

        Assert.All(lines, l => Assert.True(l.Length <= 80, $"{l.Length} chars: {l}"));
        Assert.True(lines.Count(l => l.Contains("word", StringComparison.Ordinal)) > 1);
    }

    [Fact]
    public void A_long_path_is_kept_whole_even_though_it_is_over_the_width()
    {
        var path = @"DigitalServices\9b1121f4-e30b-f111-b117-005056010908\20260312\00350015-6c00-4a1e-933a-0a00164627cf.png";
        Folder().WriteHeader("T", new (string, string?)[] { ("Path", path) });

        Assert.Contains(path, File.ReadAllText(Path.Combine(_root, _id.ToString(), "document.txt")));
    }

    [Fact]
    public void Sections_are_added_in_the_order_the_steps_happened()
    {
        var folder = Folder();
        folder.WriteHeader("DOCUMENT", new (string, string?)[] { ("File name", "a.png") });
        folder.AppendSection("THE OLD FILE", new (string, string?)[] { ("Saved as", @"old\x.png") });
        folder.AppendSection("THE NEW FILE", new (string, string?)[] { ("Saved as", @"new\y.png") });

        var text = File.ReadAllText(folder.SummaryPath);

        Assert.True(text.IndexOf("DOCUMENT", StringComparison.Ordinal)
                  < text.IndexOf("THE OLD FILE", StringComparison.Ordinal));
        Assert.True(text.IndexOf("THE OLD FILE", StringComparison.Ordinal)
                  < text.IndexOf("THE NEW FILE", StringComparison.Ordinal));
    }

    [Fact]
    public void Writing_the_header_again_replaces_it_rather_than_doubling_it()
    {
        var folder = Folder();
        folder.WriteHeader("DOCUMENT", new (string, string?)[] { ("File name", "a.png") });
        folder.WriteHeader("DOCUMENT", new (string, string?)[] { ("File name", "a.png") });

        var text = File.ReadAllText(folder.SummaryPath);

        Assert.Equal(1, text.Split("File name").Length - 1);
    }

    [Fact]
    public void A_note_can_be_added_for_something_that_is_not_a_label_and_a_value()
    {
        var folder = Folder();
        folder.WriteHeader("DOCUMENT", Array.Empty<(string, string?)>());
        folder.AppendNote("Quarantined: download failed.");

        Assert.Contains("Quarantined: download failed.", File.ReadAllText(folder.SummaryPath));
    }

    [Fact]
    public void The_file_is_utf8_so_arabic_names_survive()
    {
        var folder = Folder();
        folder.WriteHeader("DOCUMENT", new (string, string?)[] { ("File name", "القرار الوزاري.pdf") });

        Assert.Contains("القرار الوزاري.pdf", File.ReadAllText(folder.SummaryPath));
    }
}
