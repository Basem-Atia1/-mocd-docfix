using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The file service's address and key live in CRM, in mocd_crmconfiguration, which is where the
/// upload plugin reads them on every call. Fetching them at setup removes the one thing a new
/// operator cannot work out alone — the API key is a shared secret unrelated to their login, and
/// having to ask a colleague for it is how such a key ends up pasted into a chat window.
/// </summary>
public class CrmSettingsReaderTests
{
    private static (CrmSettingsReader reader, FakeHttpMessageHandler handler) Build()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://crm/MoCD/api/data/v9.1/") };
        return (new CrmSettingsReader(http), handler);
    }

    private static string Row(string value) =>
        $$"""{"value":[{"mocd_value":"{{value}}"}]}""";

    private const string Empty = """{"value":[]}""";

    [Fact]
    public async Task Both_values_are_read_from_crm()
    {
        var (reader, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, Row("http://files.example:83"));
        handler.Enqueue(HttpStatusCode.OK, Row("THE-API-KEY"));

        var settings = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("http://files.example:83", settings.FileServiceBaseUrl);
        Assert.Equal("THE-API-KEY", settings.ApiKey);
        Assert.True(settings.Found);
        Assert.Null(settings.Problem);
    }

    /// <summary>It asks for the same two rows the plugin asks for, by name.</summary>
    [Fact]
    public async Task It_asks_for_the_rows_the_plugin_uses()
    {
        var (reader, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, Empty);
        handler.Enqueue(HttpStatusCode.OK, Empty);

        await reader.ReadAsync(CancellationToken.None);

        var asked = string.Join(" ", handler.Requests.Select(r => Uri.UnescapeDataString(r.RequestUri!.ToString())));
        Assert.Contains("mocd_crmconfigurations", asked);
        Assert.Contains(CrmSettingsReader.UrlKey, asked);
        Assert.Contains(CrmSettingsReader.KeyKey, asked);
    }

    /// <summary>A trailing slash would double up when the upload URL is built onto it.</summary>
    [Fact]
    public async Task A_trailing_slash_on_the_url_is_trimmed()
    {
        var (reader, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, Row("http://files.example:83/"));
        handler.Enqueue(HttpStatusCode.OK, Row("KEY"));

        Assert.Equal("http://files.example:83",
            (await reader.ReadAsync(CancellationToken.None)).FileServiceBaseUrl);
    }

    [Fact]
    public async Task One_value_present_is_still_worth_offering()
    {
        var (reader, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, Empty);
        handler.Enqueue(HttpStatusCode.OK, Row("KEY"));

        var settings = await reader.ReadAsync(CancellationToken.None);

        Assert.Null(settings.FileServiceBaseUrl);
        Assert.Equal("KEY", settings.ApiKey);
        Assert.True(settings.Found);
    }

    [Fact]
    public async Task Neither_row_present_is_explained_rather_than_guessed_at()
    {
        var (reader, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, Empty);
        handler.Enqueue(HttpStatusCode.OK, Empty);

        var settings = await reader.ReadAsync(CancellationToken.None);

        Assert.False(settings.Found);
        Assert.Contains("mocd_crmconfiguration", settings.Problem);
    }

    /// <summary>
    /// Setting up is not the moment to fail hard. A wrong password, a closed VPN or a CRM that
    /// refuses the query all end the same way: say so, and let the operator type the values in.
    /// </summary>
    [Fact]
    public async Task A_crm_that_refuses_the_query_is_not_fatal()
    {
        var (reader, handler) = Build();
        handler.Enqueue(HttpStatusCode.Unauthorized, "no");
        handler.Enqueue(HttpStatusCode.Unauthorized, "no");

        var settings = await reader.ReadAsync(CancellationToken.None);

        Assert.False(settings.Found);
        Assert.NotNull(settings.Problem);
    }

    [Fact]
    public async Task A_connection_that_fails_is_reported_not_thrown()
    {
        var http = new HttpClient(new AlwaysThrows()) { BaseAddress = new Uri("https://crm/api/") };

        var settings = await new CrmSettingsReader(http).ReadAsync(CancellationToken.None);

        Assert.False(settings.Found);
        Assert.Contains("no route", settings.Problem, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AlwaysThrows : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("An error occurred while sending the request.",
                new IOException("no route to host"));
    }
}
