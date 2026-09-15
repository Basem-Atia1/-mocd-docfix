namespace MocdDocFix.Domain;

/// <summary>
/// What the operator's verdict cell says, once parsed. Anything the vocabulary does not contain
/// is <see cref="Unrecognised"/> — deliberately its own case rather than a default, so a typo is
/// reported at the end of a run instead of being read as the value it nearly is.
/// </summary>
public enum RowVerdict { Fix, Review, Skip, Ignore, Redo, Unrecognised }

public static class RowVerdicts
{
    public const string Fix = "fix";
    public const string Review = "review";
    public const string Skip = "skip";
    public const string Ignore = "ignore";
    public const string Redo = "redo";

    public static RowVerdict Parse(string? cell) => (cell ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Fix => RowVerdict.Fix,
        Review => RowVerdict.Review,
        Skip => RowVerdict.Skip,
        Ignore => RowVerdict.Ignore,
        Redo => RowVerdict.Redo,
        _ => RowVerdict.Unrecognised
    };

    public static string Text(RowVerdict verdict) => verdict switch
    {
        RowVerdict.Fix => Fix,
        RowVerdict.Review => Review,
        RowVerdict.Skip => Skip,
        RowVerdict.Ignore => Ignore,
        RowVerdict.Redo => Redo,
        _ => string.Empty
    };
}
