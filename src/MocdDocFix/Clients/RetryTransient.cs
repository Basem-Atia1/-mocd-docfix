using System.Net;
using System.Net.Http.Headers;

namespace MocdDocFix.Clients;

/// <summary>
/// Tries again when the other end says "not now".
///
/// A 503 from CRM's gateway arrives after the file is already on the server, and it means come
/// back shortly — not that anything is wrong with the request. Letting one of those end a run of
/// four hundred documents, and leave a copy on the file server that nothing points at, is a
/// harsh answer to a blip.
///
/// **Only methods that can be repeated safely.** GET and PATCH say the same thing however many
/// times they are sent; POST does not, and retrying an upload or a create would make a second
/// one. So a POST that fails, fails — deliberately.
/// </summary>
public sealed class RetryTransient : DelegatingHandler
{
    /// <summary>
    /// Three in total, not three extra. Enough to ride out a restart or a moment of load;
    /// not so many that a system which is genuinely down takes minutes to say so.
    /// </summary>
    private const int Attempts = 3;

    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <param name="wait">How to pause between tries. Injected so tests do not sleep.</param>
    public RetryTransient(HttpMessageHandler inner,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
        : base(inner) =>
        _wait = wait ?? Task.Delay;

    /// <summary>How many requests this handler has actually sent. For tests and diagnosis.</summary>
    public int Sent { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (!CanBeRepeated(request.Method))
        {
            Sent++;
            return await base.SendAsync(request, ct);
        }

        // Read once and kept: a request that has been sent cannot be sent again, so each try
        // needs its own message, and its own copy of the body with it.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var type = request.Content?.Headers.ContentType;

        for (var attempt = 1; ; attempt++)
        {
            var lastTry = attempt == Attempts;

            try
            {
                Sent++;
                var response = await base.SendAsync(Copy(request, body, type), ct);

                if (lastTry || !IsTransient(response.StatusCode)) return response;

                // Not returned, so nobody will dispose it for us.
                response.Dispose();
            }
            catch (Exception problem) when (!lastTry && IsTransient(problem, ct))
            {
                // Swallowed only to try again; the last attempt's exception is thrown as it is.
            }

            await _wait(Pause(attempt), ct);
        }
    }

    /// <summary>
    /// Whether sending this twice means the same as sending it once.
    ///
    /// PATCH sets named columns to given values, so a repeat is the same statement. GET reads.
    /// POST creates, and DELETE here is a GET the vendor happens to have called delete — asking
    /// twice for a file to be gone is also the same statement.
    /// </summary>
    private static bool CanBeRepeated(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Patch || method == HttpMethod.Delete;

    /// <summary>Come back shortly, rather than do not ask again.</summary>
    private static bool IsTransient(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,       // 408
        HttpStatusCode.TooManyRequests => true,      // 429
        HttpStatusCode.BadGateway => true,           // 502
        HttpStatusCode.ServiceUnavailable => true,   // 503
        HttpStatusCode.GatewayTimeout => true,       // 504
        _ => false
    };

    /// <summary>
    /// A connection that dropped or a request that timed out. A cancellation the operator asked
    /// for is neither, and must not be retried into carrying on after they said stop.
    /// </summary>
    private static bool IsTransient(Exception problem, CancellationToken ct) => problem switch
    {
        OperationCanceledException => !ct.IsCancellationRequested,
        HttpRequestException => true,
        _ => false
    };

    /// <summary>Two seconds, then five. Long enough for a restart, short enough to watch.</summary>
    private static TimeSpan Pause(int attempt) => TimeSpan.FromSeconds(attempt == 1 ? 2 : 5);

    private static HttpRequestMessage Copy(
        HttpRequestMessage from, byte[]? body, MediaTypeHeaderValue? type)
    {
        var copy = new HttpRequestMessage(from.Method, from.RequestUri) { Version = from.Version };

        foreach (var header in from.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (body is null) return copy;

        copy.Content = new ByteArrayContent(body);
        if (type is not null) copy.Content.Headers.ContentType = type;

        return copy;
    }
}
