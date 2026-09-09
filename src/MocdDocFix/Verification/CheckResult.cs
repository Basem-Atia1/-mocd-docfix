namespace MocdDocFix.Verification;

/// <param name="HaltsRun">
/// True when a failure of this check means a shared assumption is wrong, so the whole run
/// must stop rather than just this document.
/// </param>
public sealed record CheckResult(string Name, bool Passed, string Detail, bool HaltsRun);
