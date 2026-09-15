namespace MocdDocFix.Domain;

/// <summary>What Azure DevOps has to say about a document type, if anything.</summary>
public enum AdoVerdict
{
    /// <summary>DevOps places this document in the service CRM says it belongs to.</summary>
    Agrees,

    /// <summary>DevOps places it somewhere else, with enough evidence to be worth stopping for.</summary>
    Disagrees,

    /// <summary>Nothing found, too little found, or too much found. The operator is asked.</summary>
    CannotTell,

    /// <summary>
    /// DevOps was never consulted — not set up, or switched off. Kept apart from CannotTell on
    /// purpose: "I asked and could not tell" and "I never asked" are different facts, and
    /// reporting them with one word is how a check that silently never ran goes unnoticed.
    /// </summary>
    NotChecked
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

        // The whole name with its noisy ends cut off, and everything between them kept. "A Copy
        // of Board of Director's Decision" becomes "Board of Director's Decision" — which DevOps
        // does write, and which the full name does not match. It has to come second, ahead of
        // the windows: it is still one contiguous run of characters, so WIQL CONTAINS can still
        // find it, and it carries more of the name than any shorter window does. Add() skips it
        // when the name had no noisy ends, so a clean name never spends two of its six slots
        // saying one thing.
        var from = 0;
        var to = words.Count - 1;

        while (from <= to && IsNoise(words[from])) from++;
        while (to >= from && IsNoise(words[to])) to--;

        if (to - from + 1 >= 2) Add(string.Join(' ', words.Skip(from).Take(to - from + 1)));

        // WIQL CONTAINS matches a run of characters, not a bag of words, so a term has to be a
        // phrase that really appears. Reshuffling "A Copy of Certificate of Good Conduct and
        // Behavior" into "Certificate Behavior" produced a phrase in no title anywhere and found
        // nothing, while the contiguous "Good Conduct" finds four work items.
        var windows = new List<(string Term, bool AllMeaningful, int Length)>();

        for (var length = words.Count - 1; length >= 2; length--)
        {
            for (var start = 0; start + length <= words.Count; start++)
            {
                var window = words.Skip(start).Take(length).ToList();

                // A window has to begin and end on a word that carries identity — "of Good" and
                // "Conduct and" are as useless as the reshuffled version.
                if (IsNoise(window[0]) || IsNoise(window[^1])) continue;

                windows.Add((string.Join(' ', window), window.All(w => !IsNoise(w)), length));
            }
        }

        // Windows of nothing but meaningful words come first, longest of those first. For "A Copy
        // of Certificate of Good Conduct and Behavior" that is "Good Conduct" — the phrase DevOps
        // actually uses, and four work items deep. Working strictly longest-first instead spent
        // the whole budget on long phrasings nobody wrote and never reached it.
        foreach (var window in windows.OrderBy(w => w.AllMeaningful ? 0 : 1).ThenByDescending(w => w.Length))
            Add(window.Term);

        // A single short word is an acronym or a category, never an identity. "FAHR" matches 694
        // work items; searching it would produce noise and nothing else.
        return terms
            .Where(t => t.Length >= 6 && t.Contains(' '))
            .Take(MostTermsWeWillTry)
            .ToList();

        void Add(string term)
        {
            if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
        }
    }

    /// <summary>
    /// How many phrasings are worth a live query before giving up and asking the operator. Each
    /// one is a round trip, and by the fourth the phrase is short enough to be meaningless.
    /// </summary>
    public const int MostTermsWeWillTry = 6;

    private static bool IsNoise(string word) =>
        Noise.Contains(word, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How close the words of a name have to be to each other before their being in the same file
    /// means they are the same phrase.
    ///
    /// The matcher below is order-free, which is what lets CRM's "A copy of the certificate of
    /// good conduct and behavior" find a story that writes the same thing in another order.
    /// Order-free with no distance limit is useless: a workbook's shared string table is every
    /// cell value in the document, a few hundred kilobytes, and "all of these words appear
    /// somewhere in it" is true of almost any name you care to ask about. The window is what
    /// keeps the rule honest.
    /// </summary>
    public const int NearbyWindow = 160;

    /// <summary>
    /// The words of a name that carry identity: the noise words dropped, and anything under three
    /// characters with them. "Director's" normalises to "director s", and the orphaned "s" is no
    /// more use than "of".
    /// </summary>
    public static IReadOnlyList<string> MeaningfulWords(string? phrase) =>
        Normalise(phrase)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !IsNoise(w))
            .ToList();

    /// <summary>
    /// Whether a body of text — a story description, a test's steps, a workbook's strings — is
    /// talking about this document type.
    ///
    /// Every meaningful word has to be there, and they all have to fall inside one
    /// <see cref="NearbyWindow"/> span. A name that boils down to a single meaningful word gets
    /// no set to match, so the phrase itself has to appear: "Passport Copy" is not answered by a
    /// sentence that merely says "passport".
    /// </summary>
    public static bool Mentions(string? text, string? phrase)
    {
        var haystack = Normalise(text);
        if (haystack.Length == 0) return false;

        var words = MeaningfulWords(phrase);

        if (words.Count < 2)
        {
            var whole = Normalise(phrase);
            return whole.Length > 0 && haystack.Contains(whole, StringComparison.Ordinal);
        }

        // Whole words, not runs of characters, so "conduct" is not answered by "misconduct" —
        // and stemmed, so CRM's "Director's Decision" is answered by the backlog's "Directors'
        // Decision". The two systems were written by different hands and disagree about plurals
        // constantly; refusing to match on that alone would be the same miss in a new place.
        var wanted = words.Select(Stem).ToList();
        var found = new List<(int At, int Word)>();
        var hit = new bool[wanted.Count];

        var at = 0;

        while (at < haystack.Length)
        {
            var end = haystack.IndexOf(' ', at);
            if (end < 0) end = haystack.Length;

            var stem = Stem(haystack[at..end]);

            for (var word = 0; word < wanted.Count; word++)
            {
                if (!string.Equals(stem, wanted[word], StringComparison.Ordinal)) continue;

                found.Add((at, word));
                hit[word] = true;
            }

            at = end + 1;
        }

        // One word missing settles it — no window can cover a word that is not there.
        if (hit.Any(h => !h)) return false;

        var seen = new int[words.Count];
        var distinct = 0;
        var first = 0;

        for (var last = 0; last < found.Count; last++)
        {
            if (seen[found[last].Word]++ == 0) distinct++;

            while (distinct == words.Count)
            {
                if (found[last].At - found[first].At <= NearbyWindow) return true;

                if (--seen[found[first].Word] == 0) distinct--;
                first++;
            }
        }

        return false;
    }

    /// <summary>
    /// A word with a plural "s" taken off it, so "director" and "directors" are one word.
    ///
    /// Deliberately the crudest rule that works: a trailing "s", unless the word ends in "ss"
    /// and taking it off would maul a word that was never plural ("address"). Anything cleverer
    /// is a stemmer, and a stemmer would start folding words a document name relies on being
    /// different.
    /// </summary>
    private static string Stem(string word) =>
        word.Length >= 4 && word[^1] == 's' && word[^2] != 's' ? word[..^1] : word;

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
