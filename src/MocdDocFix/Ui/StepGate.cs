namespace MocdDocFix.Ui;

/// <summary>
/// The stop between one phase and the next. Every path through the tool goes through this, so a
/// single answer can never set off two phases (spec 2026-09-13 section 6.4).
/// </summary>
public sealed class StepGate
{
    public const int TotalSteps = 5;

    private readonly IPrompts _prompts;
    private readonly Asker _asker;

    public StepGate(IPrompts prompts)
    {
        _prompts = prompts;
        _asker = new Asker(prompts);
    }

    /// <param name="step">"1", "2", "3 and 4", "5" — or empty for a phase outside the sequence.</param>
    public void Report(string step, string name, string headline, IReadOnlyList<string> details)
    {
        _prompts.Section(step.Length > 0
            ? $"Step {step} of {TotalSteps} — {name} — done"
            : $"{name} — done");

        _prompts.Say(headline);

        if (details.Count > 0)
        {
            _prompts.Blank();
            ShowDetails(details);
        }
    }

    /// <summary>
    /// Detail lines come from the phases themselves and can be a sentence long, so they wrap and
    /// hang under their own indent rather than running off the right of the screen.
    /// </summary>
    private void ShowDetails(IReadOnlyList<string> details)
    {
        foreach (var detail in details)
        {
            var wrapped = Screen.Wrap(detail, Screen.Width - 4);

            _prompts.Info("    " + wrapped[0], Tone.Muted);
            foreach (var line in wrapped.Skip(1)) _prompts.Info("      " + line, Tone.Muted);
        }
    }

    /// <returns>True to carry on to the next step.</returns>
    public bool Ask(string step, string name, string headline, IReadOnlyList<string> details, string next)
    {
        Report(step, name, headline, details);

        while (true)
        {
            var answer = _asker.Ask($"Step {step} finished. What next?", new[]
            {
                new Choice("Continue", $"go on and {next}"),
                new Choice("Show details", "print what happened to each file, then ask again"),
                new Choice("Stop here", "nothing else runs; what is done stays done")
            }, defaultIndex: 0, allowBack: false);

            switch (answer.Kind == AnswerKind.Chosen ? answer.Index : 2)
            {
                case 0: return true;

                case 1:
                    _prompts.Blank();
                    if (details.Count == 0) _prompts.Info("    (nothing further to show)", Tone.Muted);
                    ShowDetails(details);
                    continue;

                default:
                    _prompts.Blank();
                    _prompts.Say("Stopped. Nothing further was run.", Tone.Strong);
                    _prompts.Say("The old files are untouched, so this is always safe to stop at.",
                        Tone.Muted);
                    return false;
            }
        }
    }
}
