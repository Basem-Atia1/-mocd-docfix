# Live DevOps search for document types — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the run asking the operator about a document type whose answer DevOps is holding — by asking DevOps live for story bodies and workbooks, not only titles, and letting what comes back settle the verdict.

**Architecture:** `DocumentTypeCheck.AskDevOpsAsync` becomes a three-stage enquiry — titles (WIQL, as today), then bodies (WIQL picks candidates, we fetch their fields and match in process, because this server refuses full-text WIQL), then the drop folder of downloaded and hand-placed workbooks. Every stage produces ordinary `AdoHit`s and goes through the existing `DocumentTypeAuthority.Weigh`, so the safety bar — one hit may confirm CRM, two are needed to contradict it — is enforced in one place for all three. Matching gains an order-free proximity rule so noise words like `A Copy of` stop guaranteeing a miss.

**Tech Stack:** C# / .NET, xUnit, `HttpClient` over NTLM, Azure DevOps REST API 6.0 (WIQL + work items), `System.IO.Compression` for workbooks.

**Spec:** `docs/specs/2026-09-15-live-ado-document-type-search-design.md`

## Global Constraints

- **Read-only toward DevOps.** No task may add a method that writes, patches or posts anything except the existing WIQL query POST. `IAdoClient` stays a reader.
- **`Weigh` is the only judge.** No stage may invent its own agree/disagree rule. `TooMany = 25`, `EnoughToOverrule = 2` keep their current meaning and values.
- **A failed backlog call never kills a run.** Every new network call is wrapped exactly like the existing one: catch `OperationCanceledException` when `ct.IsCancellationRequested` and rethrow; catch everything else, set `_unreachable`, return `CannotTell`.
- **Existing fakes must keep compiling.** New `IAdoClient` members get default interface implementations.
- **Test command:** `dotnet test tests/MocdDocFix.Tests` from `D:\mocd-docfix`. Filter a single test with `--filter "FullyQualifiedName~<TestName>"`.
- **Comment style:** this codebase explains *why*, in prose, above the thing. Match it. No `// increment i` comments.

### How the test doubles actually work

Read this before writing a test. These are the real members; do not invent others.

- **`FakePrompts` is non-interactive unless you give it `Keys`.** `Interactive => Keys is not null`. With no keys the menu falls back to typing a number, so an answer is queued as text and the options are **1-based**:

  ```csharp
  _prompts.ReadLineQueue = new Queue<string>(new[] { "1" });   // "Take CRM's answer"
  ```

  The seven options of the document-type menu are `1` take CRM's answer, `2` needs a human, `3` type the service, `4` search my own words, `5` find the spreadsheet, `6` wait, `7` skip.

- **What was printed is `_prompts.Messages`**, a `List<string>`. There is no `Lines`.
- **What was asked is `_prompts.Questions`.** `Assert.Empty(_prompts.Questions)` is how "it never had to ask" is proven.
- **`FakeHttpMessageHandler` queues responses in order** with `Enqueue(HttpStatusCode, string json)` and records `Requests` (a `List<HttpRequestMessage>`) and `RequestBodies`. There is no URL-matching `Respond`. A WIQL search is two calls: the POST to `_apis/wit/wiql`, then the GET of the ids — so queue two responses, in that order.

---

### Task 1: The order-free proximity matcher

The rule that makes `a good conduct life` find `good conduct … behaviour … life`, and the guard that stops it firing on any large file that happens to contain those three words far apart.

**Files:**
- Modify: `src/MocdDocFix/Domain/DocumentTypeAuthority.cs`
- Test: `tests/MocdDocFix.Tests/DocumentTypeAuthorityTests.cs`

**Interfaces:**
- Consumes: nothing — pure, no new dependencies.
- Produces:
  - `public const int NearbyWindow = 160;`
  - `public static IReadOnlyList<string> MeaningfulWords(string? phrase)`
  - `public static bool Mentions(string? text, string? phrase)`

- [ ] **Step 1: Write the failing tests**

Append to `tests/MocdDocFix.Tests/DocumentTypeAuthorityTests.cs`:

```csharp
// ---- the order-free matcher over story bodies and workbooks ----

[Fact]
public void The_words_that_carry_identity_survive_and_the_noise_does_not()
{
    Assert.Equal(new[] { "good", "conduct", "life" },
        DocumentTypeAuthority.MeaningfulWords("a good conduct life"));

    // "Copy" and "of" are noise; "A" and the possessive "s" are too short to identify anything.
    Assert.Equal(new[] { "board", "director", "decision" },
        DocumentTypeAuthority.MeaningfulWords("A Copy of Board of Director's Decision"));
}

[Fact]
public void A_name_written_in_another_order_with_other_words_between_is_still_a_mention()
{
    const string body =
        "The system shall display the below list of documents. " +
        "Certificate of good conduct and behavior, valid for the life of the appointment.";

    Assert.True(DocumentTypeAuthority.Mentions(body, "a good conduct life"));
}

[Fact]
public void The_same_words_scattered_across_a_whole_file_are_not_a_mention()
{
    // Exactly the words, far enough apart that they are three unrelated cells of a workbook
    // rather than one phrase. Without the window this is the false positive that would fire on
    // almost any shared string table.
    var body = "good " + new string('x', DocumentTypeAuthority.NearbyWindow) +
               " conduct " + new string('y', DocumentTypeAuthority.NearbyWindow) + " life";

    Assert.False(DocumentTypeAuthority.Mentions(body, "a good conduct life"));
}

[Fact]
public void Punctuation_and_case_are_not_allowed_to_hide_a_mention()
{
    Assert.True(DocumentTypeAuthority.Mentions(
        "Upload the BOARD OF DIRECTORS' DECISION here.", "A Copy of Board of Director's Decision"));
}

[Fact]
public void A_name_with_one_meaningful_word_falls_back_to_looking_for_the_name_itself()
{
    // One word is not a set — "passport" alone would match any sentence mentioning a passport,
    // so the whole phrase has to appear.
    Assert.True(DocumentTypeAuthority.Mentions("Attach the passport copy.", "Passport Copy"));
    Assert.False(DocumentTypeAuthority.Mentions("Attach the passport.", "Passport Copy"));
}

[Fact]
public void Nothing_to_match_against_is_never_a_mention()
{
    Assert.False(DocumentTypeAuthority.Mentions(null, "Board Decision"));
    Assert.False(DocumentTypeAuthority.Mentions("", "Board Decision"));
    Assert.False(DocumentTypeAuthority.Mentions("Board Decision", null));
    Assert.False(DocumentTypeAuthority.Mentions("Board Decision", "   "));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DocumentTypeAuthorityTests"
```

Expected: FAIL to build — `'DocumentTypeAuthority' does not contain a definition for 'MeaningfulWords'`.

- [ ] **Step 3: Write the implementation**

In `src/MocdDocFix/Domain/DocumentTypeAuthority.cs`, add these members. Put them directly after `IsNoise`, and make `Normalise` non-private is *not* needed — it stays private and is used from here.

```csharp
/// <summary>
/// How close the words of a name have to be to each other before their being in the same file
/// means they are the same phrase.
///
/// The matcher below is order-free, which is what lets CRM's "A copy of the certificate of good
/// conduct and behavior" find a story that writes the same thing in another order. Order-free
/// with no distance limit is useless: a workbook's shared string table is every cell value in
/// the document, a few hundred kilobytes, and "all of these words appear somewhere in it" is
/// true of almost any name you care to ask about. The window is what keeps the rule honest.
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
/// <see cref="NearbyWindow"/> span. A name that boils down to a single meaningful word gets no
/// set to match, so the phrase itself has to appear: "Passport Copy" is not answered by a
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

    // Where each word turns up, as (position, which word). Sorted by position, the answer is the
    // classic smallest window that covers every word at least once.
    var found = new List<(int At, int Word)>();

    for (var word = 0; word < words.Count; word++)
    {
        var at = 0;
        var hit = false;

        while ((at = IndexOfWholeWord(haystack, words[word], at)) >= 0)
        {
            found.Add((at, word));
            hit = true;
            at += words[word].Length;
        }

        // One word missing settles it — no window can cover a word that is not there.
        if (!hit) return false;
    }

    found.Sort((left, right) => left.At.CompareTo(right.At));

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
/// Where a whole word sits in normalised text. Normalising has already turned every separator
/// into a single space, so a word is whole when a space or an end of text sits either side of
/// it — which is what stops "conduct" being answered by "misconduct".
/// </summary>
private static int IndexOfWholeWord(string haystack, string word, int from)
{
    for (var at = haystack.IndexOf(word, from, StringComparison.Ordinal);
         at >= 0;
         at = haystack.IndexOf(word, at + 1, StringComparison.Ordinal))
    {
        var before = at == 0 || haystack[at - 1] == ' ';
        var after = at + word.Length == haystack.Length || haystack[at + word.Length] == ' ';

        if (before && after) return at;
    }

    return -1;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DocumentTypeAuthorityTests"
```

Expected: PASS, all tests in the class including the ones that were there before.

- [ ] **Step 5: Commit**

```bash
git add src/MocdDocFix/Domain/DocumentTypeAuthority.cs tests/MocdDocFix.Tests/DocumentTypeAuthorityTests.cs
git commit -m "feat(search): match a document name by its words, near each other, in any order"
```

---

### Task 2: Promote the end-trimmed name to the second thing we search for

`A Copy of Board of Director's Decision` → `Board of Director's Decision`. The string is already generated by the window loop, but it contains *of*, so it sorts behind every noise-free window and `MostTermsWeWillTry = 6` can cut it off before it is tried.

**Files:**
- Modify: `src/MocdDocFix/Domain/DocumentTypeAuthority.cs` — `SearchTerms`
- Test: `tests/MocdDocFix.Tests/DocumentTypeAuthorityTests.cs`

**Interfaces:**
- Consumes: `MeaningfulWords` from Task 1 is *not* used here — trimming keeps interior words, so it works off `IsNoise` directly.
- Produces: no new signature; `SearchTerms` ordering changes.

- [ ] **Step 1: Write the failing tests**

Append to `tests/MocdDocFix.Tests/DocumentTypeAuthorityTests.cs`:

```csharp
[Fact]
public void The_name_with_its_noisy_ends_trimmed_is_the_second_thing_we_try()
{
    var terms = DocumentTypeAuthority.SearchTerms("A Copy of Board of Director's Decision");

    Assert.Equal("A Copy of Board of Director's Decision", terms[0]);
    Assert.Equal("Board of Director's Decision", terms[1]);
}

[Fact]
public void A_name_with_nothing_noisy_on_its_ends_is_not_searched_for_twice()
{
    var terms = DocumentTypeAuthority.SearchTerms("Board of Director's Decision");

    Assert.Equal("Board of Director's Decision", terms[0]);
    Assert.DoesNotContain(terms.Skip(1), t =>
        t.Equals("Board of Director's Decision", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void A_name_that_is_nothing_but_noise_still_searches_for_something_or_nothing_safely()
{
    // "A copy of the document" trims away to nothing. It must not throw, and it must not
    // produce an empty search term that would match every work item in the project.
    var terms = DocumentTypeAuthority.SearchTerms("A copy of the document");

    Assert.DoesNotContain(terms, string.IsNullOrWhiteSpace);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DocumentTypeAuthorityTests"
```

Expected: FAIL — `The_name_with_its_noisy_ends_trimmed_is_the_second_thing_we_try` reports `terms[1]` as `"Director's Decision"` (the longest noise-free window), not `"Board of Director's Decision"`.

- [ ] **Step 3: Write the implementation**

In `SearchTerms`, insert the trimmed core immediately after the `words` list is built and **before** the `windows` loop. The full method reads:

```csharp
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

    // The whole name with its noisy ends cut off, and everything between them kept. "A Copy of
    // Board of Director's Decision" becomes "Board of Director's Decision" — which DevOps does
    // write, and which the full name does not match. It has to come second, ahead of the
    // windows: it is still one contiguous run of characters, so WIQL CONTAINS can still find it,
    // and it carries more of the name than any shorter window does. Add() skips it when the name
    // had no noisy ends, so a clean name never spends two of its six slots saying one thing.
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
```

Note the only change is the `from`/`to` block and its comment. Everything else is as it was.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DocumentTypeAuthorityTests"
```

Expected: PASS. If a pre-existing test asserting term order now fails, the trimmed core has displaced a window — read that test, and if its intent is "the noise-free window is tried", assert it is still *present* rather than at a fixed index.

- [ ] **Step 5: Commit**

```bash
git add src/MocdDocFix/Domain/DocumentTypeAuthority.cs tests/MocdDocFix.Tests/DocumentTypeAuthorityTests.cs
git commit -m "feat(search): try the name without its noisy ends before shortening it further"
```

---

### Task 3: Ask DevOps for work item bodies

WIQL picks the candidates by title; we fetch their fields and read them ourselves. This is the whole of "search the descriptions live" on a server that answers TF401349 to a full-text query.

**Files:**
- Modify: `src/MocdDocFix/Clients/AdoClient.cs`
- Test: `tests/MocdDocFix.Tests/AdoClientBodyTests.cs` (create)
- Modify: `tests/MocdDocFix.Tests/Fakes/FakeAdoClient.cs`

**Interfaces:**
- Consumes: the existing private `AdoClient.SearchAsync(string, CancellationToken)`, `BatchSize = 180`.
- Produces:
  - `public sealed record AdoWorkItemText(int Id, string Title, string Text);`
  - `IAdoClient.FindCandidatesAsync(string phrase, CancellationToken ct)` → `Task<IReadOnlyList<AdoWorkItemText>>`, default implementation returns empty.
  - `AdoClient.MostCandidates = 60`
  - `FakeAdoClient.Bodies` → `Dictionary<string, List<AdoWorkItemText>>`, and `FakeAdoClient.CandidatesAskedFor` → `List<string>`.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/AdoClientBodyTests.cs`:

```csharp
using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Reading work item bodies off the server. The descriptions are HTML and the test steps are
/// XML with HTML encoded inside them; both have to come back as something a word can be found in.
/// </summary>
public class AdoClientBodyTests
{
    /// <summary>A search is two calls: the WIQL POST, then the GET of the ids it returned.</summary>
    private static AdoClient Client(FakeHttpMessageHandler handler) =>
        new(handler, "https://devops.example/MOCD", "NPO - Phase 2");

    [Fact]
    public async Task A_description_comes_back_as_words_not_markup()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, """{"workItems":[{"id":27628}]}""")
            .Enqueue(HttpStatusCode.OK,
                """
                {"value":[{"id":27628,"fields":{
                  "System.Title":"1.1.6 NPOP- Employee Appointment Request Form- Documents",
                  "System.Description":"<div>The system shall display:<br/>A Copy of Board of Director&#39;s Decision</div>"
                }}]}
                """);

        var found = await Client(handler).FindCandidatesAsync("Employee Appointment Request",
            CancellationToken.None);

        var item = Assert.Single(found);
        Assert.Equal(27628, item.Id);
        Assert.Equal("1.1.6 NPOP- Employee Appointment Request Form- Documents", item.Title);
        Assert.Contains("A Copy of Board of Director's Decision", item.Text);
        Assert.DoesNotContain("<div>", item.Text);
    }

    [Fact]
    public async Task Test_steps_survive_being_xml_with_html_encoded_inside_them()
    {
        // This is the shape the server really returns: <steps> wrapping <parameterizedString>
        // whose content is HTML that has been encoded a second time.
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, """{"workItems":[{"id":31000}]}""")
            .Enqueue(HttpStatusCode.OK,
                """
                {"value":[{"id":31000,"fields":{
                  "System.Title":"NPOP|Employee Appointment Request|Documents|Verify uploads",
                  "Microsoft.VSTS.TCM.Steps":"<steps id=\"0\"><step id=\"2\"><parameterizedString isformatted=\"true\">&lt;P&gt;Upload the Good Conduct certificate&lt;/P&gt;</parameterizedString></step></steps>"
                }}]}
                """);

        var found = await Client(handler).FindCandidatesAsync("Employee Appointment Request",
            CancellationToken.None);

        Assert.Contains("Upload the Good Conduct certificate", Assert.Single(found).Text);
    }

    [Fact]
    public async Task A_phrase_nothing_matches_asks_for_no_bodies_at_all()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, """{"workItems":[]}""");

        Assert.Empty(await Client(handler).FindCandidatesAsync("nothing", CancellationToken.None));

        // One call only — the WIQL POST. Asking for the bodies of no work items would be a
        // wasted round trip, and with nothing queued the fake would answer 500.
        Assert.Single(handler.Requests);
    }
}
```

The test needs a constructor that takes a handler. Add one to `AdoClient`, public — it is no more dangerous than the one beside it, and it keeps the test project free of `InternalsVisibleTo`:

```csharp
/// <summary>
/// Over a handler of your choosing, so the wire can be faked. The NTLM constructor above builds
/// its own handler and is what the application uses.
/// </summary>
public AdoClient(HttpMessageHandler handler, string collectionUrl, string project)
{
    _project = project;

    _http = new HttpClient(handler)
    {
        BaseAddress = new Uri(collectionUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromMinutes(2)
    };
    _http.DefaultRequestHeaders.Add("Accept", "application/json");
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~AdoClientBodyTests"
```

Expected: FAIL to build — `'AdoClient' does not contain a definition for 'FindCandidatesAsync'`.

- [ ] **Step 3: Write the implementation**

In `src/MocdDocFix/Clients/AdoClient.cs`, add `using System.Text.RegularExpressions;` and `using System.Net;` (already present), then the record beside `AdoAttachment`:

```csharp
/// <param name="Text">
/// Description, acceptance criteria and test steps, stripped of markup and run together. Enough
/// to answer "is this document name written down in this work item", which is the only question
/// asked of it.
/// </param>
public sealed record AdoWorkItemText(int Id, string Title, string Text);
```

the interface member on `IAdoClient`:

```csharp
/// <summary>
/// Work items whose title matches a phrase, with their bodies read out as plain text.
///
/// This is how a description is searched on a server that refuses to search one: WIQL picks the
/// candidates by title — the one thing it will match — and the bodies are fetched and read here.
/// </summary>
Task<IReadOnlyList<AdoWorkItemText>> FindCandidatesAsync(string phrase, CancellationToken ct)
    => Task.FromResult<IReadOnlyList<AdoWorkItemText>>(Array.Empty<AdoWorkItemText>());
```

and the implementation on `AdoClient`:

```csharp
/// <summary>
/// How many work items are worth reading in full for one phrase. A service's name matches a few
/// dozen stories and tests; past that the phrase was too general to be worth the wait.
/// </summary>
public const int MostCandidates = 60;

private const string BodyFields =
    "System.Title,System.Description,Microsoft.VSTS.Common.AcceptanceCriteria,Microsoft.VSTS.TCM.Steps";

public async Task<IReadOnlyList<AdoWorkItemText>> FindCandidatesAsync(
    string phrase, CancellationToken ct)
{
    var ids = await SearchAsync(phrase, ct);
    if (ids.Count == 0) return Array.Empty<AdoWorkItemText>();

    var found = new List<AdoWorkItemText>();

    foreach (var batch in ids.Take(MostCandidates).Chunk(BatchSize))
    {
        var url = $"{Uri.EscapeDataString(_project)}/_apis/wit/workitems" +
                  $"?ids={string.Join(',', batch)}&fields={BodyFields}&api-version=6.0";

        using var response = await _http.GetAsync(url, ct);

        // A field this work item type does not have makes the whole batch a 400 on some
        // servers. Asking for fewer fields would lose the descriptions, which are the point,
        // so a refused batch is skipped rather than allowed to end the search.
        if (!response.IsSuccessStatusCode) continue;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("value", out var items)) continue;

        foreach (var item in items.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
            if (!item.TryGetProperty("fields", out var fields)) continue;

            var text = new StringBuilder();

            foreach (var field in new[]
                     {
                         "System.Description",
                         "Microsoft.VSTS.Common.AcceptanceCriteria",
                         "Microsoft.VSTS.TCM.Steps"
                     })
            {
                if (fields.TryGetProperty(field, out var value))
                    text.Append(PlainText(value.GetString())).Append(' ');
            }

            found.Add(new AdoWorkItemText(id, Title(fields), text.ToString()));
        }
    }

    return found;
}

private static string Title(JsonElement fields) =>
    fields.TryGetProperty("System.Title", out var t) ? t.GetString() ?? string.Empty : string.Empty;

/// <summary>
/// Markup out, words in.
///
/// Descriptions are HTML. Test steps are XML whose text content is HTML that was encoded a
/// second time on the way in, so one pass leaves "&lt;P&gt;Upload…" sitting in the middle of the
/// result. Stripping, decoding and stripping again covers both without needing to know which
/// field it was handed.
/// </summary>
private static string PlainText(string? markup)
{
    if (string.IsNullOrEmpty(markup)) return string.Empty;

    var text = WebUtility.HtmlDecode(Regex.Replace(markup, "<[^>]+>", " "));
    text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));

    return Regex.Replace(text, @"\s+", " ").Trim();
}
```

Then teach the fake, in `tests/MocdDocFix.Tests/Fakes/FakeAdoClient.cs`:

```csharp
/// <summary>Search phrase → the work items it finds, with their bodies.</summary>
public Dictionary<string, List<AdoWorkItemText>> Bodies { get; } =
    new(StringComparer.OrdinalIgnoreCase);

/// <summary>Every phrase the body search was given, in order.</summary>
public List<string> CandidatesAskedFor { get; } = new();

public Task<IReadOnlyList<AdoWorkItemText>> FindCandidatesAsync(string phrase, CancellationToken ct)
{
    CandidatesAskedFor.Add(phrase);

    if (Throws is not null) throw Throws;

    return Task.FromResult<IReadOnlyList<AdoWorkItemText>>(
        Bodies.TryGetValue(phrase, out var found) ? found : new List<AdoWorkItemText>());
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~AdoClientBodyTests"
dotnet test tests/MocdDocFix.Tests
```

Expected: PASS, and the whole suite still green — the interface gained a defaulted member, so nothing else should need touching.

- [ ] **Step 5: Commit**

```bash
git add src/MocdDocFix/Clients/AdoClient.cs tests/MocdDocFix.Tests/AdoClientBodyTests.cs tests/MocdDocFix.Tests/Fakes/FakeAdoClient.cs
git commit -m "feat(devops): read work item bodies live, since the server will not search them"
```

---

### Task 4: Stage 2 — the enquiry asks about bodies when titles cannot tell

**Files:**
- Modify: `src/MocdDocFix/Commands/DocumentTypeCheck.cs`
- Test: `tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs`

**Interfaces:**
- Consumes: `AdoWorkItemText`, `IAdoClient.FindCandidatesAsync` (Task 3); `DocumentTypeAuthority.Mentions` (Task 1).
- Produces: `DocumentTypeCheck` private `FromTitlesAsync`, `FromBodiesAsync`, and an `AskDevOpsAsync` that calls them in order. No public signature changes.

- [ ] **Step 1: Write the failing tests**

Append to `tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs`:

```csharp
// ---- stage 2: the story bodies, read live ----

private static AdoWorkItemText Story(int id, string title, string body) =>
    new(id, title, body);

[Fact]
public async Task A_name_no_title_carries_is_settled_by_the_story_that_lists_it()
{
    // The real case: no work item is *called* "A Copy of Board of Director's Decision", and the
    // story that lists it is called something else entirely.
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
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DocumentTypeCheckTests"
```

Expected: FAIL — `A_name_no_title_carries_is_settled_by_the_story_that_lists_it` gets `CannotTell` and a prompt, because nothing reads bodies yet.

- [ ] **Step 3: Write the implementation**

In `src/MocdDocFix/Commands/DocumentTypeCheck.cs`, rename the existing `AskDevOpsAsync` body to `FromTitlesAsync` (its contents are unchanged), and add the new orchestrator and the body stage:

```csharp
/// <summary>
/// Everything the backlog can be asked, in the order worth asking it, stopping at the first
/// stage that can actually settle the name.
///
/// Titles first, because one WIQL query answers them. Then the bodies of the work items a title
/// search can reach, because that is where the document lists are actually written. The drop
/// folder last, because reading files is only worth it once the server has run out of answers.
/// </summary>
private async Task<AdoOpinion> AskDevOpsAsync(string name, string? crmService, CancellationToken ct)
{
    var fromTitles = await FromTitlesAsync(name, crmService, ct);
    if (fromTitles.Verdict != AdoVerdict.CannotTell || _unreachable is not null) return fromTitles;

    var fromBodies = await FromBodiesAsync(name, crmService, ct);
    if (fromBodies.Verdict != AdoVerdict.CannotTell || _unreachable is not null) return fromBodies;

    // Neither could tell. Report both, because "no title says so" and "no story body says so"
    // are two different facts and the operator is about to be asked to supply the answer.
    return fromTitles with
    {
        Detail = $"{fromTitles.Detail} {fromBodies.Detail}",
        Evidence = fromTitles.Evidence.Count > 0 ? fromTitles.Evidence : fromBodies.Evidence
    };
}

/// <summary>
/// The story bodies, read live. WIQL will not search a description on this server, so the search
/// is done in two moves: a title query picks the work items worth reading, and their descriptions,
/// acceptance criteria and test steps are read here.
///
/// The service CRM names is the best seed — its stories are where its document list lives — and
/// the surviving search terms are tried after it, for the case where titles found the right work
/// items but none of their titles named a service.
/// </summary>
private async Task<AdoOpinion> FromBodiesAsync(string name, string? crmService, CancellationToken ct)
{
    var seeds = new List<string>();

    if (!string.IsNullOrWhiteSpace(crmService)) seeds.Add(crmService!);
    seeds.AddRange(DocumentTypeAuthority.SearchTerms(name).Take(2));

    var hits = new List<AdoHit>();
    var read = 0;

    foreach (var seed in seeds.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<AdoWorkItemText> candidates;

        try
        {
            candidates = await _ado!.FindCandidatesAsync(seed, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _unreachable = $"{ex.GetType().Name}: {Innermost(ex)}";

            return new AdoOpinion(AdoVerdict.CannotTell, null, Array.Empty<AdoHit>(),
                $"DevOps could not be reached — {_unreachable}");
        }

        read += candidates.Count;

        foreach (var candidate in candidates.Where(c => DocumentTypeAuthority.Mentions(c.Text, name)))
            hits.Add(new AdoHit(candidate.Id, candidate.Title, ServiceOf(candidate.Title)));

        if (hits.Count > 0) break;
    }

    if (read == 0)
        return new AdoOpinion(AdoVerdict.CannotTell, null, Array.Empty<AdoHit>(),
            "No work item matched by title either, so there was nothing to read — " +
            "the story bodies were not searched.");

    if (hits.Count == 0)
        return new AdoOpinion(AdoVerdict.CannotTell, null, Array.Empty<AdoHit>(),
            $"It is not written in the {read} story bodies I could read either.");

    return DocumentTypeAuthority.Weigh(name, crmService,
        hits.DistinctBy(h => h.WorkItemId).ToList());
}

/// <summary>
/// The service a work item's own title names. Test cases are pipe-delimited
/// ("NPOP|Employee Appointment Request|Documents|Verify …") and user stories are dash-delimited
/// ("1.1.6 NPOP- Employee Appointment Request Form- Documents"), so both readers are tried.
/// </summary>
private static string? ServiceOf(string? title) =>
    DocumentTypeAuthority.ServiceInTitle(title) ?? LocalBacklogSearch.ServiceInStoryTitle(title);
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DocumentTypeCheckTests"
dotnet test tests/MocdDocFix.Tests
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MocdDocFix/Commands/DocumentTypeCheck.cs tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs
git commit -m "feat(devops): read the story bodies when no title can settle a document type"
```

---

### Task 5: The local search matches by words, and reads a service out of an underscored file name

**Files:**
- Modify: `src/MocdDocFix/Domain/LocalBacklogSearch.cs`
- Test: `tests/MocdDocFix.Tests/LocalBacklogSearchTests.cs`

**Interfaces:**
- Consumes: `DocumentTypeAuthority.Mentions` (Task 1).
- Produces: no signature change. `LocalBacklogSearch.Find` matches order-free; `ServiceInStoryTitle` also splits on `_`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/MocdDocFix.Tests/LocalBacklogSearchTests.cs`:

```csharp
[Fact]
public void A_story_that_words_the_name_differently_is_still_found()
{
    Story("us-27628.md", "1.1.6 NPOP- Employee Appointment Request Form- Documents",
        "Certificate of good conduct and behavior, valid for the life of the appointment.");

    var hit = Assert.Single(LocalBacklogSearch.Find(_root, "a good conduct life"));

    Assert.Equal("Employee Appointment Request", hit.Service);
}

[Fact]
public void The_same_words_pages_apart_are_not_a_match()
{
    Story("us-1.md", "1.1.1 NPOP- Something Else Form- Documents",
        "good " + new string('x', 400) + " conduct " + new string('y', 400) + " life");

    Assert.Empty(LocalBacklogSearch.Find(_root, "a good conduct life"));
}

[Fact]
public void A_workbook_downloaded_from_the_backlog_gives_up_the_service_in_its_name()
{
    Assert.Equal("Employee Appointment Request", LocalBacklogSearch.ServiceInStoryTitle(
        "MoCD_NPOP_Employee Appointment Request_DD_20250509_V.0.2"));
}

[Fact]
public void A_real_workbook_is_searched_through_its_shared_string_table()
{
    // The document lists arrive as .xlsx, and a workbook is a zip of XML: every cell value in
    // the file sits in xl/sharedStrings.xml. This proves the matcher reaches them, rather than
    // only the markdown the rest of these tests use.
    Workbook("MoCD_NPOP_Employee Appointment Request_DD.xlsx",
        "Document Type", "Certificate of good conduct and behavior, valid for the life of it",
        "Mandatory");

    var hit = Assert.Single(LocalBacklogSearch.Find(_root, "a good conduct life"));

    Assert.Equal("Employee Appointment Request", hit.Service);
}
```

and the helper that writes one, beside `Story`:

```csharp
/// <summary>The smallest thing that is honestly an .xlsx for our purposes: a zip with a shared
/// string table in it. That is the only entry <see cref="LocalBacklogSearch"/> reads.</summary>
private void Workbook(string name, params string[] cells)
{
    using var file = File.Create(Path.Combine(_root, name));
    using var zip = new ZipArchive(file, ZipArchiveMode.Create);

    using var entry = new StreamWriter(zip.CreateEntry("xl/sharedStrings.xml").Open());

    entry.Write("<sst>");
    foreach (var cell in cells) entry.Write($"<si><t>{cell}</t></si>");
    entry.Write("</sst>");
}
```

with `using System.IO.Compression;` at the top of the test file.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~LocalBacklogSearchTests"
```

Expected: FAIL — the first returns empty (exact substring), the third returns the whole underscored string.

- [ ] **Step 3: Write the implementation**

In `src/MocdDocFix/Domain/LocalBacklogSearch.cs`, replace the matching line inside `Find`:

```csharp
var text = ReadText(file);

// Order-free, and near each other. CRM and the backlog almost never word a document the same
// way round — "A copy of the certificate of good conduct and behavior" against "Certificate of
// good conduct and behavior … life of the appointment" — and an exact substring finds neither.
if (text is null || !DocumentTypeAuthority.Mentions(text, wanted)) continue;
```

and widen the split in `ServiceInStoryTitle`:

```csharp
// Underscores as well as dashes and pipes. Story titles use dashes; the workbooks attached to
// them are named "MoCD_NPOP_Employee Appointment Request_DD_20250509_V.0.2", and a file the
// operator dropped in by hand has nothing but its name to say which service it belongs to.
foreach (var raw in Regex.Split(title!, @"-\s|\s-|\||_"))
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~LocalBacklogSearchTests"
dotnet test tests/MocdDocFix.Tests
```

Expected: PASS. If `A_name_nobody_wrote_down_is_not_found` now fails, the order-free rule has matched something it should not — check whether the story body genuinely contains all the meaningful words nearby; if it does, change that test's body text so it does not, since the new rule is the intended behaviour.

- [ ] **Step 5: Commit**

```bash
git add src/MocdDocFix/Domain/LocalBacklogSearch.cs tests/MocdDocFix.Tests/LocalBacklogSearchTests.cs
git commit -m "feat(search): find a document name written in another order, and name the workbook's service"
```

---

### Task 6: Stage 3 — the drop folder decides, the knowledge-base copy only informs

**Files:**
- Modify: `src/MocdDocFix/Commands/DocumentTypeCheck.cs`
- Test: `tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs`

**Interfaces:**
- Consumes: `LocalBacklogSearch.Find` (Task 5), `AskDevOpsAsync` (Task 4).
- Produces: `DocumentTypeCheck` private `FromDropFolder(string, string?)` → `AdoOpinion`; `ShowLocalHits` renamed `ShowSnapshotHits` and reading only `_localBacklog`; private field `_fetched` mapping a saved file path to the attachment it came from.

- [ ] **Step 1: Write the failing tests**

Append to `tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs`:

```csharp
// ---- stage 3: the workbooks on disk ----

private DocumentTypeCheck CheckWithFolders(string? snapshot, string? drop) =>
    new(_ado, Decisions(), _prompts, snapshot, drop);

private string Drop(string name, string body)
{
    var folder = Path.Combine(_root, "drop");
    Directory.CreateDirectory(folder);

    var path = Path.Combine(folder, name);
    File.WriteAllText(path, body);
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
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DocumentTypeCheckTests"
```

Expected: FAIL — the workbook test prompts instead of settling.

- [ ] **Step 3: Write the implementation**

In `DocumentTypeCheck`, add the field:

```csharp
/// <summary>
/// Files this run fetched from the backlog, and the attachment each came from. A workbook has no
/// service of its own — the work item it hangs off is what says where it belongs — so the link
/// has to be kept rather than guessed at from the file name.
/// </summary>
private readonly Dictionary<string, AdoAttachment> _fetched =
    new(StringComparer.OrdinalIgnoreCase);
```

extend `AskDevOpsAsync` with the third stage — replace its final `return` block:

```csharp
    var fromBodies = await FromBodiesAsync(name, crmService, ct);
    if (fromBodies.Verdict != AdoVerdict.CannotTell || _unreachable is not null) return fromBodies;

    var fromFiles = FromDropFolder(name, crmService);
    if (fromFiles.Verdict != AdoVerdict.CannotTell) return fromFiles;

    // None of the three could tell. Report what each one looked at, because "no title says so",
    // "no story body says so" and "no workbook says so" are three different facts and the
    // operator is about to be asked to supply the answer.
    return fromTitles with
    {
        Detail = $"{fromTitles.Detail} {fromBodies.Detail}",
        Evidence = fromTitles.Evidence.Count > 0 ? fromTitles.Evidence : fromBodies.Evidence
    };
```

and add the stage:

```csharp
/// <summary>
/// The workbooks and files on disk — fetched from the backlog during this run, or saved there by
/// the operator a minute ago. They are as live as anything else here, so unlike the synced
/// snapshot they are allowed to settle a document type.
///
/// A file fetched from an attachment takes its service from the work item it hung off. One
/// dropped in by hand has only its name, which is usually enough:
/// "MoCD_NPOP_Employee Appointment Request_DD_20250509_V.0.2.xlsx".
/// </summary>
private AdoOpinion FromDropFolder(string name, string? crmService)
{
    var files = LocalBacklogSearch.Find(_dropFolder, name);

    if (files.Count == 0)
        return new AdoOpinion(AdoVerdict.CannotTell, null, Array.Empty<AdoHit>(),
            "Nothing in the downloaded backlog files names it either.");

    var hits = files
        .Select(f => _fetched.TryGetValue(f.File, out var from)
            ? new AdoHit(from.WorkItemId, from.WorkItemTitle, ServiceOf(from.WorkItemTitle))
            : new AdoHit(0, Path.GetFileName(f.File), f.Service))
        .ToList();

    var opinion = DocumentTypeAuthority.Weigh(name, crmService, hits);
    var where = string.Join(", ", files.Take(3).Select(f => Path.GetFileName(f.File)));

    // Weigh's wording is about work items. The verdict it reached is right and is kept; what it
    // says has to name the file, because that is the thing the operator can go and open.
    return opinion with
    {
        Detail = opinion.Verdict == AdoVerdict.CannotTell
            ? $"{opinion.Detail} (found in {where})"
            : $"{opinion.Detail} Found in {where}."
    };
}
```

Finally, narrow the display. Rename `ShowLocalHits` to `ShowSnapshotHits`, drop the drop-folder half — stage 3 has it now — and label it:

```csharp
/// <summary>
/// What the synced copy of the backlog has. It is a snapshot of unknown age, so it is shown as a
/// hint and never allowed to settle anything: the live stages above are the ones that decide.
/// </summary>
private void ShowSnapshotHits(string name)
{
    var hits = LocalBacklogSearch.Find(_localBacklog, name);
    if (hits.Count == 0) return;

    _prompts.Blank();
    _prompts.Say($"The name does appear in {hits.Count} file(s) of the local backlog copy — " +
                 "a synced snapshot, which may be stale:");

    foreach (var hit in hits)
    {
        _prompts.Info($"      {Trim(hit.Title ?? Path.GetFileName(hit.File), 62)}", Tone.Muted);
        if (hit.Service is not null)
            _prompts.Info($"        service: {hit.Service}", Tone.Good);
    }
}
```

and change the single call site inside `Ask` from `ShowLocalHits(name);` to `ShowSnapshotHits(name);`.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DocumentTypeCheckTests"
dotnet test tests/MocdDocFix.Tests
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MocdDocFix/Commands/DocumentTypeCheck.cs tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs
git commit -m "feat(devops): let a downloaded workbook settle a type, and mark the synced copy stale"
```

---

### Task 7: "Find the spreadsheet" reads what it just downloaded

**Files:**
- Modify: `src/MocdDocFix/Commands/DocumentTypeCheck.cs`
- Test: `tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs`

**Interfaces:**
- Consumes: `FromDropFolder` and `_fetched` (Task 6), `SearchAgain` (existing).
- Produces: nothing new. `Ask` case 4 changes behaviour; `FetchTheSpreadsheets` records into `_fetched`.

- [ ] **Step 1: Write the failing test**

Append to `tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs`:

```csharp
[Fact]
public async Task Downloading_the_spreadsheet_settles_the_type_instead_of_asking_again()
{
    var drop = Path.Combine(_root, "drop");

    _ado.Attachments[Emap] = new()
    {
        new AdoAttachment(27632,
            "1.1.10 CRM- Employee Appointment Request Form- Display Submitted Documents",
            "MoCD_NPOP_Employee Appointment Request_DD.csv",
            "https://devops.example/attachment")
    };

    // FakeAdoClient writes "pretend workbook"; this test needs the file to actually name the
    // document, so the download is made to write the list the real one carries.
    _ado.WritesOnDownload = "Document Type,Mandatory\nA Copy of Board of Director's Decision,Yes\n";

    _prompts.ReadLineQueue = new Queue<string>(new[] { "5" });   // "Find the spreadsheet"

    var ruling = await CheckWithFolders(null, drop)
        .RuleOnAsync("A Copy of Board of Director's Decision", Emap, CancellationToken.None);

    Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
    Assert.Equal(Emap, ruling.Service);

    // Asked once. If the menu came back a second time the queue would be empty and ReadLine
    // would throw, so this assertion and that throw both prove the same thing.
    Assert.Single(_prompts.Questions);
}

[Fact]
public async Task Going_away_to_save_the_file_yourself_works_the_same_way()
{
    // "Wait — I will go and look" re-enters the enquiry, so a workbook saved into the drop
    // folder while the question sat on screen is read without choosing anything else. This is
    // the same mechanism as the test above, reached by the other door.
    var folder = Path.Combine(_root, "drop");
    Directory.CreateDirectory(folder);

    _prompts.ReadLineQueue = new Queue<string>(new[] { "6", "" });   // wait, then press Enter

    var check = CheckWithFolders(null, folder);

    // Dropped in by hand between the question going up and the Enter coming back.
    File.WriteAllText(Path.Combine(folder, "MoCD_NPOP_Employee Appointment Request_DD.csv"),
        "A Copy of Board of Director's Decision,Mandatory");

    var ruling = await check.RuleOnAsync("A Copy of Board of Director's Decision", Emap,
        CancellationToken.None);

    Assert.Equal(AdoVerdict.Agrees, ruling.Verdict);
    Assert.Contains("MoCD_NPOP_Employee Appointment Request", ruling.Detail);
}
```

Assert on `Detail`, not on `Source`: the stage names arrive in Task 8, and until then `SearchAgain`
still stamps everything "DevOps, looked at again".

Add the knob it needs to `tests/MocdDocFix.Tests/Fakes/FakeAdoClient.cs`:

```csharp
/// <summary>What a downloaded attachment should contain. Default is a file with no list in it.</summary>
public string WritesOnDownload { get; set; } = "pretend workbook";
```

and use it in `DownloadAttachmentAsync` in place of the literal `"pretend workbook"`.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~Downloading_the_spreadsheet_settles"
```

Expected: FAIL — two questions asked, because case 4 returns to the same menu with the pre-download opinion.

- [ ] **Step 3: Write the implementation**

In `FetchTheSpreadsheets`, record every file that lands, so stage 3 can read its work item back. Inside the `foreach (var attachment in …)` loop, in both places a file ends up present:

```csharp
            var to = Path.Combine(folder, attachment.Name);

            if (File.Exists(to))
            {
                _fetched[to] = attachment;
                _prompts.Info("      already here", Tone.Good);
                continue;
            }

            try
            {
                var got = _ado.DownloadAttachmentAsync(attachment, to, CancellationToken.None)
                    .GetAwaiter().GetResult();

                if (got) _fetched[to] = attachment;

                _prompts.Info(got ? $"      downloaded to {to}" : "      could not be downloaded",
                    got ? Tone.Good : Tone.Warn);
            }
```

Then change `Ask` case 4 from returning to the same question to re-running the whole enquiry — which now includes stage 3, so the files just written are read:

```csharp
            case 4:
                FetchTheSpreadsheets(name, crmService);

                // Not back to the same question with the same answer. Everything is asked again
                // from scratch, and stage 3 reads what the download just put on disk — which is
                // the point of having fetched it.
                return SearchAgain(name, crmService, null, opinion);
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DocumentTypeCheckTests"
dotnet test tests/MocdDocFix.Tests
```

Expected: PASS, whole suite green.

- [ ] **Step 5: Commit**

```bash
git add src/MocdDocFix/Commands/DocumentTypeCheck.cs tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs tests/MocdDocFix.Tests/Fakes/FakeAdoClient.cs
git commit -m "feat(devops): read the workbook we just downloaded instead of asking again"
```

---

### Task 8: Say what the backlog was asked, on screen and in the report

The enquiry now makes up to three kinds of call. When it settles a type the operator should be able to see which stage did it, and when it cannot they should see what was actually looked at.

**Files:**
- Modify: `src/MocdDocFix/Commands/DocumentTypeCheck.cs`
- Test: `tests/MocdDocFix.Tests/ScanDevOpsTests.cs`

**Interfaces:**
- Consumes: `TypeRuling.Source` (existing record field).
- Produces: `TypeRuling.Source` gains the values `"DevOps titles"`, `"DevOps bodies"`, `"backlog files"`. `"DevOps"` is no longer produced by the automatic path.

- [ ] **Step 1: Write the failing test**

Append to `tests/MocdDocFix.Tests/ScanDevOpsTests.cs`:

```csharp
[Fact]
public async Task The_report_says_which_stage_of_the_backlog_answered()
{
    var ado = new FakeAdoClient();

    ado.Bodies["Employee Appointment Request"] = new()
    {
        new AdoWorkItemText(27628, "1.1.6 NPOP- Employee Appointment Request Form- Documents",
            "A Copy of Board of Director's Decision")
    };

    // Its own decisions file, in its own folder, so the test says nothing about any other.
    var folder = Path.Combine(Path.GetTempPath(), "docfix-stage-" + Guid.NewGuid());
    Directory.CreateDirectory(folder);

    try
    {
        var decisions = new DocumentTypeDecisions(Path.Combine(folder, "document-types.json"));
        var check = new DocumentTypeCheck(ado, decisions, new FakePrompts());

        var ruling = await check.RuleOnAsync("A Copy of Board of Director's Decision",
            "Employee Appointment Request", CancellationToken.None);

        Assert.Equal("DevOps bodies", ruling.Source);
    }
    finally
    {
        Directory.Delete(folder, true);
    }
}
```

`ScanDevOpsTests` may already have a `Decisions()` helper of its own; if so use it and drop the folder plumbing above.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~The_report_says_which_stage"
```

Expected: FAIL — `Assert.Equal() Failure: Expected "DevOps bodies", Actual "DevOps"`.

- [ ] **Step 3: Write the implementation**

`AdoOpinion` has no room to carry which stage produced it, and adding a field to it would touch every construction site. Carry it on the opinion's `Detail` instead? No — that is a string the operator reads. Add the field; there are few sites and the compiler finds them all.

In `src/MocdDocFix/Domain/DocumentTypeAuthority.cs`:

```csharp
/// <param name="Source">
/// Which stage of the enquiry produced this — "DevOps titles", "DevOps bodies", "backlog files".
/// Kept on the opinion so the report can say how a document type was settled, not merely that it
/// was: a title match and a workbook match are different strengths of evidence.
/// </param>
public sealed record AdoOpinion(
    AdoVerdict Verdict,
    string? Service,
    IReadOnlyList<AdoHit> Evidence,
    string Detail,
    string Source = "DevOps");
```

The default keeps every existing construction site compiling. Then stamp each stage:

- at the end of `FromTitlesAsync`, `return opinion with { Source = "DevOps titles" };` on the settling path
- at the end of `FromBodiesAsync`, `return DocumentTypeAuthority.Weigh(...) with { Source = "DevOps bodies" };`
- in `FromDropFolder`, add `Source = "backlog files"` to the `opinion with { … }` it already builds

and in `DecideAsync`, where the ruling is built from the opinion, use it:

```csharp
        return opinion.Verdict == AdoVerdict.CannotTell
            ? Ask(name, crmService, opinion)
            : new TypeRuling(name, opinion.Verdict, opinion.Service, opinion.Detail,
                opinion.Evidence, opinion.Source);
```

Do the same at the two other places that build a `TypeRuling` from an opinion — inside `AskAboutTheOutageAsync` case 0, and inside `SearchAgain`. In `SearchAgain` keep the "looked at again" / "searched for '…'" wording it already has, appending the stage:

```csharp
        if (opinion.Verdict != AdoVerdict.CannotTell)
            return new TypeRuling(name, opinion.Verdict, opinion.Service, opinion.Detail,
                opinion.Evidence,
                phrase is null
                    ? $"{opinion.Source}, looked at again"
                    : $"{opinion.Source}, searched for '{phrase}'");
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/MocdDocFix.Tests
```

Expected: PASS. Tests that assert `Source == "DevOps"` for a title match now need `"DevOps titles"` — update them; that is the intended change and the assertion is what proves it.

- [ ] **Step 5: Commit**

```bash
git add src/MocdDocFix/Domain/DocumentTypeAuthority.cs src/MocdDocFix/Commands/DocumentTypeCheck.cs tests/MocdDocFix.Tests
git commit -m "feat(report): say which stage of the backlog settled a document type"
```

---

### Task 9: Run it against the real backlog and record what came back

The suite proves the logic. Only the server can prove the two guesses in it: that `Microsoft.VSTS.TCM.Steps` comes back on a plain `fields=` batch fetch, and that `MostCandidates = 60` is a wide enough pool.

**Files:**
- Modify: `docs/specs/2026-09-15-live-ado-document-type-search-design.md` (a "what the server actually did" section)

**Interfaces:** none — this task writes no code.

- [ ] **Step 1: Check the VPN is up**

```bash
curl --ntlm -u "$ADO_USER:$ADO_PASSWORD" -s -o /dev/null -w "%{http_code}\n" "https://devops.mocd.gov.ae/MOCD/_apis/projects?api-version=6.0"
```

Expected: `200`. Anything else — connect the client VPN and try again before going further.

- [ ] **Step 2: Confirm the server returns test steps on a fields fetch**

```bash
curl --ntlm -u "$ADO_USER:$ADO_PASSWORD" -s \
  "https://devops.mocd.gov.ae/MOCD/NPO%20-%20Phase%202/_apis/wit/workitems?ids=27564&fields=System.Title,System.Description,Microsoft.VSTS.Common.AcceptanceCriteria,Microsoft.VSTS.TCM.Steps&api-version=6.0"
```

Expected: JSON containing `System.Description`. If `Microsoft.VSTS.TCM.Steps` is absent or the call is a 400, that is the degradation the spec anticipated — note it in step 4 and leave the code alone; descriptions carry the document lists.

- [ ] **Step 3: Run the real thing against the case that started this**

```bash
dotnet run --project src/MocdDocFix -- --targeted
```

Drive it to `A Copy of Board of Director's Decision`. Expected: it settles as **Agrees / Employee Appointment Request** with source `DevOps bodies`, and no menu appears.

- [ ] **Step 4: Write down what happened**

Append to the spec a short section — what the server returned for test steps, which stage settled the document type, and how many candidates stage 2 read. Facts only, including the ones that went badly.

- [ ] **Step 5: Commit**

```bash
git add docs/specs/2026-09-15-live-ado-document-type-search-design.md
git commit -m "docs(spec): what the backlog actually returned"
```

---

## Notes for whoever executes this

- **Tasks 1, 2, 3 and 5 are independent of each other** and can be done in any order or in parallel. Task 4 needs 1 and 3. Task 6 needs 4 and 5. Task 7 needs 6. Task 8 needs 4 and 6. Task 9 needs everything.
- **`DocumentTypeCheck.cs` is 580 lines before this plan and will be around 700 after.** If it becomes hard to hold, the three `From…` stages and `AskDevOpsAsync` lift cleanly into a `Domain/BacklogEnquiry.cs`, leaving `DocumentTypeCheck` with the asking and the remembering. Do that as its own commit, with no behaviour change, and only if it is actually in the way.
- **Do not push.** Commit locally; the operator pushes.
