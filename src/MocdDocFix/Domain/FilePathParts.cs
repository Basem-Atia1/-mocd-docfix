namespace MocdDocFix.Domain;

/// <summary>
/// A vendor file path broken into its parts. The canonical shape is
/// DigitalServices\{Category}\{yyyyMMdd}\{fileGuid}.{ext} but production
/// contains several malformed variants (see spec section 1.1).
/// </summary>
public sealed record FilePathParts(
    string Raw,
    string Root,
    string? CategorySegment,
    string? DateSegment,
    string FileStem,
    string Extension,
    int SegmentCount,
    bool HasDoubledSeparators)
{
    public static FilePathParts Empty(string raw) =>
        new(raw, string.Empty, null, null, string.Empty, string.Empty, 0, false);

    /// <summary>True when the path has the expected number of segments.</summary>
    public bool IsWellFormed => SegmentCount == 4 && !HasDoubledSeparators;
}
