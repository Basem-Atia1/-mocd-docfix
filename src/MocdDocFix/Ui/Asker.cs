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

    /// <param name="confirm">
    /// Ask "are you sure" after the choice. On for anything that sets work going: a question
    /// answered by a stray keypress once ran a whole step nobody had asked for.
    /// </param>
    public Answer Ask(string question, IReadOnlyList<Choice> choices,
        int? defaultIndex = null, bool allowBack = true, bool confirm = false)
    {
        if (_prompts.Interactive)
            return Navigate(question, choices, defaultIndex, allowBack, confirm);

        return Typed(question, choices, defaultIndex, allowBack);
    }

    /// <summary>
    /// The live menu: up and down to move, Enter to choose, and a plain question after it.
    /// Nothing here can be answered by accident — a keypress moves the highlight, and only
    /// Enter followed by a yes actually does anything.
    /// </summary>
    private Answer Navigate(string question, IReadOnlyList<Choice> choices,
        int? defaultIndex, bool allowBack, bool confirm)
    {
        var selectable = Enumerable.Range(0, choices.Count).Where(i => choices[i].Enabled).ToList();

        if (selectable.Count == 0) return Answer.Quit;

        var at = selectable.Contains(defaultIndex ?? -1) ? defaultIndex!.Value : selectable[0];
        var drawn = 0;

        while (true)
        {
            _prompts.Rewind(drawn);
            drawn = Show(question, choices, defaultIndex, at);
            _prompts.Info("   ↑ ↓ to move · Enter to choose · ? for detail · " +
                          (allowBack ? "b to go back · " : "") + "q to quit", Tone.Muted);
            drawn++;

            var (key, character) = _prompts.ReadMenuKey();

            switch (key)
            {
                case MenuKey.Up:
                    at = selectable[(selectable.IndexOf(at) - 1 + selectable.Count) % selectable.Count];
                    continue;

                case MenuKey.Down:
                    at = selectable[(selectable.IndexOf(at) + 1) % selectable.Count];
                    continue;

                case MenuKey.Digit:
                    var typed = character - '1';
                    if (typed >= 0 && typed < choices.Count && choices[typed].Enabled) at = typed;
                    continue;

                case MenuKey.Help:
                    _prompts.Rewind(drawn);
                    drawn = 0;
                    Explain(choices);
                    continue;

                case MenuKey.Back when allowBack:
                    return Answer.Back;

                case MenuKey.Escape:
                    return Answer.Quit;

                case MenuKey.Enter:
                    if (!confirm || AreYouSure(choices[at])) return Answer.Choose(at);
                    drawn = 0;                     // the confirmation printed over the menu
                    continue;

                default:
                    continue;
            }
        }
    }

    private bool AreYouSure(Choice choice)
    {
        _prompts.Blank();
        return _prompts.YesNo($"  {choice.Label} — are you sure?", defaultYes: true, Tone.Warn);
    }

    private Answer Typed(string question, IReadOnlyList<Choice> choices,
        int? defaultIndex, bool allowBack)
    {
        while (true)
        {
            Show(question, choices, defaultIndex, defaultIndex ?? -1);

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

    /// <param name="highlighted">The row the cursor is on, or -1 when nothing is highlighted.</param>
    /// <returns>How many lines were printed, so a live menu can rub them out and redraw.</returns>
    private int Show(string question, IReadOnlyList<Choice> choices, int? defaultIndex, int highlighted)
    {
        var lines = 0;

        void Line(string text, Tone tone = Tone.Normal) { _prompts.Info(text, tone); lines++; }

        _prompts.Info("");
        Line("  " + question, Tone.Strong);
        Line("  " + new string('─', Math.Min(Screen.Width - 2, question.Length)), Tone.Muted);
        lines++;                                   // the blank line above the heading
        Line("");

        // One column for this question's descriptions, wide enough for its own longest label.
        // Fixing it in advance would either waste the width of every short-labelled question or
        // push the one long label in a list onto a line of its own.
        var heads = choices
            .Select((c, i) => $"  {Marker(c, highlighted == i)}{i + 1,2}  {c.Label}")
            .ToList();

        var column = Math.Min(WidestLabelColumn, heads.Max(h => h.Length) + 2);

        for (var i = 0; i < choices.Count; i++)
        {
            var c = choices[i];

            var tone = !c.Enabled ? Tone.Muted
                : highlighted == i ? Tone.Strong
                : Tone.Normal;

            var wrapped = Screen.Wrap(c.Description, Screen.Width - column);

            if (heads[i].Length >= column)
            {
                // Too long even for the widened column: the description goes underneath rather
                // than shunting every other row across to meet it.
                Line(heads[i], tone);
                foreach (var line in wrapped) Line(new string(' ', column) + line, tone);
                continue;
            }

            Line(heads[i].PadRight(column) + wrapped[0], tone);
            foreach (var line in wrapped.Skip(1))
                Line(new string(' ', column) + line, tone);
        }

        // Only the typed path needs telling what Enter does; the live menu shows it, because the
        // row Enter would take is the one under the cursor.
        if (!_prompts.Interactive && defaultIndex is { } d && choices[d].Enabled)
            Line($"   > Enter chooses {d + 1}, {choices[d].Label} (default).", Tone.Muted);

        Line("");
        return lines;
    }

    /// <summary>
    /// The column before the number: '-' for a choice that cannot be picked, '›' for the row the
    /// cursor is on, blank otherwise. A marker as well as a colour, so the list still reads where
    /// there is none — and so the selected row is obvious on a monochrome console.
    /// </summary>
    private static string Marker(Choice choice, bool highlighted) =>
        !choice.Enabled ? "-" : highlighted ? "›" : " ";

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
