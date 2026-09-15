using System.Text.Json;
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="Aborted">The operator did not agree to the step, so nothing was attempted.</param>
public sealed record DeleteSummary(
    int Deleted, int Refused, bool Aborted, IReadOnlyList<string> Reasons);

/// <summary>
/// Removes the old file of every row the repair run finished, and nothing else.
///
/// Because a correction updated the record rather than replacing it, there is no orphaned CRM
/// row to delete — the record the document points at is the same one it always pointed at, now
/// naming the new file. So this step touches the file server only.
///
/// Two checks stand between a row and an irreversible deletion, and both ask a system rather
/// than the ledger: does the record really hold the new path now, and does any other record
/// still name the old one.
/// </summary>
public sealed class DeleteOldFiles
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;
    private readonly IPrompts _prompts;

    public DeleteOldFiles(IFileServiceClient files, ICrmReadClient read, ChangeJournal journal,
        LedgerStore ledger, IPrompts prompts)
    {
        _files = files;
        _read = read;
        _journal = journal;
        _ledger = ledger;
        _prompts = prompts;
    }

    public async Task<DeleteSummary> RunAsync(
        IReadOnlyList<LedgerRow> rows, bool isProduction, CancellationToken ct)
    {
        var eligible = rows.Where(r => r.State() == RowState.Corrected).ToList();
        var reasons = new List<string>();

        if (eligible.Count == 0)
            return new DeleteSummary(0, 0, false,
                new[] { $"No row says \"{RowStates.Corrected}\", so there is nothing to delete." });

        if (!Agreed(eligible.Count, isProduction))
            return new DeleteSummary(0, 0, true, reasons);

        int deleted = 0, refused = 0;

        foreach (var row in eligible)
        {
            ct.ThrowIfCancellationRequested();

            var problem = await DeleteOneAsync(row, ct);

            if (problem is null)
            {
                deleted++;
                _prompts.Info($"  [ row {row.Row} ]  {row.DocFileName}  old file deleted", Tone.Good);
            }
            else
            {
                refused++;
                reasons.Add($"row {row.Row}: {problem}");
                _prompts.Info($"  [ row {row.Row} ]  {row.DocFileName}  REFUSED — {problem}", Tone.Warn);
            }

            _ledger.Write(rows);
        }

        return new DeleteSummary(deleted, refused, false, reasons);
    }

    /// <returns>Null when the old file was deleted, or why it was refused.</returns>
    private async Task<string?> DeleteOneAsync(LedgerRow row, CancellationToken ct)
    {
        if (row.OldFilePath.Length == 0) return "the ledger has no old file path for it";
        if (row.NewFilePath.Length == 0) return "the ledger has no new file path for it";

        // CRM, live. The ledger's belief that the correction landed is not evidence that it did.
        var record = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocFileId, ct);
        var holds = ReadString(record, "mocd_filepath");

        if (!FilePaths.Same(holds, row.NewFilePath))
            return $"its record still names '{holds ?? "nothing"}', not the new file — the " +
                   "correction did not land, so the old file is still the one in use";

        // One file referenced by two records is rare and real, and deleting it breaks the other.
        var referencing = await _read.FindDocumentFilesByPathAsync(row.OldFilePath, ct);
        if (referencing.Any(id => id != row.DocFileId))
            return "another mocd_documentfile still points at the old file";

        var gone = await _files.DeleteAsync(row.OldFilePath, ct);
        if (!gone.Success)
            return $"the file server would not delete it: {gone.Message}";

        _journal.Append(new ChangeEntry(
            DateTimeOffset.UtcNow, row.DocId, row.DocFileId, ChangeActions.Deleted,
            new RecordValues(row.OldFilePath, row.OldCategory, row.OldHash,
                row.OldFileName.Length == 0 ? null : row.OldFileName,
                row.OldFileId.Length == 0 ? null : row.OldFileId),
            null));

        row.FinalState = RowStates.Text(RowState.Deleted);
        return null;
    }

    private bool Agreed(int count, bool isProduction)
    {
        _prompts.Section("Delete old files", Tone.Danger);
        _prompts.Say($"{count} row(s) say \"{RowStates.Corrected}\". For each one, the OLD file " +
                     "is removed from the file server.");
        _prompts.Blank();
        _prompts.Warn("This cannot be undone.", Tone.Danger);
        _prompts.Blank();
        _prompts.Bullet("No CRM record is deleted. The correction updated the record the document " +
                        "already pointed at, so there is no orphan to remove.", Tone.Muted);
        _prompts.Bullet("Each row is re-checked against CRM immediately before its file goes.",
            Tone.Muted);
        _prompts.Bullet("Your local backup keeps the bytes, but a restored file gets a new id and " +
                        "today's date folder — it cannot go back to its old path.", Tone.Muted);
        _prompts.Blank();

        if (!_prompts.YesNo("  Delete the old files now?", defaultYes: false, Tone.Danger))
            return false;

        return !isProduction || _prompts.TypedWord(
            "  This is PRODUCTION. Type DELETE to confirm", "DELETE");
    }

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
