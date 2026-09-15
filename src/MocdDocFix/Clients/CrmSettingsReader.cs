using System.Text.Json;
using MocdDocFix.Config;

namespace MocdDocFix.Clients;

/// <param name="FileServiceBaseUrl">mocd_crmconfiguration / UploadDocumentAPIURL.</param>
/// <param name="ApiKey">mocd_crmconfiguration / UploadDocumentApiKey.</param>
/// <param name="Problem">Why nothing came back, when nothing did.</param>
public sealed record CrmSettings(string? FileServiceBaseUrl, string? ApiKey, string? Problem)
{
    public bool Found => FileServiceBaseUrl is not null || ApiKey is not null;
}

/// <summary>
/// Reads the file service's address and key out of CRM, where the platform itself keeps them.
///
/// The upload plugin looks them up this way on every call (UploadDocument.cs, GetCRMConfiguration),
/// so these are the values in use rather than a copy that can drift. It matters at setup time
/// because the API key is the one thing a new operator cannot work out for themselves — it is a
/// shared secret with no relation to their CRM login — and having to ask a colleague for it is
/// how a key ends up pasted into a chat window.
///
/// Reads two rows. Writes nothing, ever.
/// </summary>
public sealed class CrmSettingsReader
{
    public const string UrlKey = "UploadDocumentAPIURL";
    public const string KeyKey = "UploadDocumentApiKey";

    private readonly HttpClient _http;

    public CrmSettingsReader(HttpClient http) => _http = http;

    public async Task<CrmSettings> ReadAsync(CancellationToken ct)
    {
        try
        {
            var url = await ValueOfAsync(UrlKey, ct);
            var key = await ValueOfAsync(KeyKey, ct);

            if (url is null && key is null)
                return new CrmSettings(null, null,
                    $"CRM has no '{UrlKey}' or '{KeyKey}' row in mocd_crmconfiguration.");

            return new CrmSettings(url?.TrimEnd('/'), key, null);
        }
        catch (Exception problem) when (problem is HttpRequestException or TaskCanceledException
                                            or JsonException or InvalidOperationException)
        {
            // Setting up is not the moment to fail hard: the operator can always type the two
            // values in by hand, and being told why is more use than a stack trace.
            return new CrmSettings(null, null, Innermost(problem).Message);
        }
    }

    private async Task<string?> ValueOfAsync(string name, CancellationToken ct)
    {
        var url = $"mocd_crmconfigurations?$select=mocd_value&$filter=mocd_name eq '{Uri.EscapeDataString(name)}'&$top=1";

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        if (!json.RootElement.TryGetProperty("value", out var rows) || rows.GetArrayLength() == 0)
            return null;

        var value = rows[0].TryGetProperty("mocd_value", out var cell) && cell.ValueKind == JsonValueKind.String
            ? cell.GetString()
            : null;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static Exception Innermost(Exception problem) => problem switch
    {
        { InnerException: { } inner } => Innermost(inner),
        _ => problem
    };

    /// <summary>
    /// A client for CRM alone, built from what the operator has just typed. The file service's
    /// key is exactly what is missing at this point, and CRM does not need it.
    /// </summary>
    public static HttpClient HttpFor(string crmUrl, string domain, string user, string password) =>
        CrmHttp.Create(new ResolvedEnvironment(
            Name: "setup",
            FileServiceBaseUrl: "", UploadUrl: "", DownloadUrlPrefix: "", DeleteUrlPrefix: "",
            ApiKey: "",
            CrmUrl: crmUrl.TrimEnd('/'),
            CrmDomain: domain, CrmUser: user, CrmPassword: password,
            IsProduction: false));
}
