namespace MocdDocFix.Ui;

public enum ConfirmChoice { Yes, No, Skip, Quit }

/// <summary>
/// What a line is for, so the console can colour it. Everything still reads correctly with no
/// colour at all — a tone is emphasis, never the only thing carrying the meaning.
/// </summary>
public enum Tone
{
    /// <summary>Ordinary prose.</summary>
    Normal,

    /// <summary>Supporting detail: paths, ids, counts, the small print under a heading.</summary>
    Muted,

    /// <summary>A heading or a line the eye should land on first.</summary>
    Strong,

    /// <summary>Something worked.</summary>
    Good,

    /// <summary>Worth reading before answering, but nothing is lost either way.</summary>
    Warn,

    /// <summary>Irreversible, refused, or failed.</summary>
    Danger
}

/// <summary>One keypress while a live menu is on screen.</summary>
public enum MenuKey { Up, Down, Enter, Escape, Back, Help, Digit, Other }

public interface IPrompts
{
    /// <summary>
    /// True when a real console is on the other end, so a menu can be driven with the arrow keys
    /// and redrawn in place. False for piped input, which takes the typed-number path instead —
    /// the same path that keeps scripted runs and the tests deterministic.
    /// </summary>
    bool Interactive => false;

    /// <summary>One key for a live menu. Only called when <see cref="Interactive"/> is true.</summary>
    (MenuKey Key, char Character) ReadMenuKey() => (MenuKey.Escape, '\0');

    /// <summary>
    /// Whether the operator has asked to stop, without having been asked anything.
    ///
    /// Checked between documents by the modes that do not pause, so a run of four hundred can be
    /// halted by pressing q at any moment — and it takes effect at the next boundary rather than
    /// part-way through a document, which is the difference between stopping and interrupting.
    ///
    /// Never blocks: nothing pressed means false and the run carries on.
    /// </summary>
    bool StopRequested() => false;

    /// <summary>Clears the last n printed lines and puts the cursor back at the first of them.</summary>
    void Rewind(int lines) { }

    ConfirmChoice Confirm(string question);

    /// <summary>
    /// A plain yes or no. Case does not matter and neither does the long form: y, Y, yes and YES
    /// all mean yes. Anything else re-asks.
    /// </summary>
    /// <param name="defaultYes">What a bare Enter means. False everywhere that matters.</param>
    bool YesNo(string question, bool defaultYes = false, Tone tone = Tone.Normal);

    bool TypedWord(string question, string requiredWord);
    string ReadLine(string question);
    void Info(string message, Tone tone = Tone.Normal);
}

public sealed class ConsolePrompts : IPrompts
{
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly bool _coloured;

    /// <summary>The reader and writer are injectable only so the end-of-input path can be tested.</summary>
    public ConsolePrompts(TextReader? input = null, TextWriter? output = null)
    {
        _in = input ?? Console.In;
        _out = output ?? Console.Out;

        // Colour only when we own a real console. A redirected stream gets plain text, so piping
        // the tool into a file or a test still produces something readable.
        _coloured = output is null && SafeToColour();
    }

    public ConfirmChoice Confirm(string question)
    {
        while (true)
        {
            Ask($"{question} [y / n / skip / quit]: ", Tone.Normal);

            if (ReadOrEndOfInput() is not { } answer) return ConfirmChoice.Quit;

            switch (answer.Trim().ToLowerInvariant())
            {
                case "y" or "yes": return ConfirmChoice.Yes;
                case "n" or "no": return ConfirmChoice.No;
                case "s" or "skip": return ConfirmChoice.Skip;
                case "q" or "quit": return ConfirmChoice.Quit;
                default: Info("  Please answer y, n, skip or quit.", Tone.Warn); break;
            }
        }
    }

    public bool YesNo(string question, bool defaultYes = false, Tone tone = Tone.Normal)
    {
        while (true)
        {
            Ask($"{question} [{(defaultYes ? "Y/n" : "y/N")}]: ", tone);

            if (ReadOrEndOfInput() is not { } answer) return false;

            var typed = answer.Trim().ToLowerInvariant();

            if (typed.Length == 0) return defaultYes;

            switch (typed)
            {
                case "y" or "yes": return true;
                case "n" or "no" or "q" or "quit": return false;
                default: Info("  Please answer y or n.", Tone.Warn); break;
            }
        }
    }

    /// <summary>
    /// Used where a keypress is not enough — choosing production, and confirming a count.
    /// Deliberately not case-sensitive: the friction that matters is having to type the word at
    /// all, and a right answer in the wrong case reads as a rejected answer, not as a caps-lock
    /// mistake.
    /// </summary>
    public bool TypedWord(string question, string requiredWord)
    {
        Ask($"{question}{Environment.NewLine}  Type {requiredWord} to go ahead: ", Tone.Warn);

        return ReadOrEndOfInput() is { } answer &&
               string.Equals(answer.Trim(), requiredWord, StringComparison.OrdinalIgnoreCase);
    }

    public string ReadLine(string question)
    {
        Ask($"{question}: ", Tone.Normal);

        // End of input is an answer, not an absence of one. Without this, a question that
        // re-asks on unusable input spins forever the moment stdin closes — which is exactly
        // what happens when the tool is run with its input piped in.
        return ReadOrEndOfInput() is { } answer ? answer.Trim() : "q";
    }

    /// <summary>
    /// Arrow keys need a real console on both ends: a key to read, and a cursor to move. Piped
    /// input has neither, so it falls back to typing a number — which is also how every test and
    /// every scripted run drives the menus.
    /// </summary>
    public bool Interactive => _coloured && SafeToRead();

    /// <summary>
    /// Drains whatever has been typed since the last look and says whether any of it was a
    /// request to stop. Everything else is thrown away — a run is not reading input, so a stray
    /// keystroke must not be left in the buffer to answer the next real question by accident.
    /// </summary>
    public bool StopRequested()
    {
        if (!Interactive) return false;

        var asked = false;

        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key is ConsoleKey.Escape || key.KeyChar is 'q' or 'Q') asked = true;
            }
        }
        catch (InvalidOperationException) { return false; }   // input redirected after all

        return asked;
    }

    public (MenuKey Key, char Character) ReadMenuKey()
    {
        ConsoleKeyInfo key;

        try { key = Console.ReadKey(intercept: true); }
        catch (InvalidOperationException) { return (MenuKey.Escape, '\0'); }

        return key.Key switch
        {
            ConsoleKey.UpArrow or ConsoleKey.K => (MenuKey.Up, '\0'),
            ConsoleKey.DownArrow or ConsoleKey.J => (MenuKey.Down, '\0'),
            ConsoleKey.Enter or ConsoleKey.Spacebar => (MenuKey.Enter, '\0'),
            ConsoleKey.Escape => (MenuKey.Escape, '\0'),
            _ => key.KeyChar switch
            {
                'q' or 'Q' => (MenuKey.Escape, key.KeyChar),
                'b' or 'B' => (MenuKey.Back, key.KeyChar),
                '?' or 'h' or 'H' => (MenuKey.Help, key.KeyChar),
                >= '1' and <= '9' => (MenuKey.Digit, key.KeyChar),
                _ => (MenuKey.Other, key.KeyChar)
            }
        };
    }

    public void Rewind(int lines)
    {
        if (!Interactive || lines <= 0) return;

        try
        {
            var top = Math.Max(0, Console.CursorTop - lines);
            var width = Math.Max(1, Console.WindowWidth - 1);

            Console.SetCursorPosition(0, top);
            for (var i = 0; i < lines; i++) _out.WriteLine(new string(' ', width));
            Console.SetCursorPosition(0, top);
        }
        catch (IOException) { /* the console went away; the menu simply reprints below */ }
        catch (ArgumentOutOfRangeException) { /* scrolled past the top of the buffer */ }
    }

    public void Info(string message, Tone tone = Tone.Normal) => Write(message, tone, newLine: true);

    private void Ask(string prompt, Tone tone) => Write(prompt, tone, newLine: false);

    private void Write(string text, Tone tone, bool newLine)
    {
        if (!_coloured || tone == Tone.Normal)
        {
            if (newLine) _out.WriteLine(text); else _out.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ColourFor(tone);
            if (newLine) _out.WriteLine(text); else _out.Write(text);
        }
        finally
        {
            try { Console.ForegroundColor = previous; } catch (IOException) { /* console gone */ }
        }
    }

    private static ConsoleColor ColourFor(Tone tone) => tone switch
    {
        Tone.Muted => ConsoleColor.DarkGray,
        Tone.Strong => ConsoleColor.White,
        Tone.Good => ConsoleColor.Green,
        Tone.Warn => ConsoleColor.Yellow,
        Tone.Danger => ConsoleColor.Red,
        _ => Console.ForegroundColor
    };

    private static bool SafeToColour()
    {
        try { return !Console.IsOutputRedirected; }
        catch (IOException) { return false; }
    }

    private static bool SafeToRead()
    {
        try { return !Console.IsInputRedirected; }
        catch (IOException) { return false; }
    }

    /// <returns>The line, or null once there is no more input to be had.</returns>
    private string? ReadOrEndOfInput()
    {
        var line = _in.ReadLine();
        if (line is not null) return line;

        _out.WriteLine();
        Info("  (end of input — stopping)", Tone.Muted);
        return null;
    }
}
