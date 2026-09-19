using MocdDocFix.Domain;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

public enum GateAnswer { GoAhead, Cancel }

/// <summary>
/// The pause before a mode acts, with a way to pick up edits made during it.
///
/// Every mode already reads the sheet from disk the moment it is chosen, so anything edited
/// before that is seen. The gap is afterwards: a mode reads the ledger, prints what it is about
/// to do, and waits — and that is the moment somebody thinks "hold on, not that row", opens
/// Excel, changes it and answers yes, whereupon the mode acts on what it read a minute ago.
///
/// One gate for all four modes, so "read the sheet again" cannot come to mean one thing in the
/// delete step and something else in the repair run.
/// </summary>
public static class LedgerGate
{
    /// <param name="reread">
    /// Fetches the ledger from disk again. A delegate rather than a store, because the caller is
    /// the one that knows how to reconcile what comes back — the journal replay and the verdict
    /// guard both belong to the session, not here.
    /// </param>
    /// <returns>What to do, and the rows to do it with — which may not be the rows passed in.</returns>
    public static (GateAnswer Answer, IReadOnlyList<LedgerRow> Rows) Ask(
        IPrompts prompts,
        string question,
        IReadOnlyList<LedgerRow> rows,
        Func<IReadOnlyList<LedgerRow>> reread)
    {
        var asker = new Asker(prompts);

        while (true)
        {
            var answer = asker.Ask(question, new[]
            {
                new Choice("Go ahead", $"{rows.Count} row(s) as the sheet now reads"),

                new Choice("Read the sheet again", "pick up edits you just made",
                    "Reads the workbook from disk again, says what changed, and asks this " +
                    "question once more. A reload only reads — it never writes the sheet back."),

                new Choice("Cancel", "do nothing")
            }, defaultIndex: 0);

            if (answer.Kind != AnswerKind.Chosen || answer.Index == 2)
                return (GateAnswer.Cancel, rows);

            if (answer.Index == 0) return (GateAnswer.GoAhead, rows);

            var fresh = reread();
            if (fresh.Count == 0)
            {
                prompts.Say("Nothing came back from the sheet. Carrying on with what was already " +
                            "read.", Tone.Warn);
                continue;
            }

            Report(prompts, rows, fresh);
            rows = fresh;
        }
    }

    /// <summary>
    /// What moved, by document rather than by row number. Row numbers are positional and change
    /// on every write, so comparing them would report every row as having changed.
    /// </summary>
    private static void Report(
        IPrompts prompts, IReadOnlyList<LedgerRow> before, IReadOnlyList<LedgerRow> after)
    {
        var was = new Dictionary<Guid, string>();
        foreach (var row in before) was[row.DocId] = row.Verdict;

        var changes = new List<string>();

        foreach (var row in after)
        {
            if (!was.TryGetValue(row.DocId, out var old)) continue;
            if (string.Equals(old, row.Verdict, StringComparison.OrdinalIgnoreCase)) continue;

            changes.Add($"{Spell(old)} → {Spell(row.Verdict)}");
        }

        prompts.Blank();

        if (changes.Count == 0)
        {
            prompts.Say($"{after.Count} row(s) read back. No verdict changed.", Tone.Muted);
            return;
        }

        var grouped = changes
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}");

        prompts.Say($"{changes.Count} row(s) changed: {string.Join(", ", grouped)}", Tone.Good);
    }

    private static string Spell(string verdict) => verdict.Length == 0 ? "(blank)" : verdict;
}
