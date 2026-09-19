using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// The ledger on disk: one workbook per environment.
///
/// **The workbook is the ledger.** It is what the operator edits — the verdict and final state
/// columns are dropdowns, which only works if the file carrying them is the file read back —
/// and it is what every mode reads.
///
/// It is rewritten in full the moment a row finishes. Rewriting the whole file for one cell is
/// deliberate: the operator opens it between runs, so it must be complete at every instant, and
/// a crash then loses at most the row in flight.
/// </summary>
public sealed class LedgerStore : ILedger
{
    private readonly LedgerWorkbook _workbook;

    /// <summary>
    /// Taken before the first write of a sitting, never again. The ledger is the only route back
    /// once a correction has overwritten a CRM record, so the state it was in when this session
    /// started is worth one copy — and only one, or the useful earliest copy is buried.
    /// </summary>
    private bool _backedUpThisSitting;

    /// <param name="path">
    /// The .xlsx. A .csv spelling is still accepted and read as its .xlsx sibling, so a caller
    /// holding a path from an older build does not break.
    /// </param>
    public LedgerStore(string path)
    {
        Path = System.IO.Path.ChangeExtension(path, ".xlsx");

        _workbook = new LedgerWorkbook(Path);
    }

    /// <summary>The workbook — the file the operator edits and every mode reads.</summary>
    public string Path { get; }

    public bool Exists => _workbook.Exists;

    /// <summary>False when Excel has the workbook open, so a run can say so before it starts.</summary>
    public bool CanWrite() => _workbook.CanWrite();

    /// <summary>
    /// Asked when the workbook cannot be written — almost always because Excel has it open.
    /// Returning true makes the write be attempted again, so the operator can close Excel and
    /// carry on with nothing lost; returning false lets the failure through.
    ///
    /// A delegate rather than an IPrompts because this is storage: it should not know how a
    /// question gets asked, only that someone can answer one. Left null — in tests and in the
    /// direct commands — a locked file throws, as it did before.
    /// </summary>
    public Func<string, bool>? AskToRetry { get; set; }

    public IReadOnlyList<LedgerRow> Read() => _workbook.Read();

    public void Write(IReadOnlyList<LedgerRow> rows)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var ordered = LedgerOrder.Sorted(rows);

        // Excel holding the ledger open is the ordinary reason a write fails, and it is entirely
        // recoverable — so the operator is asked to close it and the write is tried again.
        // Giving up here would strand a document that has already been uploaded and had its
        // CRM record changed, with nothing on disk saying so.
        while (true)
        {
            try
            {
                // Inside the loop with the write: the sitting's backup copy reads the workbook,
                // so a locked file stops it here just as surely, and it must be retried too.
                BackUpOnce();
                _workbook.Write(ordered);
                break;
            }
            catch (Exception problem) when (IsLocked(problem))
            {
                if (AskToRetry is null || !AskToRetry(Innermost(problem).Message))
                    throw new IOException(
                        $"The ledger could not be written: {Path} is open in another program.",
                        problem);
            }
        }

        // The plain-text copy this tool used to keep beside the ledger is gone. Nothing ever
        // read it, and one left behind by an earlier build is worse than none: it looks current
        // while showing fewer corrections than really happened.
        try
        {
            var abandonedCopy = System.IO.Path.ChangeExtension(Path, ".csv");
            if (File.Exists(abandonedCopy)) File.Delete(abandonedCopy);
        }
        catch (IOException) { }                  // tidying is never worth failing a run over
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Where a sitting's backup copy goes.
    ///
    /// A subfolder rather than beside the ledger, because a timestamped workbook in the same
    /// folder gets opened by mistake — and an old copy opens perfectly well, showing yesterday's
    /// answers with nothing to say they are stale. There is one ledger; everything else is here.
    /// </summary>
    public string PreviousDirectory =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "previous");

    /// <summary>
    /// How many backup copies are kept. Enough to undo a bad sitting or two; not so many that
    /// the folder becomes something to tidy. The ledger itself is the record — these exist only
    /// for the short window where a mistake has not yet been noticed.
    /// </summary>
    private const int CopiesKept = 3;

    private void BackUpOnce()
    {
        if (_backedUpThisSitting || !Exists) return;

        Directory.CreateDirectory(PreviousDirectory);

        // Milliseconds because two sittings can begin in the same second — and colliding here
        // threw "already exists", which the lock check then reported as "open in another
        // program". Overwriting as well, so a collision can never be fatal.
        File.Copy(Path, System.IO.Path.Combine(PreviousDirectory,
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.xlsx"),
            overwrite: true);

        _backedUpThisSitting = true;

        Prune();
    }

    /// <summary>Keeps the newest few copies and removes the rest.</summary>
    private void Prune()
    {
        try
        {
            var old = new DirectoryInfo(PreviousDirectory)
                .GetFiles("*.xlsx")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(CopiesKept);

            foreach (var file in old) file.Delete();
        }
        catch (IOException) { }                  // tidying is never worth failing a run over
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Whether this is the file being held open by something else.
    ///
    /// It has to look through the whole chain: ClosedXML saves through OpenXml, which runs the
    /// write on its own task and hands back an <see cref="AggregateException"/> — so catching a
    /// bare IOException here silently never matched, and the retry that depends on it never fired.
    /// </summary>
    private static bool IsLocked(Exception problem) => problem switch
    {
        IOException or UnauthorizedAccessException => true,
        AggregateException many => many.InnerExceptions.Any(IsLocked),
        { InnerException: { } inner } => IsLocked(inner),
        _ => false
    };

    /// <summary>The real complaint, not the wrapper — that is what is worth showing.</summary>
    private static Exception Innermost(Exception problem) => problem switch
    {
        AggregateException many when many.InnerExceptions.Count > 0 => Innermost(many.InnerExceptions[0]),
        { InnerException: { } inner } => Innermost(inner),
        _ => problem
    };
}
