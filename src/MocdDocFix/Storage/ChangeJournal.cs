using System.Text.Json;

namespace MocdDocFix.Storage;

/// <summary>The five fields of a mocd_documentfile that a correction overwrites.</summary>
public sealed record RecordValues(string? Path, string? Category, string? Hash, string? FileName, string? FileId);

/// <param name="Action">One of <see cref="ChangeActions"/>.</param>
public sealed record ChangeEntry(
    DateTimeOffset At, Guid Doc, Guid Record, string Action, RecordValues? Old, RecordValues? New);

public static class ChangeActions
{
    public const string Corrected = "corrected";
    public const string Reverted = "reverted";
    public const string Deleted = "deleted";
}

/// <summary>
/// Every change this tool makes to a mocd_documentfile, with the values before and after,
/// appended as it happens and never rewritten.
///
/// It exists because a correction overwrites the old record rather than replacing it, so CRM
/// stops holding the before-state. The ledger holds it too, but the ledger is rewritten
/// constantly and is edited by hand in Excel; this is the copy that no mistake can reach.
/// </summary>
public sealed class ChangeJournal
{
    private readonly string _path;

    public ChangeJournal(string path) => _path = path;

    public void Append(ChangeEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    public IReadOnlyList<ChangeEntry> Read()
    {
        var entries = new List<ChangeEntry>();
        if (!File.Exists(_path)) return entries;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // A process killed mid-write leaves a half line. Skipping it keeps every complete
            // line before it readable, which is the whole reason this is JSONL and not JSON.
            try
            {
                if (JsonSerializer.Deserialize<ChangeEntry>(line) is { } entry) entries.Add(entry);
            }
            catch (JsonException) { }
        }

        return entries;
    }
}
