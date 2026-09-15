using MocdDocFix.Clients;
using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

public sealed record CheckSummary(
    int Checked, int AsExpected, int NotAsExpected, IReadOnlyList<string> Problems);

/// <summary>
/// Asks both systems whether the ledger is telling the truth about the old files.
///
/// A row saying the old file was deleted should have nothing on the server at that path and
/// nothing in CRM referring to it. A row awaiting deletion should still have its old file
/// exactly where it was. Anything else is worth knowing about — a file deleted outside this
/// tool leaves a row Redo can no longer help, and a file the ledger believes is gone but is not
/// is an orphan nobody is counting.
///
/// It writes nothing. Not to CRM, not to the file server, not to the ledger.
/// </summary>
public sealed class CheckItAll
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;

    public CheckItAll(IFileServiceClient files, ICrmReadClient read)
    {
        _files = files;
        _read = read;
    }

    public async Task<CheckSummary> RunAsync(IReadOnlyList<LedgerRow> rows, CancellationToken ct)
    {
        int checkedRows = 0, asExpected = 0, notAsExpected = 0;
        var problems = new List<string>();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var state = row.State();
            if (state is not (RowState.Deleted or RowState.Corrected)) continue;
            if (row.OldFilePath.Length == 0) continue;

            checkedRows++;

            var onServer = (await _files.DownloadAsync(row.OldFilePath, ct)).Success;
            var found = new List<string>();

            if (state == RowState.Deleted)
            {
                if (onServer)
                    found.Add($"row {row.Row} ({row.DocFileName}): the ledger says the old file " +
                              $"was deleted, but it is still on the file server at {row.OldFilePath}");

                var referencing = await _read.FindDocumentFilesByPathAsync(row.OldFilePath, ct);
                if (referencing.Count > 0)
                    found.Add($"row {row.Row} ({row.DocFileName}): {referencing.Count} " +
                              "mocd_documentfile record(s) still refer to the old path");
            }
            else if (!onServer)
            {
                found.Add($"row {row.Row} ({row.DocFileName}): the row is awaiting the delete " +
                          "step, but its old file is already gone from the file server — " +
                          "something removed it outside this tool, and Redo can no longer " +
                          "restore this row");
            }

            if (found.Count == 0) asExpected++;
            else { notAsExpected++; problems.AddRange(found); }
        }

        return new CheckSummary(checkedRows, asExpected, notAsExpected, problems);
    }
}
