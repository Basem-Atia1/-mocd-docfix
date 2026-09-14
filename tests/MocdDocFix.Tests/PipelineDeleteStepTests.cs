using MocdDocFix.Cli;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The delete step and the per-document delete offered during step 3 and 4 are the same work.
/// An operator who takes that offer every time has already finished step 5, and being walked
/// through it — twice confirming a delete with nothing in it, then told "nothing is awaiting
/// deletion" as though something had gone wrong — is three questions and a false alarm about a
/// run that went perfectly.
/// </summary>
public class PipelineDeleteStepTests
{
    private readonly List<string> _ran = new();
    private int _awaiting;

    private static readonly ScanOutcome Scan = new("1 broken", Array.Empty<string>(),
        new[] { new PickableFile(3, Guid.NewGuid().ToString(), "a.png", "Employee", "Type") });

    private Wizard Build(ScriptedPrompts prompts) =>
        new(prompts, "dev", isProduction: false, "https://crm", "http://files",
            new WizardActions(
                ScanAsync: () => Task.FromResult(Scan),
                ClassifyAsync: _ => Task.FromResult(Scan),
                BackupAsync: () => { _ran.Add("backup"); return Task.FromResult(StepOutcome.Of("1 saved")); },
                MigrateAsync: () => { _ran.Add("migrate"); return Task.FromResult(StepOutcome.Of("1 migrated")); },
                DeleteAsync: () => { _ran.Add("delete"); return Task.FromResult(StepOutcome.Of("1 deleted")); },
                VerifyAsync: () => { _ran.Add("verify"); return Task.FromResult(StepOutcome.Of("all correct")); },
                AwaitingDeleteAsync: () => Task.FromResult(_awaiting)));

    [Fact]
    public async Task With_the_old_files_already_gone_the_delete_step_is_not_offered_at_all()
    {
        _awaiting = 0;

        // Full run: continue past the check, the file server, the upload — and then nothing more
        // should be asked, because there is nothing left to delete.
        var prompts = new ScriptedPrompts("2", "1", "y", "1", "y", "q");

        await Build(prompts).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "backup", "migrate", "verify" }, _ran);
        Assert.True(prompts.Said("nothing left for the delete step"));
        Assert.DoesNotContain(prompts.Questions, q => q.Contains("Go to the delete step?"));
    }

    [Fact]
    public async Task With_old_files_still_there_the_step_runs_and_says_how_many()
    {
        _awaiting = 3;

        var prompts = new ScriptedPrompts("2", "1", "y", "1", "y", "1", "y", "q");

        await Build(prompts).RunAsync(CancellationToken.None);

        Assert.Contains("delete", _ran);
        Assert.True(prompts.Said("delete all 3 files"), "the gate should say how many are left");
    }

    /// <summary>
    /// A build that cannot count still behaves as it always did, rather than silently skipping
    /// the step that removes the old files.
    /// </summary>
    [Fact]
    public async Task Without_a_way_to_count_the_step_is_still_offered()
    {
        var prompts = new ScriptedPrompts("2", "1", "y", "1", "y", "1", "y", "q");

        await new Wizard(prompts, "dev", false, "https://crm", "http://files",
                new WizardActions(
                    () => Task.FromResult(Scan),
                    _ => Task.FromResult(Scan),
                    () => Task.FromResult(StepOutcome.Of("1 saved")),
                    () => Task.FromResult(StepOutcome.Of("1 migrated")),
                    () => { _ran.Add("delete"); return Task.FromResult(StepOutcome.Of("1 deleted")); },
                    () => Task.FromResult(StepOutcome.Of("all correct"))))
            .RunAsync(CancellationToken.None);

        Assert.Contains("delete", _ran);
    }
}
