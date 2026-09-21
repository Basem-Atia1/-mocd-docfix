using System.Text.Json;
using MocdDocFix.Clients;
using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <summary>How a row that CRM already has right should be settled.</summary>
public enum SettleAs
{
    /// <summary>The path never changed. There was never anything to correct or to delete.</summary>
    AlwaysRight,

    /// <summary>Corrected by something else, and the old file is still on the server.</summary>
    PendingDelete,

    /// <summary>Corrected by something else, and the old file has gone. Nothing outstanding.</summary>
    Finished,

    /// <summary>
    /// Corrected by another row that shares this document file record. The old file belongs to
    /// that row, which owns its deletion — this row must not queue a second one.
    /// </summary>
    BySibling
}

public sealed record AlreadyCorrectRow(LedgerRow Row, string NowAt, SettleAs As, LedgerRow? Sibling);

/// <summary>
/// A row CRM files under the right catalogue whose file is not on the server. Not settled: the
/// path being right is not the same as the document working, and the repair run can put the
/// file back, so it keeps its verdict and the run picks it up as usual.
/// </summary>
public sealed record MissingFileRow(LedgerRow Row, string NowAt);

public sealed record AlreadyCorrectScan(
    IReadOnlyList<AlreadyCorrectRow> Settled, IReadOnlyList<MissingFileRow> MissingFile);

/// <summary>
/// Asks CRM, before a repair run starts, which of the rows marked fix are already right.
///
/// The same question used to be asked inside the loop, one document at a time, which left
/// nowhere to put the answer: Quiet and Unattended have nobody to ask, and Watch would have
/// interrupted every row. Asking for all of them first means one question, the same in all
/// three modes, and the operator sees the whole list before anything is written.
///
/// It only reads. Nothing is uploaded, nothing in CRM is changed, and no row is settled until
/// <see cref="Apply"/> is called.
/// </summary>
public sealed class AlreadyCorrect
{
    private readonly ICrmReadClient _read;
    private readonly IFileServiceClient _files;

    public AlreadyCorrect(ICrmReadClient read, IFileServiceClient files)
    {
        _read = read;
        _files = files;
    }

    public async Task<AlreadyCorrectScan> FindAsync(
        IReadOnlyList<LedgerRow> rows, CancellationToken ct)
    {
        var settled = new List<AlreadyCorrectRow>();
        var missing = new List<MissingFileRow>();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (row.Verdict2() != RowVerdict.Fix) continue;
            if (row.State() is RowState.Corrected or RowState.Deleted) continue;
            if (row.DocFileId == Guid.Empty) continue;
            if (!Guid.TryParse(row.CorrectServiceCatalogueId, out var correct)) continue;

            var record = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocFileId, ct);
            var now = ReadString(record, "mocd_filepath");
            if (now is null) continue;

            var filed = FilePathParser.Parse(now).CategorySegment;
            if (!Guid.TryParse(filed, out var under) || under != correct) continue;

            if (FilePaths.Same(now, row.OldFilePath))
            {
                settled.Add(new AlreadyCorrectRow(row, now, SettleAs.AlwaysRight, null));
                continue;
            }

            // The catalogue in the path is right, but that is not the same as the document
            // working. A record naming a file nobody can fetch is still broken, and the repair
            // run can put it back — so the row keeps its verdict rather than being settled.
            if (!(await _files.DownloadAsync(now, ct)).Success)
            {
                missing.Add(new MissingFileRow(row, now));
                continue;
            }

            var sibling = SiblingThatCorrected(row, rows);
            if (sibling is not null)
            {
                settled.Add(new AlreadyCorrectRow(row, now, SettleAs.BySibling, sibling));
                continue;
            }

            var oldStillThere = (await _files.DownloadAsync(row.OldFilePath, ct)).Success;

            settled.Add(new AlreadyCorrectRow(row, now,
                oldStillThere ? SettleAs.PendingDelete : SettleAs.Finished, null));
        }

        return new AlreadyCorrectScan(settled, missing);
    }

    /// <summary>
    /// Writes the settlements the operator agreed to. Nothing here talks to CRM or to the file
    /// server — every question was answered by <see cref="FindAsync"/>.
    /// </summary>
    public static void Apply(IReadOnlyList<AlreadyCorrectRow> settled)
    {
        foreach (var (row, nowAt, how, sibling) in settled)
        {
            row.Error = string.Empty;

            // Done, not skip: skip no longer exists, and it always meant the same thing here —
            // we looked, there was nothing to do. Nothing is outstanding, which is what done says.
            if (how == SettleAs.AlwaysRight)
            {
                row.Verdict = RowVerdicts.Done;
                row.Notes = Note(row.Notes,
                    $"checked {Now()} — CRM already files this under the right catalogue and the " +
                    "path has not changed, so there is nothing to correct and nothing to delete");
                continue;
            }

            row.NewFilePath = nowAt;
            row.Verdict = RowVerdicts.Done;

            switch (how)
            {
                // Deliberately no final state. The old file is the sibling's to delete, and a
                // second row saying "pending the delete of old docs" would queue the same file
                // twice — so this row is finished and the delete step never looks at it.
                case SettleAs.BySibling:
                    row.FinalState = string.Empty;
                    row.Notes = Note(row.Notes,
                        $"checked {Now()} — its file was corrected by {sibling!.Ref()}, which " +
                        "shares this document file record and owns the delete of the old file");
                    break;

                case SettleAs.PendingDelete:
                    row.FinalState = RowStates.Text(RowState.Corrected);
                    row.Notes = Note(row.Notes,
                        $"checked {Now()} — CRM was already corrected by something other than " +
                        $"this run, and the old file is still on the server at {row.OldFilePath}, " +
                        "so the delete step still has work to do here");
                    break;

                default:
                    row.FinalState = RowStates.Text(RowState.Deleted);
                    row.Notes = Note(row.Notes,
                        $"checked {Now()} — CRM was already corrected and the old file is no " +
                        "longer on the server, so nothing is outstanding");
                    break;
            }
        }
    }

    /// <summary>
    /// Settles the rows that share a corrected row's document file record, the moment it is
    /// corrected.
    ///
    /// One mocd_documentfile can be the file of several mocd_document records, so correcting one
    /// row moves the file under every row that shares it. Those rows are not work any more, and
    /// the answer is known without asking anybody — the run has just done it.
    ///
    /// Until now they were found by asking CRM again: either the pre-run scan of the *next* run,
    /// or the check at the top of each row, which is one HTTP call to learn something already in
    /// memory. Worse, that check has no idea a sibling is involved, so it wrote "corrected and
    /// pending the delete of old docs" and queued the same old file for deletion twice.
    ///
    /// **No final state, deliberately.** The old file is the corrected row's to delete, and it
    /// owns that deletion alone.
    /// </summary>
    /// <param name="all">The whole ledger, not the working set — a sibling can be any row.</param>
    /// <returns>The rows that were settled, so the caller can say how many.</returns>
    public static IReadOnlyList<LedgerRow> SettleSiblingsOf(
        LedgerRow corrected, IReadOnlyList<LedgerRow> all)
    {
        if (corrected.DocFileId == Guid.Empty || corrected.NewFilePath.Length == 0)
            return Array.Empty<LedgerRow>();

        // Only rows nothing has happened to yet, and only ones that say fix. A row somebody
        // marked ignore, review or redo is an instruction, and this is not the place to overrule
        // one — the file having moved does not tell us what they meant by it.
        var siblings = all
            .Where(other => other.DocId != corrected.DocId &&
                            other.DocFileId == corrected.DocFileId &&
                            other.State() == RowState.NotStarted &&
                            other.Verdict2() == RowVerdict.Fix)
            .ToList();

        foreach (var row in siblings)
        {
            row.NewFilePath = corrected.NewFilePath;
            row.Verdict = RowVerdicts.Done;
            row.FinalState = string.Empty;
            row.Error = string.Empty;
            row.Notes = Note(row.Notes,
                $"settled {Now()} — its file was corrected by {corrected.Named()}, which shares " +
                "this document file record and owns the delete of the old file");
        }

        return siblings;
    }

    /// <summary>
    /// A row that corrected the same mocd_documentfile record. One record can be the file of
    /// several documents, so correcting one row moves the file under all of them.
    /// </summary>
    private static LedgerRow? SiblingThatCorrected(LedgerRow row, IReadOnlyList<LedgerRow> rows) =>
        rows.FirstOrDefault(other =>
            other.DocId != row.DocId &&
            other.DocFileId == row.DocFileId &&
            other.State() is RowState.Corrected or RowState.Deleted);

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");

    private static string Note(string existing, string addition) =>
        existing.Length == 0 ? addition : $"{existing}; {addition}";

    private static string? ReadString(string? json, string attribute)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(attribute, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
