namespace MocdDocFix.Verification;

public sealed record VerificationReport(IReadOnlyList<CheckResult> Checks)
{
    public bool AllPassed => Checks.All(c => c.Passed);
    public bool MustHalt => Checks.Any(c => !c.Passed && c.HaltsRun);
    public IEnumerable<CheckResult> Failures => Checks.Where(c => !c.Passed);
}
