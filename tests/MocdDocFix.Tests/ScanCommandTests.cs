using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class ScanCommandTests : IDisposable
{
    private static readonly Guid EmployeeAppointment = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid GamRequest = Guid.Parse("3ff27d73-653e-f111-b119-005056010908");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-scan-" + Guid.NewGuid());
    private readonly FakeCrmReadClient _crm = new();

    public ScanCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private ScanCommand Command() =>
        new(_crm, new Reporter(_dir), "https://crm/MoCD", new[] { EmployeeAppointment, GamRequest },
            new GroupedReportWriter(_dir));

    private static DocumentRow Doc(string? path, Guid? docTypeCat, Guid? crossCheck = null,
        string name = "cert.jpg") =>
        new(Guid.NewGuid(), name, Guid.NewGuid(), path, name, "image/jpeg", "hash",
            Guid.NewGuid(), "Certificate of Good Conduct", docTypeCat, crossCheck,
            crossCheck is null ? null : "mocd_employeeappintmentrequest", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Buckets_documents_into_fix_review_and_skip()
    {
        _crm.KnownCatalogues.Add(GamRequest.ToString());
        _crm.Documents.AddRange(new[]
        {
            Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment),      // FIX, group 3
            Doc(@"DigitalServices\0\20260423\b.png", EmployeeAppointment),                            // FIX, group 3
            Doc($@"DigitalServices\{GamRequest}\20260518\c.pdf", EmployeeAppointment),                // FIX, group 5
            Doc($@"DigitalServices\{EmployeeAppointment}\20260330\d.jpg", EmployeeAppointment),       // SKIP
            Doc(null, EmployeeAppointment),                                                            // SKIP
            Doc(@"DigitalServices\x\20260330\e.jpg", null),                                            // SKIP

            // The only thing still held back: the parent request contradicts the document type.
            Doc(@"DigitalServices\goodConductCertificate\20260330\f.jpg", EmployeeAppointment,
                GamRequest),                                                                          // REVIEW, group 6
        });

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Equal(3, result.Fix.Count);
        Assert.Single(result.Review);
        Assert.Equal(3, result.Skip.Count);
        Assert.Equal(7, result.All.Count);
    }

    [Fact]
    public async Task A_scan_writes_the_grouped_report_when_one_is_configured()
    {
        _crm.KnownCatalogues.Add(GamRequest.ToString());
        _crm.Documents.AddRange(new[]
        {
            Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment),
            Doc($@"DigitalServices\{GamRequest}\20260518\c.pdf", EmployeeAppointment),
        });

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.True(File.Exists(result.GroupsPath), result.GroupsPath);

        var text = File.ReadAllText(result.GroupsPath);
        Assert.Contains("GROUP 3", text);
        Assert.Contains("GROUP 5", text);
        Assert.Contains("2  TOTAL to fix (groups 1-5)", text);
    }

    [Fact]
    public async Task The_group_counts_are_available_without_re_reading_the_file()
    {
        _crm.KnownCatalogues.Add(GamRequest.ToString());
        _crm.Documents.AddRange(new[]
        {
            Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment),
            Doc(@"DigitalServices\boardDecision\20260330\b.jpg", EmployeeAppointment),
            Doc($@"DigitalServices\{GamRequest}\20260518\c.pdf", EmployeeAppointment),
        });

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Equal(2, result.CountByGroup[3]);
        Assert.Equal(1, result.CountByGroup[5]);
    }

    [Fact]
    public async Task Writes_a_scan_file_and_a_review_file()
    {
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.True(File.Exists(result.ScanPath));
        Assert.True(File.Exists(result.ReviewPath));
        Assert.Contains("scan-dev-", Path.GetFileName(result.ScanPath));
    }

    [Fact]
    public async Task Every_row_carries_a_clickable_crm_link()
    {
        var doc = Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment);
        _crm.Documents.Add(doc);

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Contains($"id={doc.DocumentId}", result.All[0].CrmLink);
    }

    [Fact]
    public async Task Catalogue_membership_is_asked_of_the_server_not_hardcoded()
    {
        // Same path shape, but this GUID is NOT registered as a catalogue, so it is a FIX.
        var unknown = Guid.NewGuid();
        _crm.Documents.Add(Doc($@"DigitalServices\{unknown}\20260518\c.pdf", EmployeeAppointment));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Single(result.Fix);
        Assert.Empty(result.Review);
    }

    [Fact]
    public async Task A_cross_check_conflict_is_reported_as_review()
    {
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg",
            EmployeeAppointment, crossCheck: GamRequest));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Single(result.Review);
        Assert.Contains("cross-check", result.Review[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_org_produces_empty_buckets_and_still_writes_the_files()
    {
        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Empty(result.All);
        Assert.True(File.Exists(result.ScanPath));
    }

    [Fact]
    public async Task The_banner_reports_the_population_as_well_as_the_corrupted_subset()
    {
        _crm.Documents.AddRange(new[]
        {
            Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment),
            Doc($@"DigitalServices\{EmployeeAppointment}\20260330\b.jpg", EmployeeAppointment),
            Doc(null, EmployeeAppointment)
        });

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Equal(3, result.TotalInScope);
        Assert.Equal(2, result.WithFilePath);
        Assert.Equal(1, result.WithoutFilePath);

        var banner = result.Banner();
        Assert.Contains("In scope", banner);
        Assert.Contains("BROKEN", banner);
        Assert.Contains("AMBIGUOUS", banner);
    }

    [Fact]
    public async Task Every_row_states_a_solution_as_well_as_a_reason()
    {
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.All(result.All, r => Assert.False(string.IsNullOrWhiteSpace(r.Solution)));
        Assert.Contains("Re-upload", result.Fix[0].Solution);
        Assert.Contains("Solution", File.ReadAllText(result.ScanPath));
    }
}
