using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Where a targeted run writes its account, and what it says. A run about one document has no
/// business leaving a spreadsheet in the whole-run folder: the record belongs with the document,
/// beside everything else that happens to it, and readable without a spreadsheet program.
/// </summary>
public class TargetedReportTests : IDisposable
{
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-tr-" + Guid.NewGuid());
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeAdoClient _ado = new();
    private readonly FakePrompts _prompts = new();

    public TargetedReportTests()
    {
        Directory.CreateDirectory(_root);
        _read.CatalogueNames[Correct.ToString()] = "Employee Appointment Request";
        var doc = new DocumentRow(
            Guid.Parse("cbaf6bfb-d01d-f111-b119-005056010908"), "PAM USERS GUIDLINE.pdf",
            Guid.NewGuid(), @"DigitalServices\ab5fd6b8-e30b-f111-b117-005056010908\20260312\a.pdf",
            "PAM USERS GUIDLINE.pdf", "application/pdf", "VHASH", Guid.NewGuid(),
            "A Copy of Academic Qualification Certificate", Correct, null, null, DateTimeOffset.UtcNow);

        _read.Documents.Add(doc);
        _read.Resolutions["cbaf6bfb-d01d-f111-b119-005056010908"] = new List<DocumentRow> { doc };
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string ReportsRoot => Path.Combine(_root, "reports");

    private TargetedCommand Command(bool withDevOps)
    {
        var reporter = new Reporter(Path.Combine(ReportsRoot, "_whole-run"));

        var scan = new ScanCommand(_read, reporter, "https://crm/MoCD", new[] { Correct },
            null, null,
            withDevOps
                ? new DocumentTypeCheck(_ado, new DocumentTypeDecisions(Path.Combine(_root, "d.json")), _prompts)
                : null);

        return new TargetedCommand(_read, scan, reporter, _prompts,
            rows => Task.FromResult(rows.Count),
            new DocumentReportStore(ReportsRoot));
    }

    private Task<TargetedSummary> Run(bool withDevOps = true) =>
        Command(withDevOps).RunAsync("dev",
            new[] { "cbaf6bfb-d01d-f111-b119-005056010908" },
            forceReview: false, isProduction: false, CancellationToken.None);

    [Fact]
    public async Task The_account_is_a_text_file_in_the_documents_own_folder()
    {
        var summary = await Run();

        Assert.EndsWith(".txt", summary.ScanPath);
        Assert.Contains("cbaf6bfb-d01d-f111-b119-005056010908", summary.ScanPath);
        Assert.DoesNotContain("_whole-run", summary.ScanPath);
        Assert.True(File.Exists(summary.ScanPath));
    }

    [Fact]
    public async Task No_spreadsheet_is_left_in_the_whole_run_folder()
    {
        await Run();

        var wholeRun = Path.Combine(ReportsRoot, "_whole-run");

        Assert.True(!Directory.Exists(wholeRun) ||
                    Directory.GetFiles(wholeRun, "scan-*.csv").Length == 0);
    }

    [Fact]
    public async Task The_account_reads_as_prose_and_carries_the_verdict()
    {
        var summary = await Run();
        var text = File.ReadAllText(summary.ScanPath);

        Assert.Contains("STEP 1 — CHECK", text);
        Assert.Contains("A Copy of Academic Qualification Certificate", text);
        Assert.Contains("FIX", text);
        Assert.Contains("Employee Appointment Request", text);
    }

    // ---- the DevOps answer is always said, agreement included ----

    [Fact]
    public async Task Agreement_is_said_on_screen_and_written_down()
    {
        _ado.Titles["Academic Qualification Certificate"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify the academic qualification",
            "Portal | Confirm Employment | Verify the academic qualification certificate"
        };

        var summary = await Run();

        Assert.Contains(_prompts.Messages, m => m.Contains("DevOps") && m.Contains("agrees"));
        Assert.Contains("agrees", File.ReadAllText(summary.ScanPath));
    }

    /// <summary>
    /// The complaint that prompted this: the check ran, agreed, and said nothing — which is
    /// indistinguishable from never having run at all.
    /// </summary>
    [Fact]
    public async Task Not_being_set_up_is_said_too_rather_than_looking_like_nothing_happened()
    {
        var summary = await Run(withDevOps: false);

        Assert.Contains(_prompts.Messages, m => m.Contains("DevOps") && m.Contains("not checked"));
        Assert.Contains("not checked", File.ReadAllText(summary.ScanPath));
    }
}
