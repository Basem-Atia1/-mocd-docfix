using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using MocdDocFix.Domain;

namespace MocdDocFix.Clients;

/// <param name="Url">Where the file itself can be fetched from.</param>
public sealed record AdoAttachment(int WorkItemId, string WorkItemTitle, string Name, string Url);

/// <param name="Text">
/// Description, acceptance criteria and test steps, stripped of markup and run together. Enough
/// to answer "is this document name written down in this work item", which is the only question
/// asked of it.
/// </param>
public sealed record AdoWorkItemText(int Id, string Title, string Text);

public interface IAdoClient
{
    /// <summary>
    /// Work items whose TITLE contains this phrase. Titles only: the on-prem server answers
    /// TF401349 to a CONTAINS WORDS search over descriptions, so full text is not available.
    /// </summary>
    Task<IReadOnlyList<AdoHit>> FindByTitleAsync(string phrase, CancellationToken ct);

    /// <summary>
    /// The spreadsheets attached to the work items a phrase finds — the data dictionaries and
    /// document lists, which is where the answer lives when no title carries it.
    /// </summary>
    Task<IReadOnlyList<AdoAttachment>> FindSpreadsheetsAsync(string phrase, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AdoAttachment>>(Array.Empty<AdoAttachment>());

    /// <summary>
    /// Work items whose title matches a phrase, with their bodies read out as plain text.
    ///
    /// This is how a description is searched on a server that refuses to search one: WIQL picks
    /// the candidates by title — the one thing it will match — and the bodies are fetched and
    /// read here. The document lists live in those bodies and in no title anywhere.
    /// </summary>
    Task<IReadOnlyList<AdoWorkItemText>> FindCandidatesAsync(string phrase, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AdoWorkItemText>>(Array.Empty<AdoWorkItemText>());

    /// <summary>Saves one attachment. False when it could not be fetched, with the reason said.</summary>
    Task<bool> DownloadAttachmentAsync(AdoAttachment attachment, string toPath, CancellationToken ct)
        => Task.FromResult(false);

    /// <summary>The address a person can open in a browser.</summary>
    string LinkTo(int workItemId) => workItemId.ToString();
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

    /// <summary>
    /// Over a handler of your choosing, so the wire can be faked. The NTLM constructor above
    /// builds its own handler and is what the application uses.
    /// </summary>
    public AdoClient(HttpMessageHandler handler, string collectionUrl, string project)
    {
        _project = project;

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

    /// <summary>
    /// How many work items are worth reading in full for one phrase. A service's name matches a
    /// few dozen stories and tests; past that the phrase was too general to be worth the wait.
    /// </summary>
    public const int MostCandidates = 60;

    private const string BodyFields =
        "System.Title,System.Description,Microsoft.VSTS.Common.AcceptanceCriteria," +
        "Microsoft.VSTS.TCM.Steps";

    public async Task<IReadOnlyList<AdoWorkItemText>> FindCandidatesAsync(
        string phrase, CancellationToken ct)
    {
        var ids = await SearchAsync(phrase, ct);
        if (ids.Count == 0) return Array.Empty<AdoWorkItemText>();

        var found = new List<AdoWorkItemText>();

        foreach (var batch in ids.Take(MostCandidates).Chunk(BatchSize))
        {
            var url = $"{Uri.EscapeDataString(_project)}/_apis/wit/workitems" +
                      $"?ids={string.Join(',', batch)}&fields={BodyFields}&api-version=6.0";

            using var response = await _http.GetAsync(url, ct);

            // A field this work item type does not have makes the whole batch a 400 on some
            // servers. Asking for fewer fields would lose the descriptions, which are the point,
            // so a refused batch is skipped rather than allowed to end the search.
            if (!response.IsSuccessStatusCode) continue;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!json.RootElement.TryGetProperty("value", out var items)) continue;

            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
                if (!item.TryGetProperty("fields", out var fields)) continue;

                var text = new StringBuilder();

                foreach (var field in new[]
                         {
                             "System.Description",
                             "Microsoft.VSTS.Common.AcceptanceCriteria",
                             "Microsoft.VSTS.TCM.Steps"
                         })
                {
                    if (fields.TryGetProperty(field, out var value))
                        text.Append(PlainText(value.GetString())).Append(' ');
                }

                found.Add(new AdoWorkItemText(id, TitleIn(fields), text.ToString().Trim()));
            }
        }

        return found;
    }

    private static string TitleIn(JsonElement fields) =>
        fields.TryGetProperty("System.Title", out var t) ? t.GetString() ?? string.Empty : string.Empty;

    /// <summary>
    /// Markup out, words in.
    ///
    /// Descriptions are HTML. Test steps are XML whose text content is HTML that was encoded a
    /// second time on the way in, so one pass leaves "&lt;P&gt;Upload…" sitting in the middle of
    /// the result. Stripping, decoding and stripping again covers both without needing to know
    /// which field it was handed.
    /// </summary>
    private static string PlainText(string? markup)
    {
        if (string.IsNullOrEmpty(markup)) return string.Empty;

        var text = WebUtility.HtmlDecode(Regex.Replace(markup, "<[^>]+>", " "));
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public async Task<IReadOnlyList<AdoAttachment>> FindSpreadsheetsAsync(
        string phrase, CancellationToken ct)
    {
        var ids = await SearchAsync(phrase, ct);
        var found = new List<AdoAttachment>();

        // Relations have to be asked for one work item at a time, so only the first few are
        // looked at. Any more and this becomes a minute of waiting for a list nobody reads.
        foreach (var id in ids.Take(MostWeWillOpen))
        {
            using var response = await _http.GetAsync(
                $"{Uri.EscapeDataString(_project)}/_apis/wit/workitems/{id}" +
                "?$expand=relations&api-version=6.0", ct);

            if (!response.IsSuccessStatusCode) continue;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;

            var title = root.TryGetProperty("fields", out var fields) &&
                        fields.TryGetProperty("System.Title", out var t)
                ? t.GetString() ?? string.Empty
                : string.Empty;

            if (!root.TryGetProperty("relations", out var relations)) continue;

            foreach (var relation in relations.EnumerateArray())
            {
                if (!relation.TryGetProperty("rel", out var rel) ||
                    rel.GetString() != "AttachedFile") continue;

                var url = relation.TryGetProperty("url", out var u) ? u.GetString() : null;
                var name = relation.TryGetProperty("attributes", out var attributes) &&
                           attributes.TryGetProperty("name", out var n)
                    ? n.GetString()
                    : null;

                if (url is null || name is null || !IsSpreadsheet(name)) continue;

                found.Add(new AdoAttachment(id, title, name, url));
            }
        }

        return found;
    }

    public async Task<bool> DownloadAttachmentAsync(
        AdoAttachment attachment, string toPath, CancellationToken ct)
    {
        using var response = await _http.GetAsync(attachment.Url, ct);
        if (!response.IsSuccessStatusCode) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(toPath)!);

        await using var file = File.Create(toPath);
        await response.Content.CopyToAsync(file, ct);
        return true;
    }

    public string LinkTo(int workItemId) =>
        $"{_http.BaseAddress}{Uri.EscapeDataString(_project)}/_workitems/edit/{workItemId}";

    /// <summary>Work items opened for their attachments before giving up on a phrase.</summary>
    private const int MostWeWillOpen = 12;

    private static bool IsSpreadsheet(string name) =>
        name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

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
