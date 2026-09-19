using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// The ledger, to a mode that is working through it: read the rows, write them back.
///
/// It exists because there can be one file or two. A sitting scoped to the services we work on
/// has a single <see cref="LedgerStore"/>; one scoped to every catalogue has a
/// <see cref="LedgerSet"/> holding two files whose contents do not overlap. A mode should not
/// know or care which, and must not be handed one of the two files — it writes the whole list
/// back after every row, and writing the whole list into one file would put the other services'
/// rows in the wrong place.
/// </summary>
public interface ILedger
{
    IReadOnlyList<LedgerRow> Read();

    void Write(IReadOnlyList<LedgerRow> rows);
}
