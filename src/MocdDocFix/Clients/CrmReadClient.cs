using System.Text.Json;
using MocdDocFix.Config;
using MocdDocFix.Domain;

namespace MocdDocFix.Clients;

public interface ICrmReadClient
{
    Task<IReadOnlyList<DocumentRow>> GetInScopeDocumentsAsync(IReadOnlyList<Guid> catalogues, CancellationToken ct);
    Task<IReadOnlyList<DocumentRow>> ResolveIdentifierAsync(string identifier, CancellationToken ct);
    Task<bool> IsServiceCatalogueAsync(string candidate, CancellationToken ct);
    Task<DateTimeOffset?> GetDocumentModifiedOnAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// The complete record as raw JSON — every attribute, not a chosen subset. Used by the
    /// backup phase to snapshot the CRM side before any write (spec section 8.2).
    /// </summary>
    Task<string?> GetRawRecordAsync(string entitySet, Guid id, CancellationToken ct);

    /// <summary>
    /// Metadata of every annotation (note) attached to the document. Null means the query
    /// failed — an empty list is returned as an empty JSON array, so a gap is distinguishable
    /// from "there are none".
    /// </summary>
    Task<string?> GetDocumentAnnotationsAsync(Guid documentId, CancellationToken ct);
}

public sealed class CrmReadClient : ICrmReadClient
{
    // Only these three parent entities carry mocd_servicecatalogue (spec section 4.2).
    // Casing matters: the lower-case by-laws form is rejected by the server.
    private static readonly string[] CrossCheckNavigations =
    {
        "mocd_employeeappintmentrequest",
        "mocd_gamrequest",
        "mocd_BylawsAmendmentRequestId"
    };

    private const string Select = "mocd_documentid,mocd_name,modifiedon";

    private static readonly string Expand =
        "mocd_documentfile($select=mocd_filepath,mocd_hash,mocd_name,mocd_mediatype)," +
        "mocd_documenttype($select=mocd_name,_mocd_servicecatalogue_value)," +
        string.Join(",", CrossCheckNavigations.Select(n => $"{n}($select=_mocd_servicecatalogue_value)"));

    private readonly HttpClient _http;
    private readonly ResolvedEnvironment _env;
    private readonly Dictionary<string, bool> _catalogueCache = new(StringComparer.OrdinalIgnoreCase);

    public CrmReadClient(HttpClient http, ResolvedEnvironment env)
    {
        _http = http;
        _env = env;
    }

    public Task<IReadOnlyList<DocumentRow>> GetInScopeDocumentsAsync(
        IReadOnlyList<Guid> catalogues, CancellationToken ct)
    {
        var filter = string.Join(" or ",
            catalogues.Select(c => $"mocd_documenttype/_mocd_servicecatalogue_value eq {c}"));
        return QueryAsync($"mocd_documents?$select={Select}&$filter={filter}&$expand={Expand}", ct);
    }

    public async Task<IReadOnlyList<DocumentRow>> ResolveIdentifierAsync(string identifier, CancellationToken ct)
    {
        identifier = identifier.Trim();

        if (Guid.TryParse(identifier, out var id))
        {
            var asDocument = await QueryAsync(
                $"mocd_documents?$select={Select}&$filter=mocd_documentid eq {id}&$expand={Expand}", ct);
            if (asDocument.Count > 0) return asDocument;

            return await QueryAsync(
                $"mocd_documents?$select={Select}&$filter=_mocd_documentfile_value eq {id}&$expand={Expand}", ct);
        }

        // A file name. The path's file stem is the documentfile id (spec section 4.1), so strip
        // any extension and try that as a GUID first; otherwise match mocd_name.
        var stem = Path.GetFileNameWithoutExtension(identifier);
        if (Guid.TryParse(stem, out var stemId))
        {
            var byStem = await QueryAsync(
                $"mocd_documents?$select={Select}&$filter=_mocd_documentfile_value eq {stemId}&$expand={Expand}", ct);
            if (byStem.Count > 0) return byStem;
        }

        var escaped = identifier.Replace("'", "''");
        return await QueryAsync(
            $"mocd_documents?$select={Select}&$filter=mocd_documentfile/mocd_name eq '{escaped}'&$expand={Expand}", ct);
    }

    public async Task<bool> IsServiceCatalogueAsync(string candidate, CancellationToken ct)
    {
        if (!Guid.TryParse(candidate, out var id)) return false;
        if (_catalogueCache.TryGetValue(candidate, out var cached)) return cached;

        using var response = await _http.GetAsync($"mocd_servicecatalogues({id})?$select=mocd_name", ct);
        var exists = response.IsSuccessStatusCode;
        _catalogueCache[candidate] = exists;
        return exists;
    }

    public async Task<string?> GetRawRecordAsync(string entitySet, Guid id, CancellationToken ct)
    {
        // No $select — we want every attribute, so a restore does not depend on us having
        // predicted which ones matter. The Prefer header adds formatted values and, crucially,
        // lookuplogicalname: without it a polymorphic lookup is a bare GUID with no entity type.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{entitySet}({id})");
        request.Headers.Add("Prefer", "odata.include-annotations=\"*\"");

        using var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
    }

    public async Task<string?> GetDocumentAnnotationsAsync(Guid documentId, CancellationToken ct)
    {
        // documentbody is deliberately excluded: it is a second copy of the file content, and
        // annotations are never modified by this tool. Metadata records that they exist.
        var url = "annotations?$select=annotationid,subject,notetext,filename,mimetype,filesize," +
                  "isdocument,createdon,modifiedon,_objectid_value" +
                  $"&$filter=_objectid_value eq {documentId} and objecttypecode eq 'mocd_document'";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Prefer", "odata.include-annotations=\"*\"");

        using var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
    }

    public async Task<DateTimeOffset?> GetDocumentModifiedOnAsync(Guid documentId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"mocd_documents({documentId})?$select=modifiedon", ct);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("modifiedon", out var m) && m.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(m.GetString()!)
            : null;
    }

    private async Task<IReadOnlyList<DocumentRow>> QueryAsync(string url, CancellationToken ct)
    {
        var rows = new List<DocumentRow>();
        string? next = url;

        while (next is not null)
        {
            using var response = await _http.GetAsync(next, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"CRM query failed against {_env.CrmUrl}: HTTP {(int)response.StatusCode}. {body}");

            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("value", out var value))
                foreach (var element in value.EnumerateArray())
                    rows.Add(Map(element));

            next = json.RootElement.TryGetProperty("@odata.nextLink", out var link)
                ? link.GetString()
                : null;
        }

        return rows;
    }

    private static DocumentRow Map(JsonElement e)
    {
        var file = Child(e, "mocd_documentfile");
        var type = Child(e, "mocd_documenttype");

        Guid? crossCheck = null;
        string? crossSource = null;
        foreach (var nav in CrossCheckNavigations)
        {
            var parent = Child(e, nav);
            var value = GuidOrNull(parent, "_mocd_servicecatalogue_value");
            if (value is not null) { crossCheck = value; crossSource = nav; break; }
        }

        return new DocumentRow(
            DocumentId: GuidOrNull(e, "mocd_documentid") ?? Guid.Empty,
            DocumentName: StringOrNull(e, "mocd_name") ?? string.Empty,
            DocumentFileId: GuidOrNull(file, "mocd_documentfileid") ?? Guid.Empty,
            FilePath: StringOrNull(file, "mocd_filepath"),
            FileName: StringOrNull(file, "mocd_name"),
            MediaType: StringOrNull(file, "mocd_mediatype"),
            Hash: StringOrNull(file, "mocd_hash"),
            DocumentTypeId: GuidOrNull(type, "mocd_documenttypeid") ?? Guid.Empty,
            DocumentTypeName: StringOrNull(type, "mocd_name") ?? string.Empty,
            DocTypeCatalogueId: GuidOrNull(type, "_mocd_servicecatalogue_value"),
            CrossCheckCatalogueId: crossCheck,
            CrossCheckSource: crossSource,
            ModifiedOn: StringOrNull(e, "modifiedon") is { } m ? DateTimeOffset.Parse(m) : default);
    }

    private static JsonElement? Child(JsonElement? parent, string name) =>
        parent is { } p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child : null;

    private static string? StringOrNull(JsonElement? e, string name) =>
        e is { } p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static Guid? GuidOrNull(JsonElement? e, string name) =>
        StringOrNull(e, name) is { } s && Guid.TryParse(s, out var g) ? g : null;
}
