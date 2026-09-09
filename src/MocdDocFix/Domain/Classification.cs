namespace MocdDocFix.Domain;

/// <param name="Reason">Why it is wrong, in terms of the actual data.</param>
/// <param name="Solution">
/// What the tool will do about it, or what a human must decide. Spec section 8.1 — every
/// report states the remedy, not only the diagnosis.
/// </param>
public sealed record Classification(
    Verdict Verdict,
    string Reason,
    string Solution,
    Guid? CorrectCatalogueId,
    string? CurrentSegment);
