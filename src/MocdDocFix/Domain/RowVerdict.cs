namespace MocdDocFix.Domain;

/// <summary>
/// What the operator's verdict cell says, once parsed. Anything the vocabulary does not contain
/// is <see cref="Unrecognised"/> — deliberately its own case rather than a default, so a typo is
/// reported at the end of a run instead of being read as the value it nearly is.
/// </summary>
public enum RowVerdict { Fix, Review, Ignore, Redo, Done, Unrecognised }

public static class RowVerdicts
{
    public const string Fix = "fix";
    public const string Review = "review";
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
    public static readonly IReadOnlyList<string> All = new[] { Fix, Review, Redo, Ignore, Done };

    public static RowVerdict Parse(string? cell) => (cell ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Fix => RowVerdict.Fix,
        Review => RowVerdict.Review,
        Ignore => RowVerdict.Ignore,
        Redo => RowVerdict.Redo,
        Done => RowVerdict.Done,

        // Legacy, and readable for that reason alone. Every build before correct documents
        // stopped entering the sheet wrote this word, and it always meant "we looked, there was
        // nothing to do" — which is what done means now. Ledgers on disk carry it in hundreds of
        // cells, and if it stopped parsing every one of them would be reported as a typo at the
        // end of every run. Nothing writes it again.
        "skip" => RowVerdict.Done,

        _ => RowVerdict.Unrecognised
    };

    public static string Text(RowVerdict verdict) => verdict switch
    {
        RowVerdict.Fix => Fix,
        RowVerdict.Review => Review,
        RowVerdict.Ignore => Ignore,
        RowVerdict.Redo => Redo,
        RowVerdict.Done => Done,
        _ => string.Empty
    };
}
