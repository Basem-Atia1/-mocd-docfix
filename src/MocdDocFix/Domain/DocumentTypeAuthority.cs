namespace MocdDocFix.Domain;

/// <summary>What Azure DevOps has to say about a document type, if anything.</summary>
public enum AdoVerdict
{
    /// <summary>DevOps places this document in the service CRM says it belongs to.</summary>
    Agrees,

    /// <summary>DevOps places it somewhere else, with enough evidence to be worth stopping for.</summary>
    Disagrees,

    /// <summary>Nothing found, too little found, or too much found. The operator is asked.</summary>
    CannotTell
}

/// <param name="WorkItemId">The work item, so the operator can open it and judge for themselves.</param>
/// <param name="Service">The service named in the title, or null when the title does not name one.</param>
public sealed record AdoHit(int WorkItemId, string Title, string? Service);

public sealed record AdoOpinion(
    AdoVerdict Verdict,
    string? Service,
    IReadOnlyList<AdoHit> Evidence,
    string Detail);

/// <summary>
/// Reads a service off Azure DevOps work item titles and weighs it against what CRM says.
///
/// The evidence is test case titles, which name the service before anything else:
///   NPOP|Employee Appointment Request|Documents|Verify "A copy of ..."
///   Portal | NPOP- Register New Member | Documents | Verify when ...
///
/// It is deliberately hard to make this disagree. A single hit never overrules CRM: searching
/// "FAHR Document" against the live backlog returns exactly one work item — a By-Laws test named
/// "Verify no FAHR document placeholder is created" — which mentions the words in passing and has
/// nothing to do with owning that document type. Taken at face value that one hit would have
/// contradicted two CRM authorities and parked a healthy document as a conflict. So thin evidence
/// is reported as "cannot tell" and put to the operator, never resolved by guessing.
/// </summary>
public static class DocumentTypeAuthority
{
    /// <summary>Above this, the search term was too generic to mean anything ("FAHR" → 694).</summary>
    public const int TooMany = 25;

    /// <summary>Below this, there is not enough to overrule CRM. The FAHR case is exactly one.</summary>
    public const int EnoughToOverrule = 2;

    /// <summary>Words that carry no identifying weight in a document name.</summary>
    private static readonly string[] Noise =
        { "a", "an", "the", "of", "and", "or", "for", "to", "in", "copy", "document", "documents" };

    /// <summary>
    /// What to search for, in order, stopping at the first term that finds something usable.
    ///
    /// The full name often finds nothing — CRM's "A copy of the certificate of good conduct and
    /// behavior" returns zero, while "good conduct" returns four — because the two systems were
    /// written by different people. So the phrase is relaxed a step at a time rather than once.
    /// </summary>
    public static IReadOnlyList<string> SearchTerms(string? documentTypeName)
    {
        var name = (documentTypeName ?? string.Empty).Trim();
        if (name.Length == 0) return Array.Empty<string>();

        var terms = new List<string> { name };

        var words = name
            .Split(new[] { ' ', '\t', '-', '_', '/', '\\', ',', '(', ')' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('\'', '"', '.', ':'))
            .Where(w => w.Length > 0)
            .ToList();

        var meaningful = words
            .Where(w => !Noise.Contains(w, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (meaningful.Count > 0 && meaningful.Count < words.Count)
            Add(string.Join(' ', meaningful));

        // The two longest words carry the most identity: "certificate good conduct behavior"
        // becomes "certificate behavior", which still finds the right tests.
        if (meaningful.Count > 2)
            Add(string.Join(' ', meaningful.OrderByDescending(w => w.Length).Take(2)));

        // A single short word is an acronym or a category, never an identity. "FAHR" matches 694
        // work items; searching it would produce noise and nothing else.
        return terms.Where(t => t.Length >= 6 && t.Contains(' ')).ToList();

        void Add(string term)
        {
            if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
        }
    }

    /// <summary>
    /// The service a work item title names. Titles are pipe-delimited and lead with the channel
    /// (Portal, NPOP, CRM, MoCE) then the service, so the first segment that is neither a channel
    /// nor a step ("Verify …", "Documents") is the one that names it.
    /// </summary>
    public static string? ServiceInTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        foreach (var raw in title!.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Trim();

            foreach (var channel in new[] { "Portal", "NPOP", "CRM", "MoCE", "MoCD" })
            {
                if (segment.StartsWith(channel, StringComparison.OrdinalIgnoreCase))
                    segment = segment[channel.Length..].TrimStart('-', ' ', ':');
            }

            segment = segment.Trim();

            if (segment.Length < 6) continue;
            if (segment.StartsWith("Verify", StringComparison.OrdinalIgnoreCase)) continue;
            if (segment.StartsWith("Document", StringComparison.OrdinalIgnoreCase)) continue;
            if (segment.StartsWith("Check", StringComparison.OrdinalIgnoreCase)) continue;

            return segment;
        }

        return null;
    }

    /// <summary>Whether two service names are the same service, written by two different hands.</summary>
    public static bool SameService(string? left, string? right)
    {
        var a = Normalise(left);
        var b = Normalise(right);

        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;

        // "Employee Appointment" and "Employee Appointment Request" are one service. A short
        // fragment is not allowed to swallow a longer name by accident.
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 10 && longer.Contains(shorter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdict, given what CRM says and what the search found.
    /// </summary>
    public static AdoOpinion Weigh(string documentTypeName, string? crmService, IReadOnlyList<AdoHit> hits)
    {
        if (hits.Count == 0)
            return new AdoOpinion(AdoVerdict.CannotTell, null, hits,
                $"DevOps has no work item naming '{documentTypeName}'.");

        if (hits.Count > TooMany)
            return new AdoOpinion(AdoVerdict.CannotTell, null, hits.Take(5).ToList(),
                $"'{documentTypeName}' matches {hits.Count} work items — too many to mean " +
                "anything. The name is too general to identify a service.");

        var named = hits.Where(h => h.Service is not null).ToList();

        if (named.Count == 0)
            return new AdoOpinion(AdoVerdict.CannotTell, null, hits,
                $"{hits.Count} work item(s) name '{documentTypeName}', but none of their titles " +
                "say which service they belong to.");

        var agreeing = named.Where(h => SameService(h.Service, crmService)).ToList();

        if (agreeing.Count > 0)
            return new AdoOpinion(AdoVerdict.Agrees, crmService, agreeing,
                $"DevOps puts '{documentTypeName}' under {crmService} too " +
                $"({agreeing.Count} of {hits.Count} work item(s)).");

        var byService = named
            .GroupBy(h => h.Service!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        var best = byService[0];

        // One mention is a mention, not a finding. This is the FAHR Document case.
        if (best.Count() < EnoughToOverrule)
            return new AdoOpinion(AdoVerdict.CannotTell, null, named,
                $"Only {named.Count} work item mentions '{documentTypeName}', under " +
                $"'{best.Key}'. That is too thin to contradict CRM — it may be a passing " +
                "mention rather than the service that owns the document.");

        return new AdoOpinion(AdoVerdict.Disagrees, best.Key, best.ToList(),
            $"DevOps puts '{documentTypeName}' under '{best.Key}' ({best.Count()} work items), " +
            $"but CRM's document type says {crmService ?? "(nothing)"}.");
    }

    private static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        // Punctuation becomes a space rather than disappearing, so "By-Laws" and "By Laws" —
        // both of which are used, in CRM and in DevOps — come out as the same two words.
        var kept = value!
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ')
            .ToArray();

        return string.Join(' ', new string(kept).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
