using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Every test class that writes a workbook belongs here, so they run one at a time.
///
/// ClosedXML writes through System.IO.Packaging, whose core-properties reader is not safe to
/// use from several threads at once — concurrent writes fail with "Unrecognized root element in
/// Core Properties part", from a file neither test touched. xUnit runs test classes in parallel
/// by default, so once more than a couple of classes wrote ledgers this began failing at random.
///
/// It is a test-only problem: a run of the tool writes the ledger from one thread. Serialising
/// these classes costs a second or two and removes a whole class of phantom failure.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LedgerCollection
{
    public const string Name = "ledger";
}
