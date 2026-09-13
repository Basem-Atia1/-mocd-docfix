using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// These exist for one reason: a question that re-asks on unusable input must not spin forever
/// when stdin closes. It did, and it left a process running that held the published exe open.
/// </summary>
public class ConsolePromptsTests
{
    private static (ConsolePrompts Prompts, StringWriter Output) Build(string[] lines)
    {
        var output = new StringWriter();
        var input = new StringReader(lines.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine, lines) + Environment.NewLine);
        return (new ConsolePrompts(input, output), output);
    }

    [Fact]
    public void ReadLine_returns_what_was_typed_trimmed()
    {
        var (prompts, _) = Build(new[] { "  hello  " });

        Assert.Equal("hello", prompts.ReadLine("Say something"));
    }

    [Fact]
    public void ReadLine_at_end_of_input_answers_quit_rather_than_asking_again()
    {
        var (prompts, output) = Build(Array.Empty<string>());

        Assert.Equal("q", prompts.ReadLine("Say something"));
        Assert.Contains("end of input", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_asker_driven_by_exhausted_input_terminates_instead_of_looping()
    {
        var (prompts, _) = Build(new[] { "banana" });   // unusable, then nothing at all

        var answer = new Asker(prompts).Ask("Pick one", new[]
        {
            new Choice("A", "first"),
            new Choice("B", "second")
        });

        Assert.Equal(AnswerKind.Quit, answer.Kind);
    }

    [Fact]
    public void Confirm_at_end_of_input_is_a_no_not_a_yes()
    {
        var (prompts, _) = Build(Array.Empty<string>());

        Assert.Equal(ConfirmChoice.Quit, prompts.Confirm("Delete everything?"));
    }

    [Fact]
    public void Confirm_re_asks_on_nonsense_but_still_stops_at_end_of_input()
    {
        var (prompts, output) = Build(new[] { "maybe" });

        Assert.Equal(ConfirmChoice.Quit, prompts.Confirm("Go on?"));
        Assert.Contains("Please answer", output.ToString());
    }

    [Fact]
    public void Confirm_reads_the_usual_answers()
    {
        Assert.Equal(ConfirmChoice.Yes, Build(new[] { "y" }).Prompts.Confirm("?"));
        Assert.Equal(ConfirmChoice.No, Build(new[] { "no" }).Prompts.Confirm("?"));
        Assert.Equal(ConfirmChoice.Skip, Build(new[] { "skip" }).Prompts.Confirm("?"));
        Assert.Equal(ConfirmChoice.Quit, Build(new[] { "q" }).Prompts.Confirm("?"));
    }

    [Fact]
    public void TypedWord_at_end_of_input_does_not_confirm()
    {
        Assert.False(Build(Array.Empty<string>()).Prompts.TypedWord("Confirm", "prod"));
    }

    [Fact]
    public void TypedWord_is_case_sensitive_and_exact()
    {
        Assert.True(Build(new[] { "prod" }).Prompts.TypedWord("Confirm", "prod"));
        Assert.False(Build(new[] { "PROD" }).Prompts.TypedWord("Confirm", "prod"));
    }
}
