using MocdDocFix.Domain;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class RunProgressTests
{
    private static LedgerRow Row() => new()
    {
        Row = 12,
        DocId = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001"),
        DocName = "Board of Director's Decision",
        DocFileName = "cert.jpg"
    };

    private static (FakePrompts Prompts, RunProgress Progress) At(WatchMode mode)
    {
        var prompts = new FakePrompts();
        return (prompts, new RunProgress(prompts, mode));
    }

    private static string All(FakePrompts prompts) => string.Join("\n", prompts.Messages);

    [Fact]
    public void Watch_prints_every_step()
    {
        var (prompts, progress) = At(WatchMode.Watch);

        progress.StartRow(12, 431, Row());
        progress.Step("backing up", @"backup\dev\cert.jpg__a3f1");
        progress.Step("uploading", @"...\20260915\b2c3.jpg");
        progress.Finished(Row());

        var text = All(prompts);
        Assert.Contains("12/431", text);
        Assert.Contains("cert.jpg", text);
        Assert.Contains("backing up", text);
        Assert.Contains("uploading", text);
        Assert.Contains("FINISHED", text);
    }

    /// <summary>
    /// Quiet is one line per document. Printing each step over four hundred rows is thousands of
    /// lines of scrollback, and the failures are lost in it.
    /// </summary>
    [Fact]
    public void Quiet_prints_the_document_and_the_outcome_but_not_the_steps()
    {
        var (prompts, progress) = At(WatchMode.Quiet);

        progress.StartRow(12, 431, Row());
        progress.Step("backing up", @"backup\dev\cert.jpg__a3f1");
        progress.Step("uploading");
        progress.Finished(Row());

        var text = All(prompts);
        Assert.Contains("12/431", text);
        Assert.Contains("cert.jpg", text);
        Assert.Contains("FINISHED", text);
        Assert.DoesNotContain("backing up", text);
        Assert.DoesNotContain("uploading", text);
    }

    [Fact]
    public void Unattended_prints_as_little_as_quiet_does()
    {
        var (prompts, progress) = At(WatchMode.Unattended);

        progress.StartRow(12, 431, Row());
        progress.Step("uploading");
        progress.Finished(Row());

        Assert.DoesNotContain("uploading", All(prompts));
    }

    /// <summary>The only difference that can reach CRM: whether the operator is asked at all.</summary>
    [Theory]
    [InlineData(WatchMode.Watch, true)]
    [InlineData(WatchMode.Quiet, true)]
    [InlineData(WatchMode.Unattended, false)]
    public void Only_unattended_skips_the_eye_check(WatchMode mode, bool asks) =>
        Assert.Equal(asks, At(mode).Progress.AsksTheEyeCheck);

    /// <summary>Watch pauses so each document can be read before the next begins.</summary>
    [Fact]
    public void Watch_asks_between_documents()
    {
        var (prompts, progress) = At(WatchMode.Watch);
        prompts.YesNoResponse = true;

        Assert.True(progress.CarryOn());
        Assert.Single(prompts.Questions);
    }

    /// <summary>
    /// The bug this replaced: the pause took any keystroke as "carry on", so an operator who
    /// had just watched something they disliked typed "no" and the loop moved on regardless.
    /// </summary>
    [Fact]
    public void Answering_no_between_documents_stops_the_run()
    {
        var (prompts, progress) = At(WatchMode.Watch);
        prompts.YesNoResponse = false;

        Assert.False(progress.CarryOn());
    }

    [Theory]
    [InlineData(WatchMode.Quiet)]
    [InlineData(WatchMode.Unattended)]
    public void Nothing_else_pauses_between_documents(WatchMode mode)
    {
        var (prompts, progress) = At(mode);

        Assert.True(progress.CarryOn());
        Assert.Empty(prompts.Questions);
    }

    /// <summary>
    /// The modes that never interrupt still need a way out. Pressing q is noticed between
    /// documents — never mid-upload, so the document in progress is always finished first.
    /// </summary>
    [Theory]
    [InlineData(WatchMode.Quiet)]
    [InlineData(WatchMode.Unattended)]
    public void Pressing_q_stops_the_modes_that_never_ask(WatchMode mode)
    {
        var (prompts, progress) = At(mode);
        prompts.StopRequests = new Queue<bool>(new[] { true });

        Assert.False(progress.CarryOn());
        Assert.Contains("Stopping at your request", string.Join("\n", prompts.Messages));
    }

    /// <summary>Nothing pressed must never block or stop — it is checked hundreds of times.</summary>
    [Theory]
    [InlineData(WatchMode.Quiet)]
    [InlineData(WatchMode.Unattended)]
    public void Nothing_pressed_carries_on(WatchMode mode)
    {
        var (prompts, progress) = At(mode);
        prompts.StopRequests = new Queue<bool>(new[] { false, false });

        Assert.True(progress.CarryOn());
        Assert.True(progress.CarryOn());
    }

    /// <summary>Watch asks outright, so it does not read keystrokes behind the operator's back.</summary>
    [Fact]
    public void Watch_does_not_consult_the_keyboard_because_it_asks()
    {
        var (prompts, progress) = At(WatchMode.Watch);
        prompts.StopRequests = new Queue<bool>(new[] { true });
        prompts.YesNoResponse = true;

        Assert.True(progress.CarryOn());
        Assert.Single(prompts.StopRequests);        // untouched
    }

    [Theory]
    [InlineData(WatchMode.Quiet)]
    [InlineData(WatchMode.Unattended)]
    public void The_way_out_is_said_before_the_loop_begins(WatchMode mode)
    {
        var (prompts, progress) = At(mode);

        progress.SayHowToStop();

        Assert.Contains("Press q", string.Join("\n", prompts.Messages));
    }

    [Fact]
    public void Watch_is_not_told_to_press_q_because_it_is_asked_every_time()
    {
        var (prompts, progress) = At(WatchMode.Watch);

        progress.SayHowToStop();

        Assert.DoesNotContain("Press q", string.Join("\n", prompts.Messages));
    }

    /// <summary>A failure must be visible in every mode — it is the one thing nobody may miss.</summary>
    [Theory]
    [InlineData(WatchMode.Watch)]
    [InlineData(WatchMode.Quiet)]
    [InlineData(WatchMode.Unattended)]
    public void A_failure_is_printed_whatever_the_mode(WatchMode mode)
    {
        var (prompts, progress) = At(mode);

        progress.StartRow(14, 431, Row());
        progress.Failed(Row(), "upload rejected: 413");

        var text = All(prompts);
        Assert.Contains("413", text);
        Assert.Contains("cert.jpg", text);
    }

    [Fact]
    public void A_skipped_row_says_why_it_was_skipped()
    {
        var (prompts, progress) = At(WatchMode.Quiet);

        progress.Skipped(Row(), "verdict is review");

        Assert.Contains("verdict is review", All(prompts));
    }
}
