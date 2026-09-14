using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

public class FileRecordCopierTests
{
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid NewFileId = Guid.Parse("6288910e-0b46-4071-8903-17a12fbea3b1");
    private static readonly Guid DocumentId = Guid.Parse("28ef6a1c-cd1d-f111-b119-005056010908");

    // The vendor names the file after its own id, so the path's last segment is the file's name.
    private const string NewPath =
        @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260914\6288910e-0b46-4071-8903-17a12fbea3b1.png";

    /// <summary>A record as the portal leaves it: six columns, no mocd_fileid.</summary>
    private const string PortalRecord = """
        {"mocd_documentfileid":"b18f901e-0805-4f22-96e7-6ed8ae38f1c0",
         "mocd_name":"Application Summary.png",
         "mocd_mediatype":"image/png",
         "mocd_category":"9b1121f4-e30b-f111-b117-005056010908",
         "mocd_hash":"5e7ba19d",
         "mocd_filepath":"DigitalServices\\9b1121f4\\20260624\\b18f901e.png",
         "mocd_filesize":null,"mocd_extension":null,"mocd_filename":null,
         "mocd_applicationid":null,"mocd_fileid":null}
        """;

    /// <summary>A record as the plugin leaves it: ten columns, including mocd_fileid.</summary>
    private const string PluginRecord = """
        {"mocd_documentfileid":"2a1c51a3-e330-f111-b119-005056010908",
         "mocd_name":"Screenshot 2024-08-05.png",
         "mocd_fileid":"35687738-986f-413d-8846-6dc1a720a1ec",
         "mocd_filename":"35687738-986f-413d-8846-6dc1a720a1ec.png",
         "mocd_mediatype":"image/png",
         "mocd_extension":"png",
         "mocd_applicationid":"c0c2a661-e330-f111-b119-005056010908",
         "mocd_filesize":"4684",
         "mocd_hash":"021a49b9",
         "mocd_category":null,
         "mocd_filepath":"DigitalServices\\20260405\\35687738.png",
         "mocd_ismigrated":false}
        """;

    private static Dictionary<string, object?> Build(string oldRecord) =>
        FileRecordCopier.BuildPayload(oldRecord, NewFileId, NewPath, "NEWHASH", Correct,
            newVendorFileName: "6288910e-0b46-4071-8903-17a12fbea3b1.png");

    // ---- telling the two apart ----

    [Fact]
    public void A_record_with_mocd_fileid_is_plugin_created()
        => Assert.Equal(FileRecordStyle.Plugin, FileRecordCopier.StyleOf(PluginRecord));

    [Fact]
    public void A_record_without_it_is_portal_created()
        => Assert.Equal(FileRecordStyle.Portal, FileRecordCopier.StyleOf(PortalRecord));

    [Fact]
    public void A_record_we_could_not_read_is_treated_as_the_portal_shape()
    {
        Assert.Equal(FileRecordStyle.Portal, FileRecordCopier.StyleOf(null));
        Assert.Equal(FileRecordStyle.Portal, FileRecordCopier.StyleOf("<html>not json</html>"));
    }

    // ---- the id convention ----

    [Fact]
    public void A_portal_record_keeps_the_vendor_id_as_its_key()
        => Assert.Equal(NewFileId, FileRecordCopier.NewRecordId(FileRecordStyle.Portal, NewFileId));

    [Fact]
    public void A_plugin_record_lets_crm_generate_the_key()
        => Assert.Null(FileRecordCopier.NewRecordId(FileRecordStyle.Plugin, NewFileId));

    // ---- what the upload is told ----

    [Fact]
    public void The_extension_keeps_the_form_the_old_record_used()
    {
        Assert.Equal("png", FileRecordCopier.ExtensionFor(PluginRecord, ".png"));   // dotless
        Assert.Equal(".png", FileRecordCopier.ExtensionFor(PortalRecord, ".png"));  // falls back
    }

    [Fact]
    public void Only_a_plugin_upload_carries_the_document_id()
    {
        Assert.Equal(DocumentId, FileRecordCopier.ApplicationIdFor(FileRecordStyle.Plugin, DocumentId));
        Assert.Equal(Guid.Empty, FileRecordCopier.ApplicationIdFor(FileRecordStyle.Portal, DocumentId));
    }

    // ---- the payload: what changes ----

    [Fact]
    public void The_three_columns_that_describe_the_file_are_always_replaced()
    {
        var payload = Build(PortalRecord);

        Assert.Equal(NewPath, payload["mocd_filepath"]);
        Assert.Equal("NEWHASH", payload["mocd_hash"]);
        Assert.Equal(Correct.ToString(), payload["mocd_category"]);
    }

    [Fact]
    public void A_plugin_record_gets_the_new_vendor_id_and_name()
    {
        var payload = Build(PluginRecord);

        Assert.Equal(NewFileId.ToString(), payload["mocd_fileid"]);
        Assert.Equal("6288910e-0b46-4071-8903-17a12fbea3b1.png", payload["mocd_filename"]);
    }

    /// <summary>
    /// The old record's mocd_filename was its path's last segment, and the new one's must be too.
    /// Taking the vendor's fileName instead produced a record naming a file that does not exist:
    /// in dev it returned the bare id while the path it returned in the same response ended
    /// ".png". Several portal data services read this column as the name to show.
    /// </summary>
    [Fact]
    public void The_new_filename_is_the_new_paths_own_last_segment()
    {
        var payload = FileRecordCopier.BuildPayload(PluginRecord, NewFileId, NewPath, "NEWHASH",
            Correct, newVendorFileName: "6288910e-0b46-4071-8903-17a12fbea3b1");   // no extension

        Assert.Equal("6288910e-0b46-4071-8903-17a12fbea3b1.png", payload["mocd_filename"]);
    }

    [Fact]
    public void The_vendors_name_is_used_only_when_the_path_yields_nothing()
    {
        var payload = FileRecordCopier.BuildPayload(PluginRecord, NewFileId, "", "NEWHASH",
            Correct, newVendorFileName: "fallback.png");

        Assert.Equal("fallback.png", payload["mocd_filename"]);
    }

    [Fact]
    public void A_portal_record_is_not_given_columns_it_never_had()
    {
        var payload = Build(PortalRecord);

        Assert.False(payload.ContainsKey("mocd_fileid"));
        Assert.False(payload.ContainsKey("mocd_filename"));
        Assert.False(payload.ContainsKey("mocd_extension"));
        Assert.False(payload.ContainsKey("mocd_filesize"));
        Assert.False(payload.ContainsKey("mocd_applicationid"));
    }

    // ---- the payload: what survives ----

    [Fact]
    public void Everything_the_plugin_filled_in_is_carried_across()
    {
        var payload = Build(PluginRecord);

        Assert.Equal("Screenshot 2024-08-05.png", payload["mocd_name"]);
        Assert.Equal("image/png", payload["mocd_mediatype"]);
        Assert.Equal("png", payload["mocd_extension"]);
        Assert.Equal("4684", payload["mocd_filesize"]);
        Assert.Equal("c0c2a661-e330-f111-b119-005056010908", payload["mocd_applicationid"]);
    }

    [Fact]
    public void A_column_we_never_thought_about_is_carried_across_too()
    {
        // mocd_ismigrated is not in any list in this class. That is the point.
        Assert.Equal(false, Build(PluginRecord)["mocd_ismigrated"]);
    }

    [Fact]
    public void The_portal_columns_survive_as_well()
    {
        var payload = Build(PortalRecord);

        Assert.Equal("Application Summary.png", payload["mocd_name"]);
        Assert.Equal("image/png", payload["mocd_mediatype"]);
    }

    // ---- the payload: what must never appear ----

    [Fact]
    public void The_old_key_is_never_copied()
        => Assert.False(Build(PluginRecord).ContainsKey("mocd_documentfileid"));

    [Fact]
    public void Nulls_are_left_out_rather_than_written_as_nulls()
    {
        var payload = Build(PortalRecord);

        Assert.False(payload.ContainsKey("mocd_filesize"));   // was null on the old record
    }

    [Fact]
    public void Lookups_annotations_and_system_columns_are_left_alone()
    {
        const string noisy = """
            {"mocd_name":"a.png",
             "mocd_filepath":"old",
             "_owningbusinessunit_value":"f8019386-20b5-ee11-b108-0050560108b0",
             "_owningbusinessunit_value@Microsoft.Dynamics.CRM.lookuplogicalname":"businessunit",
             "mocd_name@OData.Community.Display.V1.FormattedValue":"a.png",
             "createdon":"2026-04-20T09:02:36Z",
             "versionnumber":169586071,
             "statecode":0,
             "ownerid":{"nested":"object"}}
            """;

        var payload = FileRecordCopier.BuildPayload(noisy, NewFileId, NewPath, "H", Correct, null);

        Assert.Equal("a.png", payload["mocd_name"]);
        Assert.DoesNotContain(payload.Keys, k => k.Contains('@'));
        Assert.DoesNotContain(payload.Keys, k => k.StartsWith('_'));
        Assert.False(payload.ContainsKey("createdon"));
        Assert.False(payload.ContainsKey("versionnumber"));
        Assert.False(payload.ContainsKey("statecode"));
        Assert.False(payload.ContainsKey("ownerid"));
    }

    [Fact]
    public void An_unreadable_old_record_still_produces_a_usable_payload()
    {
        var payload = FileRecordCopier.BuildPayload(null, NewFileId, NewPath, "H", Correct, null);

        Assert.Equal(NewPath, payload["mocd_filepath"]);
        Assert.Equal("H", payload["mocd_hash"]);
        Assert.Equal(Correct.ToString(), payload["mocd_category"]);
    }
}
