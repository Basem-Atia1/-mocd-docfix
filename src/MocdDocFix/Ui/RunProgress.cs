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

    /// <summary>
    /// Said in every mode, straight after a correction, when the file that just moved belongs to
    /// other documents too.
    ///
    /// One line and no list. What matters is that the count has dropped by more than one and
    /// that nothing was uploaded for those rows; which rows they are is in the sheet, and each
    /// of them says in its notes who settled it.
    /// </summary>
    public void SettledSiblings(int count) =>
        _prompts.Info($"              {count} other row(s) share this file — settled, nothing " +
                      "uploaded for them", Tone.Good);

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
    /// <summary>
    /// Once asked, it stays asked. A q pressed during an upload is noticed long before the
    /// document is finished, and the answer must survive until the boundary where stopping is
    /// safe — not be forgotten because the next look at the keyboard found nothing.
    /// </summary>
    private bool _stopAsked;

    /// <summary>
    /// Takes any pending keystroke out of the buffer and remembers whether it was a request to
    /// stop.
    ///
    /// Called before every question a document asks, not only between documents. A q pressed
    /// while a file was uploading is still sitting there when the eye-check appears, and a
    /// prompt would read it as the answer — so it is claimed here first, where it means what
    /// the operator intended.
    /// </summary>
    /// <returns>True once a stop has been asked for, at any point.</returns>
    public bool NoticeStopRequest()
    {
        if (Mode == WatchMode.Watch) return false;
        if (_prompts.StopRequested()) _stopAsked = true;

        return _stopAsked;
    }

    /// <returns>False to stop the run.</returns>
    public bool CarryOn()
    {
        if (Mode == WatchMode.Watch)
            return _prompts.YesNo("  Carry on to the next document?", defaultYes: true);

        // Quiet and Unattended never interrupt to ask, so the operator says stop whenever they
        // like and it takes effect here — after the document in progress has been finished and
        // recorded, never part-way through one.
        if (!NoticeStopRequest()) return true;

        Stopping();
        return false;
    }

    /// <summary>
    /// Said when the run ends at the operator's asking, however they asked — q between
    /// documents, or quit at the eye-check.
    /// </summary>
    public void Stopping()
    {
        _prompts.Blank();
        _prompts.Say("Stopping at your request. Everything already done is recorded; " +
                     "nothing further will be started.", Tone.Warn);
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
