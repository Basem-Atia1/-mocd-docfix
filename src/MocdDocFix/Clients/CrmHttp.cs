using System.Net;
using MocdDocFix.Config;

namespace MocdDocFix.Clients;

public static class CrmHttp
{
    /// <summary>
    /// On-prem Dataverse is v9.1 maximum — v9.2 returns HTTP 501. NTLM is the only
    /// supported scheme here.
    /// </summary>
    public static HttpClient Create(ResolvedEnvironment env)
    {
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(env.CrmUser, env.CrmPassword, env.CrmDomain),
            PreAuthenticate = true
        };

        // A 503 from the gateway means come back shortly, and one of those must not end a run of
        // four hundred documents — least of all at the PATCH, which happens after the file is
        // already on the server. Reads and PATCHes are tried again; a POST never is.
        var http = new HttpClient(new RetryTransient(handler))
        {
            BaseAddress = new Uri($"{env.CrmUrl}/api/data/v9.1/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        http.DefaultRequestHeaders.Add("OData-Version", "4.0");
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        return http;
    }
}
