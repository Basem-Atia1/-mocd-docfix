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

    /// <param name="Attributes">Only the columns the correction changed, not the whole record.</param>
    public record Updated(Guid RecordId, IReadOnlyDictionary<string, object?> Attributes)
    {
        public string? FilePath => Attributes.TryGetValue("mocd_filepath", out var v) ? v as string : null;
        public string? Hash => Attributes.TryGetValue("mocd_hash", out var v) ? v as string : null;
        public string? Category => Attributes.TryGetValue("mocd_category", out var v) ? v as string : null;
        public string? FileName => Attributes.TryGetValue("mocd_filename", out var v) ? v as string : null;
        public string? FileId => Attributes.TryGetValue("mocd_fileid", out var v) ? v as string : null;

        /// <summary>Whether the column was in the payload at all — blank and absent differ.</summary>
        public bool Wrote(string attribute) => Attributes.ContainsKey(attribute);
    }

    public List<Created> CreatedFiles { get; } = new();
    public List<Updated> UpdatedFiles { get; } = new();
    public Dictionary<Guid, Guid> Links { get; } = new();
    public List<Guid> DeletedFiles { get; } = new();

    /// <summary>When set, the next update throws with this message — a CRM refusal.</summary>
    public string? UpdateRefusal { get; set; }

    /// <summary>
    /// Called with what an update wrote, so a test can mirror it into the read client the way
    /// CRM would. The two fakes are independent, so without this a read-back after an update
    /// still finds the old record.
    /// </summary>
    public Action<Guid, IReadOnlyDictionary<string, object?>>? OnUpdated { get; set; }

    public Task UpdateDocumentFileAsync(Guid recordId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct)
    {
        if (UpdateRefusal is not null) throw new InvalidOperationException(UpdateRefusal);

        UpdatedFiles.Add(new Updated(recordId, attributes));
        OnUpdated?.Invoke(recordId, attributes);
        return Task.CompletedTask;
    }

    /// <summary>When set, the read-back returns this instead of what was written.</summary>
    public Guid? ForceLinkReadback { get; set; }

    /// <summary>The key CRM invents when none is supplied. Set it to control what comes back.</summary>
    public Guid GeneratedId { get; set; } = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>
    /// Called with the key CRM actually used. Lets a test mirror the new row into the read
    /// client the way CRM would, so a read-back by that key finds it — which is the difference
    /// between the portal shape (key = vendor file id) and the plugin shape (key from CRM).
    /// </summary>
    public Action<Guid, IReadOnlyDictionary<string, object?>>? OnCreated { get; set; }

    public Task<Guid> CreateDocumentFileAsync(Guid? explicitId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct)
    {
        var id = explicitId ?? GeneratedId;
        CreatedFiles.Add(new Created(explicitId, id, attributes));
        OnCreated?.Invoke(id, attributes);
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
