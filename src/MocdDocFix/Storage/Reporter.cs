using System.Globalization;
using System.Text;
using CsvHelper;

namespace MocdDocFix.Storage;

/// <param name="Reason">Why the file is wrong.</param>
/// <param name="Solution">What will be done about it — spec section 8.1.</param>
public sealed record ScanRow(
    Guid DocumentId,
    Guid DocumentFileId,
    string? FileName,
    string DocumentTypeName,
    Guid? ServiceCatalogueId,
    string? OldFilePath,
    string? CurrentSegment,
    Guid? CorrectCatalogueId,
    string Verdict,
    string Reason,
    string Solution,
    string? CrossCheckSource,
    string CrmLink);

public sealed record MigrationRow(
    Guid DocumentId,
    Guid OldFileId,
    Guid NewFileId,
    string OldFilePath,
    string NewFilePath,
    long Bytes,
    string? VendorHash,
    string OurHash,
    string ChecksPassed,
    string OldCrmLink,
    string NewCrmLink,
    string State,
    DateTimeOffset At);

public sealed class Reporter
{
    private readonly string _reportsDir;

    public Reporter(string reportsDir) => _reportsDir = reportsDir;

    public string WriteScan(string env, IEnumerable<ScanRow> rows) => Write("scan", env, rows);
    public string WriteReview(string env, IEnumerable<ScanRow> rows) => Write("review", env, rows);
    public string WriteQuarantine(string env, IEnumerable<ScanRow> rows) => Write("quarantine", env, rows);
    public string WriteMigration(string env, IEnumerable<MigrationRow> rows) => Write("migration-report", env, rows);

    public static string CrmLink(string crmUrl, Guid documentId) =>
        $"{crmUrl.TrimEnd('/')}/main.aspx?etn=mocd_document&pagetype=entityrecord&id={documentId}";

    private string Write<T>(string prefix, string env, IEnumerable<T> rows)
    {
        Directory.CreateDirectory(_reportsDir);
        var path = Path.Combine(_reportsDir,
            $"{prefix}-{env}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.csv");

        // UTF-8 with BOM so Excel opens Arabic file names correctly.
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteRecords(rows);

        return path;
    }
}
