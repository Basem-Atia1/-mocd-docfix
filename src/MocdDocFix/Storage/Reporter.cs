using System.Globalization;
using System.Text;
using CsvHelper;

namespace MocdDocFix.Storage;

/// <param name="Reason">Why the file is wrong.</param>
/// <param name="Solution">What will be done about it — spec section 8.1.</param>
/// <param name="ServiceCatalogueName">The service the document belongs to, by name.</param>
/// <param name="CurrentSegmentName">
/// The service the file is currently filed under, when the path segment is a real catalogue.
/// Blank when the segment is junk — which is what makes a REVIEW row readable:
/// "filed under 'Request to Join NPO' but its type belongs to 'Membership Managment'".
/// </param>
public sealed record ScanRow(
    Guid DocumentId,
    Guid DocumentFileId,
    string? FileName,
    string DocumentTypeName,
    Guid? ServiceCatalogueId,
    string? ServiceCatalogueName,
    string? OldFilePath,
    string? CurrentSegment,
    string? CurrentSegmentName,
    Guid? CorrectCatalogueId,
    string Verdict,
    int Group,
    string GroupLabel,
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
    /// <summary>
    /// Writes both: a plain-text account to read, and the CSV beside it for anything that wants
    /// to sort a hundred rows. The path returned — the one the operator is shown — is the text
    /// one, because being sent to a spreadsheet to find out what just happened is no answer.
    /// </summary>
    public string WriteMigration(string env, IEnumerable<MigrationRow> rows)
    {
        var all = rows as IReadOnlyList<MigrationRow> ?? rows.ToList();
        Write("migration-report", env, all);
        return WriteMigrationText(env, all);
    }

    private string WriteMigrationText(string env, IReadOnlyList<MigrationRow> rows)
    {
        Directory.CreateDirectory(_reportsDir);
        var path = Path.Combine(_reportsDir, $"migration-report-{env}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var text = new StringBuilder();
        text.AppendLine($"Upload, verify and repoint — {env} — {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine(new string('=', 78));
        text.AppendLine();
        text.AppendLine($"  {rows.Count} document(s) repointed in this run.");
        text.AppendLine();
        text.AppendLine("An index. Each document's own account is in its own folder, in");
        text.AppendLine("03-upload-repoint.txt, with every check it passed.");
        text.AppendLine();

        foreach (var row in rows)
        {
            text.AppendLine(new string('-', 78));
            text.AppendLine(Path.GetFileName(row.NewFilePath));
            text.AppendLine($"       document    {row.DocumentId}");
            text.AppendLine($"       old file    {row.OldFilePath}");
            text.AppendLine($"       old record  {row.OldFileId}");
            text.AppendLine($"       new file    {row.NewFilePath}");
            text.AppendLine($"       new record  {row.NewFileId}");
            text.AppendLine($"       size        {row.Bytes:N0} bytes");
            text.AppendLine($"       our hash    {row.OurHash}");
            text.AppendLine($"       checks      {row.ChecksPassed.Replace(";", ", ")}");
            text.AppendLine($"       state       {row.State}");
            text.AppendLine($"       at          {row.At.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"       in CRM      {row.NewCrmLink}");
            text.AppendLine();
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

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
