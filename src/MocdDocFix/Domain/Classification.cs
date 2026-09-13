namespace MocdDocFix.Domain;

/// <param name="Group">
/// Which kind of corruption this is, 1-7. See <see cref="DocumentGroups"/> for what each means.
/// The group decides whether the row is fixed: groups 1-5 are always <see cref="Verdict.Fix"/>,
/// groups 6 and 7 never are. Reports group by this number, and the operator reads a group
/// heading as true of every row beneath it, so the invariant must hold.
/// </param>
/// <param name="Reason">Why it is wrong, in terms of the actual data.</param>
/// <param name="Solution">
/// What the tool will do about it, or what a human must decide. Spec section 8.1 — every
/// report states the remedy, not only the diagnosis.
/// </param>
public sealed record Classification(
    Verdict Verdict,
    int Group,
    string Reason,
    string Solution,
    Guid? CorrectCatalogueId,
    string? CurrentSegment);
