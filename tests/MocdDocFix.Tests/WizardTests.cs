using MocdDocFix.Cli;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class WizardTests
{
    private readonly List<string> _ran = new();

    private LedgerActions Actions() => new(
        RepairAsync: _ => { _ran.Add("repair"); return Task.FromResult(StepOutcome.Of("repaired")); },
        DeleteAsync: _ => { _ran.Add("delete"); return Task.FromResult(StepOutcome.Of("deleted")); },
        RedoAsync: _ => { _ran.Add("redo"); return Task.FromResult(StepOutcome.Of("reverted")); },
        CheckAsync: _ => { _ran.Add("check"); return Task.FromResult(StepOutcome.Of("checked")); },
        LookAsync: _ => { _ran.Add("look"); return Task.FromResult(StepOutcome.Of("looked")); });

    private Wizard Subject(FakePrompts prompts) =>
        new(prompts, "dev", isProduction: false,
            "https://crm.example", "https://files.example", Actions());

    /// <summary>Menu answers are typed 1-based numbers; the fake is non-interactive.</summary>
    private static FakePrompts Choosing(params string[] answers) =>
        new()
        {
            ReadLineQueue = new Queue<string>(answers),
            YesNoResponse = true          // the confirm-your-choice question
        };

    [Theory]
    [InlineData("1", "repair")]
    [InlineData("2", "delete")]
    [InlineData("3", "redo")]
    [InlineData("4", "check")]
    public async Task Each_entry_runs_its_own_mode(string typed, string expected)
    {
        await Subject(Choosing(typed, "7")).RunAsync(CancellationToken.None);

        Assert.Contains(expected, _ran);
    }

    /// <summary>The look-up asks for a path before it runs, so it needs one more answer.</summary>
    [Fact]
    public async Task The_look_up_entry_asks_what_to_look_up()
    {
        await Subject(Choosing("5", "somepath.jpg", "7")).RunAsync(CancellationToken.None);

        Assert.Contains("look", _ran);
    }

    [Fact]
    public async Task Changing_environment_leaves_the_wizard_saying_so()
    {
        var exit = await Subject(Choosing("6")).RunAsync(CancellationToken.None);

        Assert.Equal(WizardExit.ChangeEnvironment, exit);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Quitting_runs_nothing()
    {
        var exit = await Subject(Choosing("7")).RunAsync(CancellationToken.None);

        Assert.Equal(WizardExit.Finished, exit);
        Assert.Empty(_ran);
    }

    /// <summary>
    /// The menu is seven entries and no more. A stray eighth would shift every number the
    /// operator has learned.
    /// </summary>
    [Fact]
    public async Task The_menu_offers_exactly_seven_choices()
    {
        var prompts = Choosing("7");

        await Subject(prompts).RunAsync(CancellationToken.None);

        var said = string.Join("\n", prompts.Messages);

        Assert.Contains("1  Repair run", said);
        Assert.Contains("2  Delete old files", said);
        Assert.Contains("3  Redo", said);
        Assert.Contains("4  Check it all", said);
        Assert.Contains("5  Is this file still there?", said);
        Assert.Contains("6  Change environment", said);
        Assert.Contains("7  Quit", said);

        // An eighth would shift every number the operator has learned.
        Assert.DoesNotContain("8  ", said);
    }

    [Fact]
    public async Task Production_is_named_in_the_banner()
    {
        var prompts = Choosing("7");

        await new Wizard(prompts, "prod", isProduction: true,
                "https://crm.example", "https://files.example", Actions())
            .RunAsync(CancellationToken.None);

        Assert.Contains(prompts.Messages, m => m.Contains("PRODUCTION"));
    }

    /// <summary>
    /// Nothing the wizard offers writes on its own: every entry hands straight to an action, and
    /// one keystroke can never start two of them.
    /// </summary>
    [Fact]
    public async Task One_answer_runs_exactly_one_mode()
    {
        await Subject(Choosing("1", "7")).RunAsync(CancellationToken.None);

        Assert.Single(_ran);
    }
}
