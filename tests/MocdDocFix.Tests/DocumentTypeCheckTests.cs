using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
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
        _prompts.ReadLineQueue = new Queue<string>(new[] { "7" });   // skip is the last option

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
    /// With nobody at the console — a scripted run, or output redirected to a file — there is no
    /// one to ask, so an outage is said once and the run carries on with the cross-check off.
    /// When somebody IS there it is a question instead: the interactive tests below.
    /// </summary>
    [Fact]
    public async Task A_devops_outage_with_nobody_to_ask_is_reported_and_the_run_carries_on()
    {
        _ado.Throws = new HttpRequestException("An error occurred while sending the request",
            new IOException("The connection was closed"));

        var check = Check();
        var ruling = await check.RuleOnAsync("Medical Certificate", Emap, CancellationToken.None);

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

    // ---- going away and coming back with an answer ----

    /// <summary>
    /// CRM and the backlog rarely word a document the same way, so the operator can hand over a
    /// phrasing the search would never have tried and get an answer from it.
    /// </summary>
    [Fact]
    public async Task The_operator_can_hand_over_a_better_phrase_and_get_an_answer_from_it()
    {
        // A phrasing nothing in the name would have produced: the backlog calls this document
        // something else entirely, which is the whole reason for handing the search over.
        _ado.Titles["staff clearance"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify staff clearance",
            "Portal | Confirm Employment | Verify staff clearance"
        };

        // Option 4: search for my own words, then the words themselves.
        _prompts.ReadLineQueue = new Queue<string>(new[] { "4", "staff clearance" });

        var ruling = await Check().RuleOnAsync("Zed Marker Sheet", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
        Assert.Contains("staff clearance", ruling.Source);
        Assert.Contains("staff clearance", _ado.Searched);
    }

    [Fact]
    public async Task A_phrase_that_settles_nothing_brings_the_same_question_back()
    {
        _prompts.ReadLineQueue = new Queue<string>(new[] { "4", "nothing matches this", "7" });

        var ruling = await Check().RuleOnAsync("Odd Type", Emap, CancellationToken.None);

        // Asked twice: once before the search, once after it came back empty.
        Assert.Equal(2, _prompts.Messages.Count(m => m.Contains("What should I do with this document type?")));
        Assert.Equal(AdoVerdict.CannotTell, ruling.Verdict);
    }

    /// <summary>
    /// Nothing runs while the operator is away, and what they changed in the backlog while they
    /// were gone is picked up — the search is run again from scratch, not replayed from memory.
    /// </summary>
    [Fact]
    public async Task Waiting_pauses_and_then_searches_again_from_scratch()
    {
        _ado.Titles["Odd Type"] = new();
        _prompts.ReadLineQueue = new Queue<string>(new[] { "6", "" });   // wait, then Enter

        // While the operator is away, the backlog gains the work items that answer it — which is
        // the point of waiting, so the second look has to be a real second look.
        _ado.OnSearched = (phrase, count) =>
        {
            if (count >= 2)
                _ado.Titles[phrase] = new List<string>
                {
                    "NPOP|Employee Appointment Request|Documents|Verify odd type",
                    "Portal | Confirm Employment | Verify odd type"
                };
        };

        var ruling = await Check().RuleOnAsync("Odd Type", Emap, CancellationToken.None);

        Assert.Contains(_prompts.Questions, q => q.Contains("Press Enter when you are ready"));
        Assert.Equal(2, _ado.Searched.Count(s => s == "Odd Type"));
        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
    }

    [Fact]
    public async Task What_the_local_backlog_copy_holds_is_shown_before_the_question()
    {
        var folder = Path.Combine(_root, "stories");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "us-27628.md"),
            "---\ntitle: 1.1.6 NPOP- Employee Appointment Request Form- Documents\n---\n" +
            "A Copy of Certificate of Good Conduct and Behavior\n");

        _prompts.ReadLineQueue = new Queue<string>(new[] { "7" });

        await new DocumentTypeCheck(_ado, Decisions(), _prompts, folder)
            .RuleOnAsync("A Copy of Certificate of Good Conduct and Behavior", Emap, CancellationToken.None);

        var said = string.Join("\n", _prompts.Messages);
        Assert.Contains("local backlog copy", said);
        Assert.Contains("Employee Appointment Request", said);
    }

    // ---- fetching the spreadsheet the answer is written in ----

    /// <summary>
    /// The tool is already signed in, so it fetches the workbook rather than asking for it. The
    /// link is printed either way, because an attachment can be refused by a permission the
    /// sign-in does not carry — and then the operator finishing the job by hand is the plan.
    /// </summary>
    [Fact]
    public async Task The_spreadsheet_is_found_downloaded_and_the_story_link_given()
    {
        var drop = Path.Combine(_root, "backlog-files");

        _ado.Attachments["Employee Appointment Request"] = new()
        {
            new AdoAttachment(27632, "1.1.6 NPOP- Employee Appointment Request Form- Documents",
                "EMAP_Data_Dictionary.xlsx", "https://devops/_apis/wit/attachments/abc")
        };

        _prompts.ReadLineQueue = new Queue<string>(new[] { "5", "7" });   // find it, then skip

        await new DocumentTypeCheck(_ado, Decisions(), _prompts, null, drop)
            .RuleOnAsync("Zed Marker Sheet", Emap, CancellationToken.None);

        var said = string.Join("\n", _prompts.Messages);

        Assert.Contains("EMAP_Data_Dictionary.xlsx", said);
        Assert.Contains("_workitems/edit/27632", said);            // the link to open
        Assert.Contains(drop, said);                                // and where it landed
        Assert.True(File.Exists(Path.Combine(drop, "EMAP_Data_Dictionary.xlsx")));
    }

    [Fact]
    public async Task Where_it_cannot_be_downloaded_the_link_and_the_folder_are_still_given()
    {
        var drop = Path.Combine(_root, "backlog-files");

        _ado.Attachments["Employee Appointment Request"] = new()
        {
            new AdoAttachment(29339, "5.1.1 NPOP- Holding GAM Request- Documents",
                "GAM_Data_Dictionary.xlsx", "https://devops/_apis/wit/attachments/xyz")
        };
        _ado.DownloadFails = true;

        _prompts.ReadLineQueue = new Queue<string>(new[] { "5", "7" });

        await new DocumentTypeCheck(_ado, Decisions(), _prompts, null, drop)
            .RuleOnAsync("Zed Marker Sheet", Emap, CancellationToken.None);

        var said = string.Join("\n", _prompts.Messages);

        Assert.Contains("could not be downloaded", said);
        Assert.Contains("_workitems/edit/29339", said);
        Assert.Contains(drop, said);
        Assert.Contains("Wait — I will go and look", said);          // what to do next
    }

    /// <summary>
    /// A file dropped in by hand is read, and — since it is as live as anything else here —
    /// allowed to settle the type rather than merely being printed at the operator.
    /// </summary>
    [Fact]
    public async Task A_file_dropped_in_by_hand_is_read_like_any_other()
    {
        var drop = Path.Combine(_root, "backlog-files");
        Directory.CreateDirectory(drop);
        File.WriteAllText(Path.Combine(drop, "documents.md"),
            "---\ntitle: 1.1.6 NPOP- Employee Appointment Request Form- Documents\n---\n" +
            "Zed Marker Sheet is required at filing\n");

        var ruling = await new DocumentTypeCheck(_ado, Decisions(), _prompts, null, drop)
            .RuleOnAsync("Zed Marker Sheet", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
        Assert.Contains("documents.md", ruling.Detail);
        Assert.Empty(_prompts.Questions);
    }

    // ---- stage 3: the workbooks on disk ----

    private DocumentTypeCheck CheckWithFolders(string? snapshot, string? drop) =>
        new(_ado, Decisions(), _prompts, snapshot, drop);

    private string Drop(string name, string body)
    {
        var folder = Path.Combine(_root, "drop");
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, name), body);
        return folder;
    }

    [Fact]
    public async Task A_workbook_in_the_drop_folder_settles_the_type_without_asking()
    {
        var drop = Drop("MoCD_NPOP_Employee Appointment Request_DD_20250509_V.0.2.csv",
            "A Copy of Board of Director's Decision,Mandatory");

        var ruling = await CheckWithFolders(null, drop)
            .RuleOnAsync("A Copy of Board of Director's Decision", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
        Assert.Empty(_prompts.Questions);
        Assert.Contains("MoCD_NPOP_Employee Appointment Request", ruling.Detail);
    }

    /// <summary>
    /// The synced copy is a snapshot of unknown age. It stays on screen as a last hint, marked as
    /// such, and is never allowed to settle anything — the live stages are the ones that decide.
    /// </summary>
    [Fact]
    public async Task The_knowledge_base_copy_is_shown_as_a_stale_snapshot_and_settles_nothing()
    {
        var snapshot = Path.Combine(_root, "kb");
        Directory.CreateDirectory(snapshot);

        File.WriteAllText(Path.Combine(snapshot, "us-27628.md"),
            "---\ntype: User Story\ntitle: 1.1.6 NPOP- Employee Appointment Request Form- Documents\n---\n\n" +
            "A Copy of Board of Director's Decision\n");

        _prompts.ReadLineQueue = new Queue<string>(new[] { "1" });

        var ruling = await CheckWithFolders(snapshot, null)
            .RuleOnAsync("A Copy of Board of Director's Decision", Emap, CancellationToken.None);

        Assert.NotEmpty(_prompts.Questions);
        Assert.Equal("you", ruling.Source);
        Assert.Contains(_prompts.Messages,
            m => m.Contains("may be stale", StringComparison.OrdinalIgnoreCase));
    }

    // ---- stage 2: the story bodies, read live ----

    private static AdoWorkItemText Story(int id, string title, string body) => new(id, title, body);

    /// <summary>
    /// The case that prompted all of this. No work item is *called* "A Copy of Board of
    /// Director's Decision", and the story that lists it is called something else entirely — so
    /// the titles cannot settle it and the operator used to be asked about an answer the backlog
    /// was holding all along.
    /// </summary>
    [Fact]
    public async Task A_name_no_title_carries_is_settled_by_the_story_that_lists_it()
    {
        _ado.Bodies[Emap] = new()
        {
            Story(27628, "1.1.6 NPOP- Employee Appointment Request Form- Documents",
                "The system shall display the below list of documents. " +
                "A Copy of Board of Director's Decision. Passport copy.")
        };

        var ruling = await Check().RuleOnAsync("A Copy of Board of Director's Decision", Emap,
            CancellationToken.None);

        Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
        Assert.Equal(Emap, ruling.Service);
        Assert.Empty(_prompts.Questions);
    }

    [Fact]
    public async Task The_bodies_are_only_read_when_the_titles_could_not_tell()
    {
        _ado.Titles["Medical Examination Certificate"] = new()
        {
            "NPOP|Employee Appointment Request|Documents|Verify the medical examination certificate",
            "Portal | Confirm Employment | Verify the medical examination certificate"
        };

        await Check().RuleOnAsync("Medical Examination Certificate", Emap, CancellationToken.None);

        Assert.Empty(_ado.CandidatesAskedFor);
    }

    [Fact]
    public async Task One_story_naming_another_service_is_still_too_thin_to_overrule_crm()
    {
        _ado.Bodies[Emap] = new()
        {
            Story(30001, "1.2.1 NPOP- By-Laws Amendment Form- Documents",
                "Upload A Copy of Board of Director's Decision here.")
        };

        _prompts.ReadLineQueue = new Queue<string>(new[] { "1" });   // take CRM's answer

        var ruling = await Check().RuleOnAsync("A Copy of Board of Director's Decision", Emap,
            CancellationToken.None);

        Assert.NotEmpty(_prompts.Questions);
        Assert.Equal("you", ruling.Source);
    }

    [Fact]
    public async Task Two_stories_naming_another_service_do_overrule_crm_without_asking()
    {
        _ado.Bodies[Emap] = new()
        {
            Story(30001, "1.2.1 NPOP- By-Laws Amendment Form- Documents",
                "Upload A Copy of Board of Director's Decision here."),
            Story(30002, "1.2.2 CRM- By-Laws Amendment Form- Documents",
                "A Copy of Board of Director's Decision is shown to the reviewer.")
        };

        var ruling = await Check().RuleOnAsync("A Copy of Board of Director's Decision", Emap,
            CancellationToken.None);

        Assert.Equal(AdoVerdict.Disagrees, ruling.Verdict);
        Assert.Equal("By-Laws Amendment", ruling.Service);
        Assert.Empty(_prompts.Questions);
    }

    [Fact]
    public async Task When_nothing_in_devops_matched_by_title_the_operator_is_told_the_bodies_were_not_read()
    {
        _prompts.ReadLineQueue = new Queue<string>(new[] { "1" });

        await Check().RuleOnAsync("A Copy of Board of Director's Decision", null,
            CancellationToken.None);

        Assert.Contains(_prompts.Messages,
            m => m.Contains("nothing to read", StringComparison.OrdinalIgnoreCase));
    }

    // ---- an outage, with somebody there to be asked ----

    /// <summary>A console fake: arrow keys and Enter, and yes to every "are you sure".</summary>
    private static FakePrompts AtTheConsole(params MenuKey[] keys)
    {
        var prompts = new FakePrompts
        {
            Keys = new Queue<(MenuKey, char)>(keys.Select(k => (k, '\0'))),
            YesNoResponse = true
        };

        return prompts;
    }

    /// <summary>
    /// The change asked for: an outage is not shrugged off. The operator is shown what happened
    /// and given the four things that can be done about it, because connecting the VPN or
    /// signing in again is something only they can do.
    /// </summary>
    [Fact]
    public async Task An_outage_is_put_to_the_operator_when_there_is_somebody_to_ask()
    {
        _ado.Throws = new HttpRequestException("An error occurred while sending the request",
            new IOException("The connection was closed"));

        // Try again (the default), it fails again, then carry on without the cross-check.
        var prompts = AtTheConsole(MenuKey.Enter, MenuKey.Down, MenuKey.Down, MenuKey.Enter);

        var ruling = await new DocumentTypeCheck(_ado, Decisions(), prompts)
            .RuleOnAsync("Medical Certificate", Emap, CancellationToken.None);

        var said = string.Join("\n", prompts.Messages);

        Assert.Contains("DevOps could not be reached", said);
        Assert.Contains("connection was closed", said.Replace(Environment.NewLine, " "));
        Assert.Equal(AdoVerdict.NotChecked, ruling.Verdict);

        // Trying again really did try again, rather than repeating the first answer.
        Assert.True(_ado.Searched.Count >= 2, $"only {_ado.Searched.Count} search(es) were made");
    }

    [Fact]
    public async Task Carrying_on_without_the_check_is_asked_once_not_once_per_document_type()
    {
        _ado.Throws = new HttpRequestException("boom");

        var prompts = AtTheConsole(MenuKey.Down, MenuKey.Down, MenuKey.Enter);
        var check = new DocumentTypeCheck(_ado, Decisions(), prompts);

        await check.RuleOnAsync("Medical Certificate", Emap, CancellationToken.None);
        var asked = prompts.Questions.Count;
        var calls = _ado.Searched.Count;

        var later = await check.RuleOnAsync("Academic Qualification Certificate", Emap, CancellationToken.None);

        Assert.Equal(AdoVerdict.NotChecked, later.Verdict);
        Assert.Equal(asked, prompts.Questions.Count);      // not asked a second time
        Assert.Equal(calls, _ado.Searched.Count);          // and nothing further attempted
    }

    /// <summary>
    /// Stopping is one of the four answers, and it stops the run the way every other halt does
    /// rather than as an unhandled error.
    /// </summary>
    [Fact]
    public async Task Choosing_to_stop_ends_the_run()
    {
        _ado.Throws = new HttpRequestException("boom");

        var prompts = AtTheConsole(MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter);

        var stopped = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new DocumentTypeCheck(_ado, Decisions(), prompts)
                .RuleOnAsync("Medical Certificate", Emap, CancellationToken.None));

        Assert.Contains("DevOps", stopped.Message);
    }
}
