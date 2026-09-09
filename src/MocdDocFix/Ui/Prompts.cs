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
    public ConfirmChoice Confirm(string question)
    {
        while (true)
        {
            Console.Write($"{question} [y / n / skip / quit]: ");
            switch ((Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "y" or "yes": return ConfirmChoice.Yes;
                case "n" or "no": return ConfirmChoice.No;
                case "s" or "skip": return ConfirmChoice.Skip;
                case "q" or "quit": return ConfirmChoice.Quit;
                default: Console.WriteLine("  Please answer y, n, skip or quit."); break;
            }
        }
    }

    /// <summary>Used where a keypress is not enough — deletion, and selecting production.</summary>
    public bool TypedWord(string question, string requiredWord)
    {
        Console.Write($"{question} (type {requiredWord} to proceed): ");
        return string.Equals((Console.ReadLine() ?? string.Empty).Trim(), requiredWord, StringComparison.Ordinal);
    }

    public string ReadLine(string question)
    {
        Console.Write($"{question}: ");
        return (Console.ReadLine() ?? string.Empty).Trim();
    }

    public void Info(string message) => Console.WriteLine(message);
}
