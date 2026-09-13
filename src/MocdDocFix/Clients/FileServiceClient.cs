using System.Text;
using System.Text.Json;
using MocdDocFix.Config;

namespace MocdDocFix.Clients;

public interface IFileServiceClient
{
    Task<ApiResponse<FileData>> DownloadAsync(string filePath, CancellationToken ct);
    Task<ApiResponse<FileData>> UploadAsync(UploadRequest request, CancellationToken ct);
    Task<ApiResponse<bool>> DeleteAsync(string filePath, CancellationToken ct);
}

public sealed class FileServiceClient : IFileServiceClient
{
    /// <summary>
    /// Case-insensitive on purpose. Production reads these same responses with Newtonsoft, which
    /// matches property names case-insensitively; System.Text.Json does not by default, and a
    /// camelCase body parsed silently into all-defaults — so a good download looked like a
    /// failure with no message at all.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ResolvedEnvironment _env;

    public FileServiceClient(HttpClient http, ResolvedEnvironment env)
    {
        _http = http;
        _env = env;
    }

    public async Task<ApiResponse<FileData>> DownloadAsync(string filePath, CancellationToken ct)
    {
        var response = await SendAsync<FileData>(
            HttpMethod.Get, _env.DownloadUrlPrefix + filePath, content: null, ct);

        // A download carrying no bytes is a failure however the vendor labelled it. Saying so
        // here spares every caller from distinguishing "failed" from "succeeded but empty".
        if (response.Success && string.IsNullOrEmpty(response.Data?.File))
            return ApiResponse<FileData>.Fail(
                $"The file server reported success but returned no file content for '{filePath}'.");

        return response;
    }

    public Task<ApiResponse<bool>> DeleteAsync(string filePath, CancellationToken ct) =>
        // GET, deliberately — that is what the vendor exposes (spec section 3.4).
        SendAsync<bool>(HttpMethod.Get, _env.DeleteUrlPrefix + filePath, content: null, ct);

    public Task<ApiResponse<FileData>> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        var body = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        return SendAsync<FileData>(HttpMethod.Post, _env.UploadUrl, body, ct);
    }

    private async Task<ApiResponse<T>> SendAsync<T>(
        HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        try
        {
            // Build the Uri from the raw string so backslashes in ?path= survive exactly as
            // FileService.cs:65 sends them — the live app does not encode them.
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Absolute));
            request.Headers.Add("Apikey", _env.ApiKey);
            if (content is not null) request.Content = content;

            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return ApiResponse<T>.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(text)}");

            if (string.IsNullOrWhiteSpace(text))
                return ApiResponse<T>.Fail(
                    $"HTTP {(int)response.StatusCode} with an empty body — nothing to read.");

            try
            {
                var parsed = JsonSerializer.Deserialize<ApiResponse<T>>(text, Json);
                if (parsed is null)
                    return ApiResponse<T>.Fail("Could not parse the response body (null).");

                // A failure the vendor did not explain is still a failure we have to explain.
                // Without this the caller gets "Download failed: " and no way to tell why.
                if (!parsed.Success && string.IsNullOrWhiteSpace(parsed.Message))
                {
                    var errors = parsed.Errors is { Count: > 0 }
                        ? " " + string.Join("; ", parsed.Errors)
                        : string.Empty;

                    return parsed with
                    {
                        Message = $"The file server returned HTTP {(int)response.StatusCode} but " +
                                  $"reported failure with no message.{errors} Body: {Truncate(text)}"
                    };
                }

                return parsed;
            }
            catch (JsonException ex)
            {
                return ApiResponse<T>.Fail($"Could not parse the response body: {ex.Message}. Body: {Truncate(text)}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResponse<T>.Fail($"Request failed: {ex.Message}");
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
