using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class ReporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-report-" + Guid.NewGuid());

    public ReporterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static ScanRow Row(string fileName = "cert.jpg", Verdict verdict = Verdict.Fix) => new(
        DocumentId: Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"),
        DocumentFileId: Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187"),
        FileName: fileName,
        DocumentTypeName: "Certificate of Good Conduct",
        ServiceCatalogueId: Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
        ServiceCatalogueName: "Employee Appointment Request",
        OldFilePath: @"DigitalServices\goodConductCertificate\20260330\5b05398a.jpg",
        CurrentSegment: "goodConductCertificate",
        CurrentSegmentName: null,
        CorrectCatalogueId: Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
        Verdict: verdict.ToString(),
        Group: 3,
        GroupLabel: "path holds a name instead of an id",
        Reason: "path segment is a document type name",
        Solution: "Re-upload with Category = cd97bf8d-…, repoint, then delete the old file.",
        CrossCheckSource: "mocd_employeeappintmentrequest",
        CrmLink: "https://crm/MoCD/main.aspx?etn=mocd_document&id=2c9d5572-a77b-f111-b10f-00505601095a");

    [Fact]
    public void WriteScan_creates_a_file_with_a_header_and_the_row()
    {
        var path = new Reporter(_dir).WriteScan("dev", new[] { Row() });

        var text = File.ReadAllText(path);
        Assert.Contains("DocumentId", text);
        Assert.Contains("2c9d5572-a77b-f111-b10f-00505601095a", text);
        Assert.Contains("goodConductCertificate", text);
        Assert.Contains("Fix", text);
    }

    [Fact]
    public void The_file_name_carries_the_environment_so_output_is_never_ambiguous()
    {
        var path = new Reporter(_dir).WriteScan("preprod", new[] { Row() });

        Assert.Contains("preprod", Path.GetFileName(path));
        Assert.StartsWith("scan-", Path.GetFileName(path));
        Assert.EndsWith(".csv", path);
    }

    [Fact]
    public void Commas_and_quotes_in_a_file_name_are_escaped_not_corrupted()
    {
        var path = new Reporter(_dir).WriteScan("dev", new[] { Row("my, \"odd\" name.jpg") });

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);                       // header plus exactly one record
        Assert.Contains("\"my, \"\"odd\"\" name.jpg\"", lines[1]);
    }

    [Fact]
    public void Arabic_file_names_survive_as_utf8()
    {
        var path = new Reporter(_dir).WriteScan("dev", new[] { Row("شهادة.pdf") });

        Assert.Contains("شهادة.pdf", File.ReadAllText(path, System.Text.Encoding.UTF8));
    }

    [Fact]
    public void Review_and_quarantine_get_their_own_files()
    {
        var reporter = new Reporter(_dir);

        var review = reporter.WriteReview("dev", new[] { Row(verdict: Verdict.Review) });
        var quarantine = reporter.WriteQuarantine("dev", new[] { Row() });

        Assert.StartsWith("review-", Path.GetFileName(review));
        Assert.StartsWith("quarantine-", Path.GetFileName(quarantine));
    }

    [Fact]
    public void WriteMigration_records_both_sides_for_verification()
    {
        var path = new Reporter(_dir).WriteMigration("dev", new[]
        {
            new MigrationRow(
                DocumentId: Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"),
                OldFileId: Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187"),
                NewFileId: Guid.Parse("a41c0b77-1111-2222-3333-444444444444"),
                OldFilePath: @"DigitalServices\goodConductCertificate\20260330\5b05398a.jpg",
                NewFilePath: @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\a41c0b77.jpg",
                Bytes: 350208,
                VendorHash: "e57d1555e2197c964daa9fd57e197b7b",
                OurHash: "abc",
                ChecksPassed: "backup-integrity;upload-hash;round-trip;path-fixed;genuinely-new;crm-repointed",
                OldCrmLink: "https://crm/old",
                NewCrmLink: "https://crm/new",
                State: "Repointed",
                At: DateTimeOffset.UtcNow)
        });

        var text = File.ReadAllText(path);
        Assert.Contains("a41c0b77-1111-2222-3333-444444444444", text);
        Assert.Contains("round-trip", text);
        Assert.Contains("Repointed", text);
    }

    [Fact]
    public void CrmLink_builds_a_document_form_url()
    {
        var link = Reporter.CrmLink("https://crm/MoCD", Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"));

        Assert.Equal(
            "https://crm/MoCD/main.aspx?etn=mocd_document&pagetype=entityrecord&id=2c9d5572-a77b-f111-b10f-00505601095a",
            link);
    }

    [Fact]
    public void An_empty_result_set_still_writes_a_header_only_file()
    {
        var path = new Reporter(_dir).WriteScan("dev", Array.Empty<ScanRow>());

        Assert.Single(File.ReadAllLines(path));
    }
}
