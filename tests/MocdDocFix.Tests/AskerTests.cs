using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class AskerTests
{
    private static readonly Choice[] Three =
    {
        new("Scan", "find the broken files", "Reads CRM and writes three report files."),
        new("Backup", "save a copy of each", "Downloads every file from the vendor server."),
        new("Quit", "stop here")
    };

    [Fact]
    public void A_number_chooses_that_option()
    {
        var prompts = new ScriptedPrompts("2");

        var answer = new Asker(prompts).Ask("What now?", Three);

        Assert.Equal(AnswerKind.Chosen, answer.Kind);
        Assert.Equal(1, answer.Index);
    }

    [Fact]
    public void The_question_and_every_choice_are_shown()
    {
        var prompts = new ScriptedPrompts("1");

        new Asker(prompts).Ask("What now?", Three);

        Assert.True(prompts.Said("What now?"));
        Assert.True(prompts.Said("Scan"));
        Assert.True(prompts.Said("find the broken files"));
        Assert.True(prompts.Said("Backup"));
        Assert.True(prompts.Said("Quit"));
    }

    [Fact]
    public void Enter_takes_the_default_when_there_is_one()
    {
        var answer = new Asker(new ScriptedPrompts("")).Ask("What now?", Three, defaultIndex: 2);

        Assert.Equal(2, answer.Index);
    }

    [Fact]
    public void Enter_re_asks_when_there_is_no_default()
    {
        var prompts = new ScriptedPrompts("", "1");

        var answer = new Asker(prompts).Ask("What now?", Three);

        Assert.Equal(0, answer.Index);
        Assert.True(prompts.Said("Type the number"));
    }

    [Fact]
    public void The_default_is_marked_so_the_operator_knows_what_Enter_does()
    {
        new Asker(new ScriptedPrompts("")).Ask("What now?", Three, defaultIndex: 0);
        // rendered as "[default]" next to the choice — checked via a fresh run
        var prompts = new ScriptedPrompts("");
        new Asker(prompts).Ask("What now?", Three, defaultIndex: 0);

        Assert.True(prompts.Said("[default]"));
    }

    [Fact]
    public void A_question_mark_explains_every_choice_then_asks_again()
    {
        var prompts = new ScriptedPrompts("?", "1");

        var answer = new Asker(prompts).Ask("What now?", Three);

        Assert.Equal(0, answer.Index);
        Assert.True(prompts.Said("Reads CRM and writes three report files."));
        Assert.True(prompts.Said("Downloads every file from the vendor server."));
    }

    [Fact]
    public void A_choice_with_no_long_help_still_appears_under_the_question_mark()
    {
        var prompts = new ScriptedPrompts("?", "1");

        new Asker(prompts).Ask("What now?", Three);

        Assert.True(prompts.Said("Quit"));
    }

    [Fact]
    public void B_goes_back()
    {
        var answer = new Asker(new ScriptedPrompts("b")).Ask("What now?", Three);

        Assert.Equal(AnswerKind.Back, answer.Kind);
    }

    [Fact]
    public void Back_is_refused_and_re_asked_when_there_is_nowhere_to_go()
    {
        var prompts = new ScriptedPrompts("b", "1");

        var answer = new Asker(prompts).Ask("What now?", Three, allowBack: false);

        Assert.Equal(AnswerKind.Chosen, answer.Kind);
        Assert.True(prompts.Said("nothing to go back to"));
    }

    [Fact]
    public void Q_quits()
    {
        Assert.Equal(AnswerKind.Quit, new Asker(new ScriptedPrompts("q")).Ask("What now?", Three).Kind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("banana")]
    [InlineData("1.5")]
    public void Anything_unusable_re_asks_rather_than_falling_through(string input)
    {
        var prompts = new ScriptedPrompts(input, "1");

        var answer = new Asker(prompts).Ask("What now?", Three);

        Assert.Equal(AnswerKind.Chosen, answer.Kind);
        Assert.Equal(0, answer.Index);
        Assert.True(prompts.Said("Type the number"));
    }

    [Fact]
    public void A_disabled_choice_says_why_and_re_asks()
    {
        var choices = new[]
        {
            new Choice("dev", "ready"),
            new Choice("prod", "production", Enabled: false,
                DisabledNote: "Production needs --confirm-production on the command line.")
        };
        var prompts = new ScriptedPrompts("2", "1");

        var answer = new Asker(prompts).Ask("Which environment?", choices);

        Assert.Equal(0, answer.Index);
        Assert.True(prompts.Said("Production needs --confirm-production"));
    }

    [Fact]
    public void A_disabled_choice_is_still_listed_so_the_numbering_never_shifts()
    {
        var choices = new[]
        {
            new Choice("dev", "ready"),
            new Choice("prod", "production", Enabled: false, DisabledNote: "no"),
            new Choice("test", "ready")
        };
        var prompts = new ScriptedPrompts("3");

        var answer = new Asker(prompts).Ask("Which environment?", choices);

        Assert.Equal(2, answer.Index);
        Assert.True(prompts.Said("prod"));
    }

    [Fact]
    public void Input_is_trimmed_and_case_does_not_matter()
    {
        Assert.Equal(AnswerKind.Quit, new Asker(new ScriptedPrompts("  Q  ")).Ask("?", Three).Kind);
        Assert.Equal(AnswerKind.Back, new Asker(new ScriptedPrompts(" B ")).Ask("?", Three).Kind);
        Assert.Equal(1, new Asker(new ScriptedPrompts(" 2 ")).Ask("?", Three).Index);
    }
}
