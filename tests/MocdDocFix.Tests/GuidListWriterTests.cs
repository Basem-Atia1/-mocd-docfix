using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class GuidListWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-guids-" + Guid.NewGuid());

    public GuidListWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static ScanRow Row(int group, Guid documentId, string service = "Employee Appointment Request",
        string verdict = "Fix") =>
        new(documentId, Guid.NewGuid(), "a.pdf", "Medical Certificate",
            Guid.NewGuid(), service, @"DigitalServices\x\20260101\a.pdf", "x", null,
            Guid.NewGuid(), verdict, group, DocumentGroups.Get(group).ShortLabel,
            "because", "do this", null, "https://crm/x");

    private string Write(params ScanRow[] rows) =>
        File.ReadAllText(new GuidListWriter(_dir).Write("dev", rows));

    /// <summary>What --docs-file would actually read: non-blank lines that do not start with #.</summary>
    private static IReadOnlyList<string> Usable(string text) =>
        text.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    [Fact]
    public void The_file_is_named_for_the_environment_and_the_moment()
    {
        var path = new GuidListWriter(_dir).Write("dev", new[] { Row(1, Guid.NewGuid()) });

        Assert.StartsWith("guids-dev-", Path.GetFileName(path));
        Assert.EndsWith(".txt", path);
    }

    [Fact]
    public void Every_fixable_document_guid_appears_on_a_line_of_its_own()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var usable = Usable(Write(Row(1, a), Row(3, b)));

        Assert.Equal(new[] { a.ToString(), b.ToString() }, usable);
    }

    [Fact]
    public void Each_group_is_announced_above_its_guids()
    {
        var text = Write(Row(1, Guid.NewGuid()), Row(5, Guid.NewGuid()));

        Assert.Contains("GROUP 1", text);
        Assert.Contains("GROUP 5", text);
        Assert.Contains(DocumentGroups.Get(1).ShortLabel, text);
        Assert.Contains(DocumentGroups.Get(5).ShortLabel, text);
    }

    [Fact]
    public void A_group_heading_counts_its_files_and_names_the_services()
    {
        var text = Write(
            Row(1, Guid.NewGuid()),
            Row(1, Guid.NewGuid()),
            Row(1, Guid.NewGuid(), "By-Laws Amendment Requests"));

        Assert.Contains("GROUP 1 — 3 files", text);
        Assert.Contains("Employee Appointment Request", text);
        Assert.Contains("By-Laws Amendment Requests", text);
    }

    [Fact]
    public void Groups_the_tool_will_not_fix_are_listed_but_commented_out()
    {
        var conflict = Guid.NewGuid();
        var text = Write(Row(1, Guid.NewGuid()), Row(6, conflict, verdict: "Review"));

        // Present, so nothing is hidden …
        Assert.Contains(conflict.ToString(), text);

        // … but not something --docs-file would ever act on.
        Assert.DoesNotContain(conflict.ToString(), Usable(text));
    }

    [Fact]
    public void A_commented_out_group_says_why_it_is_commented_out()
    {
        var text = Write(Row(6, Guid.NewGuid(), verdict: "Review"));

        Assert.Contains("NOT fixed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_header_says_how_to_use_the_file()
    {
        var text = Write(Row(1, Guid.NewGuid()));

        Assert.Contains("--docs-file", text);
        Assert.Contains("dev", text);
    }

    [Fact]
    public void Groups_come_out_in_order()
    {
        var text = Write(Row(5, Guid.NewGuid()), Row(1, Guid.NewGuid()), Row(3, Guid.NewGuid()));

        var one = text.IndexOf("GROUP 1", StringComparison.Ordinal);
        var three = text.IndexOf("GROUP 3", StringComparison.Ordinal);
        var five = text.IndexOf("GROUP 5", StringComparison.Ordinal);

        Assert.True(one < three && three < five);
    }

    [Fact]
    public void A_group_with_no_rows_is_left_out()
    {
        var text = Write(Row(1, Guid.NewGuid()));

        Assert.DoesNotContain("GROUP 2", text);
    }

    [Fact]
    public void An_empty_scan_still_writes_a_file_that_says_so()
    {
        var text = File.ReadAllText(new GuidListWriter(_dir).Write("dev", Array.Empty<ScanRow>()));

        Assert.Contains("no documents", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Usable(text));
    }

    [Fact]
    public void The_same_document_is_never_listed_twice()
    {
        var twice = Guid.NewGuid();

        Assert.Single(Usable(Write(Row(1, twice), Row(1, twice))));
    }
}
