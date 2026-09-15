using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// The ledger on disk: one workbook per environment, with a CSV copy beside it.
///
/// **The workbook is the ledger.** It is what the operator edits — the verdict and final state
/// columns are dropdowns, which only works if the file carrying them is the file read back —
/// and it is what every mode reads. The CSV is regenerated from it on every write and is never
/// read: it exists so the ledger is also greppable, diffable plain text, and so a damaged
/// workbook is not the end of the record.
///
/// Both are rewritten in full the moment a row finishes. Rewriting the whole file for one cell
/// is deliberate: the operator opens it between runs, so it must be complete at every instant,
/// and a crash then loses at most the row in flight.
/// </summary>
public sealed class LedgerStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    private readonly LedgerWorkbook _workbook;

    /// <summary>
    /// Taken before the first write of a sitting, never again. The ledger is the only route back
    /// once a correction has overwritten a CRM record, so the state it was in when this session
    /// started is worth one copy — and only one, or the useful earliest copy is buried.
    /// </summary>
    private bool _backedUpThisSitting;

    /// <param name="path">
    /// Either the .xlsx or the .csv — the other is derived. Both spellings are accepted so a
    /// caller need not know which of the pair is the authority.
    /// </param>
    public LedgerStore(string path)
    {
        Path = System.IO.Path.ChangeExtension(path, ".xlsx");
        CsvPath = System.IO.Path.ChangeExtension(path, ".csv");

        _workbook = new LedgerWorkbook(Path);
    }

    /// <summary>The workbook — the file the operator edits and every mode reads.</summary>
    public string Path { get; }

    /// <summary>The plain-text copy. Regenerated on every write, never read back.</summary>
    public string CsvPath { get; }

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

        // The workbook first: it is the ledger, and a failure to write it must stop the caller
        // rather than leave the copy ahead of the original.
        //
        // Excel holding it open is the ordinary reason that fails, and it is entirely
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

        try
        {
            using var writer = new StreamWriter(CsvPath, append: false, Utf8);
            using var csv = new CsvWriter(writer, Config());
            csv.WriteRecords(ordered);
        }
        catch (IOException)
        {
            // The copy is a convenience. Losing it for one write is not worth failing a run,
            // and the next completed row rewrites it.
            LastCsvProblem = $"could not rewrite {CsvPath} — is it open in something?";
        }
    }

    /// <summary>Why the plain-text copy could not be rewritten, when it could not.</summary>
    public string? LastCsvProblem { get; private set; }

    /// <summary>
    /// Where a sitting's backup copy goes.
    ///
    /// A subfolder rather than beside the ledger, because a timestamped workbook in the same
    /// folder gets opened by mistake — and an old copy opens perfectly well, showing yesterday's
    /// answers with nothing to say they are stale. There is one ledger; everything else is here.
    /// </summary>
    public string PreviousDirectory =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "previous");

    private void BackUpOnce()
    {
        if (_backedUpThisSitting || !Exists) return;

        Directory.CreateDirectory(PreviousDirectory);

        File.Copy(Path, System.IO.Path.Combine(PreviousDirectory,
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx"));

        _backedUpThisSitting = true;
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


    /// <summary>
    /// A missing column is ignored rather than fatal, so a ledger written by an earlier build
    /// still opens — the cells it lacks simply come back empty.
    /// </summary>
    private static CsvConfiguration Config() => new(CultureInfo.InvariantCulture)
    {
        MissingFieldFound = null,
        HeaderValidated = null
    };
}
