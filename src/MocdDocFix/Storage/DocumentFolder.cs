using System.Text;

namespace MocdDocFix.Storage;

/// <summary>
/// Where everything about one document lives, and the plain-text record kept alongside it.
///
///   &lt;DataRoot&gt;\backup\&lt;env&gt;\&lt;documentId&gt;\
///        document.txt          what it is, what is wrong, and what has been done so far
///        old\&lt;oldFileId&gt;.png   the original bytes, exactly as downloaded
///        old\crm.json          the CRM records as they were before any change
///        new\&lt;newFileId&gt;.png   the corrected copy, once uploaded
///        new\crm.json          the CRM records after repointing
///
/// One folder per document, so everything about it stays together as it moves through the steps
/// rather than being scattered across a shared folder and a spreadsheet.
/// </summary>
public sealed class DocumentFolder
{
    private readonly string _root;

    /// <param name="parentRoot">&lt;DataRoot&gt;\backup\&lt;env&gt; or &lt;DataRoot&gt;\reports\&lt;env&gt;</param>
    /// <param name="fileName">
    /// Used to name the folder, so it can be recognised without opening it. The id is what
    /// identifies it, so the folder is always found by id whatever the name turns out to be.
    /// </param>
    public DocumentFolder(string parentRoot, Guid documentId, string? fileName = null)
    {
        _root = Resolve(parentRoot, documentId, fileName);
        DocumentId = documentId;
    }

    /// <summary>
    /// A folder is named "&lt;file name&gt;__&lt;document id&gt;". The id is the part that matters, so an
    /// existing folder for this document is reused whatever name it was given — otherwise a run
    /// that learns the name later would create a second folder for the same document.
    /// </summary>
    private static string Resolve(string parentRoot, Guid documentId, string? fileName)
    {
        var suffix = "__" + documentId;

        if (Directory.Exists(parentRoot))
        {
            var existing = Directory.EnumerateDirectories(parentRoot)
                .FirstOrDefault(d => Path.GetFileName(d).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                                  || Path.GetFileName(d).Equals(documentId.ToString(), StringComparison.OrdinalIgnoreCase));

            if (existing is not null) return existing;
        }

        return Path.Combine(parentRoot, Safe(fileName) + suffix);
    }

    /// <summary>Trims a file name down to something a folder can be called on any filesystem.</summary>
    private static string Safe(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "document";

        var stem = Path.GetFileNameWithoutExtension(fileName.Trim());
        var cleaned = new string(stem.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray())
            .Replace('.', '-')
            .Trim('-', ' ');

        if (cleaned.Length == 0) return "document";
        return cleaned.Length <= 60 ? cleaned : cleaned[..60].TrimEnd('-', ' ');
    }

    public Guid DocumentId { get; }

    public string Root => _root;
    public string OldDir => Path.Combine(_root, "old");
    public string NewDir => Path.Combine(_root, "new");
    public string SummaryPath => Path.Combine(_root, "document.txt");

    public string EnsureRoot() { Directory.CreateDirectory(_root); return _root; }
    public string EnsureOld() { Directory.CreateDirectory(OldDir); return OldDir; }
    public string EnsureNew() { Directory.CreateDirectory(NewDir); return NewDir; }

    /// <summary>Starts document.txt. Rewrites it, so re-running a step never doubles the header.</summary>
    public void WriteHeader(string title, IEnumerable<(string Label, string? Value)> points)
    {
        EnsureRoot();

        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine(new string('=', 78));
        text.AppendLine();
        Points(text, points);

        File.WriteAllText(SummaryPath, text.ToString(), Utf8);
    }

    /// <summary>Adds a section for a step that has just finished.</summary>
    public void AppendSection(string heading, IEnumerable<(string Label, string? Value)> points)
    {
        EnsureRoot();

        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine(heading);
        text.AppendLine(new string('-', 78));
        Points(text, points);

        File.AppendAllText(SummaryPath, text.ToString(), Utf8);
    }

    /// <summary>One free-text note, for something that does not fit a label and a value.</summary>
    public void AppendNote(string note)
    {
        EnsureRoot();
        File.AppendAllText(SummaryPath, "  " + note + Environment.NewLine, Utf8);
    }

    private static void Points(StringBuilder text, IEnumerable<(string Label, string? Value)> points)
    {
        foreach (var (label, value) in points)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            // Long values wrap under their label rather than running off the screen.
            var lines = Wrap(value, 78 - 22);
            text.AppendLine($"  {label,-20}{lines[0]}");
            for (var i = 1; i < lines.Count; i++) text.AppendLine(new string(' ', 22) + lines[i]);
        }
    }

    private static IReadOnlyList<string> Wrap(string value, int width)
    {
        var lines = new List<string>();

        foreach (var paragraph in value.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                // A long unbroken token — a path or a GUID — is never split; it is more useful
                // whole and over-long than neatly cut in half.
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }

                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }

            lines.Add(line.ToString());
        }

        return lines.Count == 0 ? new List<string> { string.Empty } : lines;
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);
}
