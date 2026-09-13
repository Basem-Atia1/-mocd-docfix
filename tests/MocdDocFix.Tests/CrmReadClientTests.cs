using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Config;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class CrmReadClientTests
{
    private static ResolvedEnvironment Env() => new(
        "dev", "http://files", "http://files/api/File/Upload",
        "http://files/api/File/Download?path=", "http://files/api/File/Delete?path=",
        "KEY", "https://crm/MoCD", "MOCD", "svc", "pw", IsProduction: false);

    private static (CrmReadClient client, FakeHttpMessageHandler handler) Build()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://crm/MoCD/api/data/v9.1/") };
        return (new CrmReadClient(http, Env()), handler);
    }

    private const string OneDocument = """
    {"value":[{
      "mocd_documentid":"2c9d5572-a77b-f111-b10f-00505601095a",
      "mocd_name":"cert.jpg",
      "modifiedon":"2026-07-09T10:11:12Z",
      "mocd_documentfile":{
        "mocd_documentfileid":"98f9e3b2-867d-49ed-9af4-57cc938ee1f9",
        "mocd_filepath":"DigitalServices\\goodConductCertificate\\20260330\\98f9e3b2-867d-49ed-9af4-57cc938ee1f9.jpg",
        "mocd_hash":"e57d1555e2197c964daa9fd57e197b7b",
        "mocd_name":"cert.jpg",
        "mocd_mediatype":"image/jpeg"},
      "mocd_documenttype":{
        "mocd_documenttypeid":"6e79d291-722b-f111-b119-005056010908",
        "mocd_name":"Payment Receipt",
        "_mocd_servicecatalogue_value":"6bcb221c-6c2b-f111-b119-005056010908"},
      "mocd_employeeappintmentrequest":null,
      "mocd_gamrequest":null,
      "mocd_BylawsAmendmentRequestId":null
    }]}
    """;

    [Fact]
    public async Task GetInScopeDocuments_maps_a_row_completely()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, OneDocument);

        var rows = await client.GetInScopeDocumentsAsync(
            new[] { Guid.Parse("6bcb221c-6c2b-f111-b119-005056010908") }, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"), row.DocumentId);
        Assert.Equal(Guid.Parse("98f9e3b2-867d-49ed-9af4-57cc938ee1f9"), row.DocumentFileId);
        Assert.Equal("e57d1555e2197c964daa9fd57e197b7b", row.Hash);
        Assert.Equal("image/jpeg", row.MediaType);
        Assert.Equal("Payment Receipt", row.DocumentTypeName);
        Assert.Equal(Guid.Parse("6bcb221c-6c2b-f111-b119-005056010908"), row.DocTypeCatalogueId);
        Assert.Null(row.CrossCheckCatalogueId);
        Assert.EndsWith(".jpg", row.FilePath);
    }

    [Fact]
    public async Task The_filter_ors_every_catalogue_and_expands_the_cross_checks()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await client.GetInScopeDocumentsAsync(
            new[] { Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
                    Guid.Parse("930f636a-077a-f111-b119-005056010908") }, CancellationToken.None);

        var url = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("mocd_documenttype/_mocd_servicecatalogue_value eq cd97bf8d-bea8-f011-b116-005056010908", url);
        Assert.Contains(" or ", url);
        Assert.Contains("mocd_employeeappintmentrequest($select=_mocd_servicecatalogue_value)", url);
        Assert.Contains("mocd_gamrequest($select=_mocd_servicecatalogue_value)", url);
        Assert.Contains("mocd_BylawsAmendmentRequestId($select=_mocd_servicecatalogue_value)", url);
    }

    [Fact]
    public async Task Cross_check_catalogue_and_source_are_picked_up_from_whichever_parent_has_one()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """
        {"value":[{
          "mocd_documentid":"11111111-1111-1111-1111-111111111111",
          "mocd_name":"a.pdf","modifiedon":"2026-07-09T10:11:12Z",
          "mocd_documentfile":{"mocd_documentfileid":"22222222-2222-2222-2222-222222222222",
            "mocd_filepath":"DigitalServices\\0\\20260423\\x.pdf","mocd_hash":"h","mocd_name":"a.pdf","mocd_mediatype":"application/pdf"},
          "mocd_documenttype":{"mocd_documenttypeid":"33333333-3333-3333-3333-333333333333",
            "mocd_name":"Board Decision","_mocd_servicecatalogue_value":"cd97bf8d-bea8-f011-b116-005056010908"},
          "mocd_employeeappintmentrequest":{"_mocd_servicecatalogue_value":"cd97bf8d-bea8-f011-b116-005056010908"},
          "mocd_gamrequest":null,"mocd_BylawsAmendmentRequestId":null}]}
        """);

        var rows = await client.GetInScopeDocumentsAsync(new[] { Guid.NewGuid() }, CancellationToken.None);

        Assert.Equal(Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), rows[0].CrossCheckCatalogueId);
        Assert.Equal("mocd_employeeappintmentrequest", rows[0].CrossCheckSource);
    }

    [Fact]
    public async Task Paging_follows_odata_nextLink()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK,
            """{"value":[],"@odata.nextLink":"https://crm/MoCD/api/data/v9.1/mocd_documents?$skiptoken=abc"}""");
        handler.Enqueue(HttpStatusCode.OK, OneDocument);

        var rows = await client.GetInScopeDocumentsAsync(new[] { Guid.NewGuid() }, CancellationToken.None);

        Assert.Single(rows);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("$skiptoken=abc", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task IsServiceCatalogue_is_true_on_200_and_false_on_404()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"mocd_name":"GAM Request"}""");
        handler.Enqueue(HttpStatusCode.NotFound, """{"error":{"message":"Does Not Exist"}}""");

        Assert.True(await client.IsServiceCatalogueAsync("3ff27d73-653e-f111-b119-005056010908", CancellationToken.None));
        Assert.False(await client.IsServiceCatalogueAsync("9b1121f4-e30b-f111-b117-005056010908", CancellationToken.None));
    }

    [Fact]
    public async Task IsServiceCatalogue_is_false_for_a_non_guid_without_calling_the_server()
    {
        var (client, handler) = Build();

        Assert.False(await client.IsServiceCatalogueAsync("goodConductCertificate", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IsServiceCatalogue_caches_so_repeated_segments_cost_one_call()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"mocd_name":"GAM Request"}""");

        await client.IsServiceCatalogueAsync("3ff27d73-653e-f111-b119-005056010908", CancellationToken.None);
        await client.IsServiceCatalogueAsync("3ff27d73-653e-f111-b119-005056010908", CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResolveIdentifier_treats_a_document_guid_as_a_document()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, OneDocument);

        var rows = await client.ResolveIdentifierAsync("2c9d5572-a77b-f111-b10f-00505601095a", CancellationToken.None);

        Assert.Single(rows);
        var url = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("mocd_documentid eq 2c9d5572-a77b-f111-b10f-00505601095a", url);
    }

    [Fact]
    public async Task ResolveIdentifier_falls_back_to_documentfile_then_file_name()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");   // not a document id
        handler.Enqueue(HttpStatusCode.OK, OneDocument);          // matched as a documentfile id

        var rows = await client.ResolveIdentifierAsync("98f9e3b2-867d-49ed-9af4-57cc938ee1f9", CancellationToken.None);

        Assert.Single(rows);
        var second = Uri.UnescapeDataString(handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("_mocd_documentfile_value eq 98f9e3b2-867d-49ed-9af4-57cc938ee1f9", second);
    }

    [Fact]
    public async Task ResolveIdentifier_matches_a_plain_file_name()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, OneDocument);

        var rows = await client.ResolveIdentifierAsync("cert.jpg", CancellationToken.None);

        Assert.Single(rows);
        var url = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("mocd_documentfile/mocd_name eq 'cert.jpg'", url);
    }

    // --- snapshot completeness (spec section 8.2) ---------------------------

    [Fact]
    public async Task Raw_record_reads_ask_for_annotations_so_lookup_types_and_labels_survive()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"mocd_documentid":"x"}""");

        await client.GetRawRecordAsync("mocd_documents", Guid.NewGuid(), CancellationToken.None);

        // Without this header a polymorphic lookup comes back as a bare GUID with no entity
        // type, which cannot be restored.
        var prefer = handler.Requests[0].Headers.GetValues("Prefer").Single();
        Assert.Contains("odata.include-annotations", prefer);
        Assert.Contains("*", prefer);
    }

    [Fact]
    public async Task Raw_record_asks_for_every_attribute_with_no_select()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"mocd_documentfileid":"x"}""");
        var id = Guid.NewGuid();

        await client.GetRawRecordAsync("mocd_documentfiles", id, CancellationToken.None);

        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains($"mocd_documentfiles({id})", url);
        Assert.DoesNotContain("$select", url);
    }

    [Fact]
    public async Task Raw_record_returns_null_when_the_record_cannot_be_read()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NotFound, """{"error":{"message":"Does Not Exist"}}""");

        Assert.Null(await client.GetRawRecordAsync("mocd_documents", Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Annotations_are_fetched_for_the_document_without_their_bodies()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK,
            """{"value":[{"annotationid":"a1","filename":"logo.png","filesize":1234,"isdocument":true}]}""");
        var documentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");

        var json = await client.GetDocumentAnnotationsAsync(documentId, CancellationToken.None);

        Assert.NotNull(json);
        Assert.Contains("logo.png", json);

        var url = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString());
        Assert.Contains($"_objectid_value eq {documentId}", url);
        Assert.Contains("objecttypecode eq 'mocd_document'", url);
        // documentbody is a second copy of the file; metadata is what we need here.
        Assert.DoesNotContain("documentbody", url);
    }

    [Fact]
    public async Task Annotations_return_null_when_the_query_fails_so_the_gap_is_visible()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");

        Assert.Null(await client.GetDocumentAnnotationsAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
