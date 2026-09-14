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
        new(documentType, AdoVerdict.CannotTell, null, "DevOps was not consulted.",
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

    /// <param name="ado">Null when DevOps is not configured; every type then reads "not checked".</param>
    public DocumentTypeCheck(IAdoClient? ado, DocumentTypeDecisions decisions, IPrompts prompts)
    {
        _ado = ado;
        _decisions = decisions;
        _prompts = prompts;
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

        if (_ado is null) return TypeRuling.NotChecked(name);

        var opinion = await AskDevOpsAsync(name, crmService, ct);

        return opinion.Verdict == AdoVerdict.CannotTell
            ? Ask(name, crmService, opinion)
            : new TypeRuling(name, opinion.Verdict, opinion.Service, opinion.Detail,
                opinion.Evidence, "DevOps");
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
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new AdoOpinion(AdoVerdict.CannotTell, null, Array.Empty<AdoHit>(),
                    $"DevOps could not be reached: {ex.Message}");
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

            new Choice("Skip for now", "decide later; ask me again next run",
                "Nothing is remembered. The documents keep the verdict the path gave them, and " +
                "this question comes back on the next scan.")
        }, defaultIndex: 0, allowBack: false);

        var index = answer.Kind == AnswerKind.Chosen ? answer.Index : 3;

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

            default:
                return Skipped(name, opinion);
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
}
