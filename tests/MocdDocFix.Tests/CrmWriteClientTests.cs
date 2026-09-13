using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class CrmWriteClientTests
{
    private static readonly Guid NewFileId  = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");

    private static (CrmWriteClient client, FakeHttpMessageHandler handler) Build()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://crm/MoCD/api/data/v9.1/") };
        return (new CrmWriteClient(http), handler);
    }

    private static readonly Dictionary<string, object?> Attributes = new()
    {
        ["mocd_filepath"] = @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\a41c0b77.jpg",
        ["mocd_hash"] = "e57d1555e2197c964daa9fd57e197b7b",
        ["mocd_mediatype"] = "image/jpeg",
        ["mocd_name"] = "cert.jpg"
    };

    [Fact]
    public async Task CreateDocumentFile_posts_the_chosen_key_when_one_is_given()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        var id = await client.CreateDocumentFileAsync(NewFileId, Attributes, CancellationToken.None);

        Assert.Equal(NewFileId, id);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("mocd_documentfiles", handler.Requests[0].RequestUri!.ToString());

        var body = handler.RequestBodies[0];
        Assert.Contains("\"mocd_documentfileid\":\"a41c0b77-1111-2222-3333-444444444444\"", body);
        Assert.Contains("\"mocd_hash\":\"e57d1555e2197c964daa9fd57e197b7b\"", body);
        Assert.Contains("\"mocd_mediatype\":\"image/jpeg\"", body);
    }

    [Fact]
    public async Task CreateDocumentFile_sends_no_key_when_crm_should_generate_one()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NoContent, "");
        handler.ResponseHeaders["OData-EntityId"] =
            "https://crm/MoCD/api/data/v9.1/mocd_documentfiles(7d8e2c11-aaaa-bbbb-cccc-999999999999)";

        var id = await client.CreateDocumentFileAsync(null, Attributes, CancellationToken.None);

        Assert.Equal(Guid.Parse("7d8e2c11-aaaa-bbbb-cccc-999999999999"), id);
        Assert.DoesNotContain("mocd_documentfileid", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task A_generated_key_that_never_comes_back_stops_the_run()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NoContent, "");     // no OData-EntityId header

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateDocumentFileAsync(null, Attributes, CancellationToken.None));

        Assert.Contains("did not return its id", ex.Message);
    }

    [Fact]
    public async Task RepointDocument_patches_with_an_odata_bind()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        await client.RepointDocumentAsync(DocumentId, NewFileId, CancellationToken.None);

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Contains($"mocd_documents({DocumentId})", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains($"\"mocd_documentfile@odata.bind\":\"/mocd_documentfiles({NewFileId})\"",
                        handler.RequestBodies[0]);
    }

    [Fact]
    public async Task GetDocumentFileLink_reads_back_the_lookup()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK,
            $$"""{"_mocd_documentfile_value":"{{NewFileId}}"}""");

        var linked = await client.GetDocumentFileLinkAsync(DocumentId, CancellationToken.None);

        Assert.Equal(NewFileId, linked);
    }

    [Fact]
    public async Task GetDocumentFileLink_returns_null_when_the_lookup_is_empty()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"_mocd_documentfile_value":null}""");

        Assert.Null(await client.GetDocumentFileLinkAsync(DocumentId, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteDocumentFile_issues_a_DELETE()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        await client.DeleteDocumentFileAsync(NewFileId, CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Contains($"mocd_documentfiles({NewFileId})", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task A_failed_write_throws_with_the_server_message()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"duplicate key"}}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RepointDocumentAsync(DocumentId, NewFileId, CancellationToken.None));

        Assert.Contains("duplicate key", ex.Message);
        Assert.Contains("400", ex.Message);
    }
}
