namespace MocdDocFix.Domain;

public enum Verdict
{
    /// <summary>Unambiguously wrong and we know the correct catalogue. Safe to migrate.</summary>
    Fix,

    /// <summary>Wrong-looking but possibly correct. Reported for a human, never modified.</summary>
    Review,

    /// <summary>Already correct, or nothing to write. No action.</summary>
    Skip
}
