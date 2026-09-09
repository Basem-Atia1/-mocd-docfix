using System.Text.Json;
using System.Text.Json.Serialization;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

public sealed record StateRecord(
    Guid DocumentId,
    MigrationState State,
    DateTimeOffset At,
    Guid? NewFileId,
    string? NewFilePath,
    string? Detail);

/// <summary>
/// Append-only JSONL. Nothing is ever rewritten, so an interrupted run leaves a readable
/// history and a restart resumes from the last known state per document.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public StateStore(string path) => _path = path;

    public void Append(StateRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, JsonSerializer.Serialize(record, Json) + Environment.NewLine);
    }

    public IReadOnlyDictionary<Guid, StateRecord> LoadLatest()
    {
        var latest = new Dictionary<Guid, StateRecord>();
        if (!File.Exists(_path)) return latest;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            StateRecord? record;
            try { record = JsonSerializer.Deserialize<StateRecord>(line, Json); }
            catch (JsonException) { continue; }   // a torn write should not kill a resume

            if (record is not null) latest[record.DocumentId] = record;
        }

        return latest;
    }

    /// <summary>True when the document has reached <paramref name="state"/> on the happy path.</summary>
    public bool IsAtLeast(Guid documentId, MigrationState state)
    {
        if (!LoadLatest().TryGetValue(documentId, out var record)) return false;
        if (record.State is MigrationState.Quarantined or MigrationState.Failed) return false;
        return record.State >= state;
    }
}
