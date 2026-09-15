using MocdDocFix.Ui;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakePrompts : IPrompts
{
    private readonly Queue<ConfirmChoice> _answers = new();

    public List<string> Questions { get; } = new();
    public List<string> Messages { get; } = new();
    public string? TypedWordResponse { get; set; }
    public string ReadLineResponse { get; set; } = string.Empty;

    /// <summary>What every yes/no question is answered with. No, unless a test says otherwise.</summary>
    public bool YesNoResponse { get; set; }

    /// <summary>When set, each yes/no question takes the next queued answer instead.</summary>
    public Queue<bool>? YesNoQueue { get; set; }

    /// <summary>When set, each ReadLine takes the next queued answer instead of ReadLineResponse.</summary>
    public Queue<string>? ReadLineQueue { get; set; }

    public FakePrompts Answer(params ConfirmChoice[] choices)
    {
        foreach (var c in choices) _answers.Enqueue(c);
        return this;
    }

    public ConfirmChoice Confirm(string question)
    {
        Questions.Add(question);
        return _answers.Count > 0 ? _answers.Dequeue() : ConfirmChoice.Quit;
    }

    public bool YesNo(string question, bool defaultYes = false, Tone tone = Tone.Normal)
    {
        Questions.Add(question);
        return YesNoQueue is { Count: > 0 } ? YesNoQueue.Dequeue() : YesNoResponse;
    }

    public bool TypedWord(string question, string requiredWord)
    {
        Questions.Add(question);
        return string.Equals(TypedWordResponse, requiredWord, StringComparison.OrdinalIgnoreCase);
    }

    public string ReadLine(string question)
    {
        Questions.Add(question);

        if (ReadLineQueue is null) return ReadLineResponse;

        // Loudly, rather than by returning something unusable forever: a question that re-asks
        // on bad input spun here until the runtime died, and the failure said nothing about
        // which question had run out of answers.
        if (ReadLineQueue.Count == 0)
            throw new InvalidOperationException(
                $"The scripted answers ran out at '{question}'. Asked so far: " +
                string.Join(" | ", Questions));

        return ReadLineQueue.Dequeue();
    }

    // ---- the live menu ----

    /// <summary>Setting this makes the fake behave like a real console: arrow keys, not typing.</summary>
    public Queue<(MenuKey Key, char Character)>? Keys { get; set; }

    /// <summary>How many lines the menu has rubbed out, so redrawing can be checked.</summary>
    public int Rewound { get; private set; }

    public bool Interactive => Keys is not null;

    public (MenuKey Key, char Character) ReadMenuKey() =>
        Keys is { Count: > 0 } ? Keys.Dequeue() : (MenuKey.Escape, '\0');

    /// <summary>
    /// Queued answers to "has the operator pressed q?", one per document boundary. Left unset
    /// nobody ever has, which is how every test that is not about stopping behaves.
    /// </summary>
    public Queue<bool>? StopRequests { get; set; }

    public bool StopRequested() => StopRequests is { Count: > 0 } && StopRequests.Dequeue();

    public void Rewind(int lines) => Rewound += lines;

    public void Info(string message, Tone tone = Tone.Normal) => Messages.Add(message);
}
