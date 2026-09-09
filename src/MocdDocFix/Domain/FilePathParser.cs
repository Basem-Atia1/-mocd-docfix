using System.Text.RegularExpressions;

namespace MocdDocFix.Domain;

public static class FilePathParser
{
    private static readonly Regex DateSegment = new(@"^\d{8}$", RegexOptions.Compiled);

    public static FilePathParts Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return FilePathParts.Empty(raw ?? string.Empty);

        var doubled = raw.Contains(@"\\", StringComparison.Ordinal);

        var segments = raw
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        if (segments.Length == 0)
            return FilePathParts.Empty(raw);

        var root = segments[0];
        var last = segments[^1];
        var extension = Path.GetExtension(last);
        var stem = Path.GetFileNameWithoutExtension(last);

        // The date is the last segment before the file name that looks like yyyyMMdd.
        string? date = null;
        for (var i = segments.Length - 2; i >= 1; i--)
        {
            if (DateSegment.IsMatch(segments[i])) { date = segments[i]; break; }
        }

        // Category is segment 1 only when it is not itself the date.
        string? category = null;
        if (segments.Length >= 3 && !DateSegment.IsMatch(segments[1]))
            category = segments[1];

        return new FilePathParts(raw, root, category, date, stem, extension, segments.Length, doubled);
    }
}
