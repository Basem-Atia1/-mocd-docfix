using MocdDocFix.Ui;

namespace MocdDocFix.Tests.Fakes;

/// <summary>
/// Prompts driven by a fixed script. Unlike <see cref="FakePrompts"/> it throws when the script
/// runs out, so a question that re-asks forever fails the test loudly instead of hanging.
/// </summary>
public sealed class ScriptedPrompts : IPrompts
{
    private readonly Queue<string> _lines;

    public ScriptedPrompts(params string[] lines) => _lines = new Queue<string>(lines);

    public List<string> Questions { get; } = new();
    public List<string> Messages { get; } = new();

    public string Transcript => string.Join(Environment.NewLine, Messages);

    public bool Said(string fragment) =>
        Messages.Any(m => m.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public string ReadLine(string question)
    {
        Questions.Add(question);

        if (_lines.Count == 0)
            throw new InvalidOperationException(
                $"Ran out of scripted input at '{question}'. Asked so far: " +
                string.Join(" | ", Questions));

        return _lines.Dequeue();
    }

    public ConfirmChoice Confirm(string question) =>
        ReadLine(question).Trim().ToLowerInvariant() switch
        {
            "y" or "yes" => ConfirmChoice.Yes,
            "s" or "skip" => ConfirmChoice.Skip,
            "q" or "quit" => ConfirmChoice.Quit,
            _ => ConfirmChoice.No
        };

    public bool TypedWord(string question, string requiredWord) =>
        string.Equals(ReadLine(question).Trim(), requiredWord, StringComparison.Ordinal);

    public void Info(string message) => Messages.Add(message);
}
