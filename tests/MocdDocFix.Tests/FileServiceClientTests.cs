using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Config;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class FileServiceClientTests
{
    private const string OldPath =
        @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg";

    private static ResolvedEnvironment Env() => new(
        "dev", "http://files", "http://files/api/File/Upload",
        "http://files/api/File/Download?path=", "http://files/api/File/Delete?path=",
        "KEY123", "https://crm/MoCD", "MOCD", "svc", "pw", IsProduction: false);

    private static (FileServiceClient client, FakeHttpMessageHandler handler) Build()
    {
        var handler = new FakeHttpMessageHandler();
        return (new FileServiceClient(new HttpClient(handler), Env()), handler);
    }

    [Fact]
    public async Task Download_sends_the_path_unencoded_with_the_api_key()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK,
            """{"Success":true,"Message":null,"Errors":null,"Data":{"FileId":"00000000-0000-0000-0000-000000000001","FilePath":"p","Hash":"abc","File":"QUJD"}}""");

        var result = await client.DownloadAsync(OldPath, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("QUJD", result.Data!.File);

        var uri = handler.Requests[0].RequestUri!.OriginalString;
        Assert.Equal("http://files/api/File/Download?path=" + OldPath, uri);
        Assert.Contains(@"\", uri, StringComparison.Ordinal);          // backslashes survive
        Assert.DoesNotContain("%5C", uri, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("KEY123", handler.Requests[0].Headers.GetValues("Apikey").Single());
    }

    [Fact]
    public async Task Upload_posts_the_expected_body_shape()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK,
            """{"Success":true,"Data":{"FileId":"11111111-1111-1111-1111-111111111111","FilePath":"new","Hash":"h"}}""");

        var request = new UploadRequest(
            Category: "cd97bf8d-bea8-f011-b116-005056010908",
            FileName: "cert.jpg", File: "QUJD", MediaType: "image/jpeg",
            Extension: ".jpg", ApplicationId: Guid.Empty);

        var result = await client.UploadAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result.Data!.FileId);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);

        var body = handler.RequestBodies[0];
        Assert.Contains("\"Category\":\"cd97bf8d-bea8-f011-b116-005056010908\"", body);
        Assert.Contains("\"FileName\":\"cert.jpg\"", body);
        Assert.Contains("\"File\":\"QUJD\"", body);
    }

    [Fact]
    public async Task Delete_uses_GET_because_that_is_what_the_vendor_exposes()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"Success":true,"Data":true}""");

        var result = await client.DeleteAsync(OldPath, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("http://files/api/File/Delete?path=" + OldPath,
                     handler.Requests[0].RequestUri!.OriginalString);
    }

    [Fact]
    public async Task A_non_success_status_becomes_a_failed_ApiResponse_not_an_exception()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");

        var result = await client.DownloadAsync(OldPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("500", result.Message);
        Assert.NotEmpty(result.Errors!);
    }

    [Fact]
    public async Task Unparseable_json_becomes_a_failed_ApiResponse()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, "<html>not json</html>");

        var result = await client.DownloadAsync(OldPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("parse", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
