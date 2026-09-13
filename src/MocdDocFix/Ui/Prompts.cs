namespace MocdDocFix.Ui;

public enum ConfirmChoice { Yes, No, Skip, Quit }

public interface IPrompts
{
    ConfirmChoice Confirm(string question);
    bool TypedWord(string question, string requiredWord);
    string ReadLine(string question);
    void Info(string message);
}

public sealed class ConsolePrompts : IPrompts
{
    private readonly TextReader _in;
    private readonly TextWriter _out;

    /// <summary>The reader and writer are injectable only so the end-of-input path can be tested.</summary>
    public ConsolePrompts(TextReader? input = null, TextWriter? output = null)
    {
        _in = input ?? Console.In;
        _out = output ?? Console.Out;
    }

    public ConfirmChoice Confirm(string question)
    {
        while (true)
        {
            _out.Write($"{question} [y / n / skip / quit]: ");

            if (ReadOrEndOfInput() is not { } answer) return ConfirmChoice.Quit;

            switch (answer.Trim().ToLowerInvariant())
            {
                case "y" or "yes": return ConfirmChoice.Yes;
                case "n" or "no": return ConfirmChoice.No;
                case "s" or "skip": return ConfirmChoice.Skip;
                case "q" or "quit": return ConfirmChoice.Quit;
                default: _out.WriteLine("  Please answer y, n, skip or quit."); break;
            }
        }
    }

    /// <summary>Used where a keypress is not enough — deletion, and selecting production.</summary>
    public bool TypedWord(string question, string requiredWord)
    {
        _out.Write($"{question} (type {requiredWord} to proceed): ");

        return ReadOrEndOfInput() is { } answer &&
               string.Equals(answer.Trim(), requiredWord, StringComparison.Ordinal);
    }

    public string ReadLine(string question)
    {
        _out.Write($"{question}: ");

        // End of input is an answer, not an absence of one. Without this, a question that
        // re-asks on unusable input spins forever the moment stdin closes — which is exactly
        // what happens when the tool is run with its input piped in.
        return ReadOrEndOfInput() is { } answer ? answer.Trim() : "q";
    }

    public void Info(string message) => _out.WriteLine(message);

    /// <returns>The line, or null once there is no more input to be had.</returns>
    private string? ReadOrEndOfInput()
    {
        var line = _in.ReadLine();
        if (line is not null) return line;

        _out.WriteLine();
        _out.WriteLine("  (end of input — stopping)");
        return null;
    }
}
