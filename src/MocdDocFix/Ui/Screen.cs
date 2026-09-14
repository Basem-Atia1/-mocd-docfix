
namespace MocdDocFix.Ui;

/// <summary>
/// The tool's house style for laying text out.
///
/// It exists because the alternative — every screen hand-breaking its own paragraphs into a
/// series of Info calls — produced text that was ragged wherever a sentence was later edited,
/// and left nothing to distinguish a heading from a detail line. Here a paragraph is written as
/// one sentence and wrapped at one width, and headings, fields and lists all line up because
/// there is one place that decides where they sit.
/// </summary>
public static class Screen
{
    /// <summary>Wrap width. Narrow enough to stay readable in a default 80-column console.</summary>
    public const int Width = 76;

    private const string Indent = "  ";

    public static void Blank(this IPrompts prompts) => prompts.Info("");

    /// <summary>The banner at the top of a screen.</summary>
    public static void Title(this IPrompts prompts, string text)
    {
        var rule = new string('═', Width);

        prompts.Info("");
        prompts.Info(rule, Tone.Strong);
        prompts.Info(Indent + text, Tone.Strong);
        prompts.Info(rule, Tone.Strong);
    }

    /// <summary>A heading with a rule under it, to break a long screen into parts.</summary>
    public static void Section(this IPrompts prompts, string heading, Tone tone = Tone.Strong)
    {
        prompts.Info("");
        prompts.Info(Indent + heading, tone);
        prompts.Info(Indent + new string('─', Math.Min(Width - Indent.Length, heading.Length)), Tone.Muted);
    }

    /// <summary>A paragraph, wrapped. Write the sentence; do not break it yourself.</summary>
    public static void Say(this IPrompts prompts, string text, Tone tone = Tone.Normal)
    {
        foreach (var line in Wrap(text, Width - Indent.Length))
            prompts.Info(Indent + line, tone);
    }

    /// <summary>One item of a list, with the continuation lines hanging under the first.</summary>
    public static void Bullet(this IPrompts prompts, string text, Tone tone = Tone.Normal)
    {
        var lines = Wrap(text, Width - 6);

        for (var i = 0; i < lines.Count; i++)
            prompts.Info((i == 0 ? "    • " : "      ") + lines[i], tone);
    }

    /// <summary>A label and its value, with values aligned down the screen.</summary>
    public static void Field(this IPrompts prompts, string label, string value, Tone tone = Tone.Normal)
        => prompts.Info(Indent + label.PadRight(13) + value, tone);

    /// <summary>
    /// A line the operator must not skim past. The marker carries the same weight as the colour,
    /// so it still stands out where there is no colour.
    /// </summary>
    public static void Warn(this IPrompts prompts, string text, Tone tone = Tone.Warn)
    {
        var lines = Wrap(text, Width - 6);

        for (var i = 0; i < lines.Count; i++)
            prompts.Info((i == 0 ? "  !   " : "      ") + lines[i], tone);
    }

    /// <summary>
    /// Splits text to fit the width, breaking at spaces.
    ///
    /// Runs of spaces inside a line are left exactly as they are: descriptions arrive here with
    /// their own small columns already padded ("[   2]  wrong catalogue"), and collapsing that
    /// padding would quietly un-align every list the tool prints. A word longer than the width —
    /// a file path, a GUID — is left whole on its own line rather than broken, because a broken
    /// path cannot be copied and cannot be searched for.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        if (width < 1) width = 1;

        var lines = new List<string>();
        var remaining = text;

        while (remaining.Length > width)
        {
            var cut = remaining.LastIndexOf(' ', width);

            // Nothing to break on within the width: run on to the next space instead of
            // cutting a word in half.
            if (cut <= 0) cut = remaining.IndexOf(' ', width);
            if (cut < 0) break;

            lines.Add(remaining[..cut].TrimEnd());
            remaining = remaining[(cut + 1)..].TrimStart();
        }

        lines.Add(remaining);
        return lines;
    }
}
