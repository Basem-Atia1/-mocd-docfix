using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The DevOps side of step 1: asked once per document type, remembered once answered, and never
/// willing to guess when the backlog is unclear.
/// </summary>
public class DocumentTypeCheckTests : IDisposable
{
    private const string Emap = "Employee Appointment Request";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-dt-" + Guid.NewGuid());
    private readonly FakeAdoClient _ado = new();
    private readonly FakePrompts _prompts = new();

    private DocumentTypeDecisions Decisions() =>
        new(Path.Combine(_root, "document-types.json"));

    private DocumentTypeCheck Check() => new(_ado, Decisions(), _prompts);

    public DocumentTypeCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    // ---- when DevOps can answer ----

    [Fact]
    public async Task Agreement_is_reported_without_asking_anybody_anything()
    {
        _ado.Titles["Medical Examination Certificate"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify the medical examination certificate",
            "Portal | Confirm Employment | Verify the medical examination certificate"
        };

        var ruling = await Check().RuleOnAsync("Medical Examination Certificate", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
        Assert.Equal("DevOps", ruling.Source);
        Assert.Empty(_prompts.Questions);
    }

    [Fact]
    public async Task A_real_disagreement_is_reported_without_asking_too()
    {
        _ado.Titles["Board Decision"] = new()
        {
            "CRM | By-Laws Amendment | Documents | Verify the board decision",
            "Portal | By-Laws Amendment | Documents | Verify the board decision upload"
        };

        var ruling = await Check().RuleOnAsync("Board Decision", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Disagrees, ruling.Verdict);
        Assert.Equal("By-Laws Amendment", ruling.Service);
        Assert.Empty(_prompts.Questions);
    }

    /// <summary>
    /// CRM's full phrasing often finds nothing while a shorter phrase finds the right tests, so
    /// the search has to relax — and stop as soon as it has an answer.
    /// </summary>
    [Fact]
    public async Task The_search_relaxes_until_something_is_found()
    {
        _ado.Titles["Certificate Good Conduct Behavior"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify good conduct",
            "Portal | NPOP- Register New Member | Documents | Verify good conduct"
        };

        var ruling = await Check().RuleOnAsync(
            "A Copy of Certificate of Good Conduct and Behavior", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
        Assert.True(_ado.Searched.Count > 1, "it should have tried the full phrase first");
        Assert.Equal("A Copy of Certificate of Good Conduct and Behavior", _ado.Searched[0]);
    }

    // ---- when DevOps cannot ----

    /// <summary>
    /// The FAHR Document case, end to end: one passing mention in a By-Laws test, against CRM
    /// saying Employee Appointment. It must ask rather than contradict.
    /// </summary>
    [Fact]
    public async Task A_single_weak_hit_puts_the_question_to_the_operator()
    {
        _ado.Titles["FAHR Document"] = new()
        {
            "CRM | By-Laws Amendment | FAHR Integration - Workflow Type 1 | " +
            "Verify no FAHR document placeholder is created"
        };
        _prompts.ReadLineResponse = "";           // take the default: accept CRM's answer

        var ruling = await Check().RuleOnAsync("FAHR Document", Emap, CancellationToken.None);

        Assert.Contains(_prompts.Messages, m => m.Contains("What should I do with this document type?"));
        Assert.Contains(_prompts.Messages, m => m.Contains("DevOps cannot settle 'FAHR Document'"));
        Assert.Contains(_prompts.Messages, m => m.Contains("56630") || m.Contains("FAHR Integration"));
        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);       // the default is: trust CRM
        Assert.Equal("you", ruling.Source);
    }

    [Fact]
    public async Task The_operator_can_send_a_document_type_to_a_human()
    {
        _ado.Titles["Odd Type"] = new();
        _prompts.ReadLineQueue = new Queue<string>(new[] { "2" });   // needs a human

        var ruling = await Check().RuleOnAsync("Odd Type", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Disagrees, ruling.Verdict);
        Assert.Contains("needs a human", ruling.Detail);
    }

    [Fact]
    public async Task The_operator_can_name_the_service_themselves()
    {
        _prompts.ReadLineQueue = new Queue<string>(new[] { "3", "By-Laws Amendment" });

        var ruling = await Check().RuleOnAsync("Odd Type", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Disagrees, ruling.Verdict);      // not what CRM says
        Assert.Equal("By-Laws Amendment", ruling.Service);
    }

    [Fact]
    public async Task Skipping_remembers_nothing_and_leaves_the_verdict_open()
    {
        _prompts.ReadLineQueue = new Queue<string>(new[] { "4" });

        var ruling = await Check().RuleOnAsync("Odd Type", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.CannotTell, ruling.Verdict);
        Assert.Null(Decisions().For("Odd Type"));
    }

    // ---- what is remembered ----

    [Fact]
    public async Task An_answered_question_is_never_asked_again()
    {
        _prompts.ReadLineQueue = new Queue<string>(new[] { "1" });   // take CRM's answer
        await Check().RuleOnAsync("Odd Type", Emap, CancellationToken.None);

        var asked = _prompts.Questions.Count;
        var later = new FakePrompts();
        var ruling = await new DocumentTypeCheck(_ado, Decisions(), later)
            .RuleOnAsync("Odd Type", Emap, CancellationToken.None);

        Assert.Empty(later.Questions);
        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
        Assert.Equal("saved decision", ruling.Source);
        Assert.True(asked > 0);
    }

    [Fact]
    public async Task A_saved_decision_is_used_instead_of_asking_devops_at_all()
    {
        Decisions().Remember("Odd Type", DocumentTypeDecisions.NeedsHuman, null, "operator");

        var ruling = await Check().RuleOnAsync("Odd Type", Emap, CancellationToken.None);

        Assert.Empty(_ado.Searched);
        Assert.Equal(AdoVerdict.Disagrees, ruling.Verdict);
    }

    [Fact]
    public async Task The_same_document_type_is_only_looked_up_once_a_run()
    {
        _ado.Titles["Medical Certificate"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify the medical certificate",
            "Portal | Confirm Employment | Verify the medical certificate"
        };

        var check = Check();
        for (var i = 0; i < 5; i++)
            await check.RuleOnAsync("Medical Certificate", Emap, CancellationToken.None);

        Assert.Single(_ado.Searched);
    }

    // ---- when DevOps is not there ----

    [Fact]
    public async Task With_no_devops_configured_nothing_is_claimed()
    {
        var ruling = await new DocumentTypeCheck(null, Decisions(), _prompts)
            .RuleOnAsync("Anything", Emap, CancellationToken.None);

        // NotChecked, not CannotTell: "I never asked" and "I asked and could not tell" are
        // different facts, and reporting them with one word is how a check that silently never
        // ran goes unnoticed — which is exactly what happened.
        Assert.Equal(AdoVerdict.NotChecked, ruling.Verdict);
        Assert.Equal("not checked", ruling.Source);
        Assert.Empty(_prompts.Questions);
    }

    /// <summary>
    /// No VPN, or a server having a bad day, must not stop a scan — the backlog is a third
    /// opinion, not a dependency.
    /// </summary>
    [Fact]
    public async Task A_devops_outage_is_reported_and_the_run_carries_on()
    {
        _ado.Throws = new HttpRequestException("An error occurred while sending the request",
            new IOException("The connection was closed"));

        var check = Check();
        var ruling = await check.RuleOnAsync("Medical Certificate", Emap, CancellationToken.None);

        // Not a question: an unreachable backlog is the same answer for every document type, so
        // the operator is told once instead of being asked twenty times.
        Assert.Equal(AdoVerdict.NotChecked, ruling.Verdict);
        Assert.Empty(_prompts.Questions);
        Assert.Contains(_prompts.Messages, m => m.Contains("DevOps is unreachable"));

        // And the message worth reading is the cause, not HttpClient's wrapper. The transcript is
        // rejoined first, because the warning wraps and hangs like every paragraph the tool prints.
        var said = System.Text.RegularExpressions.Regex.Replace(
            string.Join(" ", _prompts.Messages), @"\s+", " ");

        Assert.Contains("connection was closed", said);
    }

    /// <summary>
    /// Once the backlog has failed, the rest of the run stops paying for it: no more calls, no
    /// more questions, and every remaining document type simply reads "not checked".
    /// </summary>
    [Fact]
    public async Task After_an_outage_it_stops_asking_devops_for_the_rest_of_the_run()
    {
        _ado.Throws = new HttpRequestException("boom");

        var check = Check();
        await check.RuleOnAsync("Medical Certificate", Emap, CancellationToken.None);
        var calls = _ado.Searched.Count;

        var later = await check.RuleOnAsync("Academic Qualification Certificate", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.NotChecked, later.Verdict);
        Assert.Equal(calls, _ado.Searched.Count);      // nothing further was attempted
        Assert.Empty(_prompts.Questions);
    }

    /// <summary>
    /// An IOException thrown mid-read is not one of the two obvious HTTP exceptions, and it used
    /// to escape and end the run — after the check had already finished and written its report.
    /// </summary>
    [Fact]
    public async Task No_kind_of_failure_is_allowed_to_escape_and_end_the_run()
    {
        foreach (var failure in new Exception[]
                 {
                     new IOException("connection reset"),
                     new InvalidOperationException("bad handler state"),
                     new System.Text.Json.JsonException("unexpected token")
                 })
        {
            var prompts = new FakePrompts();
            var ado = new FakeAdoClient { Throws = failure };

            var ruling = await new DocumentTypeCheck(ado, Decisions(), prompts)
                .RuleOnAsync("Medical Certificate", Emap, CancellationToken.None);

            Assert.Equal(AdoVerdict.NotChecked, ruling.Verdict);
        }
    }
}
