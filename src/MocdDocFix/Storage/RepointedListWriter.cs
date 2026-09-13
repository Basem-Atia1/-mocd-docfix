using System.Text;

namespace MocdDocFix.Storage;

/// <summary>
/// Written at the end of the upload step: every document that was repointed, with the id and the
/// CRM link of the NEW mocd_documentfile, so the result can be opened and checked without
/// digging through a spreadsheet.
///
/// The old file and its CRM record are still in place when this is written — deleting them is a
/// separate, later decision — so the old side is listed too.
/// </summary>
public sealed class RepointedListWriter
{
    private readonly string _reportsDir;

    public RepointedListWriter(string reportsDir) => _reportsDir = reportsDir;

    public static string DocumentFileLink(string crmUrl, Guid documentFileId) =>
        $"{crmUrl.TrimEnd('/')}/main.aspx?etn=mocd_documentfile&pagetype=entityrecord&id={documentFileId}";

    public string Write(string env, string crmUrl, IReadOnlyCollection<MigrationRow> rows)
    {
        Directory.CreateDirectory(_reportsDir);
        var path = Path.Combine(_reportsDir, $"repointed-{env}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var text = new StringBuilder();
        text.AppendLine($"Repointed documents — {env} — {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine(new string('=', 78));
        text.AppendLine();

        if (rows.Count == 0)
        {
            text.AppendLine("Nothing was repointed in this run.");
        }
        else
        {
            text.AppendLine($"{rows.Count} document(s) now point at a new file.");
            text.AppendLine("The old files and their CRM records are still in place.");
            text.AppendLine();
        }

        var n = 0;
        foreach (var row in rows)
        {
            n++;
            text.AppendLine(new string('-', 78));
            text.AppendLine($"{n}.  document        {row.DocumentId}");
            text.AppendLine($"    open it         {row.OldCrmLink}");
            text.AppendLine();
            text.AppendLine($"    NEW file id     {row.NewFileId}");
            text.AppendLine($"    open the file   {DocumentFileLink(crmUrl, row.NewFileId)}");
            text.AppendLine($"    new path        {row.NewFilePath}");
            text.AppendLine();
            text.AppendLine($"    old file id     {row.OldFileId}   (still in CRM)");
            text.AppendLine($"    old path        {row.OldFilePath}   (still on the server)");
            text.AppendLine($"    size            {row.Bytes:N0} bytes");
            text.AppendLine($"    checks          {row.ChecksPassed}");
            text.AppendLine($"    at              {row.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            text.AppendLine();
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
