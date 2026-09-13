using System.Text;

namespace MocdDocFix.Storage;

/// <summary>
/// One folder per document, under the reports root, holding only reports:
///
///   &lt;DataRoot&gt;\reports\&lt;env&gt;\&lt;file name&gt;__&lt;document id&gt;\
///        01-check.txt            what is wrong with it and what we intend to do
///        02-backup.txt           what was downloaded and saved, or why it was not
///        03-upload-repoint.txt   what was uploaded, what was checked, what CRM now points at
///        04-delete.txt           what was found on the server, deleted, and confirmed gone
///        05-final-check.txt      what both systems say afterwards
///
/// Deliberately separate from the backup folder: that one holds the file and everything needed
/// to restore it, this one holds only the account of what happened. Numbered so they read in the
/// order the steps ran.
/// </summary>
public sealed class DocumentReportStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    private readonly string _root;

    /// <param name="root">&lt;DataRoot&gt;\reports\&lt;env&gt;</param>
    public DocumentReportStore(string root) => _root = root;

    public string FolderFor(Guid documentId, string? fileName = null) =>
        new DocumentFolder(_root, documentId, fileName).Root;

    /// <summary>Writes one step's report, replacing any earlier one for the same step.</summary>
    public string Write(Guid documentId, string? fileName, string step, string title,
        IEnumerable<(string Label, string? Value)> points, IEnumerable<string>? extraLines = null)
    {
        var folder = new DocumentFolder(_root, documentId, fileName);
        Directory.CreateDirectory(folder.Root);

        var path = Path.Combine(folder.Root, $"{step}.txt");

        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine(new string('=', 78));
        text.AppendLine($"  {"document",-20}{documentId}");
        if (!string.IsNullOrWhiteSpace(fileName)) text.AppendLine($"  {"file name",-20}{fileName}");
        text.AppendLine($"  {"written",-20}{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine();

        foreach (var (label, value) in points)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            var first = true;
            foreach (var line in Wrap(value, 56))
            {
                text.AppendLine($"  {(first ? label : string.Empty),-20}{line}");
                first = false;             // only the first line carries the label
            }
        }

        if (extraLines is not null)
        {
            text.AppendLine();
            foreach (var line in extraLines) text.AppendLine(line);
        }

        File.WriteAllText(path, text.ToString(), Utf8);
        return path;
    }

    private static IReadOnlyList<string> Wrap(string value, int width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();

        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Paths and GUIDs are kept whole even when over-long: cut in half they are useless.
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) lines.Add(line.ToString());
        return lines.Count == 0 ? new List<string> { string.Empty } : lines;
    }
}
