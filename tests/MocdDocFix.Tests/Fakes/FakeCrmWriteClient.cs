using MocdDocFix.Clients;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakeCrmWriteClient : ICrmWriteClient
{
    /// <param name="ExplicitId">Null when CRM was left to generate the key, as the plugin does.</param>
    public record Created(Guid? ExplicitId, Guid FileId, IReadOnlyDictionary<string, object?> Attributes)
    {
        public string? FilePath => Attributes.TryGetValue("mocd_filepath", out var v) ? v as string : null;
        public string? Hash => Attributes.TryGetValue("mocd_hash", out var v) ? v as string : null;
        public string? Name => Attributes.TryGetValue("mocd_name", out var v) ? v as string : null;
        public string? MediaType => Attributes.TryGetValue("mocd_mediatype", out var v) ? v as string : null;
        public string? Category => Attributes.TryGetValue("mocd_category", out var v) ? v as string : null;
    }

    public List<Created> CreatedFiles { get; } = new();
    public Dictionary<Guid, Guid> Links { get; } = new();
    public List<Guid> DeletedFiles { get; } = new();

    /// <summary>When set, the read-back returns this instead of what was written.</summary>
    public Guid? ForceLinkReadback { get; set; }

    /// <summary>The key CRM invents when none is supplied. Set it to control what comes back.</summary>
    public Guid GeneratedId { get; set; } = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public Task<Guid> CreateDocumentFileAsync(Guid? explicitId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct)
    {
        var id = explicitId ?? GeneratedId;
        CreatedFiles.Add(new Created(explicitId, id, attributes));
        return Task.FromResult(id);
    }

    public Task RepointDocumentAsync(Guid documentId, Guid newFileId, CancellationToken ct)
    {
        Links[documentId] = newFileId;
        return Task.CompletedTask;
    }

    public Task<Guid?> GetDocumentFileLinkAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(ForceLinkReadback ?? (Links.TryGetValue(documentId, out var v) ? v : (Guid?)null));

    public Task DeleteDocumentFileAsync(Guid fileId, CancellationToken ct)
    {
        DeletedFiles.Add(fileId);
        return Task.CompletedTask;
    }
}
