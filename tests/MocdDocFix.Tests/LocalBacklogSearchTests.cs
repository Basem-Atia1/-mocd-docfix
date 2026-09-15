using System.IO.Compression;
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Reading the local copy of the backlog. It exists because the live search sees only work item
/// titles — this server refuses a full-text query — while the document lists are written in the
/// story bodies and in the spreadsheets attached to them.
/// </summary>
public class LocalBacklogSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-local-" + Guid.NewGuid());

    public LocalBacklogSearchTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private void Story(string name, string title, string body) =>
        File.WriteAllText(Path.Combine(_root, name),
            $"---\ntype: User Story\ntitle: {title}\n---\n\n{body}\n");

    /// <summary>
    /// The smallest thing that is honestly an .xlsx for our purposes: a zip with a shared string
    /// table in it. That is the only entry <see cref="LocalBacklogSearch"/> reads.
    /// </summary>
    private void Workbook(string name, params string[] cells)
    {
        using var file = File.Create(Path.Combine(_root, name));
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using var entry = new StreamWriter(zip.CreateEntry("xl/sharedStrings.xml").Open());

        entry.Write("<sst>");
        foreach (var cell in cells) entry.Write($"<si><t>{cell}</t></si>");
        entry.Write("</sst>");
    }

    [Fact]
    public void A_name_written_in_a_story_body_is_found_with_the_service_it_belongs_to()
    {
        Story("us-27628.md", "1.1.6 NPOP- Employee Appointment Request Form- Documents",
            "The system shall display the below list of documents.\n" +
            "A Copy of Certificate of Good Conduct and Behavior\n");

        var hit = Assert.Single(
            LocalBacklogSearch.Find(_root, "A Copy of Certificate of Good Conduct and Behavior"));

        Assert.Equal("Employee Appointment Request", hit.Service);
        Assert.Contains("us-27628", hit.File);
    }

    [Fact]
    public void A_name_nobody_wrote_down_is_not_found()
    {
        Story("us-1.md", "1.1.1 NPOP- Something Else", "nothing relevant here");

        Assert.Empty(LocalBacklogSearch.Find(_root, "A Copy of Certificate of Good Conduct"));
    }

    [Fact]
    public void Nothing_configured_or_a_folder_that_is_not_there_searches_nothing()
    {
        Assert.Empty(LocalBacklogSearch.Find(null, "anything"));
        Assert.Empty(LocalBacklogSearch.Find(_root, null));
        Assert.Empty(LocalBacklogSearch.Find(Path.Combine(_root, "nope"), "anything"));
    }

    [Fact]
    public void A_story_that_words_the_name_differently_is_still_found()
    {
        Story("us-27628.md", "1.1.6 NPOP- Employee Appointment Request Form- Documents",
            "Certificate of good conduct and behavior, valid for the life of the appointment.");

        var hit = Assert.Single(LocalBacklogSearch.Find(_root, "a good conduct life"));

        Assert.Equal("Employee Appointment Request", hit.Service);
    }

    [Fact]
    public void The_same_words_pages_apart_are_not_a_match()
    {
        Story("us-1.md", "1.1.1 NPOP- Something Else Form- Documents",
            "good " + new string('x', 400) + " conduct " + new string('y', 400) + " life");

        Assert.Empty(LocalBacklogSearch.Find(_root, "a good conduct life"));
    }

    [Fact]
    public void A_real_workbook_is_searched_through_its_shared_string_table()
    {
        // The document lists arrive as .xlsx, and a workbook is a zip of XML: every cell value in
        // the file sits in xl/sharedStrings.xml. This proves the matcher reaches them, rather
        // than only the markdown the rest of these tests use.
        Workbook("MoCD_NPOP_Employee Appointment Request_DD.xlsx",
            "Document Type",
            "Certificate of good conduct and behavior, valid for the life of it",
            "Mandatory");

        var hit = Assert.Single(LocalBacklogSearch.Find(_root, "a good conduct life"));

        Assert.Equal("Employee Appointment Request", hit.Service);
    }

    // ---- reading the service off a story title ----

    [Fact]
    public void A_workbook_downloaded_from_the_backlog_gives_up_the_service_in_its_name()
    {
        Assert.Equal("Employee Appointment Request", LocalBacklogSearch.ServiceInStoryTitle(
            "MoCD_NPOP_Employee Appointment Request_DD_20250509_V.0.2"));
    }

    [Fact]
    public void The_service_is_read_from_the_titles_dash_separated_parts()
    {
        Assert.Equal("Employee Appointment Request", LocalBacklogSearch.ServiceInStoryTitle(
            "1.1.6 NPOP- Employee Appointment Request Form- Documents"));

        Assert.Equal("Register New Member", LocalBacklogSearch.ServiceInStoryTitle(
            "4.1.6 NPOP- Register New Member- Documents"));

        Assert.Equal("Holding GAM Request", LocalBacklogSearch.ServiceInStoryTitle(
            "5.2.1 Portal- Holding GAM Request Screen"));
    }

    /// <summary>
    /// A plain dash cannot be the separator, or "By-Laws Amendment" comes apart in the middle.
    /// </summary>
    [Fact]
    public void A_hyphenated_service_name_survives()
    {
        Assert.Equal("By-Laws Amendment Request", LocalBacklogSearch.ServiceInStoryTitle(
            "10.1.9 NPOP- By-Laws Amendment Request Form- Documents"));
    }

    [Fact]
    public void A_title_that_names_no_service_yields_nothing_rather_than_a_guess()
    {
        Assert.Null(LocalBacklogSearch.ServiceInStoryTitle("1.1.1 NPOP- Documents"));
        Assert.Null(LocalBacklogSearch.ServiceInStoryTitle(""));
        Assert.Null(LocalBacklogSearch.ServiceInStoryTitle(null));
    }

    // ---- the real synced backlog, if it is on this machine ----

    /// <summary>
    /// The case that prompted all of this: CRM's "A Copy of Certificate of Good Conduct and
    /// Behavior" appears in no work item title anywhere, and verbatim in the body of user story
    /// 27628. Skipped where the synced copy is not present.
    /// </summary>
    [Fact]
    public void The_real_backlog_copy_answers_the_name_that_defeated_the_title_search()
    {
        const string synced = @"D:\Claude code for mocd\mocd-knowledge-base\kb\user-stories";
        if (!Directory.Exists(synced)) return;

        var hits = LocalBacklogSearch.Find(synced, "A Copy of Certificate of Good Conduct and Behavior");

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Service is not null &&
                                   h.Service.Contains("Employee Appointment", StringComparison.OrdinalIgnoreCase));
    }
}
