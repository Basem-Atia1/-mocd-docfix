using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="Verdict">What the run should do about this document type.</param>
/// <param name="Service">The service the answer points at, when there is one.</param>
/// <param name="Source">Where the answer came from, for the report: DevOps, a saved decision, or you.</param>
public sealed record TypeRuling(
    string DocumentType,
    AdoVerdict Verdict,
    string? Service,
    string Detail,
    IReadOnlyList<AdoHit> Evidence,
    string Source)
{
    public static TypeRuling NotChecked(string documentType) =>
        new(documentType, AdoVerdict.NotChecked, null, "DevOps was not consulted.",
            Array.Empty<AdoHit>(), "not checked");
}

/// <summary>
/// The DevOps side of step 1: for each document type, which service does the backlog say it
/// belongs to, and does that agree with the service catalogue on the CRM document type?
///
/// It asks once per document type, not once per file — a few hundred documents share a handful
/// of types, and every question costs a live query. Where DevOps cannot answer, the operator is
/// asked there and then and the answer is written down, so the same awkward name is never put
/// to them twice.
/// </summary>
public sealed class DocumentTypeCheck
{
    private readonly IAdoClient? _ado;
    private readonly DocumentTypeDecisions _decisions;
    private readonly IPrompts _prompts;
    private readonly Dictionary<string, TypeRuling> _thisRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why DevOps stopped answering, once it has. Set means: do not try again this run.</summary>
    private string? _unreachable;

    /// <summary>Where a local copy of the backlog lives, if there is one.</summary>
    private readonly string? _localBacklog;

    /// <summary>Where downloaded and hand-placed backlog files are kept and read from.</summary>
    private readonly string? _dropFolder;

    /// <param name="ado">Null when DevOps is not configured; every type then reads "not checked".</param>
    /// <param name="localBacklog">
    /// A folder holding a local copy of the backlog — the synced user stories and the files
    /// attached to them. Searched only when the live look-up cannot settle a name, because it is
    /// the one place the document lists are actually written down.
    /// </param>
    /// <param name="dropFolder">
    /// Where workbooks fetched from the backlog are saved, and where the operator can put one
    /// they downloaded themselves. Searched along with the local copy.
    /// </param>
    public DocumentTypeCheck(IAdoClient? ado, DocumentTypeDecisions decisions, IPrompts prompts,
        string? localBacklog = null, string? dropFolder = null)
    {
        _ado = ado;
        _decisions = decisions;
        _prompts = prompts;
        _localBacklog = localBacklog;
        _dropFolder = dropFolder;
    }

    /// <summary>Document types asked about in this run, in the order they were first seen.</summary>
    public IReadOnlyCollection<TypeRuling> Rulings => _thisRun.Values;

    public async Task<TypeRuling> RuleOnAsync(string? documentType, string? crmService, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(documentType))
            return TypeRuling.NotChecked("(no document type)");

        var name = documentType!.Trim();

        if (_thisRun.TryGetValue(name, out var already)) return already;

        var ruling = await DecideAsync(name, crmService, ct);
        _thisRun[name] = ruling;
        return ruling;
    }

    private async Task<TypeRuling> DecideAsync(string name, string? crmService, CancellationToken ct)
    {
        if (_decisions.For(name) is { } saved) return FromDecision(name, crmService, saved);

        // Set only once the operator has chosen to carry on without the cross-check, so from
        // here on the answer is the same for every type and is not put to them again.
        if (_ado is null || _unreachable is not null)
            return new TypeRuling(name, AdoVerdict.NotChecked, null,
                _unreachable ?? "DevOps was not consulted.", Array.Empty<AdoHit>(), "not checked");

        var opinion = await AskDevOpsAsync(name, crmService, ct);

        // A backlog that cannot be reached used to be announced once and then quietly dropped
        // for the rest of the run. That is the wrong default: the check exists so that no file
        // is moved on one authority's word, and "it could not be asked" is the moment to stop
        // and let it be put right — the VPN, the sign-in — not to carry on regardless.
        if (_unreachable is not null)
            return await AskAboutTheOutageAsync(name, crmService, ct);

        return opinion.Verdict == AdoVerdict.CannotTell
            ? Ask(name, crmService, opinion)
            : new TypeRuling(name, opinion.Verdict, opinion.Service, opinion.Detail,
                opinion.Evidence, "DevOps");
    }

    /// <summary>
    /// Told, and asked, rather than decided for them. The operator is the only one who can fix
    /// an outage — connect the VPN, sign in again — so the run stops here and offers the four
    /// things that can actually be done about it.
    ///
    /// Choosing to carry on without the check latches, so this is asked once however many
    /// document types follow. Choosing to try again clears the latch, so a VPN that comes back
    /// is picked up immediately.
    /// </summary>
    private async Task<TypeRuling> AskAboutTheOutageAsync(string name, string? crmService, CancellationToken ct)
    {
        // Nobody to ask — a scripted run, or output redirected to a file. Asking into the void
        // would either hang or invent an answer, so it behaves as it always did: says what
        // happened, carries on with the cross-check off, and marks every type "not checked".
        if (!_prompts.Interactive)
        {
            _prompts.Blank();
            _prompts.Warn($"DevOps is unreachable, so the cross-check is off for this run. " +
                          $"Everything else is unaffected. {_unreachable}");

            return new TypeRuling(name, AdoVerdict.NotChecked, null, _unreachable!,
                Array.Empty<AdoHit>(), "not checked");
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            _prompts.Blank();
            _prompts.Section("DevOps could not be reached", Tone.Danger);
            _prompts.Field("document type", name, Tone.Muted);
            _prompts.Field("CRM says", crmService ?? "(no service catalogue)", Tone.Muted);
            _prompts.Blank();
            _prompts.Say(_unreachable ?? "No reason was given.", Tone.Warn);
            _prompts.Blank();
            _prompts.Say("Nothing has been changed. The cross-check is the only thing affected — " +
                         "CRM and the file server are untouched by this.", Tone.Muted);

            var answer = new Asker(_prompts).Ask("What should I do?", new[]
            {
                new Choice("Try again now", "I have put it right — connected the VPN, signed in",
                    "The search is run again from scratch. If it works, this document type is " +
                    "settled the usual way and the rest of the run carries on with the check on."),

                new Choice("Decide this document type myself", "show me what there is and ask me",
                    "The same question you get when the backlog cannot settle a name: take CRM's " +
                    "answer, send it to a human, type the service yourself, or go and look. Your " +
                    "answer is written down and used for every document of this type."),

                new Choice("Carry on without the cross-check", "for the rest of this run",
                    "Every document type from here on reads 'not checked' — in the reports too, " +
                    "so nothing later pretends the backlog agreed. CRM's answer decides, exactly " +
                    "as it did before this check existed. You are not asked about this again."),

                new Choice("Stop the run", "leave everything as it is",
                    "Nothing further runs. Everything already done stays done and is recorded on " +
                    "disk, so the run can be picked up once the connection is back.")
            }, defaultIndex: 0, allowBack: false, confirm: true);

            switch (answer.Kind == AnswerKind.Chosen ? answer.Index : 3)
            {
                case 0:
                    _unreachable = null;
                    var again = await AskDevOpsAsync(name, crmService, ct);

                    if (_unreachable is not null) continue;      // still down — ask again

                    _prompts.Say("DevOps answered.", Tone.Good);

                    return again.Verdict == AdoVerdict.CannotTell
                        ? Ask(name, crmService, again)
                        : new TypeRuling(name, again.Verdict, again.Service, again.Detail,
                            again.Evidence, "DevOps");

                case 1:
                    var why = _unreachable;

                    // Not latched: deciding this one by hand says nothing about the next one,
                    // and an outage that clears halfway through a run should be picked up. The
                    // way to stop being asked is the option below, which says so plainly.
                    _unreachable = null;

                    return Ask(name, crmService, new AdoOpinion(AdoVerdict.CannotTell, null,
                        Array.Empty<AdoHit>(), $"DevOps could not be reached — {why}"));

                case 2:
                    _prompts.Say("The cross-check is off for the rest of this run. Every document " +
                                 "type will read 'not checked'.", Tone.Muted);

                    return new TypeRuling(name, AdoVerdict.NotChecked, null,
                        $"DevOps could not be reached — {_unreachable}",
                        Array.Empty<AdoHit>(), "not checked");

                default:
                    throw new OperationCanceledException(
                        "Stopped at your request: DevOps could not be reached.");
            }
        }
    }

    /// <summary>
    /// Searches the backlog, relaxing the phrase a step at a time, and stops at the first term
    /// that produces an answer rather than silence.
    /// </summary>
    private async Task<AdoOpinion> AskDevOpsAsync(string name, string? crmService, CancellationToken ct)
    {
        AdoOpinion? weakest = null;

        foreach (var term in DocumentTypeAuthority.SearchTerms(name))
        {
            IReadOnlyList<AdoHit> hits;

            try
            {
                hits = await _ado!.FindByTitleAsync(term, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;                      // the operator stopping the run is not a failure
            }
            catch (Exception ex)
            {
                // Anything at all. The backlog is a third opinion, not a dependency: a dropped
                // connection, an expired password, a proxy, a server having a bad day — none of
                // it may take down a run that is otherwise working. Catching only the two
                // obvious HTTP exceptions let an IOException mid-read escape and kill the run.
                _unreachable = $"{ex.GetType().Name}: {Innermost(ex)}";

                return new AdoOpinion(AdoVerdict.CannotTell, null, Array.Empty<AdoHit>(),
                    $"DevOps could not be reached — {_unreachable}");
            }

            var opinion = DocumentTypeAuthority.Weigh(name, crmService, hits);
            weakest ??= opinion;

            if (opinion.Verdict != AdoVerdict.CannotTell) return opinion;
            if (hits.Count > 0) weakest = opinion;     // something was found, even if it settles nothing
        }

        return weakest ?? new AdoOpinion(AdoVerdict.CannotTell, null, Array.Empty<AdoHit>(),
            $"'{name}' is too short or too general to search DevOps for.");
    }

    private TypeRuling FromDecision(string name, string? crmService, TypeDecision saved)
    {
        var when = $"your decision of {saved.At:yyyy-MM-dd HH:mm}";

        if (saved.Ruling == DocumentTypeDecisions.AcceptCrm)
            return new TypeRuling(name, AdoVerdict.Agrees, crmService,
                $"Settled by {when}: take CRM's answer.", Array.Empty<AdoHit>(), "saved decision");

        if (saved.Ruling == DocumentTypeDecisions.NeedsHuman)
            return new TypeRuling(name, AdoVerdict.Disagrees, null,
                $"Settled by {when}: this document type needs a human.",
                Array.Empty<AdoHit>(), "saved decision");

        var agrees = DocumentTypeAuthority.SameService(saved.Ruling, crmService);

        return new TypeRuling(name, agrees ? AdoVerdict.Agrees : AdoVerdict.Disagrees, saved.Ruling,
            agrees
                ? $"Settled by {when}: '{saved.Ruling}', which is what CRM says."
                : $"Settled by {when}: '{saved.Ruling}', but CRM says {crmService ?? "(nothing)"}.",
            Array.Empty<AdoHit>(), "saved decision");
    }

    /// <summary>
    /// Put to the operator at the moment it comes up, with the evidence on screen — this is the
    /// case where guessing would be worse than asking.
    /// </summary>
    private TypeRuling Ask(string name, string? crmService, AdoOpinion opinion)
    {
        _prompts.Section($"DevOps cannot settle '{name}'", Tone.Warn);
        _prompts.Field("CRM says", crmService ?? "(no service catalogue)", Tone.Muted);
        _prompts.Blank();
        _prompts.Say(opinion.Detail);

        if (opinion.Evidence.Count > 0)
        {
            _prompts.Blank();
            foreach (var hit in opinion.Evidence.Where(h => h.WorkItemId > 0).Take(6))
                _prompts.Info($"      {hit.WorkItemId}  {Trim(hit.Title, 60)}", Tone.Muted);
        }

        // The titles are only half the backlog. The document lists live in the bodies of the
        // stories and in the workbooks attached to them — this name appears verbatim in user
        // story 27628 and in no title anywhere — and the live search cannot reach either,
        // because this server refuses a full-text query. So the local copy is read as well.
        ShowLocalHits(name);

        var answer = new Asker(_prompts).Ask("What should I do with this document type?", new[]
        {
            new Choice("Take CRM's answer", $"treat {crmService ?? "the document type's catalogue"} as correct",
                "The document type's own service catalogue is used, exactly as before this check " +
                "existed. Its documents stay in whatever group the path put them in."),

            new Choice("Needs a human", "send its documents to group 6, untouched",
                "Every document of this type is moved to group 6 and left alone, whatever its " +
                "path says. Nothing is uploaded, repointed or deleted for them."),

            new Choice("I will type the service", "say which service owns this document type",
                "Type the service name as CRM spells it. If it matches the document type's " +
                "catalogue the documents carry on; if it does not, they go to group 6."),

            new Choice("Search DevOps for my own words", "I will give you a better phrase to look for",
                "CRM and the backlog rarely word a document the same way. Type any phrase you " +
                "think the backlog uses — part of the name, a screen, a work item — and it is " +
                "searched live and shown to you, then this question comes back."),

            new Choice("Find the spreadsheet", "get the story and its attached workbook",
                "The document lists live in the spreadsheets attached to the stories, and no " +
                "work item title carries them. This finds those stories, gives you their links, " +
                "and fetches the workbooks where it can. Anything it cannot fetch you can " +
                "download and drop in the folder it names, and it reads them from there."),

            new Choice("Wait — I will go and look", "pause while I check, then search again",
                "Nothing happens until you come back. Go and read the story, the spreadsheet or " +
                "the backlog; when you press Enter everything is searched again from scratch, so " +
                "anything you changed in DevOps in the meantime is picked up."),

            new Choice("Skip for now", "decide later; ask me again next run",
                "Nothing is remembered. The documents keep the verdict the path gave them, and " +
                "this question comes back on the next scan.")
        }, defaultIndex: 0, allowBack: false, confirm: true);

        var index = answer.Kind == AnswerKind.Chosen ? answer.Index : 5;

        switch (index)
        {
            case 0:
                _decisions.Remember(name, DocumentTypeDecisions.AcceptCrm, opinion.Detail, "operator");
                return new TypeRuling(name, AdoVerdict.Agrees, crmService,
                    "You decided to take CRM's answer.", opinion.Evidence, "you");

            case 1:
                _decisions.Remember(name, DocumentTypeDecisions.NeedsHuman, opinion.Detail, "operator");
                return new TypeRuling(name, AdoVerdict.Disagrees, null,
                    "You decided this document type needs a human.", opinion.Evidence, "you");

            case 2:
                var typed = _prompts.ReadLine("  Which service owns it").Trim();

                if (typed.Length == 0 || typed.Equals("q", StringComparison.OrdinalIgnoreCase))
                    return Skipped(name, opinion);

                _decisions.Remember(name, typed, opinion.Detail, "operator");
                var agrees = DocumentTypeAuthority.SameService(typed, crmService);

                return new TypeRuling(name, agrees ? AdoVerdict.Agrees : AdoVerdict.Disagrees, typed,
                    agrees
                        ? $"You said '{typed}', which is what CRM says."
                        : $"You said '{typed}', but CRM says {crmService ?? "(nothing)"}.",
                    opinion.Evidence, "you");

            case 3:
                return SearchAgain(name, crmService, MyOwnWords(), opinion);

            case 4:
                FetchTheSpreadsheets(name, crmService);
                return Ask(name, crmService, opinion);

            case 5:
                _prompts.Blank();
                _prompts.Say("Take your time. Nothing is running and nothing has been changed.",
                    Tone.Muted);
                _prompts.Say($"Anything you download can go in {DropFolder()} — it is read from " +
                             "there.", Tone.Muted);
                _prompts.ReadLine("  Press Enter when you are ready");

                return SearchAgain(name, crmService, null, opinion);

            default:
                return Skipped(name, opinion);
        }
    }

    /// <summary>
    /// Finds the stories whose attachments should hold this document's list, fetches the
    /// workbooks where it can, and hands over the links where it cannot.
    ///
    /// Downloading it here rather than asking for it is the better half of the bargain — the
    /// tool is already signed in — but an attachment can be refused, or live behind a permission
    /// the sign-in does not carry. So the link and the folder are always printed: the operator
    /// can finish the job by hand, and the search reads whatever ends up in that folder.
    /// </summary>
    private void FetchTheSpreadsheets(string name, string? crmService)
    {
        if (_ado is null) return;

        var folder = DropFolder();
        Directory.CreateDirectory(folder);

        // The document's own name rarely appears in a title; the service's name finds its
        // stories, and the workbooks hang off those.
        var phrases = new[] { name, crmService }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        var found = new List<AdoAttachment>();

        foreach (var phrase in phrases)
        {
            try
            {
                found.AddRange(_ado.FindSpreadsheetsAsync(phrase, CancellationToken.None)
                    .GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                _prompts.Blank();
                _prompts.Warn($"Could not ask DevOps for attachments — {ex.GetType().Name}: {Innermost(ex)}");
                return;
            }

            if (found.Count > 0) break;
        }

        _prompts.Section("Spreadsheets attached to the backlog");

        if (found.Count == 0)
        {
            _prompts.Say("No work item found by these searches has a spreadsheet attached.");
            _prompts.Blank();
            _prompts.Say($"If you know of one, put it in {folder} and it will be read from there.",
                Tone.Muted);
            return;
        }

        foreach (var attachment in found.DistinctBy(a => a.Name).Take(10))
        {
            _prompts.Blank();
            _prompts.Info($"    {Trim(attachment.WorkItemTitle, 66)}", Tone.Strong);
            _prompts.Info($"      {_ado.LinkTo(attachment.WorkItemId)}", Tone.Muted);
            _prompts.Info($"      {attachment.Name}", Tone.Muted);

            var to = Path.Combine(folder, attachment.Name);

            if (File.Exists(to))
            {
                _prompts.Info("      already here", Tone.Good);
                continue;
            }

            try
            {
                var got = _ado.DownloadAttachmentAsync(attachment, to, CancellationToken.None)
                    .GetAwaiter().GetResult();

                _prompts.Info(got ? $"      downloaded to {to}" : "      could not be downloaded",
                    got ? Tone.Good : Tone.Warn);
            }
            catch (Exception ex)
            {
                _prompts.Info($"      could not be downloaded — {Innermost(ex)}", Tone.Warn);
            }
        }

        _prompts.Blank();
        _prompts.Say($"Whatever is not downloaded, open the link above, save the file into " +
                     $"{folder}, and choose \"Wait — I will go and look\". Everything in that " +
                     "folder is searched.", Tone.Muted);
    }

    /// <summary>Where downloaded and hand-placed backlog files are read from.</summary>
    private string DropFolder() => _dropFolder ?? Path.Combine(Path.GetTempPath(), "docfix-backlog-files");

    private string? MyOwnWords()
    {
        _prompts.Blank();
        _prompts.Say("Type a phrase the backlog might use. It is matched inside work item " +
                     "titles, so a few words out of the middle work better than the whole name.",
                     Tone.Muted);

        var typed = _prompts.ReadLine("  Search DevOps for").Trim();

        return typed.Length == 0 || typed.Equals("q", StringComparison.OrdinalIgnoreCase) ? null : typed;
    }

    /// <summary>
    /// Goes back to the backlog — with the operator's phrase, or with the original search run
    /// again — and returns to the same question carrying whatever came back. The point is that
    /// the question can be left and come back better informed, rather than answered blind.
    /// </summary>
    private TypeRuling SearchAgain(string name, string? crmService, string? phrase, AdoOpinion before)
    {
        if (_ado is null) return Skipped(name, before);

        // A fresh look means a fresh look: anything settled a moment ago by an outage or by a
        // thin result should not be held against the new answer.
        _unreachable = null;

        var opinion = phrase is null
            ? AskDevOpsAsync(name, crmService, CancellationToken.None).GetAwaiter().GetResult()
            : WeighOneTerm(name, crmService, phrase);

        _prompts.Blank();
        _prompts.Say(opinion.Detail, opinion.Verdict == AdoVerdict.CannotTell ? Tone.Warn : Tone.Good);

        if (opinion.Verdict != AdoVerdict.CannotTell)
            return new TypeRuling(name, opinion.Verdict, opinion.Service, opinion.Detail,
                opinion.Evidence, phrase is null ? "DevOps, looked at again" : $"DevOps, searched for '{phrase}'");

        return Ask(name, crmService, opinion);
    }

    private AdoOpinion WeighOneTerm(string name, string? crmService, string phrase)
    {
        try
        {
            var hits = _ado!.FindByTitleAsync(phrase, CancellationToken.None).GetAwaiter().GetResult();
            return DocumentTypeAuthority.Weigh(name, crmService, hits);
        }
        catch (Exception ex)
        {
            return new AdoOpinion(AdoVerdict.CannotTell, null, Array.Empty<AdoHit>(),
                $"That search could not be run — {ex.GetType().Name}: {Innermost(ex)}");
        }
    }

    /// <summary>
    /// What the local copy of the backlog has — the story bodies and the spreadsheets attached
    /// to them, which is where the document lists actually live.
    /// </summary>
    private void ShowLocalHits(string name)
    {
        // Both places: the synced copy of the backlog, and whatever has been downloaded or
        // dropped in by hand since.
        var hits = LocalBacklogSearch.Find(_localBacklog, name)
            .Concat(LocalBacklogSearch.Find(_dropFolder, name))
            .DistinctBy(h => h.File)
            .ToList();

        if (hits.Count == 0) return;

        _prompts.Blank();
        _prompts.Say($"The name does appear in {hits.Count} file(s) of the local backlog copy:");

        foreach (var hit in hits)
        {
            _prompts.Info($"      {Trim(hit.Title ?? Path.GetFileName(hit.File), 62)}", Tone.Muted);
            if (hit.Service is not null)
                _prompts.Info($"        service: {hit.Service}", Tone.Good);
        }
    }

    private TypeRuling Skipped(string name, AdoOpinion opinion)
    {
        _prompts.Say("Left undecided. Nothing was written down, and I will ask again next run.",
            Tone.Muted);

        return new TypeRuling(name, AdoVerdict.CannotTell, null,
            "Left undecided: " + opinion.Detail, opinion.Evidence, "undecided");
    }

    private static string Trim(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    /// <summary>
    /// The message worth reading. "An error occurred while sending the request" is the outer
    /// wrapper of every HttpClient failure and says nothing; the cause underneath names the
    /// host, the refusal or the timeout.
    /// </summary>
    private static string Innermost(Exception ex)
    {
        var cause = ex;
        while (cause.InnerException is not null) cause = cause.InnerException;

        return cause.Message;
    }
}
