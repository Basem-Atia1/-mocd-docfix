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
    /// The pause after a document in Watch.
    ///
    /// A real question, not a bare enter: an operator who has just watched something they did
    /// not like types "no", and a prompt that accepts any keystroke as "carry on" would move to
    /// the next document anyway. Answering no stops the run where it stands.
    /// </summary>
    /// <returns>False to stop the run.</returns>
    public bool CarryOn()
    {
        if (Mode == WatchMode.Watch)
            return _prompts.YesNo("  Carry on to the next document?", defaultYes: true);

        // Quiet and Unattended never interrupt to ask — so instead the operator can say stop
        // whenever they like, and it is noticed here, between documents. Pressing q part-way
        // through an upload does not abandon it; the document finishes and the run ends.
        if (!_prompts.StopRequested()) return true;

        _prompts.Blank();
        _prompts.Say("Stopping at your request. The document just finished is recorded; " +
                     "nothing further will be started.", Tone.Warn);

        return false;
    }

    /// <summary>
    /// Said once before the loop begins, so the way out is known before it is wanted. Watch
    /// asks after every document and needs no telling.
    /// </summary>
    public void SayHowToStop()
    {
        if (Mode == WatchMode.Watch) return;

        _prompts.Blank();
        _prompts.Say("Press q at any time to stop. The document in progress is always finished " +
                     "and recorded first.", Tone.Muted);
    }

    private static string Short(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";
}
