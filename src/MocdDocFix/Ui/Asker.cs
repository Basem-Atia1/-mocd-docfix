namespace MocdDocFix.Ui;

/// <param name="Label">The short name, shown next to the number.</param>
/// <param name="Description">One line saying what it does, or its status.</param>
/// <param name="LongHelp">Shown when the operator types '?'. Falls back to the description.</param>
/// <param name="Enabled">
/// A disabled choice is still printed and still consumes its number, so the numbering never
/// shifts under the operator between one showing of a question and the next.
/// </param>
public sealed record Choice(
    string Label,
    string Description,
    string? LongHelp = null,
    bool Enabled = true,
    string? DisabledNote = null);

public enum AnswerKind { Chosen, Back, Quit }

public sealed record Answer(AnswerKind Kind, int Index)
{
    public static readonly Answer Back = new(AnswerKind.Back, -1);
    public static readonly Answer Quit = new(AnswerKind.Quit, -1);

    public static Answer Choose(int index) => new(AnswerKind.Chosen, index);
}

/// <summary>
/// Asks one question with numbered answers, and keeps asking until it gets something it can use.
/// Nothing typed here can end the program by accident: unusable input re-asks (spec 2026-09-13
/// section 6).
/// </summary>
public sealed class Asker
{
    /// <summary>
    /// How far a long label may push the description column out before it is left to have its
    /// description on the line below instead. Past this there is too little room left to say
    /// anything useful in the description.
    /// </summary>
    private const int WidestLabelColumn = 36;

    private readonly IPrompts _prompts;

    public Asker(IPrompts prompts) => _prompts = prompts;

    public Answer Ask(string question, IReadOnlyList<Choice> choices,
        int? defaultIndex = null, bool allowBack = true)
    {
        while (true)
        {
            Show(question, choices, defaultIndex);

            var typed = _prompts.ReadLine("  Choose").Trim();

            if (typed.Length == 0)
            {
                if (defaultIndex is { } d && choices[d].Enabled) return Answer.Choose(d);
                Hint(choices.Count, allowBack);
                continue;
            }

            switch (typed.ToLowerInvariant())
            {
                case "?" or "help":
                    Explain(choices);
                    continue;

                case "q" or "quit" or "exit":
                    return Answer.Quit;

                case "b" or "back":
                    if (allowBack) return Answer.Back;
                    _prompts.Info("  There is nothing to go back to from here.", Tone.Warn);
                    continue;
            }

            if (!int.TryParse(typed, out var number) || number < 1 || number > choices.Count)
            {
                Hint(choices.Count, allowBack);
                continue;
            }

            var choice = choices[number - 1];
            if (choice.Enabled) return Answer.Choose(number - 1);

            _prompts.Blank();
            _prompts.Warn($"{choice.Label} is not available: " +
                          (choice.DisabledNote ?? "it is not set up."));
        }
    }

    private void Show(string question, IReadOnlyList<Choice> choices, int? defaultIndex)
    {
        _prompts.Section(question);
        _prompts.Blank();

        // One column for this question's descriptions, wide enough for its own longest label.
        // Fixing it in advance would either waste the width of every short-labelled question or
        // push the one long label in a list onto a line of its own.
        var heads = choices
            .Select((c, i) => $"   {Marker(c, defaultIndex == i)}{i + 1,2}  {c.Label}")
            .ToList();

        var column = Math.Min(WidestLabelColumn, heads.Max(h => h.Length) + 2);

        for (var i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            var tone = c.Enabled ? Tone.Normal : Tone.Muted;
            var wrapped = Screen.Wrap(c.Description, Screen.Width - column);

            if (heads[i].Length >= column)
            {
                // Too long even for the widened column: the description goes underneath rather
                // than shunting every other row across to meet it.
                _prompts.Info(heads[i], tone);
                foreach (var line in wrapped) _prompts.Info(new string(' ', column) + line, tone);
                continue;
            }

            _prompts.Info(heads[i].PadRight(column) + wrapped[0], tone);
            foreach (var line in wrapped.Skip(1))
                _prompts.Info(new string(' ', column) + line, tone);
        }

        // Said once, under the list, rather than tacked onto one description — where it used to
        // push that one row's text onto a second line and make the list look ragged.
        if (defaultIndex is { } d && choices[d].Enabled)
            _prompts.Info($"   > Enter chooses {d + 1}, {choices[d].Label} (default).", Tone.Muted);

        _prompts.Blank();
    }

    /// <summary>
    /// The column before the number: '-' for a choice that cannot be picked, '>' for the one
    /// Enter would take, blank otherwise. A marker as well as a colour, so the list still reads
    /// where there is none.
    /// </summary>
    private static string Marker(Choice choice, bool isDefault) =>
        !choice.Enabled ? "-" : isDefault ? ">" : " ";

    private void Explain(IReadOnlyList<Choice> choices)
    {
        foreach (var (c, i) in choices.Select((c, i) => (c, i)))
        {
            _prompts.Blank();
            _prompts.Info($"   {i + 1,2}  {c.Label}", Tone.Strong);

            foreach (var line in Screen.Wrap(c.LongHelp ?? c.Description, Screen.Width - 8))
                _prompts.Info("       " + line, Tone.Muted);

            if (!c.Enabled && c.DisabledNote is not null)
                foreach (var line in Screen.Wrap("Not available: " + c.DisabledNote, Screen.Width - 8))
                    _prompts.Info("       " + line, Tone.Warn);
        }
    }

    private void Hint(int count, bool allowBack) =>
        _prompts.Info($"  Type the number of a choice, 1 to {count}" +
                      (allowBack ? ", or b to go back" : "") +
                      ", ? for more detail, or q to quit.", Tone.Warn);
}
