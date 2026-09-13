using System.Text;

namespace MocdDocFix.Storage;

/// <summary>
/// An INDEX of what one run repointed — not a report about any document.
///
/// Everything about a single document belongs in that document's own folder, so the detail lives
/// in reports\&lt;env&gt;\&lt;name&gt;__&lt;id&gt;\03-upload-repoint.txt. What cannot live there is the answer
/// to "which documents did this run touch", which is what this is: one short block each, and the
/// path to the folder holding the rest.
/// </summary>
public sealed class RepointedListWriter
{
    private readonly string _reportsDir;

    public RepointedListWriter(string reportsDir) => _reportsDir = reportsDir;

    public static string DocumentFileLink(string crmUrl, Guid documentFileId) =>
        $"{crmUrl.TrimEnd('/')}/main.aspx?etn=mocd_documentfile&pagetype=entityrecord&id={documentFileId}";

    /// <param name="folderFor">Where this document's own reports live.</param>
    public string Write(string env, string crmUrl, IReadOnlyCollection<MigrationRow> rows,
        Func<Guid, string>? folderFor = null)
    {
        Directory.CreateDirectory(_reportsDir);
        var path = Path.Combine(_reportsDir, $"repointed-index-{env}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var text = new StringBuilder();
        text.AppendLine($"Repointed in this run — {env} — {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine(new string('=', 78));
        text.AppendLine();
        text.AppendLine("An index. Everything about each document is in that document's own folder.");
        text.AppendLine();

        if (rows.Count == 0)
        {
            text.AppendLine("Nothing was repointed in this run.");
        }
        else
        {
            text.AppendLine($"{rows.Count} document(s) now point at a new file.");
            text.AppendLine("Their old files and CRM records are still in place.");
            text.AppendLine();
        }

        var n = 0;
        foreach (var row in rows)
        {
            n++;
            text.AppendLine(new string('-', 78));
            text.AppendLine($"{n}.  document      {row.DocumentId}");
            text.AppendLine($"    NEW file id   {row.NewFileId}");
            text.AppendLine($"    open the file {DocumentFileLink(crmUrl, row.NewFileId)}");
            if (folderFor is not null)
                text.AppendLine($"    full detail   {Path.Combine(folderFor(row.DocumentId), "03-upload-repoint.txt")}");
            text.AppendLine();
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
