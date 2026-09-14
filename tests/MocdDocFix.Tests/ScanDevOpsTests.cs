using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Step 1 with the DevOps cross-check wired in. It may only ever make a row safer: a
/// disagreement moves it to group 6, and nothing DevOps says can promote a row into being fixed.
/// </summary>
public class ScanDevOpsTests : IDisposable
{
    private static readonly Guid EmployeeAppointment = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-scanado-" + Guid.NewGuid());
    private readonly FakeCrmReadClient _crm = new();
    private readonly FakeAdoClient _ado = new();
    private readonly FakePrompts _prompts = new();

    public ScanDevOpsTests()
    {
        Directory.CreateDirectory(_dir);
        _crm.CatalogueNames[EmployeeAppointment.ToString()] = "Employee Appointment Request";
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private ScanCommand Command() =>
        new(_crm, new Reporter(_dir), "https://crm/MoCD", new[] { EmployeeAppointment },
            null, null,
            new DocumentTypeCheck(_ado, new DocumentTypeDecisions(Path.Combine(_dir, "d.json")), _prompts));

    private static DocumentRow Doc(string path, string documentType) =>
        new(Guid.NewGuid(), "cert.jpg", Guid.NewGuid(), path, "cert.jpg", "image/jpeg", "hash",
            Guid.NewGuid(), documentType, EmployeeAppointment, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_disagreement_moves_a_fixable_row_to_group_6()
    {
        _ado.Titles["Board Decision"] = new()
        {
            "CRM | By-Laws Amendment | Documents | Verify the board decision",
            "Portal | By-Laws Amendment | Documents | Verify the board decision upload"
        };

        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", "Board Decision"));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        var row = Assert.Single(result.All);
        Assert.Equal(nameof(Verdict.Review), row.Verdict);
        Assert.Equal(6, row.Group);
        Assert.Contains("By-Laws Amendment", row.Reason);
        Assert.Null(row.CorrectCatalogueId);        // nothing safe to write
        Assert.Empty(result.Fix);
    }

    [Fact]
    public async Task Agreement_leaves_the_row_exactly_as_the_path_classified_it()
    {
        _ado.Titles["Medical Certificate"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify the medical certificate",
            "Portal | Confirm Employment | Verify the medical certificate"
        };

        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", "Medical Certificate"));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        var row = Assert.Single(result.Fix);
        Assert.Equal(3, row.Group);
        Assert.Equal(nameof(AdoVerdict.Agrees), row.AdoVerdict);
        Assert.Equal("Employee Appointment Request", row.AdoService);
    }

    [Fact]
    public async Task The_answer_and_its_evidence_are_written_into_the_report()
    {
        _ado.Titles["Medical Certificate"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify the medical certificate",
            "Portal | Confirm Employment | Verify the medical certificate"
        };

        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", "Medical Certificate"));

        var result = await Command().RunAsync("dev", CancellationToken.None);
        var csv = File.ReadAllText(result.ScanPath);

        Assert.Contains("AdoVerdict", csv);
        Assert.Contains("AdoService", csv);
        Assert.Contains("AdoEvidence", csv);
        Assert.Contains("Agrees", csv);
        Assert.Contains("1000", csv);                       // the work item it rests on
        Assert.Contains("1 document type(s)", result.DevOpsSummary()!);
    }

    /// <summary>
    /// A document the path already agrees with is never dragged into group 6 by the backlog:
    /// there is nothing to fix, so there is nothing to hold back.
    /// </summary>
    [Fact]
    public async Task A_document_with_nothing_to_do_is_left_alone_even_on_a_disagreement()
    {
        _ado.Titles["Board Decision"] = new()
        {
            "CRM | By-Laws Amendment | Documents | Verify the board decision",
            "Portal | By-Laws Amendment | Documents | Verify the board decision upload"
        };

        _crm.Documents.Add(Doc($@"DigitalServices\{EmployeeAppointment}\20260330\a.jpg", "Board Decision"));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Single(result.Skip);
        Assert.Empty(result.Review);
    }

    [Fact]
    public async Task Many_documents_of_one_type_ask_devops_once()
    {
        _ado.Titles["Medical Certificate"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify the medical certificate",
            "Portal | Confirm Employment | Verify the medical certificate"
        };

        for (var i = 0; i < 6; i++)
            _crm.Documents.Add(Doc($@"DigitalServices\bad\20260330\{i}.jpg", "Medical Certificate"));

        await Command().RunAsync("dev", CancellationToken.None);

        Assert.Single(_ado.Searched);
    }

    /// <summary>
    /// A full run IS the scan, so the backlog's answer has to reach where a full run's operator
    /// actually reads: the document's own folder, not only a column in a spreadsheet.
    /// </summary>
    [Fact]
    public async Task A_full_run_writes_the_backlogs_answer_into_each_documents_own_report()
    {
        _ado.Titles["Medical Certificate"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify the medical certificate",
            "Portal | Confirm Employment | Verify the medical certificate"
        };

        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", "Medical Certificate"));

        var reports = new DocumentReportStore(Path.Combine(_dir, "reports"));

        var scan = new ScanCommand(_crm, new Reporter(_dir), "https://crm/MoCD",
            new[] { EmployeeAppointment }, null, null,
            new DocumentTypeCheck(_ado, new DocumentTypeDecisions(Path.Combine(_dir, "d.json")), _prompts),
            reports, new BackupStore(Path.Combine(_dir, "backup")));

        var result = await scan.RunAsync("dev", CancellationToken.None);
        var row = Assert.Single(result.Fix);

        var text = File.ReadAllText(
            Path.Combine(reports.FolderFor(row.DocumentId, row.FileName), "01-check.txt"));

        Assert.Contains("DevOps says", text);
        Assert.Contains("agrees", text);
        Assert.Contains("1000", text);              // the work item the answer rests on
    }
}
