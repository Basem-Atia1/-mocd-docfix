using System.Text.Json;
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="Reasons">One line per refusal, and one per disagreement that was overridden.</param>
public sealed record RedoSummary(int Reverted, int Refused, IReadOnlyList<string> Reasons);

/// <summary>
/// Puts a corrected record back the way it was.
///
/// This is the route back that CRM used to hold by itself: before this design a correction
/// created a new record and repointed, so the old record survived untouched. Now the old record
/// is overwritten, and the only copies of what it held are the ledger and the crm.json the
/// backup step wrote. The snapshot wins where they disagree — it is the record as it actually
/// was, so it cannot be wrong — and the disagreement is reported so the operator knows their
/// edit was not used.
///
/// It refuses any row whose old file is no longer on the server. Pointing CRM at a path that
/// does not exist would break the document for good, and a ledger can go stale.
/// </summary>
public sealed class RedoRun
{
    private readonly IFileServiceClient _files;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;
    private readonly IPrompts _prompts;

    public RedoRun(IFileServiceClient files, ICrmWriteClient write, BackupStore backups,
        ChangeJournal journal, LedgerStore ledger, IPrompts prompts)
    {
        _files = files;
        _write = write;
        _backups = backups;
        _journal = journal;
        _ledger = ledger;
        _prompts = prompts;
    }

    public async Task<RedoSummary> RunAsync(IReadOnlyList<LedgerRow> rows, CancellationToken ct)
    {
        int reverted = 0, refused = 0;
        var reasons = new List<string>();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (row.Verdict2() != RowVerdict.Redo) continue;

            var problem = await RevertAsync(row, reasons, ct);

            if (problem is null)
            {
                reverted++;
                _prompts.Info($"  [ row {row.Row} ]  {row.DocFileName}  RESTORED", Tone.Good);
            }
            else
            {
                refused++;
                reasons.Add($"row {row.Row}: {problem}");
                _prompts.Info($"  [ row {row.Row} ]  {row.DocFileName}  REFUSED — {problem}", Tone.Warn);
            }

            _ledger.Write(rows);
        }

        return new RedoSummary(reverted, refused, reasons);
    }

    /// <returns>Null when the row was reverted, or why it was refused.</returns>
    private async Task<string?> RevertAsync(LedgerRow row, List<string> reasons, CancellationToken ct)
    {
        // Once the old file is deleted there is nothing to go back to. Checked before the
        // server is asked, because the answer is already known.
        if (row.State() == RowState.Deleted)
            return "its old file has already been deleted, so there is nothing to go back to";

        if (row.OldFilePath.Length == 0) return "the ledger has no old file path for it";

        // The ledger can be stale — somebody may have removed the file outside this tool.
        var still = await _files.DownloadAsync(row.OldFilePath, ct);
        if (!still.Success)
            return $"its old file is no longer on the server at {row.OldFilePath}";

        var snapshot = ReadSnapshot(row);
        if (snapshot is null)
            return "there is no crm.json snapshot in its backup folder, and the old values will " +
                   "not be guessed at";

        // The ledger's own columns are compared but not used. Saying so matters: an operator who
        // edited a cell must not be left thinking it took effect.
        Compare(row, snapshot, reasons);

        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mocd_filepath"] = snapshot.Path,
            ["mocd_category"] = snapshot.Category,
            ["mocd_hash"] = snapshot.Hash
        };

        // Exactly the fields a correction overwrote, and no others. A correction never fills a
        // field that was empty, so nothing here needs clearing.
        if (snapshot.FileId is not null) attributes["mocd_fileid"] = snapshot.FileId;
        if (snapshot.FileName is not null) attributes["mocd_filename"] = snapshot.FileName;

        try
        {
            await _write.UpdateDocumentFileAsync(row.DocFileId, attributes, ct);
        }
        catch (Exception broke)
        {
            return $"CRM refused the update: {broke.Message}";
        }

        _journal.Append(new ChangeEntry(
            DateTimeOffset.UtcNow, row.DocId, row.DocFileId, ChangeActions.Reverted,
            new RecordValues(row.NewFilePath, row.CorrectServiceCatalogueId, null, null, null),
            snapshot));

        // The abandoned copy is kept and recorded. Nothing in CRM points at it now, so its path
        // is the only way anyone will find it again.
        if (row.NewFilePath.Length > 0)
            row.SupersededPaths = row.SupersededPaths.Length == 0
                ? row.NewFilePath
                : $"{row.SupersededPaths};{row.NewFilePath}";

        row.NewFilePath = string.Empty;
        row.Verdict = RowVerdicts.Fix;
        row.FinalState = string.Empty;
        row.Error = string.Empty;
        row.Notes = row.Notes.Length == 0
            ? $"reverted {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"
            : $"{row.Notes}; reverted {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";

        return null;
    }

    /// <summary>The old record, out of the crm.json the backup step wrote beside the bytes.</summary>
    private RecordValues? ReadSnapshot(LedgerRow row)
    {
        var path = Path.Combine(_backups.Folder(row.DocId, row.DocFileName).OldDir, "crm.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var image = JsonDocument.Parse(File.ReadAllText(path));

            if (!image.RootElement.TryGetProperty(nameof(CrmImage.DocumentFileRaw), out var raw) ||
                raw.GetString() is not { Length: > 0 } json)
                return null;

            using var record = JsonDocument.Parse(json);

            return new RecordValues(
                Text(record, "mocd_filepath"),
                Text(record, "mocd_category"),
                Text(record, "mocd_hash"),
                Text(record, "mocd_filename"),
                Text(record, "mocd_fileid"));
        }
        catch (JsonException) { return null; }
    }

    private static void Compare(LedgerRow row, RecordValues snapshot, List<string> reasons)
    {
        Differ("old file path", row.OldFilePath, snapshot.Path);
        Differ("old category", row.OldCategory, snapshot.Category);
        Differ("old hash", row.OldHash, snapshot.Hash);
        Differ("old file name", row.OldFileName, snapshot.FileName);
        Differ("old file id", row.OldFileId, snapshot.FileId);

        void Differ(string column, string inLedger, string? inSnapshot)
        {
            var snap = inSnapshot ?? string.Empty;
            if (string.Equals(inLedger, snap, StringComparison.OrdinalIgnoreCase)) return;

            reasons.Add($"row {row.Row}: the ledger's {column} says '{inLedger}' and the snapshot " +
                        $"says '{snap}' — the snapshot was written, the ledger was not used");
        }
    }

    private static string? Text(JsonDocument record, string attribute) =>
        record.RootElement.TryGetProperty(attribute, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
