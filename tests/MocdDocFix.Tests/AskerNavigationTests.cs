using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Driving a menu with the arrow keys, and the confirmation that goes with it.
///
/// The reason both exist: a question that took a bare Enter as "yes, continue" ran a whole step
/// nobody had asked for. Moving the cursor now changes nothing, and the only thing that acts is
/// Enter followed by a plain yes.
/// </summary>
public class AskerNavigationTests
{
    private static readonly Choice[] Three =
    {
        new("First", "the first one"),
        new("Second", "the second one"),
        new("Third", "the third one")
    };

    private static FakePrompts WithKeys(params (MenuKey, char)[] keys) =>
        new() { Keys = new Queue<(MenuKey, char)>(keys) };

    [Fact]
    public void Down_then_enter_chooses_the_next_row()
    {
        var prompts = WithKeys((MenuKey.Down, '\0'), (MenuKey.Enter, '\0'));

        var answer = new Asker(prompts).Ask("Pick one", Three, defaultIndex: 0);

        Assert.Equal(AnswerKind.Chosen, answer.Kind);
        Assert.Equal(1, answer.Index);
    }

    [Fact]
    public void Up_from_the_first_row_wraps_to_the_last()
    {
        var prompts = WithKeys((MenuKey.Up, '\0'), (MenuKey.Enter, '\0'));

        Assert.Equal(2, new Asker(prompts).Ask("Pick one", Three, defaultIndex: 0).Index);
    }

    [Fact]
    public void Moving_the_cursor_does_nothing_until_enter()
    {
        // Four moves and no Enter: the menu is left by escape, having chosen nothing at all.
        var prompts = WithKeys(
            (MenuKey.Down, '\0'), (MenuKey.Down, '\0'), (MenuKey.Up, '\0'), (MenuKey.Escape, '\0'));

        Assert.Equal(AnswerKind.Quit, new Asker(prompts).Ask("Pick one", Three).Kind);
    }

    [Fact]
    public void A_disabled_row_is_stepped_over_rather_than_landed_on()
    {
        var choices = new[]
        {
            new Choice("First", "yes"),
            new Choice("Locked", "no", Enabled: false, DisabledNote: "not set up"),
            new Choice("Third", "yes")
        };

        var prompts = WithKeys((MenuKey.Down, '\0'), (MenuKey.Enter, '\0'));

        Assert.Equal(2, new Asker(prompts).Ask("Pick one", choices, defaultIndex: 0).Index);
    }

    [Fact]
    public void A_number_still_jumps_straight_to_a_row()
    {
        var prompts = WithKeys((MenuKey.Digit, '3'), (MenuKey.Enter, '\0'));

        Assert.Equal(2, new Asker(prompts).Ask("Pick one", Three, defaultIndex: 0).Index);
    }

    [Fact]
    public void Escape_and_b_leave_without_choosing()
    {
        Assert.Equal(AnswerKind.Quit,
            new Asker(WithKeys((MenuKey.Escape, '\0'))).Ask("Pick one", Three).Kind);

        Assert.Equal(AnswerKind.Back,
            new Asker(WithKeys((MenuKey.Back, 'b'))).Ask("Pick one", Three, allowBack: true).Kind);
    }

    [Fact]
    public void Back_is_refused_where_there_is_nothing_to_go_back_to()
    {
        var prompts = WithKeys((MenuKey.Back, 'b'), (MenuKey.Enter, '\0'));

        Assert.Equal(AnswerKind.Chosen,
            new Asker(prompts).Ask("Pick one", Three, defaultIndex: 0, allowBack: false).Kind);
    }

    // ---- are you sure ----

    [Fact]
    public void With_confirm_on_a_yes_is_needed_as_well_as_enter()
    {
        var prompts = WithKeys((MenuKey.Enter, '\0'));
        prompts.YesNoResponse = true;

        var answer = new Asker(prompts).Ask("Pick one", Three, defaultIndex: 0, confirm: true);

        Assert.Equal(0, answer.Index);
        Assert.Contains(prompts.Questions, q => q.Contains("First — are you sure?"));
    }

    [Fact]
    public void Saying_no_to_the_confirmation_puts_the_menu_back()
    {
        // Enter on the first row, declined; then down and Enter, accepted.
        var prompts = WithKeys((MenuKey.Enter, '\0'), (MenuKey.Down, '\0'), (MenuKey.Enter, '\0'));
        prompts.YesNoQueue = new Queue<bool>(new[] { false, true });

        var answer = new Asker(prompts).Ask("Pick one", Three, defaultIndex: 0, confirm: true);

        Assert.Equal(1, answer.Index);
    }

    [Fact]
    public void The_menu_is_rubbed_out_and_redrawn_rather_than_printed_again_below()
    {
        var prompts = WithKeys((MenuKey.Down, '\0'), (MenuKey.Enter, '\0'));

        new Asker(prompts).Ask("Pick one", Three, defaultIndex: 0);

        Assert.True(prompts.Rewound > 0, "the second draw should have cleared the first");
    }

    [Fact]
    public void The_row_under_the_cursor_is_marked_so_it_reads_without_colour()
    {
        var prompts = WithKeys((MenuKey.Down, '\0'), (MenuKey.Enter, '\0'));

        new Asker(prompts).Ask("Pick one", Three, defaultIndex: 0);

        Assert.Contains(prompts.Messages, m => m.Contains("› 2  Second"));
    }

    // ---- the typed path is untouched ----

    [Fact]
    public void Without_a_console_it_still_takes_a_typed_number()
    {
        var prompts = new ScriptedPrompts("2");

        Assert.Equal(1, new Asker(prompts).Ask("Pick one", Three).Index);
    }

    /// <summary>
    /// The bug this was all for: a question with no default must not be answerable by a stray
    /// newline. It re-asks instead.
    /// </summary>
    [Fact]
    public void A_bare_enter_cannot_answer_a_question_that_has_no_default()
    {
        var prompts = new ScriptedPrompts("", "3");

        Assert.Equal(2, new Asker(prompts).Ask("Pick one", Three, defaultIndex: null).Index);
    }
}
