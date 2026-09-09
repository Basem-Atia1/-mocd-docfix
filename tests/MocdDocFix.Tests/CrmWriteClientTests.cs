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

    [Fact]
    public async Task CreateDocumentFile_posts_the_vendor_FileId_as_the_primary_key()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        await client.CreateDocumentFileAsync(
            NewFileId,
            @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\a41c0b77-1111-2222-3333-444444444444.jpg",
            "e57d1555e2197c964daa9fd57e197b7b", "cert.jpg", "image/jpeg",
            "cd97bf8d-bea8-f011-b116-005056010908", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("mocd_documentfiles", handler.Requests[0].RequestUri!.ToString());

        var body = handler.RequestBodies[0];
        Assert.Contains("\"mocd_documentfileid\":\"a41c0b77-1111-2222-3333-444444444444\"", body);
        Assert.Contains("\"mocd_hash\":\"e57d1555e2197c964daa9fd57e197b7b\"", body);
        Assert.Contains("\"mocd_mediatype\":\"image/jpeg\"", body);
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
