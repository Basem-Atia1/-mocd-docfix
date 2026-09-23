using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <param name="Removed">Rows taken out because their document is gone and nothing was owed.</param>
/// <param name="Kept">
/// Rows whose document is gone but which still record work — a correction awaiting its delete,
/// or a copy left on the file server. Marked, not removed.
/// </param>
public sealed record DroppedDeleted(IReadOnlyList<LedgerRow> Rows, int Removed, int Kept);

/// <summary>
/// Takes out the rows whose document CRM no longer returns.
///
/// The scan reads every document under the services in scope; a row for one that did not come
/// back is a document somebody has deleted. Nothing will ever act on it again, so it is clutter
/// — and being told about it on every scan is clutter too, because there is nothing to decide.
///
/// **A row that records work stays.** Deleting the document in CRM does not remove the files:
/// a correction awaiting its delete still has an old file on the server, and a superseded path
/// is a copy nothing points at. The row is the only thing that knows where either of them is,
/// so it is marked instead and the mark is in the sheet, where somebody can sort by it.
///
/// Out of scope is not gone. A row whose service simply was not scanned this run is left
/// entirely alone — it is not missing, it was not looked for.
/// </summary>
public static class DropDeletedDocuments
{
    /// <summary>Written into the notes of a row kept because it still records work.</summary>
    public const string Mark = "[deleted in crm]";

    /// <param name="gone">Rows the scan did not see, whatever the reason.</param>
    /// <param name="inScope">The service catalogues this run actually read.</param>
    public static DroppedDeleted From(
        IReadOnlyList<LedgerRow> rows,
        IReadOnlyList<LedgerRow> gone,
        IReadOnlySet<string> inScope)
    {
        // A row with no service catalogue counts as in scope: it is almost always one of these
        // already, and leaving it out would keep it for ever on the strength of a blank cell.
        var absent = gone
            .Where(r => r.ServiceCatalogueId.Length == 0 || inScope.Contains(r.ServiceCatalogueId))
            .ToHashSet();

        if (absent.Count == 0) return new DroppedDeleted(rows, 0, 0);

        var kept = new List<LedgerRow>(rows.Count);
        int removed = 0, marked = 0;

        foreach (var row in rows)
        {
            if (!absent.Contains(row))
            {
                kept.Add(row);
                continue;
            }

            if (row.State() == RowState.NotStarted && row.SupersededPaths.Length == 0)
            {
                removed++;
                continue;
            }

            // Once. This runs on every scan, and a mark added each time would fill the cell.
            if (!row.Notes.Contains(Mark, StringComparison.Ordinal))
            {
                row.Notes = Note(row.Notes,
                    $"{Now()} — CRM no longer returns this document, so it has been deleted " +
                    $"there. Kept because work is recorded against it {Mark}");
                marked++;
            }

            kept.Add(row);
        }

        return new DroppedDeleted(kept, removed, marked);
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");

    private static string Note(string existing, string addition) =>
        existing.Length == 0 ? addition : $"{existing}; {addition}";
}
