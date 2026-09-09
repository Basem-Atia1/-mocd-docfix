using System.Text;
using System.Text.Json;

namespace MocdDocFix.Clients;

public interface ICrmWriteClient
{
    Task CreateDocumentFileAsync(Guid fileId, string filePath, string? hash, string? name,
        string? mediaType, string? category, CancellationToken ct);
    Task RepointDocumentAsync(Guid documentId, Guid newFileId, CancellationToken ct);
    Task<Guid?> GetDocumentFileLinkAsync(Guid documentId, CancellationToken ct);
    Task DeleteDocumentFileAsync(Guid fileId, CancellationToken ct);
}

public sealed class CrmWriteClient : ICrmWriteClient
{
    private readonly HttpClient _http;

    public CrmWriteClient(HttpClient http) => _http = http;

    /// <summary>
    /// Creates the row with the vendor's FileId as its primary key, preserving the invariant
    /// mocd_documentfileid == vendor FileId == the path's file stem (spec section 4.1).
    /// </summary>
    public Task CreateDocumentFileAsync(Guid fileId, string filePath, string? hash, string? name,
        string? mediaType, string? category, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["mocd_documentfileid"] = fileId,
            ["mocd_filepath"] = filePath,
            ["mocd_hash"] = hash,
            ["mocd_name"] = name,
            ["mocd_mediatype"] = mediaType,
            ["mocd_category"] = category
        };

        return SendAsync(HttpMethod.Post, "mocd_documentfiles", payload, ct);
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
