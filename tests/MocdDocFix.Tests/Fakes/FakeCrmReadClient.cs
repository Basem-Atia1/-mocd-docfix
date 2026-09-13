using MocdDocFix.Clients;
using MocdDocFix.Domain;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakeCrmReadClient : ICrmReadClient
{
    public List<DocumentRow> Documents { get; } = new();
    public HashSet<string> KnownCatalogues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DocumentRow>> Resolutions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? ModifiedOn { get; set; }

    public Task<IReadOnlyList<DocumentRow>> GetInScopeDocumentsAsync(
        IReadOnlyList<Guid> catalogues, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DocumentRow>>(Documents);

    public Task<IReadOnlyList<DocumentRow>> ResolveIdentifierAsync(string identifier, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DocumentRow>>(
            Resolutions.TryGetValue(identifier, out var rows) ? rows : new List<DocumentRow>());

    public Task<bool> IsServiceCatalogueAsync(string candidate, CancellationToken ct) =>
        Task.FromResult(KnownCatalogues.Contains(candidate));

    /// <summary>Optional display names; any known catalogue without one gets a generated name.</summary>
    public Dictionary<string, string> CatalogueNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetServiceCatalogueNameAsync(string candidate, CancellationToken ct)
    {
        if (CatalogueNames.TryGetValue(candidate, out var name)) return Task.FromResult<string?>(name);
        return Task.FromResult<string?>(
            KnownCatalogues.Contains(candidate) ? $"Service {candidate[..8]}" : null);
    }

    public Task<DateTimeOffset?> GetDocumentModifiedOnAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(ModifiedOn);

    /// <summary>entitySet + id → raw JSON. Defaults to a minimal stub so tests need not set it.</summary>
    public Dictionary<string, string> RawRecords { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records that do not exist. CRM answers 404 for these and the client returns null, so this
    /// is how a test says "this row has been deleted" — without it every id gets a stub.
    /// </summary>
    public HashSet<string> MissingRecords { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetRawRecordAsync(string entitySet, Guid id, CancellationToken ct)
    {
        var key = $"{entitySet}:{id}";

        if (MissingRecords.Contains(key)) return Task.FromResult<string?>(null);

        return Task.FromResult<string?>(RawRecords.TryGetValue(key, out var json)
            ? json
            : $$"""{"stub":true,"entitySet":"{{entitySet}}","id":"{{id}}"}""");
    }

    /// <summary>documentId → raw JSON, or set the value to null to simulate a failed query.</summary>
    public Dictionary<Guid, string?> Annotations { get; } = new();

    public Task<string?> GetDocumentAnnotationsAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(Annotations.TryGetValue(documentId, out var json) ? json : """{"value":[]}""");
}
