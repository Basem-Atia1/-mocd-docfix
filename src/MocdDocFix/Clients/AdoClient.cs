using System.Net;
using System.Text;
using System.Text.Json;
using MocdDocFix.Domain;

namespace MocdDocFix.Clients;

public interface IAdoClient
{
    /// <summary>
    /// Work items whose TITLE contains this phrase. Titles only: the on-prem server answers
    /// TF401349 to a CONTAINS WORDS search over descriptions, so full text is not available.
    /// </summary>
    Task<IReadOnlyList<AdoHit>> FindByTitleAsync(string phrase, CancellationToken ct);
}

/// <summary>
/// Reads the Azure DevOps backlog over NTLM. Reads only — it runs WIQL queries and fetches
/// titles, and has no method that writes anything.
/// </summary>
public sealed class AdoClient : IAdoClient, IDisposable
{
    /// <summary>Titles are fetched in batches; the API refuses more than 200 ids at a time.</summary>
    private const int BatchSize = 180;

    /// <summary>Beyond this the answer is noise anyway, so we stop paying for it.</summary>
    private const int MostWeWillLookAt = 200;

    private readonly HttpClient _http;
    private readonly string _project;

    public AdoClient(string collectionUrl, string project, string user, string password, string? domain = null)
    {
        _project = project;

        var handler = new HttpClientHandler
        {
            Credentials = domain is { Length: > 0 }
                ? new NetworkCredential(user, password, domain)
                : new NetworkCredential(user, password),
            PreAuthenticate = true
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(collectionUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2)
        };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<IReadOnlyList<AdoHit>> FindByTitleAsync(string phrase, CancellationToken ct)
    {
        var ids = await SearchAsync(phrase, ct);
        if (ids.Count == 0) return Array.Empty<AdoHit>();

        var hits = new List<AdoHit>(ids.Count);

        foreach (var batch in ids.Take(MostWeWillLookAt).Chunk(BatchSize))
        {
            var url = $"{Uri.EscapeDataString(_project)}/_apis/wit/workitems" +
                      $"?ids={string.Join(',', batch)}&fields=System.Title&api-version=6.0";

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) continue;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!json.RootElement.TryGetProperty("value", out var items)) continue;

            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
                var title = item.TryGetProperty("fields", out var f) &&
                            f.TryGetProperty("System.Title", out var t)
                    ? t.GetString() ?? string.Empty
                    : string.Empty;

                hits.Add(new AdoHit(id, title, DocumentTypeAuthority.ServiceInTitle(title)));
            }
        }

        // The count matters — "too many to mean anything" is a verdict — so report what the
        // query really found, even where only the first few titles were fetched.
        return ids.Count > MostWeWillLookAt
            ? hits.Concat(Enumerable.Range(0, ids.Count - hits.Count)
                    .Select(_ => new AdoHit(0, string.Empty, null)))
                .ToList()
            : hits;
    }

    private async Task<IReadOnlyList<int>> SearchAsync(string phrase, CancellationToken ct)
    {
        // WIQL is a query language, not SQL over a database we own, but the phrase still goes
        // inside a string literal — so the single quote is doubled, as WIQL requires.
        var wiql = "SELECT [System.Id] FROM WorkItems WHERE " +
                   $"[System.TeamProject] = '{_project.Replace("'", "''")}' AND " +
                   $"[System.Title] CONTAINS '{phrase.Replace("'", "''")}'";

        var body = new StringContent(
            JsonSerializer.Serialize(new { query = wiql }), Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync(
            $"{Uri.EscapeDataString(_project)}/_apis/wit/wiql?api-version=6.0", body, ct);

        if (!response.IsSuccessStatusCode) return Array.Empty<int>();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return json.RootElement.TryGetProperty("workItems", out var items)
            ? items.EnumerateArray()
                .Select(w => w.TryGetProperty("id", out var id) ? id.GetInt32() : 0)
                .Where(id => id > 0)
                .ToList()
            : Array.Empty<int>();
    }

    public void Dispose() => _http.Dispose();
}
