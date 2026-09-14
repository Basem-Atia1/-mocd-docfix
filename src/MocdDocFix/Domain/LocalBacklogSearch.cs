using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MocdDocFix.Domain;

/// <param name="File">The file the phrase was found in.</param>
/// <param name="Title">The work item's title, where the file carries one.</param>
/// <param name="Service">The service read off that title, when it names one.</param>
public sealed record LocalHit(string File, string? Title, string? Service);

/// <summary>
/// Searches a local copy of the backlog for a document name.
///
/// It exists because the live search can only see titles — this server answers TF401349 to a
/// full-text query over descriptions — and the document lists are in the descriptions and in the
/// spreadsheets attached to them. CRM's "A Copy of Certificate of Good Conduct and Behavior"
/// appears in no work item title anywhere, and verbatim in the body of user story 27628.
///
/// A workbook is a zip of XML, so its text is readable without a spreadsheet library: the shared
/// string table holds every cell value in the file.
/// </summary>
public static class LocalBacklogSearch
{
    /// <summary>Enough to judge by; more would be a wall of near-identical stories.</summary>
    public const int MostHits = 8;

    private static readonly string[] Channels = { "NPOP", "Portal", "CRM", "MoCE", "MoCD", "ESMS" };

    public static IReadOnlyList<LocalHit> Find(string? folder, string? phrase, int most = MostHits)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(phrase)) return Array.Empty<LocalHit>();
        if (!Directory.Exists(folder)) return Array.Empty<LocalHit>();

        var wanted = phrase!.Trim();
        var hits = new List<LocalHit>();

        foreach (var file in Files(folder!))
        {
            if (hits.Count >= most) break;

            try
            {
                var text = ReadText(file);
                if (text is null || text.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var title = TitleIn(text) ?? Path.GetFileNameWithoutExtension(file);
                hits.Add(new LocalHit(file, title, ServiceInStoryTitle(title)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A file being written, or one that is not the workbook its name claims. One
                // unreadable file is not a reason to abandon the search.
            }
        }

        return hits;
    }

    private static IEnumerable<string> Files(string folder) =>
        Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    private static string? ReadText(string file) =>
        file.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? TextInWorkbook(file)
            : File.ReadAllText(file);

    /// <summary>
    /// Every string in a workbook, straight out of its shared string table. Enough to answer
    /// "is this document name written down in here", which is the only question being asked.
    /// </summary>
    private static string? TextInWorkbook(string file)
    {
        using var zip = ZipFile.OpenRead(file);

        var strings = zip.GetEntry("xl/sharedStrings.xml");
        if (strings is null) return null;

        using var reader = new StreamReader(strings.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();

        var text = new StringBuilder();
        foreach (Match match in Regex.Matches(xml, @"<t[^>]*>([^<]*)</t>"))
            text.Append(match.Groups[1].Value).Append(' ');

        return text.ToString();
    }

    private static string? TitleIn(string text)
    {
        var match = Regex.Match(text, @"^title:\s*(.+?)\s*$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The service named in a story title. These are written "1.1.6 NPOP- Employee Appointment
    /// Request Form- Documents", so the parts are separated by a dash and a space — and a plain
    /// dash cannot be the separator, or "By-Laws" comes apart in the middle.
    /// </summary>
    public static string? ServiceInStoryTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        foreach (var raw in Regex.Split(title!, @"-\s|\s-|\|"))
        {
            var segment = Regex.Replace(raw.Trim(), @"^[\d.]+\s*", "").Trim();

            foreach (var channel in Channels)
                if (segment.Equals(channel, StringComparison.OrdinalIgnoreCase))
                    segment = string.Empty;
                else if (segment.StartsWith(channel + " ", StringComparison.OrdinalIgnoreCase))
                    segment = segment[(channel.Length + 1)..].Trim();

            // The screen a story is about is not the service: "… Form", "… Tab", "Documents".
            segment = Regex.Replace(segment, @"\s+(Form|Tab|Screen|Page)$", "",
                RegexOptions.IgnoreCase).Trim();

            if (segment.Length < 6) continue;
            if (Regex.IsMatch(segment, @"^(Verify|Documents?|Check|View|Edit)\b", RegexOptions.IgnoreCase))
                continue;

            return segment;
        }

        return null;
    }
}
