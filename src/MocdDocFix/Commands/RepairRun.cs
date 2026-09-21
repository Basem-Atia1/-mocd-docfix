using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="Why">Said as it should appear on screen, without a count.</param>
public sealed record SkipTally(string Why, int Count);

/// <param name="Declined">
/// Rows where the operator said the two copies did not match. Counted apart from failures on
/// purpose: nothing went wrong, a person decided.
/// </param>
/// <param name="Unrecognised">
/// Verdict cells nobody recognises, with their row numbers. Named at the end so a typo is
/// found before the operator assumes the row was done.
/// </param>
/// <param name="SettledBySibling">
/// Rows settled because another row corrected the file they share. Counted apart from
/// <paramref name="Corrected"/>: nothing was uploaded and nothing in CRM was written for them.
/// </param>
public sealed record RepairSummary(
    int Corrected, int Declined, int Failed, bool Stopped, int AlreadyRight,
    IReadOnlyList<SkipTally> Skips, IReadOnlyList<string> Unrecognised,
    int SettledBySibling = 0);

/// <summary>
/// The loop. It decides which rows are worked on, keeps the ledger on disk current, and stops
/// to ask when something breaks. The work itself is <see cref="RepairOneRow"/>'s.
///
/// The ledger is rewritten the moment a row finishes rather than once at the end, so an
/// interruption — a crash, a Ctrl-C, a stopped run — loses at most the row in flight.
/// </summary>
public sealed class RepairRun
{
    private readonly ILedger _ledger;
    private readonly RepairOneRow _one;
    private readonly RunProgress _progress;
    private readonly IPrompts _prompts;
    private readonly ErrorLog _errors;

    public RepairRun(ILedger ledger, RepairOneRow one, RunProgress progress,
        IPrompts prompts, ErrorLog errors)
    {
        _ledger = ledger;
        _one = one;
        _progress = progress;
        _prompts = prompts;
        _errors = errors;
    }

    /// <param name="working">The rows the loop acts on — the whole ledger, or one document.</param>
    /// <param name="wholeLedger">
    /// What gets written back. Never <paramref name="working"/> when the operator has narrowed to
    /// one document: the ledger is rewritten after every row, and writing only the working set
    /// would truncate four hundred rows to one. LedgerRow is a mutable class, so the rows in
    /// <paramref name="working"/> are the same objects as those inside this list.
    /// </param>
    public async Task<RepairSummary> RunAsync(IReadOnlyList<LedgerRow> working,
        IReadOnlyList<LedgerRow> wholeLedger, CancellationToken ct)
    {
        int corrected = 0, declined = 0, failed = 0, alreadyRight = 0;
        var stopped = false;

        _progress.SayHowToStop();
        var unrecognised = new List<string>();
        var skips = new Dictionary<string, int>(StringComparer.Ordinal);

        // Counted, not printed.
        //
        // A run over 2,478 rows with 668 to fix printed 1,810 lines saying a row was ignored or
        // reviewed or already done — before the first document was touched. The operator is
        // watching for what the run *does*, and the tally at the end says the same thing in one
        // line per reason. Only rows the loop actually works on get a line of their own.
        void Skip(LedgerRow row, string why) =>
            skips[why] = skips.TryGetValue(why, out var n) ? n + 1 : 1;

        // Only the rows this run will actually act on.
        //
        // The counter used to run over every row in the ledger, so a run with 668 documents to
        // correct out of 2,478 announced "[ 8/2478 ]" — a number that says nothing about how far
        // through the work you are, and reads as several times more of it than there is.
        var toDo = working.Count(r =>
            r.Verdict2() == RowVerdict.Fix &&
            r.State() is not (RowState.Corrected or RowState.Deleted));

        // So that settling a sibling can take it back out of the total. Reference identity is
        // what is wanted here — LedgerRow is a mutable class, and the rows in the working set
        // are the same objects as the ones in the whole ledger.
        var inTheTotal = new HashSet<LedgerRow>(working.Where(r =>
            r.Verdict2() == RowVerdict.Fix &&
            r.State() is not (RowState.Corrected or RowState.Deleted)));

        var started = 0;
        var settledBySibling = 0;

        for (var i = 0; i < working.Count && !stopped; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = working[i];

            // An unrecognised verdict means "leave this row alone" — but loudly, at the end.
            if (row.Verdict2() == RowVerdict.Unrecognised)
            {
                unrecognised.Add($"row {row.Row}: '{row.Verdict}'");
                Skip(row, "its verdict is not one this tool understands");
                continue;
            }

            if (row.Verdict2() != RowVerdict.Fix)
            {
                Skip(row, $"its verdict is {RowVerdicts.Text(row.Verdict2())}");
                continue;
            }

            if (row.State() is RowState.Corrected or RowState.Deleted)
            {
                Skip(row, "it was already corrected in an earlier run");
                continue;
            }

            _progress.StartRow(++started, toDo, row);

            var outcome = await _one.RunAsync(row, ct);

            if (outcome.Corrected)
            {
                corrected++;
                _progress.Finished(row);

                // The file this row just moved belongs to other documents too, and they are
                // settled here rather than by asking CRM about each of them later. Said out
                // loud: rows going green that the run never appeared to touch reads as a bug,
                // and the operator watching the count drop by three deserves to know why.
                var siblings = AlreadyCorrect.SettleSiblingsOf(row, wholeLedger);

                if (siblings.Count > 0)
                {
                    settledBySibling += siblings.Count;
                    toDo -= siblings.Count(inTheTotal.Remove);

                    _progress.SettledSiblings(siblings.Count);
                }
            }
            else if (outcome.Failed)
            {
                failed++;

                row.FinalState = RowStates.Text(RowState.Failed);
                row.Error = Short(outcome.Failure!);

                _errors.Append(started, toDo, row, outcome.FailedStep!, outcome.Failure!);
                _progress.Failed(row, row.Error);
            }
            else if (outcome.WasAlreadyRight)
            {
                alreadyRight++;
                _progress.Skipped(row, "CRM already had it right — nothing was uploaded");
            }
            else if (!outcome.StopAsked)
            {
                declined++;
            }

            // Written before the question, so a stop here still leaves the ledger current.
            _ledger.Write(wholeLedger);

            // Asked for at the eye-check — including by a q pressed mid-document, which the
            // prompt swallows. It means stop, not "skip this one".
            if (outcome.StopAsked)
            {
                _progress.Stopping();
                stopped = true;
                break;
            }

            if (outcome.Failed && !KeepGoing()) { stopped = true; break; }

            // Watch asks after every document, not only after a failure — the operator is
            // watching precisely so they can stop when they see something they do not like.
            if (!_progress.CarryOn()) { stopped = true; break; }
        }

        return new RepairSummary(corrected, declined, failed, stopped, alreadyRight,
            skips.Select(s => new SkipTally(s.Key, s.Value)).ToList(), unrecognised,
            settledBySibling);
    }

    /// <summary>
    /// Asked after every failure, in every watch mode. Unattended is unattended, not
    /// unstoppable: a run that ploughs on through an unexplained error is how one bad
    /// assumption reaches four hundred documents.
    /// </summary>
    private bool KeepGoing()
    {
        _prompts.Blank();
        _prompts.Say("That document was not corrected. Its row says failed, and the full detail " +
                     "is in the error log.", Tone.Warn);

        return _prompts.YesNo("  Carry on with the next document?", defaultYes: false, Tone.Warn);
    }

    /// <summary>The ledger's error column is read in a spreadsheet cell; the log has it in full.</summary>
    private static string Short(string detail) =>
        detail.Length <= 200 ? detail : detail[..197] + "…";
}
