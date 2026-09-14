using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;

namespace MocdDocFix.Commands;

/// <param name="RecordId">The mocd_documentfile the document actually points at, in CRM.</param>
/// <param name="FilePath">That record's mocd_filepath — filed under the right catalogue.</param>
public sealed record SettledRecord(Guid RecordId, string FilePath);

/// <param name="Was">The state the tool had written down.</param>
public sealed record Reconciliation(Guid DocumentId, string? FileName, MigrationState Was, SettledRecord Now);

/// <summary>
/// Asks CRM what is actually true and brings the state file into line with it.
///
/// The state file is a cache of what the tool believed when it last ran, and a cache can be
/// wrong: a run that finished its CRM writes and then failed a later check records Failed over
/// work that actually succeeded. Every step afterwards reads the cache, finds nothing eligible,
/// and does nothing — correctly, but leaving the old file and old record stranded with no way
/// through the app to remove them.
///
/// So the authority is CRM, and this is where the two are reconciled. It only ever moves a
/// document forward to Repointed, and only on CRM's own evidence: the document points at some
/// record other than its old one, and that record's path is filed under the right catalogue.
/// It never deletes, never uploads, and never contacts the file server.
/// </summary>
public static class Reconciler
{
    /// <summary>
    /// Null unless CRM shows this document already pointing at a correctly filed record.
    /// </summary>
    public static async Task<SettledRecord?> AlreadyCorrectAsync(
        ICrmReadClient read, ICrmWriteClient write, ManifestEntry entry, CancellationToken ct)
    {
        var linked = await write.GetDocumentFileLinkAsync(entry.DocumentId, ct);
        if (linked is not { } linkedId || linkedId == entry.OldFileId) return null;

        var path = ReadFilePath(await read.GetRawRecordAsync("mocd_documentfiles", linkedId, ct));
        if (string.IsNullOrWhiteSpace(path)) return null;

        var segment = FilePathParser.Parse(path).CategorySegment;

        return Guid.TryParse(segment, out var catalogue) && catalogue == entry.CorrectCatalogueId
            ? new SettledRecord(linkedId, path)
            : null;
    }

    /// <summary>
    /// Walks every backed-up document whose recorded state is behind reality and corrects it.
    /// Returns what it changed, so the operator is told rather than having it happen silently.
    /// </summary>
    public static async Task<IReadOnlyList<Reconciliation>> SweepAsync(
        ICrmReadClient read, ICrmWriteClient write, StateStore state,
        IEnumerable<ManifestEntry> manifest, CancellationToken ct)
    {
        var latest = state.LoadLatest();
        var corrected = new List<Reconciliation>();

        foreach (var entry in manifest)
        {
            ct.ThrowIfCancellationRequested();

            var was = latest.TryGetValue(entry.DocumentId, out var record)
                ? record.State
                : MigrationState.Pending;

            // Already where it should be, or already finished. Nothing to reconcile.
            if (was is MigrationState.Deleted) continue;
            if (was is MigrationState.Repointed &&
                record!.NewFileId is not null && !string.IsNullOrWhiteSpace(record.NewFilePath))
                continue;

            if (await AlreadyCorrectAsync(read, write, entry, ct) is not { } settled) continue;

            state.Append(new StateRecord(entry.DocumentId, MigrationState.Repointed,
                DateTimeOffset.UtcNow, settled.RecordId, settled.FilePath,
                $"Reconciled from CRM: recorded as {was}, but the document points at " +
                $"{settled.RecordId}, which is correctly filed."));

            corrected.Add(new Reconciliation(entry.DocumentId, entry.FileName, was, settled));
        }

        return corrected;
    }

    private static string? ReadFilePath(string? recordJson)
    {
        if (string.IsNullOrWhiteSpace(recordJson)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(recordJson);
            return json.RootElement.TryGetProperty("mocd_filepath", out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
