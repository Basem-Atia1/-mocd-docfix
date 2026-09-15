using MocdDocFix.Domain;

namespace MocdDocFix.Ui;

/// <summary>
/// How closely the operator wants to watch the loop. Asked once when the mode is entered and
/// never again while it runs.
///
/// It changes what is printed and what stops. It never changes what is written to CRM or to the
/// ledger, with one exception that is the whole point of <see cref="Unattended"/>: whether the
/// operator is shown the two copies and asked.
/// </summary>
public enum WatchMode
{
    /// <summary>Every step, then a pause to read it before the next document begins.</summary>
    Watch,

    /// <summary>One line per document. The eye-check is the only interruption.</summary>
    Quiet,

    /// <summary>One line per document, and nothing is asked. The automated checks decide.</summary>
    Unattended
}

/// <summary>
/// The per-document account on screen. All three modes go through here, so they cannot drift
/// into printing different things about the same event.
/// </summary>
public sealed class RunProgress
{
    private readonly IPrompts _prompts;

    public RunProgress(IPrompts prompts, WatchMode mode)
    {
        _prompts = prompts;
        Mode = mode;
    }

    public WatchMode Mode { get; }

    /// <summary>False only in <see cref="WatchMode.Unattended"/>.</summary>
    public bool AsksTheEyeCheck => Mode != WatchMode.Unattended;

    public void StartRow(int number, int total, LedgerRow row)
    {
        _prompts.Blank();
        _prompts.Info($"  [ {number}/{total} ]  {Short(row.DocName, 48)}", Tone.Strong);
        _prompts.Info($"              doc {row.DocId}   {row.DocFileName}", Tone.Muted);
    }

    /// <summary>One step of the six. Printed in Watch only.</summary>
    public void Step(string name, string detail = "")
    {
        if (Mode != WatchMode.Watch) return;

        var dots = new string('.', Math.Max(1, 24 - name.Length));
        _prompts.Info($"      {name} {dots} ok   {detail}".TrimEnd(), Tone.Muted);
    }

    public void Finished(LedgerRow row) =>
        _prompts.Info(Mode == WatchMode.Watch
            ? $"              FINISHED — {RowStates.Corrected}"
            : $"              {row.DocFileName}  FINISHED", Tone.Good);

    public void Skipped(LedgerRow row, string why) =>
        _prompts.Info($"              {row.DocFileName}  skipped — {why}", Tone.Muted);

    /// <summary>Printed in every mode. A failure is the one thing nobody may miss.</summary>
    public void Failed(LedgerRow row, string why) =>
        _prompts.Info($"              {row.DocFileName}  FAILED — {why}", Tone.Danger);

    /// <summary>
    /// The pause after a document in Watch. A bare enter to move on — not the watch question
    /// being asked again, which was settled before the loop started.
    /// </summary>
    public void BetweenRows()
    {
        if (Mode != WatchMode.Watch) return;
        _prompts.ReadLine("  enter for the next one");
    }

    private static string Short(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";
}
