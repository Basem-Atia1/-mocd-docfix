using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// A 503 from CRM's gateway means come back shortly. One of those arriving at the PATCH — after
/// the file is already on the server — used to end the run and leave a copy nothing points at.
/// </summary>
public class RetryTransientTests : IDisposable
{
    private readonly FakeHttpMessageHandler _inner = new();
    private readonly List<TimeSpan> _waited = new();
    private readonly List<HttpClient> _clients = new();

    /// <summary>
    /// Disposed rather than left to a finalizer. Thirteen undisposed HttpClients took the whole
    /// suite from eight seconds to fifty-two — the cost lands at process exit, where no single
    /// test is ever blamed for it.
    /// </summary>
    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
    }

    private HttpClient Client(out RetryTransient retry)
    {
        retry = new RetryTransient(_inner, (pause, _) =>
        {
            _waited.Add(pause);
            return Task.CompletedTask;                 // tests must not actually sleep
        });

        var client = new HttpClient(retry) { BaseAddress = new Uri("https://crm/api/") };
        _clients.Add(client);

        return client;
    }

    private static HttpRequestMessage Patch() =>
        new(HttpMethod.Patch, "mocd_documentfiles(1)")
        {
            Content = new StringContent("""{"mocd_filepath":"x"}""",
                System.Text.Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task A_patch_refused_with_503_is_sent_again_and_succeeds()
    {
        _inner.Enqueue(HttpStatusCode.ServiceUnavailable, "<html>unavailable</html>");
        _inner.Enqueue(HttpStatusCode.NoContent, "");

        var http = Client(out var retry);

        var response = await http.SendAsync(Patch());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, retry.Sent);
        Assert.Single(_waited);
    }

    /// <summary>The body has to survive being sent a second time, or the retry writes nothing.</summary>
    [Fact]
    public async Task The_body_is_sent_again_with_the_retry()
    {
        _inner.Enqueue(HttpStatusCode.ServiceUnavailable, "");
        _inner.Enqueue(HttpStatusCode.NoContent, "");

        await Client(out _).SendAsync(Patch());

        Assert.Equal(2, _inner.RequestBodies.Count);
        Assert.All(_inner.RequestBodies, b => Assert.Contains("mocd_filepath", b));
    }

    [Fact]
    public async Task It_gives_up_after_three_tries_and_hands_back_what_the_server_said()
    {
        for (var i = 0; i < 3; i++) _inner.Enqueue(HttpStatusCode.ServiceUnavailable, "still down");

        var http = Client(out var retry);

        var response = await http.SendAsync(Patch());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, retry.Sent);
        Assert.Equal(2, _waited.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task The_other_come_back_shortly_answers_are_retried_too(HttpStatusCode status)
    {
        _inner.Enqueue(status, "");
        _inner.Enqueue(HttpStatusCode.OK, "{}");

        var http = Client(out var retry);

        await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "mocd_documents"));

        Assert.Equal(2, retry.Sent);
    }

    /// <summary>
    /// A refusal is not a blip. Asking again would only be refused again, more slowly.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refusal_is_not_retried(HttpStatusCode status)
    {
        _inner.Enqueue(status, "no");

        var http = Client(out var retry);

        var response = await http.SendAsync(Patch());

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, retry.Sent);
        Assert.Empty(_waited);
    }

    /// <summary>
    /// The one that must never repeat. A second upload puts a second copy of the file on the
    /// server, and a second create makes a second record.
    /// </summary>
    [Fact]
    public async Task A_post_is_never_sent_twice()
    {
        _inner.Enqueue(HttpStatusCode.ServiceUnavailable, "");

        var http = Client(out var retry);

        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "x")
        {
            Content = new StringContent("{}")
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, retry.Sent);
    }

    [Fact]
    public async Task It_waits_two_seconds_then_five()
    {
        for (var i = 0; i < 3; i++) _inner.Enqueue(HttpStatusCode.ServiceUnavailable, "");

        await Client(out _).SendAsync(Patch());

        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) }, _waited);
    }
}
