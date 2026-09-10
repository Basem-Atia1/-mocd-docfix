using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class TargetedCommandTests : IDisposable
{
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid GamRequest = Guid.Parse("3ff27d73-653e-f111-b119-005056010908");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-tgt-" + Guid.NewGuid());
    private readonly FakeCrmReadClient _read = new();

    public TargetedCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static DocumentRow Doc(string path, Guid? cat) =>
        new(Guid.NewGuid(), "cert.jpg", Guid.NewGuid(), path, "cert.jpg", "image/jpeg", "VHASH",
            Guid.NewGuid(), "Certificate of Good Conduct", cat, null, null, DateTimeOffset.UtcNow);

    private Reporter Reports() => new(Path.Combine(_root, "reports"));

    private TargetedCommand Command(FakePrompts prompts, Func<Task<int>>? pipeline = null) =>
        new(_read,
            new ScanCommand(_read, Reports(), "https://crm/MoCD", new[] { Correct }),
            Reports(),
            prompts,
            runPipelineAsync: rows => pipeline is null
                ? Task.FromResult(rows.Count)
                : pipeline());

    [Fact]
    public async Task An_unknown_identifier_is_reported_and_nothing_runs()
    {
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "nope.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.NotFound);
        Assert.Equal(0, summary.Resolved);
        Assert.Contains(prompts.Messages, m => m.Contains("nope.jpg"));
    }

    [Fact]
    public async Task A_broken_document_is_reported_as_broken_and_queued_for_the_pipeline()
    {
        var doc = Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct);
        _read.Resolutions["cert.jpg"] = new List<DocumentRow> { doc };
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "cert.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Resolved);
        Assert.Equal(1, summary.Fixed);
        Assert.Contains(prompts.Messages, m => m.Contains("BROKEN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(prompts.Messages, m => m.Contains("goodConductCertificate"));
    }

    [Fact]
    public async Task An_already_correct_document_is_reported_and_left_alone()
    {
        var doc = Doc($@"DigitalServices\{Correct}\20260330\a.jpg", Correct);
        _read.Resolutions["cert.jpg"] = new List<DocumentRow> { doc };
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "cert.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Fixed);
        Assert.Equal(1, summary.Skipped);
        Assert.Contains(prompts.Messages, m => m.Contains("already correct", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_ambiguous_document_needs_force_review()
    {
        _read.KnownCatalogues.Add(GamRequest.ToString());
        var doc = Doc($@"DigitalServices\{GamRequest}\20260518\a.pdf", Correct);
        _read.Resolutions["a.pdf"] = new List<DocumentRow> { doc };
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "a.pdf" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Fixed);
        Assert.Equal(1, summary.Reviewed);
        Assert.Contains(prompts.Messages, m => m.Contains("--force-review"));
    }

    [Fact]
    public async Task With_force_review_an_ambiguous_document_is_queued()
    {
        _read.KnownCatalogues.Add(GamRequest.ToString());
        var doc = Doc($@"DigitalServices\{GamRequest}\20260518\a.pdf", Correct);
        _read.Resolutions["a.pdf"] = new List<DocumentRow> { doc };

        var summary = await Command(new FakePrompts()).RunAsync("dev", new[] { "a.pdf" },
            forceReview: true, isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Fixed);
    }

    [Fact]
    public async Task Several_identifiers_are_handled_in_one_run()
    {
        _read.Resolutions["a.jpg"] = new List<DocumentRow>
            { Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct) };
        _read.Resolutions["b.jpg"] = new List<DocumentRow>
            { Doc(@"DigitalServices\0\20260330\b.jpg", Correct) };

        var summary = await Command(new FakePrompts()).RunAsync("dev", new[] { "a.jpg", "b.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(2, summary.Resolved);
        Assert.Equal(2, summary.Fixed);
    }

    [Fact]
    public async Task An_identifier_matching_several_documents_is_flagged_not_guessed()
    {
        _read.Resolutions["dup.jpg"] = new List<DocumentRow>
        {
            Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct),
            Doc(@"DigitalServices\goodConductCertificate\20260331\b.jpg", Correct)
        };
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "dup.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Ambiguous);
        Assert.Equal(0, summary.Fixed);
        Assert.Contains(prompts.Messages, m => m.Contains("matches 2 documents"));
    }

    [Fact]
    public async Task A_report_is_written_in_targeted_mode_too_stating_reason_and_solution()
    {
        _read.Resolutions["a.jpg"] = new List<DocumentRow>
            { Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct) };

        var summary = await Command(new FakePrompts()).RunAsync("dev", new[] { "a.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.True(File.Exists(summary.ScanPath));
        var text = File.ReadAllText(summary.ScanPath);
        Assert.Contains("Reason", text);
        Assert.Contains("Solution", text);
        Assert.Contains("Re-upload", text);
    }

    [Fact]
    public async Task The_solution_is_shown_on_screen_for_a_broken_file()
    {
        _read.Resolutions["a.jpg"] = new List<DocumentRow>
            { Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct) };
        var prompts = new FakePrompts();

        await Command(prompts).RunAsync("dev", new[] { "a.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Contains(prompts.Messages, m => m.Contains("SOLUTION"));
    }
}
