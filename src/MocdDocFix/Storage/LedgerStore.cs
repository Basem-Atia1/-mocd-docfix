using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// The ledger on disk. One CSV per environment, rewritten in full every time a row finishes.
///
/// Rewriting the whole file for one changed cell is deliberate. The operator has the file open
/// in Excel between runs, so it must be a plain well-formed CSV at every instant, not an
/// append-log that needs replaying — and at a few hundred rows the cost is not measurable.
/// A crash therefore loses at most the row in flight.
/// </summary>
public sealed class LedgerStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Taken before the first write of a sitting, never again. The ledger is the only route back
    /// once a correction has overwritten a CRM record, so the state it was in when this session
    /// started is worth one copy — and only one, or the useful earliest copy is buried.
    /// </summary>
    private bool _backedUpThisSitting;

    public LedgerStore(string path) => Path = path;

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    public IReadOnlyList<LedgerRow> Read()
    {
        if (!Exists) return Array.Empty<LedgerRow>();

        using var reader = new StreamReader(Path, Utf8);
        using var csv = new CsvReader(reader, Config());
        return csv.GetRecords<LedgerRow>().ToList();
    }

    public void Write(IReadOnlyList<LedgerRow> rows)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        BackUpOnce();

        using var writer = new StreamWriter(Path, append: false, Utf8);
        using var csv = new CsvWriter(writer, Config());
        csv.WriteRecords(rows);
    }

    /// <summary>Renames the current ledger out of the way. Returns where it was kept.</summary>
    public string StartNewKeepingOld()
    {
        var kept = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        File.Move(Path, kept);
        _backedUpThisSitting = false;
        return kept;
    }

    private void BackUpOnce()
    {
        if (_backedUpThisSitting || !Exists) return;

        File.Copy(Path, System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}-{DateTime.Now:yyyyMMdd-HHmmss}.bak.csv"));

        _backedUpThisSitting = true;
    }

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
