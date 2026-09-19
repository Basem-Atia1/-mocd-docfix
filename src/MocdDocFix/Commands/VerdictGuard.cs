using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <param name="DeleteStillPending">
/// The old file is on the server and the delete step has not run. Only then is there anything
/// left to stop.
/// </param>
/// <param name="WouldReRun">
/// The verdict says fix on a row that has already been corrected. Running it again uploads a
/// second copy and leaves the first with nothing pointing at it — a different, worse mistake.
/// </param>
public sealed record StrandedRow(LedgerRow Row, bool DeleteStillPending, bool WouldReRun);

public enum GuardChoice
{
    /// <summary>It is done. The final state is the record, and the verdict should agree.</summary>
    BackToDone,

    /// <summary>Close the row properly: ignore in both columns, so the delete never comes.</summary>
    StopTheDelete,

    /// <summary>Exactly as typed. The old file will still be deleted.</summary>
    LeaveAsTyped
}

/// <summary>
/// Catches a verdict typed by hand onto a row that has already been worked on.
///
/// The reason this exists: **Delete old files reads the final state column, never the verdict.**
/// Setting a finished row's verdict to ignore therefore stops nothing — the old file is deleted
/// just the same — and everybody expects the opposite. Two columns, one of which looks like a
/// switch and is not.
///
/// The answer is not to make the verdict control the delete as well. Then two columns would
/// control it and neither would be the record of what happened. The answer is to notice, say
/// plainly what will really occur, and offer to write the column that does control it.
///
/// It reads rows and nothing else — no CRM, no file server, no ledger.
/// </summary>
public static class VerdictGuard
{
    public static IReadOnlyList<StrandedRow> Find(IReadOnlyList<LedgerRow> rows)
    {
        var found = new List<StrandedRow>();

        foreach (var row in rows)
        {
            var state = row.State();
            if (state is not (RowState.Corrected or RowState.Deleted)) continue;

            var verdict = row.Verdict2();

            // Done agrees with the final state, and redo is the mode built for precisely this
            // row. Neither is somebody having misread a column.
            if (verdict is RowVerdict.Done or RowVerdict.Redo) continue;

            found.Add(new StrandedRow(row,
                DeleteStillPending: state == RowState.Corrected,
                WouldReRun: verdict == RowVerdict.Fix));
        }

        return found;
    }

    public static void Apply(IReadOnlyList<StrandedRow> stranded, GuardChoice choice)
    {
        foreach (var (row, pending, _) in stranded)
        {
            switch (choice)
            {
                case GuardChoice.BackToDone:
                    row.Verdict = RowVerdicts.Done;
                    row.Notes = Note(row.Notes,
                        $"{Now()} — the verdict was put back to done; the final state says the " +
                        "work was carried out and that is the record");
                    break;

                case GuardChoice.StopTheDelete when pending:
                    row.Verdict = RowVerdicts.Ignore;
                    row.FinalState = RowStates.Ignore;
                    row.Notes = Note(row.Notes,
                        $"{Now()} — closed by hand: the old file at {row.OldFilePath} will not " +
                        "be deleted");
                    break;

                // Asking to stop a delete that has already happened. Writing ignore over the
                // final state here would overwrite the record that the file went, which is the
                // one thing that must not be lost — the file is not coming back either way.
                case GuardChoice.StopTheDelete:
                case GuardChoice.LeaveAsTyped:
                    row.Notes = Note(row.Notes, pending
                        ? $"{Now()} — left as typed; the old file at {row.OldFilePath} will " +
                          "still be deleted when you run Delete old files"
                        : $"{Now()} — left as typed; its old file was already deleted");
                    break;
            }
        }
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");

    private static string Note(string existing, string addition) =>
        existing.Length == 0 ? addition : $"{existing}; {addition}";
}
