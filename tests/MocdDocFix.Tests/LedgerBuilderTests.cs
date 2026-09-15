using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class LedgerBuilderTests
{
    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");
    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");

    private static DocumentRow Document(string? path, Guid? vendorFileId = null,
        string? oldCategory = "docTypeCatalogue") =>
        new(DocumentId: Doc,
            DocumentName: "A Copy of Board of Director's Decision",
            DocumentFileId: Record,
            FilePath: path,
            FileName: "cert.jpg",
            MediaType: "image/jpeg",
            Hash: "9f86d081",
            DocumentTypeId: Guid.NewGuid(),
            DocumentTypeName: "Board Decision",
            DocTypeCatalogueId: Correct,
            CrossCheckCatalogueId: null,
            CrossCheckSource: null,
            ModifiedOn: DateTimeOffset.UtcNow,
            OldCategory: oldCategory,
            VendorFileId: vendorFileId,
            VendorFileName: "a3f1.jpg");

    private static FakeCrmReadClient Crm(params DocumentRow[] documents)
    {
        var crm = new FakeCrmReadClient();
        crm.Documents.AddRange(documents);
        crm.KnownCatalogues.Add(Correct.ToString());
        crm.CatalogueNames[Correct.ToString()] = "Employee Appointment Request";
        return crm;
    }

    private static LedgerBuilder Builder(FakeCrmReadClient crm) =>
        new(crm, new[] { Correct }, "https://crm.example/");

    private const string BrokenPath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg";

    [Fact]
    public async Task A_broken_path_becomes_a_fix_row_carrying_everything_a_correction_needs()
    {
        var rows = await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Row);
        Assert.Equal(Doc, row.DocId);
        Assert.Equal("A Copy of Board of Director's Decision", row.DocName);
        Assert.Equal(Record, row.DocFileId);
        Assert.Equal(RowVerdict.Fix, row.Verdict2());
        Assert.Equal(2, row.Group);
        Assert.Equal(Correct.ToString(), row.CorrectServiceCatalogueId);
        Assert.Equal("Employee Appointment Request", row.CorrectServiceCatalogueName);
        Assert.Equal(RowState.NotStarted, row.State());
        Assert.Equal(string.Empty, row.BackupPath);
        Assert.Equal(string.Empty, row.NewFilePath);
    }

    /// <summary>The four values a revert writes back, taken from the record rather than the path.</summary>
    [Fact]
    public async Task The_old_values_come_off_the_record()
    {
        var row = (await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None))[0];

        Assert.Equal("docTypeCatalogue", row.OldCategory);
        Assert.Equal("9f86d081", row.OldHash);
        Assert.Equal("a3f1.jpg", row.OldFileName);
        Assert.Equal(string.Empty, row.OldFileId);
    }

    /// <summary>A portal record has no mocd_fileid at all, and that blank is meaningful.</summary>
    [Fact]
    public async Task A_portal_record_and_a_plugin_record_are_told_apart()
    {
        var portal = (await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None))[0];
        Assert.Equal("portal", portal.WayOfUpload);
        Assert.Equal(string.Empty, portal.OldFileId);

        var pluginId = Guid.Parse("11111111-0000-0000-0000-000000000001");
        var plugin = (await Builder(Crm(Document(BrokenPath, vendorFileId: pluginId)))
            .BuildAsync(CancellationToken.None))[0];
        Assert.Equal("plugin", plugin.WayOfUpload);
        Assert.Equal(pluginId.ToString(), plugin.OldFileId);
    }

    [Fact]
    public async Task The_predicted_path_names_the_correct_catalogue_and_keeps_the_extension()
    {
        var row = (await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None))[0];

        Assert.StartsWith($@"DigitalServices\{Correct}\", row.NewFilePathPredicted);
        Assert.EndsWith(@"\(new id).jpg", row.NewFilePathPredicted);
        Assert.Contains(DateTime.Now.ToString("yyyyMMdd"), row.NewFilePathPredicted);
    }

    /// <summary>
    /// Every document in scope gets a row — the correct ones too. The verdict column is what
    /// separates them, and without the correct ones there is no denominator.
    /// </summary>
    [Fact]
    public async Task An_already_correct_document_still_gets_a_row_marked_skip()
    {
        var crm = Crm(Document($@"DigitalServices\{Correct}\20250509\a3f1.jpg"));

        var row = Assert.Single(await Builder(crm).BuildAsync(CancellationToken.None));

        Assert.Equal(RowVerdict.Skip, row.Verdict2());
        Assert.Equal(7, row.Group);
    }

    [Fact]
    public async Task A_document_with_no_file_path_gets_a_row_marked_skip()
    {
        var row = Assert.Single(await Builder(Crm(Document(null))).BuildAsync(CancellationToken.None));

        Assert.Equal(RowVerdict.Skip, row.Verdict2());
        Assert.Equal(string.Empty, row.OldFilePath);
    }

    [Fact]
    public async Task A_malformed_path_needs_a_human_and_says_so()
    {
        var crm = Crm(Document(@"DigitalServices\a3f1.jpg"));

        var row = Assert.Single(await Builder(crm).BuildAsync(CancellationToken.None));

        Assert.Equal(RowVerdict.Review, row.Verdict2());
        Assert.Equal(6, row.Group);
        Assert.NotEqual(string.Empty, row.ReasonOfBug);
        Assert.NotEqual(string.Empty, row.Solution);
    }

    [Fact]
    public async Task Both_crm_links_are_written()
    {
        var row = (await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None))[0];

        Assert.Equal(CrmLinks.Document("https://crm.example/", Doc), row.CrmLinkOfDoc);
        Assert.Equal(CrmLinks.DocumentFile("https://crm.example/", Record), row.CrmLinkOfDocFile);
    }

    [Fact]
    public async Task Rows_are_numbered_from_one_in_the_order_they_are_written()
    {
        var crm = Crm(Document(BrokenPath), Document(BrokenPath), Document(BrokenPath));

        var rows = await Builder(crm).BuildAsync(CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Row));
    }
}
