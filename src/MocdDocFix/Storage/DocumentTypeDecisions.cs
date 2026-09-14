using System.Text.Json;
using System.Text.Json.Serialization;

namespace MocdDocFix.Storage;

/// <summary>
/// What the operator decided about one document type that DevOps could not settle.
/// </summary>
/// <param name="Ruling">
/// "accept-crm" to trust the document type's own service catalogue, "needs-human" to send its
/// documents to group 6 untouched, or the name of the service the operator says owns it.
/// </param>
public sealed record TypeDecision(
    string DocumentType,
    string Ruling,
    string? Why,
    DateTimeOffset At,
    string DecidedBy);

/// <summary>
/// The decisions file: one line per document type DevOps could not answer for.
///
/// It exists so the same awkward name is put to the operator once rather than on every run, and
/// it is plain JSON in the config folder precisely so it can be opened and corrected — a decision
/// made in a hurry at the fifth file of a long run should not be permanent just because it was
/// written down.
/// </summary>
public sealed class DocumentTypeDecisions
{
    public const string AcceptCrm = "accept-crm";
    public const string NeedsHuman = "needs-human";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private Dictionary<string, TypeDecision>? _cache;

    public DocumentTypeDecisions(string path) => _path = path;

    public string Path => _path;

    public TypeDecision? For(string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType)) return null;
        return Load().TryGetValue(Key(documentType!), out var decision) ? decision : null;
    }

    public void Remember(string documentType, string ruling, string? why, string decidedBy)
    {
        var all = Load();
        all[Key(documentType)] = new TypeDecision(
            documentType.Trim(), ruling, why, DateTimeOffset.Now, decidedBy);

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(all.Values.OrderBy(d => d.DocumentType), Json));
        _cache = all;
    }

    private Dictionary<string, TypeDecision> Load()
    {
        if (_cache is not null) return _cache;

        _cache = new Dictionary<string, TypeDecision>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(_path)) return _cache;

        try
        {
            var saved = JsonSerializer.Deserialize<List<TypeDecision>>(File.ReadAllText(_path));
            foreach (var decision in saved ?? new List<TypeDecision>())
                if (!string.IsNullOrWhiteSpace(decision.DocumentType))
                    _cache[Key(decision.DocumentType)] = decision;
        }
        catch (JsonException)
        {
            // A file someone edited by hand and broke must not stop a run: the decisions are a
            // convenience, and losing them costs questions, not correctness.
        }

        return _cache;
    }

    private static string Key(string documentType) => documentType.Trim();
}
