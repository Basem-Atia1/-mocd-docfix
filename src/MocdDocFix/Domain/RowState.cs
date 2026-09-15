namespace MocdDocFix.Domain;

/// <summary>
/// How far a row has got. <see cref="NotStarted"/> is a blank cell and is a real answer;
/// <see cref="Unrecognised"/> is something else entirely and must never be confused with it,
/// because the delete step acts on exactly one of these values.
/// </summary>
public enum RowState { NotStarted, Corrected, Deleted, Ignore, Failed, Unrecognised }

public static class RowStates
{
    /// <summary>The only value the delete step acts on. Written verbatim; do not abbreviate.</summary>
    public const string Corrected = "corrected and pending the delete of old docs";

    public const string Deleted = "old files deleted";
    public const string Ignore = "ignore";
    public const string Failed = "failed";

    public static RowState Parse(string? cell)
    {
        var text = (cell ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return RowState.NotStarted;

        return text switch
        {
            Corrected => RowState.Corrected,
            Deleted => RowState.Deleted,
            Ignore => RowState.Ignore,
            Failed => RowState.Failed,
            _ => RowState.Unrecognised
        };
    }

    public static string Text(RowState state) => state switch
    {
        RowState.Corrected => Corrected,
        RowState.Deleted => Deleted,
        RowState.Ignore => Ignore,
        RowState.Failed => Failed,
        _ => string.Empty
    };
}
