namespace MocdDocFix.Domain;

/// <summary>
/// What the operator's verdict cell says, once parsed. Anything the vocabulary does not contain
/// is <see cref="Unrecognised"/> — deliberately its own case rather than a default, so a typo is
/// reported at the end of a run instead of being read as the value it nearly is.
/// </summary>
public enum RowVerdict { Fix, Review, Skip, Ignore, Redo, Done, Unrecognised }

public static class RowVerdicts
{
    public const string Fix = "fix";
    public const string Review = "review";
    public const string Skip = "skip";
    public const string Ignore = "ignore";
    public const string Redo = "redo";

    /// <summary>
    /// Written by the run when a document has been corrected. It is the one value the tool puts
    /// here itself, and it exists because a finished row still reading "fix" says the opposite
    /// of the truth — it is the final state that records what happened, but the verdict is the
    /// column the eye lands on first.
    /// </summary>
    public const string Done = "done";

    /// <summary>Every value the column may hold, for the workbook's dropdown.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Fix, Skip, Review, Redo, Ignore, Done };

    public static RowVerdict Parse(string? cell) => (cell ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Fix => RowVerdict.Fix,
        Review => RowVerdict.Review,
        Skip => RowVerdict.Skip,
        Ignore => RowVerdict.Ignore,
        Redo => RowVerdict.Redo,
        Done => RowVerdict.Done,
        _ => RowVerdict.Unrecognised
    };

    public static string Text(RowVerdict verdict) => verdict switch
    {
        RowVerdict.Fix => Fix,
        RowVerdict.Review => Review,
        RowVerdict.Skip => Skip,
        RowVerdict.Ignore => Ignore,
        RowVerdict.Redo => Redo,
        RowVerdict.Done => Done,
        _ => string.Empty
    };
}
