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
            new GroupedReportWriter(_dir), new GuidListWriter(_dir));

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
    public async Task A_scan_writes_the_guid_list_and_it_can_be_fed_straight_back_in()
    {
        _crm.KnownCatalogues.Add(GamRequest.ToString());
        var broken = Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment);
        var conflict = Doc(@"DigitalServices\boardDecision\20260330\b.jpg", EmployeeAppointment, GamRequest);
        _crm.Documents.AddRange(new[] { broken, conflict });

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.True(File.Exists(result.GuidsPath), result.GuidsPath);

        // What --docs-file would read back: the fixable one only, never the conflict.
        var usable = File.ReadAllLines(result.GuidsPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        Assert.Equal(new[] { broken.DocumentId.ToString() }, usable);
        Assert.Contains(conflict.DocumentId.ToString(), File.ReadAllText(result.GuidsPath));
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

    // ---- a full run writes what a targeted run writes ----

    /// <summary>
    /// The whole point of the change: a full run's account of a document goes in that document's
    /// own folder, as text, instead of a spreadsheet in the whole-run folder that has to be
    /// opened in Excel before anyone can see what is wrong with anything.
    /// </summary>
    private (ScanCommand Scan, DocumentReportStore Reports, BackupStore Backups) WithFolders()
    {
        var reports = new DocumentReportStore(Path.Combine(_dir, "reports"));
        var backups = new BackupStore(Path.Combine(_dir, "backup"));

        return (new ScanCommand(_crm, new Reporter(_dir), "https://crm/MoCD",
                new[] { EmployeeAppointment, GamRequest },
                new GroupedReportWriter(_dir), new GuidListWriter(_dir), reports, backups),
            reports, backups);
    }

    [Fact]
    public async Task A_full_scan_writes_each_broken_document_its_own_check_report()
    {
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment));

        var (scan, reports, _) = WithFolders();
        var result = await scan.RunAsync("dev", CancellationToken.None);

        var row = Assert.Single(result.Fix);
        var path = Path.Combine(reports.FolderFor(row.DocumentId, row.FileName), "01-check.txt");

        Assert.True(File.Exists(path), path);

        var text = File.ReadAllText(path);
        Assert.Contains("STEP 1", text);
        Assert.Contains("Re-upload", text);
    }

    [Fact]
    public async Task A_document_needing_a_human_gets_a_report_but_no_backup_folder()
    {
        _crm.KnownCatalogues.Add(GamRequest.ToString());
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\f.jpg",
            EmployeeAppointment, GamRequest));

        var (scan, reports, backups) = WithFolders();
        var result = await scan.RunAsync("dev", CancellationToken.None);

        var row = Assert.Single(result.Review);
        Assert.True(File.Exists(
            Path.Combine(reports.FolderFor(row.DocumentId, row.FileName), "01-check.txt")));

        // Nothing was backed up, so nothing should suggest it was.
        Assert.False(File.Exists(backups.Folder(row.DocumentId, row.FileName).SummaryPath));
    }

    [Fact]
    public async Task With_per_document_folders_no_spreadsheet_is_left_in_the_whole_run_folder()
    {
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment));

        var (scan, _, _) = WithFolders();
        var result = await scan.RunAsync("dev", CancellationToken.None);

        Assert.Equal(string.Empty, result.ScanPath);
        Assert.Equal(string.Empty, result.ReviewPath);
        Assert.Empty(Directory.GetFiles(_dir, "*.csv"));

        // The two reports that are about the population rather than one document still get
        // written — and both are text.
        Assert.EndsWith(".txt", result.GroupsPath);
        Assert.EndsWith(".txt", result.GuidsPath);
        Assert.True(File.Exists(result.GroupsPath));
        Assert.True(File.Exists(result.GuidsPath));
    }

    [Fact]
    public async Task Without_per_document_folders_the_spreadsheets_are_written_as_before()
    {
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.NotEqual(string.Empty, result.ScanPath);
        Assert.Equal(0, result.PerDocumentReports);
    }
}
