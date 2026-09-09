namespace MocdDocFix.Domain;

/// <summary>
/// The happy path runs Pending → BackedUp → Uploaded → Verified → Repointed → Deleted.
/// Quarantined and Failed are terminal and never count as progress.
/// </summary>
public enum MigrationState
{
    Pending = 0,
    BackedUp = 1,
    Uploaded = 2,
    Verified = 3,
    Repointed = 4,
    Deleted = 5,

    Quarantined = 90,
    Failed = 91
}
