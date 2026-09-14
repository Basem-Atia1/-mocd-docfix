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

    /// <summary>
    /// Case is forgiven; a different word is not. The friction worth asking for is having to
    /// read the word off the screen and type it — not getting the shift key right. A correct
    /// answer rejected over its case reads as the tool being broken, and it cost a whole run.
    /// </summary>
    [Fact]
    public void TypedWord_forgives_the_case_but_not_the_word()
    {
        Assert.True(Build(new[] { "prod" }).Prompts.TypedWord("Confirm", "prod"));
        Assert.True(Build(new[] { "PROD" }).Prompts.TypedWord("Confirm", "prod"));
        Assert.True(Build(new[] { "  Prod  " }).Prompts.TypedWord("Confirm", "prod"));
        Assert.False(Build(new[] { "prd" }).Prompts.TypedWord("Confirm", "prod"));
    }

    // ---- yes / no ----

    [Fact]
    public void YesNo_takes_yes_in_any_case_or_length()
    {
        Assert.True(Build(new[] { "y" }).Prompts.YesNo("Go?"));
        Assert.True(Build(new[] { "Y" }).Prompts.YesNo("Go?"));
        Assert.True(Build(new[] { "yes" }).Prompts.YesNo("Go?"));
        Assert.True(Build(new[] { "  YES " }).Prompts.YesNo("Go?"));
    }

    [Fact]
    public void YesNo_takes_no_the_same_way_and_treats_quit_as_no()
    {
        Assert.False(Build(new[] { "n" }).Prompts.YesNo("Go?"));
        Assert.False(Build(new[] { "NO" }).Prompts.YesNo("Go?"));
        Assert.False(Build(new[] { "q" }).Prompts.YesNo("Go?"));
    }

    [Fact]
    public void YesNo_on_a_bare_Enter_uses_the_default_which_is_no()
    {
        Assert.False(Build(new[] { "" }).Prompts.YesNo("Go?"));
        Assert.True(Build(new[] { "" }).Prompts.YesNo("Go?", defaultYes: true));
    }

    [Fact]
    public void YesNo_re_asks_on_anything_else_rather_than_guessing()
    {
        var (prompts, output) = Build(new[] { "maybe" });

        Assert.False(prompts.YesNo("Delete everything?"));
        Assert.Contains("Please answer y or n", output.ToString());
    }

    /// <summary>Even where Enter means yes, no input at all must never mean yes.</summary>
    [Fact]
    public void YesNo_at_end_of_input_is_a_no()
    {
        Assert.False(Build(Array.Empty<string>()).Prompts.YesNo("Delete everything?", defaultYes: true));
    }
}
