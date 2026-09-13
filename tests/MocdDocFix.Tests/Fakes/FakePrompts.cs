using MocdDocFix.Ui;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakePrompts : IPrompts
{
    private readonly Queue<ConfirmChoice> _answers = new();

    public List<string> Questions { get; } = new();
    public List<string> Messages { get; } = new();
    public string? TypedWordResponse { get; set; }
    public string ReadLineResponse { get; set; } = string.Empty;

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

    public bool TypedWord(string question, string requiredWord)
    {
        Questions.Add(question);
        return TypedWordResponse == requiredWord;
    }

    /// <summary>When set, each ReadLine takes the next queued answer instead of ReadLineResponse.</summary>
    public Queue<string>? ReadLineQueue { get; set; }

    public string ReadLine(string question)
    {
        Questions.Add(question);
        if (ReadLineQueue is { Count: > 0 }) return ReadLineQueue.Dequeue();
        return ReadLineQueue is not null ? "0" : ReadLineResponse;
    }

    public void Info(string message) => Messages.Add(message);
}
