using System.Text;
using System.Text.Json;

namespace MocdDocFix.Clients;

public interface ICrmWriteClient
{
    /// <param name="explicitId">
    /// The key to create the row with, or null to let CRM generate one. The portal sets it to the
    /// vendor's FileId; the plugin lets CRM choose. Keeping whichever the old record used is what
    /// stops a migration from quietly changing a document's shape.
    /// </param>
    /// <returns>The id of the row that was created.</returns>
    Task<Guid> CreateDocumentFileAsync(Guid? explicitId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct);
    Task RepointDocumentAsync(Guid documentId, Guid newFileId, CancellationToken ct);
    Task<Guid?> GetDocumentFileLinkAsync(Guid documentId, CancellationToken ct);
    Task DeleteDocumentFileAsync(Guid fileId, CancellationToken ct);

    /// <summary>
    /// Changes the named attributes on an existing mocd_documentfile, leaving every other column
    /// alone. This is how a correction is applied: the document already points at this record,
    /// so nothing is created and nothing is repointed.
    /// </summary>
    Task UpdateDocumentFileAsync(Guid recordId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct);
}

public sealed class CrmWriteClient : ICrmWriteClient
{
    private readonly HttpClient _http;

    public CrmWriteClient(HttpClient http) => _http = http;

    public async Task<Guid> CreateDocumentFileAsync(Guid? explicitId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>(attributes);
        if (explicitId is { } id) payload["mocd_documentfileid"] = id;

        using var request = new HttpRequestMessage(HttpMethod.Post, "mocd_documentfiles");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"CRM POST mocd_documentfiles failed: HTTP {(int)response.StatusCode}. {error}");
        }

        if (explicitId is { } chosen) return chosen;

        // CRM generated the key, so read it back out of the OData-EntityId header — the only
        // place a create response carries it.
        return IdFromEntityHeader(response)
            ?? throw new InvalidOperationException(
                "CRM created the documentfile but did not return its id, so it cannot be linked. " +
                "Stopping rather than guessing.");
    }

    private static Guid? IdFromEntityHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("OData-EntityId", out var values)) return null;

        var location = values.FirstOrDefault();
        if (location is null) return null;

        var open = location.LastIndexOf('(');
        var close = location.LastIndexOf(')');
        if (open < 0 || close <= open) return null;

        return Guid.TryParse(location[(open + 1)..close], out var id) ? id : null;
    }

    public Task RepointDocumentAsync(Guid documentId, Guid newFileId, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["mocd_documentfile@odata.bind"] = $"/mocd_documentfiles({newFileId})"
        };

        return SendAsync(HttpMethod.Patch, $"mocd_documents({documentId})", payload, ct);
    }

    public async Task<Guid?> GetDocumentFileLinkAsync(Guid documentId, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"mocd_documents({documentId})?$select=_mocd_documentfile_value", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not read document {documentId}: HTTP {(int)response.StatusCode}. {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("_mocd_documentfile_value", out var v) &&
               v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g)
            ? g : null;
    }

    public Task UpdateDocumentFileAsync(Guid recordId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"mocd_documentfiles({recordId})",
            new Dictionary<string, object?>(attributes), ct);

    public Task DeleteDocumentFileAsync(Guid fileId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"mocd_documentfiles({fileId})", payload: null, ct);

    private async Task SendAsync(HttpMethod method, string url, Dictionary<string, object?>? payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"CRM {method} {url} failed: HTTP {(int)response.StatusCode}. {body}");
    }
}
