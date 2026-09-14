using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The layout helpers. These matter more than they look: every screen in the tool now writes
/// its paragraphs as one sentence and lets this decide where the lines break, so a mistake here
/// is a mistake everywhere at once.
/// </summary>
public class ScreenTests
{
    [Fact]
    public void Wrap_breaks_at_spaces_within_the_width()
    {
        var lines = Screen.Wrap("one two three four five six", 10);

        Assert.All(lines, l => Assert.True(l.Length <= 10, $"'{l}' is {l.Length} long"));
        Assert.Equal("one two three four five six", string.Join(" ", lines));
    }

    /// <summary>
    /// A path or a GUID is longer than the width and must survive whole: a path broken across
    /// two lines cannot be copied out of the console and cannot be searched for in CRM.
    /// </summary>
    [Fact]
    public void A_word_longer_than_the_width_is_left_whole()
    {
        var path = @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260914\a.png";

        Assert.Contains(path, Screen.Wrap($"at {path} today", 20));
    }

    /// <summary>
    /// Descriptions arrive here with their own columns already padded. Collapsing the runs of
    /// spaces would un-align every list the tool prints, which is exactly what happened the
    /// first time this wrapped by splitting on whitespace.
    /// </summary>
    [Fact]
    public void Runs_of_spaces_inside_a_line_are_left_alone()
    {
        Assert.Equal("[   2]  wrong catalogue",
            Screen.Wrap("[   2]  wrong catalogue", 76).Single());
    }

    [Fact]
    public void Text_that_already_fits_comes_back_unchanged()
        => Assert.Equal("short", Screen.Wrap("short", 76).Single());

    [Fact]
    public void Empty_text_is_one_empty_line_rather_than_nothing()
        => Assert.Equal("", Screen.Wrap("", 76).Single());

    [Fact]
    public void Say_indents_every_line_of_a_wrapped_paragraph_the_same()
    {
        var prompts = new FakePrompts();

        prompts.Say(string.Join(" ", Enumerable.Repeat("word", 40)));

        Assert.True(prompts.Messages.Count > 1);
        Assert.All(prompts.Messages, m => Assert.StartsWith("  word", m));
        Assert.All(prompts.Messages, m => Assert.True(m.Length <= Screen.Width));
    }

    [Fact]
    public void A_bullets_continuation_lines_hang_under_its_first()
    {
        var prompts = new FakePrompts();

        prompts.Bullet(string.Join(" ", Enumerable.Repeat("word", 40)));

        Assert.StartsWith("    • word", prompts.Messages[0]);
        Assert.All(prompts.Messages.Skip(1), m => Assert.StartsWith("      word", m));
    }

    [Fact]
    public void A_warning_is_marked_as_well_as_coloured()
    {
        var prompts = new FakePrompts();

        prompts.Warn("This cannot be undone.");

        Assert.StartsWith("  !   This cannot be undone.", prompts.Messages[0]);
    }

    [Fact]
    public void A_field_puts_its_values_in_one_column()
    {
        var prompts = new FakePrompts();

        prompts.Field("CRM", "https://example");
        prompts.Field("File server", "http://example");

        Assert.Equal(prompts.Messages[0].IndexOf("https", StringComparison.Ordinal),
                     prompts.Messages[1].IndexOf("http:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The column has to be wider than the longest label, not equal to it: "Document type" was
    /// exactly as long as the column, so it ran straight into its own value.
    /// </summary>
    [Fact]
    public void The_longest_label_still_has_a_gap_after_it()
    {
        var prompts = new FakePrompts();

        prompts.Field("Document type", "Medical Certificate");

        Assert.Contains("Document type  Medical Certificate", prompts.Messages[0]);
    }

    [Fact]
    public void A_section_underlines_its_heading_to_its_own_length()
    {
        var prompts = new FakePrompts();

        prompts.Section("Step 5 — Delete old files");

        Assert.Equal("  Step 5 — Delete old files", prompts.Messages[1]);
        Assert.Equal("  " + new string('─', "Step 5 — Delete old files".Length), prompts.Messages[2]);
    }
}
