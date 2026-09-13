using MocdDocFix.Cli;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class GuidedMenuTests
{
    private readonly List<string> _ran = new();
    private readonly List<(IReadOnlyList<string> Ids, bool Force)> _targeted = new();

    private GuidedActions Actions() => new(
        ScanAsync: () => { _ran.Add("scan"); return Task.CompletedTask; },
        BackupAsync: () => { _ran.Add("backup"); return Task.CompletedTask; },
        MigrateAsync: () => { _ran.Add("migrate"); return Task.CompletedTask; },
        DeleteAsync: () => { _ran.Add("delete"); return Task.CompletedTask; },
        TargetedAsync: (ids, force) => { _targeted.Add((ids, force)); _ran.Add("targeted"); return Task.CompletedTask; });

    private GuidedMenu Menu(FakePrompts prompts, bool isProduction = false) =>
        new(prompts, "dev", isProduction, "https://crm/MoCD", "http://files:83", Actions());

    /// <summary>Queues menu answers; the last is always 0 so the loop terminates.</summary>
    private static FakePrompts Choosing(params string[] answers) =>
        new() { ReadLineQueue = new Queue<string>(answers.Append("0")) };

    [Fact]
    public async Task The_header_names_the_environment_and_both_servers()
    {
        var prompts = Choosing();

        await Menu(prompts).RunAsync(CancellationToken.None);

        var screen = string.Join("\n", prompts.Messages);
        Assert.Contains("dev", screen);
        Assert.Contains("https://crm/MoCD", screen);
        Assert.Contains("http://files:83", screen);
    }

    [Fact]
    public async Task Every_option_says_whether_it_writes()
    {
        var prompts = Choosing();

        await Menu(prompts).RunAsync(CancellationToken.None);

        var screen = string.Join("\n", prompts.Messages);
        Assert.Contains("Scan", screen);
        Assert.Contains("Backup", screen);
        Assert.Contains("Migrate", screen);
        Assert.Contains("Delete", screen);
        Assert.Contains("reads only", screen, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("writes", screen, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("irreversible", screen, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Quitting_immediately_runs_nothing()
    {
        await Menu(Choosing()).RunAsync(CancellationToken.None);

        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Scan_runs_and_the_menu_comes_back()
    {
        var prompts = Choosing("1", "1");

        await Menu(prompts).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "scan", "scan" }, _ran);
    }

    [Fact]
    public async Task An_unrecognised_choice_runs_nothing_and_says_so()
    {
        var prompts = Choosing("99", "banana");

        await Menu(prompts).RunAsync(CancellationToken.None);

        Assert.Empty(_ran);
        Assert.Contains(prompts.Messages, m => m.Contains("not a choice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Backup_is_announced_as_touching_the_file_server_before_it_runs()
    {
        var prompts = Choosing("2");
        prompts.Answer(ConfirmChoice.Yes);

        await Menu(prompts).RunAsync(CancellationToken.None);

        Assert.Contains("backup", _ran);
        Assert.Contains(prompts.Questions, q => q.Contains("file server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Declining_the_file_server_warning_skips_the_backup()
    {
        var prompts = Choosing("2");
        prompts.Answer(ConfirmChoice.No);

        await Menu(prompts).RunAsync(CancellationToken.None);

        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Migrate_needs_confirmation_because_it_writes_to_crm()
    {
        var declined = Choosing("3");
        declined.Answer(ConfirmChoice.No);
        await Menu(declined).RunAsync(CancellationToken.None);
        Assert.Empty(_ran);

        var accepted = Choosing("3");
        accepted.Answer(ConfirmChoice.Yes);
        await Menu(accepted).RunAsync(CancellationToken.None);
        Assert.Equal(new[] { "migrate" }, _ran);
    }

    [Fact]
    public async Task Delete_warns_that_it_cannot_be_undone()
    {
        var prompts = Choosing("4");
        prompts.Answer(ConfirmChoice.Yes);

        await Menu(prompts).RunAsync(CancellationToken.None);

        Assert.Contains(prompts.Messages,
            m => m.Contains("cannot be undone", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "delete" }, _ran);
    }

    [Fact]
    public async Task Targeted_asks_for_identifiers_and_splits_them()
    {
        var prompts = new FakePrompts
        {
            ReadLineQueue = new Queue<string>(new[] { "5", "a.jpg, b.jpg ,c.jpg", "0" })
        };
        prompts.Answer(ConfirmChoice.No);      // not ambiguous-forcing
        prompts.Answer(ConfirmChoice.Yes);     // yes, contact the file server

        await Menu(prompts).RunAsync(CancellationToken.None);

        var call = Assert.Single(_targeted);
        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, call.Ids);
        Assert.False(call.Force);
    }

    [Fact]
    public async Task Targeted_with_nothing_typed_does_not_run()
    {
        var prompts = new FakePrompts { ReadLineQueue = new Queue<string>(new[] { "5", "   ", "0" }) };

        await Menu(prompts).RunAsync(CancellationToken.None);

        Assert.Empty(_targeted);
    }

    [Fact]
    public async Task Production_is_shouted_about_in_the_header()
    {
        var prompts = Choosing();

        await Menu(prompts, isProduction: true).RunAsync(CancellationToken.None);

        Assert.Contains(prompts.Messages, m => m.Contains("PRODUCTION", StringComparison.Ordinal));
    }
}
