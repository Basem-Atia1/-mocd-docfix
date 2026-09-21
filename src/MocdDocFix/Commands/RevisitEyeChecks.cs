using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="Skipped">True when the operator skipped, false when they said it did not match.</param>
/// <param name="Identical">
/// Whether the two copies in the backup folder are byte for byte the same. Null when one of them
/// cannot be found, which is not the same as "different" and must not be said as if it were.
/// </param>
public sealed record EyeCheckRow(LedgerRow Row, bool Skipped, bool? Identical);

/// <param name="FinishWithoutAsking">
/// Documents to correct without showing the two files again. The operator has just answered that
/// question; asking it a second time is how a run trains somebody to press y without looking.
/// </param>
public sealed record Revisited(IReadOnlySet<Guid> FinishWithoutAsking, int Reviewed, int Again);

/// <summary>
/// Offers back the rows a previous run stopped at the compare question, before the next one
/// starts.
///
/// They were invisible. The row still said fix, so the run simply walked up to the same question
/// and asked it again — with no reminder that it had been asked, what was answered, or the one
/// fact that settles it: whether the two files differ at all.
///
/// **The bytes are already known.** The backup folder holds the original and the copy that was
/// uploaded, so they are compared on disk. Nothing is asked of CRM or the file server here.
/// </summary>
public sealed class RevisitEyeChecks
{
    private readonly BackupStore _backups;
    private readonly IPrompts _prompts;

    public RevisitEyeChecks(BackupStore backups, IPrompts prompts)
    {
        _backups = backups;
        _prompts = prompts;
    }

    public Revisited Ask(IReadOnlyList<LedgerRow> rows)
    {
        var waiting = Find(rows);
        if (waiting.Count == 0) return new Revisited(new HashSet<Guid>(), 0, 0);

        var finish = new HashSet<Guid>();
        int reviewed = 0, again = 0;

        foreach (var one in waiting)
        {
            _prompts.Blank();
            _prompts.Say(Said(one), Tone.Warn);

            // Two questions, because the answers are not the same two. A pair that differ is not
            // something to wave through, so "finish it" is not offered for one.
            var choice = one.Identical == true ? WhenIdentical() : WhenNot();

            switch (choice)
            {
                case Answer.Finish:
                    finish.Add(one.Row.DocId);
                    one.Row.Notes = RepairOneRow.WithoutMarks(one.Row.Notes);
                    break;

                case Answer.Again:
                    again++;
                    one.Row.Notes = RepairOneRow.WithoutMarks(one.Row.Notes);
                    break;

                case Answer.Review:
                    reviewed++;
                    one.Row.Verdict = RowVerdicts.Review;
                    one.Row.Notes = RepairOneRow.WithoutMarks(one.Row.Notes);
                    break;

                // Left exactly as it is, marks and all, so the next run offers it again.
                default:
                    break;
            }
        }

        return new Revisited(finish, reviewed, again);
    }

    /// <summary>Rows carrying a mark, with the two copies compared on disk.</summary>
    public IReadOnlyList<EyeCheckRow> Find(IReadOnlyList<LedgerRow> rows)
    {
        var found = new List<EyeCheckRow>();

        foreach (var row in rows)
        {
            var skipped = row.Notes.Contains(RepairOneRow.Skipped, StringComparison.Ordinal);
            var notMatched = row.Notes.Contains(RepairOneRow.NotMatched, StringComparison.Ordinal);

            if (!skipped && !notMatched) continue;

            // A row somebody has since closed by hand is their answer, and not a question again.
            if (row.Verdict2() is not RowVerdict.Fix) continue;

            found.Add(new EyeCheckRow(row, skipped, Identical(row)));
        }

        return found;
    }

    private enum Answer { Finish, Again, Review, Leave }

    private Answer WhenIdentical()
    {
        var answer = new Asker(_prompts).Ask("What should happen to it?", new[]
        {
            new Choice("Correct it", "the copy is already on the server",
                "Points CRM at the copy an earlier run uploaded and marks the row corrected. " +
                "Nothing is uploaded, and you are not shown the two files again."),

            new Choice("Work it again", "the whole cycle, from the start",
                "Backs up, reuses the copy already there, runs the four checks and shows you " +
                "the two files again.")
        }, defaultIndex: 0);

        return answer.Kind != AnswerKind.Chosen ? Answer.Leave
            : answer.Index == 0 ? Answer.Finish : Answer.Again;
    }

    private Answer WhenNot()
    {
        var answer = new Asker(_prompts).Ask("What should happen to it?", new[]
        {
            new Choice("Work it again", "the whole cycle, from the start",
                "Backs up, uploads afresh if the copy on the server is not byte for byte the " +
                "file it came from, runs the four checks and shows you the two files again."),

            new Choice("Keep it for review", "set the verdict to review",
                "No run will act on it. The copy already uploaded stays in the superseded paths " +
                "column, and nothing is deleted.")
        }, defaultIndex: 0);

        return answer.Kind != AnswerKind.Chosen ? Answer.Leave
            : answer.Index == 0 ? Answer.Again : Answer.Review;
    }

    /// <summary>One line. What was answered, and whether the two files differ.</summary>
    private static string Said(EyeCheckRow one)
    {
        var what = one.Skipped
            ? "skipped at the compare question"
            : "you said the copies did not match";

        var bytes = one.Identical switch
        {
            true => "old and new are identical in bytes",
            false => "old and new are NOT identical in bytes",
            _ => "the two copies are not both in the backup folder, so they cannot be compared"
        };

        return $"{one.Row.Named()} — {what}; {bytes}.";
    }

    /// <summary>
    /// Whether the backed-up original and the uploaded copy are the same bytes.
    ///
    /// Both are on disk already — the original from before the upload, the copy from just before
    /// the operator was shown them — so this asks no server anything. Null when either is
    /// missing: not knowing is a third answer, and saying "not identical" for it would send
    /// somebody to redo work over a file that was never there to compare.
    /// </summary>
    private bool? Identical(LedgerRow row)
    {
        var folder = _backups.Folder(row.DocId, row.DocFileName);

        var old = OnlyCopy(folder.OldDir);
        var fresh = OnlyCopy(folder.NewDir, Newest(row.SupersededPaths));

        if (old is null || fresh is null) return null;

        return old.Length == fresh.Length && old.SequenceEqual(fresh);
    }

    /// <param name="preferStem">
    /// The file to take when the folder holds several. Each attempt saves its copy under the
    /// vendor's id for it, so a row worked twice has two, and the one that matters is the one
    /// whose path the row last recorded.
    /// </param>
    private static byte[]? OnlyCopy(string dir, string? preferStem = null)
    {
        if (!Directory.Exists(dir)) return null;

        var files = Directory.EnumerateFiles(dir)
            .Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0) return null;

        var wanted = preferStem is null
            ? null
            : files.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(preferStem, StringComparison.OrdinalIgnoreCase));

        wanted ??= files.Count == 1 ? files[0] : files.MaxBy(File.GetLastWriteTimeUtc);

        try { return File.ReadAllBytes(wanted!); }
        catch (IOException) { return null; }
    }

    /// <summary>The file stem of the most recent copy this row left on the server.</summary>
    private static string? Newest(string supersededPaths)
    {
        var last = supersededPaths
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return last is null ? null : FilePathParser.Parse(last).FileStem;
    }
}
