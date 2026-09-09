using MocdDocFix.Clients;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakeCrmWriteClient : ICrmWriteClient
{
    public record Created(Guid FileId, string FilePath, string? Hash, string? Name, string? MediaType, string? Category);

    public List<Created> CreatedFiles { get; } = new();
    public Dictionary<Guid, Guid> Links { get; } = new();
    public List<Guid> DeletedFiles { get; } = new();

    /// <summary>When set, the read-back returns this instead of what was written.</summary>
    public Guid? ForceLinkReadback { get; set; }

    public Task CreateDocumentFileAsync(Guid fileId, string filePath, string? hash, string? name,
        string? mediaType, string? category, CancellationToken ct)
    {
        CreatedFiles.Add(new Created(fileId, filePath, hash, name, mediaType, category));
        return Task.CompletedTask;
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
