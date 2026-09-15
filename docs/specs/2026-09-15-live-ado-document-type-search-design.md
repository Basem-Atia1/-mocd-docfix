# Live DevOps search for document types

**Date:** 2026-09-15
**Status:** Approved, not yet implemented

## The problem

A full run stops on `A Copy of Board of Director's Decision` and cannot settle it. The screen
says two things at once:

    DevOps has no work item naming 'A Copy of Board of Director's Decision'.

    The name does appear in 1 file(s) of the local backlog copy:
        1.1.6 NPOP- Employee Appointment Request Form- Documents
          service: Employee Appointment Request

The answer is on the screen — *Employee Appointment Request*, which is also what CRM says — and
the operator is still asked. Three separate faults put it there.

1. **The only live question is about titles.** `AskDevOpsAsync` runs WIQL
   `[System.Title] CONTAINS`, and nothing else. The document lists live in story descriptions and
   in attached workbooks, and neither is ever asked about live.

2. **The one thing that did find the name is a stale local copy, and it cannot decide anything.**
   `ShowLocalHits` reads `kb\user-stories` — a synced snapshot inside the knowledge base — and
   prints what it finds. It never reaches `Weigh`, so it cannot settle a verdict however good the
   evidence. The same is true of workbooks the tool itself has just downloaded: "Find the
   spreadsheet" fetches them and then returns to the menu carrying the *same* opinion it had
   before the download.

3. **The search phrases keep the words that guarantee a miss.** `A Copy of` is three words of
   pure noise at the front of the name, and DevOps writes none of them. There is a noise list
   already, but it is only used to decide where a contiguous window may *begin and end* — no term
   is ever produced with the leading noise removed and the rest kept whole.

## What this changes

### 1. The live enquiry gains a second stage: bodies

`AskDevOpsAsync` becomes a three-stage enquiry. Each stage runs only when the one before it
returned `CannotTell`, and every stage feeds the same `Weigh`.

**Stage 1 — titles.** Today's WIQL title search, unchanged except that it is given better terms
(see §3).

**Stage 2 — bodies.** Runs only when stage 1 returns `CannotTell`.

WIQL cannot full-text a description on this server — it answers TF401349 to `CONTAINS WORDS` — so
the body search is done in two moves rather than one:

- **Pick candidates live.** WIQL by title for the service CRM names, and for the search terms that
  survived stage 1. Capped at `MostCandidates = 60` work items.
- **Read their bodies live.** Fetch those ids with
  `fields=System.Title,System.Description,Microsoft.VSTS.Common.AcceptanceCriteria,Microsoft.VSTS.TCM.Steps`,
  in the existing 180-id batches, and match in process.

`System.Description` and `AcceptanceCriteria` are HTML; `Microsoft.VSTS.TCM.Steps` is XML, not
HTML, and needs its own strip or test-case steps arrive as markup soup. Both are reduced to plain
text before matching.

A body hit becomes an ordinary `AdoHit`. Its service is read off the work item's own title: test
cases are pipe-delimited and go through `DocumentTypeAuthority.ServiceInTitle`, user stories are
dash-delimited (`1.1.6 NPOP- Employee Appointment Request Form- Documents`) and go through
`LocalBacklogSearch.ServiceInStoryTitle`. Pipe form is tried first.

Because body hits are ordinary `AdoHit`s, they go through **`Weigh` unchanged**. That already
encodes the agreed safety bar — `agreeing.Count > 0` settles an agreement, `EnoughToOverrule = 2`
before anything may contradict CRM — so there is no second set of rules to keep in step with the
first, and the FAHR Document lesson keeps applying to bodies exactly as it does to titles.

**When stage 2 has nothing to search.** The candidate pool needs a seed. Where CRM names no
service, the only seed is the document name itself — the phrase that just failed on titles — so
stage 2 has no pool and falls straight through to stage 3. This is said on screen. It must not
read as a clean "nothing found", because nothing was looked at.

**Stage 3 — the drop folder**, described in §2. It runs when stages 1 and 2 cannot tell.

### 2. The two local sources split: one decides, one only informs

| Source | Today | After |
|---|---|---|
| `D:\mocd-docfix-data\backlog-files` — fetched this run, or dropped in by hand | printed only | scanned and **weighed**; may settle the verdict |
| `kb\user-stories` — the synced snapshot | printed only | printed only, labelled *local snapshot — may be stale* |

The drop folder holds files that came off DevOps during this run, or that the operator fetched
themselves minutes ago. It is as live as anything else here, and it is allowed to decide. The
knowledge-base copy is a snapshot of unknown age; it stays on screen as a last hint, marked as
such, and never settles anything.

Scanning the drop folder is **stage 3 of the enquiry**, not a step inside the menu. That matters
for two of the menu's options: "Wait — I will go and look" (case 5) and "Search DevOps for my own
words" (case 3) both re-enter the enquiry, so a workbook the operator saved into the folder while
the question sat on screen is picked up without them having to choose anything else. A workbook
matched here yields the same `AdoHit` shape as a title or a body hit, and goes through the same
`Weigh`.

**"Find the spreadsheet" (`Ask` case 4) stops discarding what it fetched.** After downloading, it
scans the workbooks it has just written and weighs them. A workbook carries no service of its own
— its service comes from the work item it hangs off, `AdoAttachment.WorkItemTitle`. So
`MoCD_NPOP_Employee Appointment Request_DD_20250509_V.0.2.xlsx`, attached to item 27632
(`1.1.10 CRM- Employee Appointment Request Form- Display Submitted …`), yields *Employee
Appointment Request*. Where that settles the type, the operator is told what was found and in
which file, and the menu does not come back. Where it does not, the menu returns carrying the new
detail rather than the pre-download one.

A file dropped in by hand has no work item, so its service falls back to `ServiceInStoryTitle`
over the file name.

### 3. Noise words, in the two places each approach can actually work

These are two different problems and they get two different answers.

**Titles — trim the ends only.** WIQL `CONTAINS` matches a literal run of characters. Dropping an
interior word (`Board of Director's` → `Board Director's`) produces a phrase in no title anywhere
and is guaranteed to find nothing. Dropping leading and trailing noise keeps the rest contiguous
and matchable, so `SearchTerms` promotes the end-trimmed core to term #2, immediately after the
full name:

    A Copy of Board of Director's Decision  →  Board of Director's Decision

The window loop already generates that string today. It is buried: the term contains *of*, so the
`AllMeaningful` sort tier ranks it behind every noise-free window, and `MostTermsWeWillTry = 6`
can cut it off before it is ever tried. Promoting it explicitly is the fix; the window loop is
otherwise untouched.

Where the name carries no leading or trailing noise, the trimmed core *is* the full name. It is
not added twice — the existing `Add` already de-duplicates case-insensitively, and the term list
must not spend one of its six slots on a repeat.

**Bodies, attachments and workbooks — an order-free matcher.** Here the matching is ours, so the
words need not be contiguous or in order. A new pure function on `DocumentTypeAuthority`:

- Reduce the name to its meaningful words — `Normalise`, then drop `Noise` and anything under
  three characters. `a good conduct life` → `{good, conduct, life}`.
- Match when **every** meaningful word appears in the text as a whole word, **and all of them fall
  within a `NearbyWindow = 160` character span** of the normalised text.
- Where fewer than two meaningful words survive, fall back to exact substring. A one-word set is
  too loose to mean anything.

The window is the guard that makes this safe. Without it the rule is "all three words appear
somewhere in this file", and a workbook's shared-string table is a few hundred kilobytes of every
cell value in the document — it would fire on almost anything.

## Shape

The existing split holds: fetching stays in `Clients`, matching stays in `Domain`.

One new method on `IAdoClient`, with a default implementation so the existing fakes keep
compiling:

```csharp
/// <param name="Text">Description, acceptance criteria and test steps, stripped to plain text.</param>
public sealed record AdoWorkItemText(int Id, string Title, string Text);

Task<IReadOnlyList<AdoWorkItemText>> FindCandidatesAsync(string phrase, CancellationToken ct)
    => Task.FromResult<IReadOnlyList<AdoWorkItemText>>(Array.Empty<AdoWorkItemText>());
```

One new pure function on `DocumentTypeAuthority` — the matcher — which is where the interesting
behaviour lives and is unit-testable without a server.

`DocumentTypeCheck` grows the stage-2 call and the post-download re-weigh. It is 580 lines
already; if the body-search orchestration pushes it past comfort, the stage-1/stage-2 enquiry
moves to its own `BacklogEnquiry` class and `DocumentTypeCheck` keeps only the asking.

## Cost

Stage 2 runs only where stage 1 fails — which is precisely the case that stops the run and asks a
question today. It trades one operator prompt for roughly two WIQL queries and one batched field
fetch, capped at 60 candidates. That is a good trade; the prompt costs a human minute.

The attachment path is unchanged in cost and still only runs when the operator asks for it. It is
not promoted into the automatic stages: `MostWeWillOpen = 12` relation calls plus downloads is too
much to spend without being asked.

## Testing

Test-first, following the existing per-class test files.

**`DocumentTypeAuthorityTests`** — pure, no server:
- the end-trimmed core is term #2 for `A Copy of Board of Director's Decision`
- `a good conduct life` matches text containing those three words scattered but close
- the same words spread beyond `NearbyWindow` do **not** match
- a one-meaningful-word name falls back to substring matching
- `Weigh` over body hits still needs two to disagree and one to agree — asserted through the body
  path, so the shared bar is proven shared

**`ScanDevOpsTests` / `DocumentTypeCheckTests`** — fake `IAdoClient`:
- no title hit, one body hit agreeing with CRM → settles, operator never prompted
- no title hit, one body hit naming a different service → still prompts
- no title hit, two body hits naming a different service → settles as Disagrees
- CRM names no service and titles fail → prompts, and says the bodies were not searched
- a workbook in the drop folder naming the type → settles, and the report says which file
- a hit only in `kb\user-stories` → still prompts, and the line is labelled as a stale snapshot

**`LocalBacklogSearchTests`** — the order-free matcher over a real `.xlsx` shared-string table.

## What the server actually did

Run against the live backlog on 2026-09-15, over the client VPN, with Windows SSO. Recorded by
`tests/MocdDocFix.Tests/LiveBacklogTests.cs`, which skips itself when the server is unreachable.

**`Microsoft.VSTS.TCM.Steps` is absent, and harmlessly so.** A `fields=` batch naming it does not
400 — the server simply omits the fields a work item type does not carry, and `TryGetProperty`
skips them. User stories return `System.Description` and `Microsoft.VSTS.Common.AcceptanceCriteria`
only. That is where the document lists are: story 27628's acceptance criteria run to 66,799
characters and story 27564's to 168,531, and `A Copy of Board of Director's Decision` is written
in 27628's verbatim, in a bilingual table beside its Arabic name. So the degradation the spec
anticipated is the normal case, and it costs nothing.

**The candidate pool was wide enough.** `MostCandidates = 60` was never the binding limit.

**The apostrophe decides which stage answers, and both are right.** CRM spells the document with a
curly apostrophe (U+2019); work item 34145 — `NPOP|Employee Appointment Request|Documents|Verify
that the "A copy of Board of Director's Decision" is mandatory` — uses a straight one. WIQL
CONTAINS matches characters, so:

- **the backlog's spelling** settles at stage 1, on that one title, whose pipe-delimited service is
  *Employee Appointment Request* — agreeing with CRM, and one agreeing hit is enough;
- **CRM's spelling** finds no title and settles at stage 2, in story 27628's acceptance criteria,
  where `Normalise` flattens both apostrophes to a space.

Either way the type is settled as **Agrees / Employee Appointment Request** with no prompt.

**The end-trimming alone fixed the original case.** Dropping `A Copy of` from the front is what
lets the trimmed phrase match title 34145 at all; the full name matches nothing. That was expected
to be a cheap improvement to the title stage and turned out to resolve the reported case one stage
earlier, and one round trip cheaper, than the body search built for it.

## Out of scope

- The ADO Search REST API (`almsearch`). It would give true full text in one call, but the
  extension may not be installed on this on-prem server, and the fetch-and-scan route works
  regardless. Worth revisiting if stage 2's candidate pool proves too narrow in practice.
- Writing anything to DevOps. This tool reads DevOps and always will.
