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
    private readonly IPrompts _prompts;

    public Asker(IPrompts prompts) => _prompts = prompts;

    public Answer Ask(string question, IReadOnlyList<Choice> choices,
        int? defaultIndex = null, bool allowBack = true)
    {
        while (true)
        {
            Show(question, choices, defaultIndex);

            var typed = _prompts.ReadLine("  >").Trim();

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
                    _prompts.Info("  There is nothing to go back to from here.");
                    continue;
            }

            if (!int.TryParse(typed, out var number) || number < 1 || number > choices.Count)
            {
                Hint(choices.Count, allowBack);
                continue;
            }

            var choice = choices[number - 1];
            if (choice.Enabled) return Answer.Choose(number - 1);

            _prompts.Info("");
            _prompts.Info($"  {choice.Label} is not available: " +
                          (choice.DisabledNote ?? "it is not set up."));
        }
    }

    private void Show(string question, IReadOnlyList<Choice> choices, int? defaultIndex)
    {
        _prompts.Info("");
        _prompts.Info($"  {question}");
        _prompts.Info("");

        for (var i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            var marker = c.Enabled ? " " : "-";
            var isDefault = defaultIndex == i ? "   [default]" : "";
            _prompts.Info($"   {marker}{i + 1,2}  {c.Label,-14} {c.Description}{isDefault}");
        }

        _prompts.Info("");
    }

    private void Explain(IReadOnlyList<Choice> choices)
    {
        _prompts.Info("");
        for (var i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            _prompts.Info($"   {i + 1,2}  {c.Label}");
            _prompts.Info($"       {c.LongHelp ?? c.Description}");
            if (!c.Enabled && c.DisabledNote is not null)
                _prompts.Info($"       Not available: {c.DisabledNote}");
        }
    }

    private void Hint(int count, bool allowBack) =>
        _prompts.Info($"  Type the number of a choice, 1 to {count}" +
                      (allowBack ? ", or b to go back" : "") +
                      ", ? for more detail, or q to quit.");
}
