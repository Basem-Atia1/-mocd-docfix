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

    public IReadOnlyList<LedgerRow> Read() => _workbook.Read();

    public void Write(IReadOnlyList<LedgerRow> rows)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        BackUpOnce();

        var ordered = LedgerOrder.Sorted(rows);

        // The workbook first: it is the ledger, and a failure to write it must stop the caller
        // rather than leave the copy ahead of the original.
        _workbook.Write(ordered);

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

    /// <summary>Renames the current pair out of the way. Returns where the workbook was kept.</summary>
    public string StartNewKeepingOld()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var kept = Renamed(Path, stamp, ".xlsx");

        File.Move(Path, kept);
        if (File.Exists(CsvPath)) File.Move(CsvPath, Renamed(CsvPath, stamp, ".csv"));

        _backedUpThisSitting = false;
        return kept;
    }

    private void BackUpOnce()
    {
        if (_backedUpThisSitting || !Exists) return;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(Path, Renamed(Path, stamp, ".bak.xlsx"));

        _backedUpThisSitting = true;
    }

    private static string Renamed(string path, string stamp, string extension) =>
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path)!,
            System.IO.Path.GetFileNameWithoutExtension(path) + "-" + stamp + extension);

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
