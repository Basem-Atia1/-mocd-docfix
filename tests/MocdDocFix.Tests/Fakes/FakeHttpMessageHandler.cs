using System.Net;

namespace MocdDocFix.Tests.Fakes;

/// <summary>Records every request and replies with a queued response.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    /// <summary>Headers put on every queued response — CRM returns a new key in one of them.</summary>
    public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeHttpMessageHandler Enqueue(HttpStatusCode status, string json)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

        var response = _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)
              { Content = new StringContent("no response queued") };

        foreach (var (name, value) in ResponseHeaders) response.Headers.TryAddWithoutValidation(name, value);

        return response;
    }
}
