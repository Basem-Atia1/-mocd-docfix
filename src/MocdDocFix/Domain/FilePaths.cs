namespace MocdDocFix.Domain;

/// <summary>
/// Comparing two file paths that came from different places.
///
/// The same file is written down three ways: CRM holds a relative path, the vendor returns a
/// UNC-rooted one on download and a relative one on upload, and an operator typing one in has
/// usually copied it from a screen, complete with quotes or forward slashes. Comparing those as
/// strings answers the wrong question, so everything is reduced to the part that identifies the
/// file — from "DigitalServices\" onwards — before anything is compared or looked up.
/// </summary>
public static class FilePaths
{
    private const string Root = @"DigitalServices\";

    /// <summary>The identifying part of a path, with separators and any prefix regularised.</summary>
    public static string Normalise(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var trimmed = path.Trim().Trim('"', '\'').Replace('/', '\\').TrimStart('\\');
        var at = trimmed.IndexOf(Root, StringComparison.OrdinalIgnoreCase);

        return at >= 0 ? trimmed[at..] : trimmed;
    }

    /// <summary>True when two paths name the same file, however each was written down.</summary>
    public static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Normalise(left!), Normalise(right!), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the text looks like one of these paths rather than a name or an id.</summary>
    public static bool LooksLikeAPath(string text) =>
        text.Contains('\\') || text.Contains('/');
}
