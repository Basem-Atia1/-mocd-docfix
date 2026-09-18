# Workbook-only ledger, a shorter sheet, and a choice of scope

**Date:** 2026-09-18 (revised 2026-09-19)
**Status:** approved in conversation, not yet implemented

Eleven changes, agreed one at a time. They share a subject: **what is in the sheet, how fast it
is written, and which documents it is about.**

| § | change |
|---|---|
| 1 | the CSV copy goes; the workbook is the only ledger |
| 2 | correct documents stop entering the sheet, and the existing sheet is cleared once |
| 3 | `skip` leaves the vocabulary |
| 4 | a record with no file path becomes `review`, in a group of its own |
| 5 | three tabs — still to fix, awaiting deletion, finished |
| 6 | use the file as it is, or update it from CRM |
| 7 | read the sheet again, in all four modes |
| 8 | a guard on a verdict hand-edited onto a finished row |
| 9 | choose the scope; two ledger files that do not overlap |
| 10 | fixed column widths — 27× faster to save |
| 11 | save via a temporary file, so a kill cannot leave half a ledger |

---

## 1. The CSV goes

The ledger is written twice today — a workbook and a CSV beside it. Nothing reads the CSV. It
exists so the ledger is also greppable and so a damaged workbook is not the end of the record,
and it has its own lock-warning machinery whose only job is to apologise when it falls behind.

It goes.

- `LedgerStore` loses `CsvPath`, the `CsvWriter` block, `WarnAboutCsv` and `LastCsvProblem`.
- `LedgerStore(path)` no longer accepts a `.csv` spelling; the workbook is the only name.
- `Session` loses the `WarnAboutCsv` wiring.
- The **CsvHelper package reference is removed**. `LedgerColumns` derives the column order and
  headers by reflecting over CsvHelper's `[Index]` and `[Name]` attributes on `LedgerRow`, so
  those are replaced by a local `[Column(index, "header")]` attribute in `MocdDocFix.Domain`.
  Order and headers do not change.
- A `.csv` left beside the ledger from an earlier build is **deleted once**, on the first write.
  Nothing maintains it any more, and a stale file that looks current is worse than no file.
- Wording that mentions the CSV — `Wizard`, `DocumentGroups`, `LedgerWorkbook`, `README` — is
  corrected.

Nothing about the sheet changes.

---

## 2. Correct documents stop entering the sheet

The ledger is a census today: every document gets a row, correct ones included, because the
verdict column is what separates them. That is why it is thousands of rows long when the work
is hundreds.

**Correct-and-untouched rows are no longer written.**

### Where the filter sits — and where it must not

Not in the scan. If `LedgerBuilder` simply stopped returning correct rows, then a row you had
marked `fix` that somebody corrected in CRM last week would turn correct, drop out of the scan,
and `LedgerMerge` would report it as **vanished from CRM** — which is alarming and false.

So the scan keeps classifying everything and **`LedgerMerge` declines to add a new correct row**.
Existing rows still match up, still refresh, and the "gone" detection stays honest.

### The one-time clearing

On the first run after this change, the existing sheet is put right and what happened is
announced:

> 96 rows were correct and had never been worked on. They have been taken out of the sheet.

**A row is only deleted when it has no final state at all.** A final state is the record that
something was done to that document, and it is never thrown away — whatever the verdict cell
happens to say, because the verdict is the column a hand can change and the final state is not.

| the `skip` row has | verdict becomes | lands on |
|---|---|---|
| no final state | *deleted from the sheet* | — |
| `old files deleted` | `done` | `finished` |
| `corrected and pending the delete of old docs` | `done` | `corrected` |
| `failed` | `review` | `ledger` |
| `ignore` | `ignore` | `ledger` |

The pending-delete case is the one that must not be got wrong. Its old file is still on the
server and Delete old files has not run, so it belongs on the `corrected` tab with the rest of
the outstanding deletes — not on `finished`, which would be a tab called finished holding a file
that still needs deleting.

For the deletion itself the test is **the scan's own classification**, not the stored verdict —
an operator who typed `fix` or `review` on a row keeps it. The sitting's backup copy in
`previous\` is taken before the first write, so the sheet as it was is still on disk.

**This clearing runs before §3's legacy mapping.** Otherwise every untouched `skip` row would
read as `done`, land on the `finished` tab by §5's rule, and never be cleared at all. Order:
classify → clear → map what survives.

### What this actually costs, measured

The dev ledger on 2026-09-18, before any of this:

```
  410 rows    276 fix · 125 skip · 7 review · 2 ignore
              125 skip = 96 already correct + 29 with no file path
              2 rows have a final state
```

So the clearing removes 96 rows and the sheet settles at about 314. This matters to §9: the
working file is small, and every argument about the cost of writing it has to start there.

### Group 7 empties out

Group 7 is "already the correct catalogue, or no file path at all". Its first half is now
precisely the population that never reaches the sheet. Its second half moves — see §4.

---

## 3. `skip` leaves the vocabulary

With correct documents out of the sheet, `skip` has nowhere left to land.

- `RowVerdict.Skip` and `RowVerdicts.Skip` are removed, along with `RowVerdicts.All`'s entry
  (the workbook dropdown) and `LedgerOrder.Rank`'s case.
- **`skip` stays readable.** Existing ledgers have the word in hundreds of cells, and if it
  stopped parsing every one would be reported as an unrecognised typo at the end of every run.
  `RowVerdicts.Parse` maps the legacy text to `RowVerdict.Done`. Nothing ever writes it again.
- The two places that write it today both mean *we checked, it was already right, there was
  never anything to do* — `AlreadyCorrect.Apply` (`SettleAs.AlwaysRight`) and
  `RepairOneRow.AlreadyRightAsync` (`wasAlwaysRight`). Both now write `done`, keeping the note
  they already write. `done` means nothing outstanding, which is exactly true there.
- `Verdict.Skip` — the **classifier's** enum, not the sheet's — stays. It is how the merge knows
  not to add the row.

### The message survives, the word does not

`LedgerMerge`'s disagreement report currently says *"the ledger says fix, CRM says skip"*. It
keeps the fact and loses the word:

> the scan no longer finds anything wrong with this row

and the offer changes from *set it to skip* to **mark it done** / **take it out of the sheet**.

---

## 4. A record with no file path becomes `review`, in a group of its own

A `mocd_documentfile` with an empty `mocd_filepath` is a legacy row with no file at all. Today
it is group 7, verdict skip, and would therefore vanish with the rest of §2. It should not —
there is something wrong with it and a person should look.

- New **group 8: "the record has no file path at all"**, `WillBeFixed: false`.
- Verdict `review`. The run only acts on `fix`, so nothing will ever touch one.
- It gets its own wording rather than borrowing group 6's, which is about malformed paths and
  contradicting authorities:

  > **What is in the path:** nothing — the `mocd_documentfile` record has an empty
  > `mocd_filepath`.
  > **Why it is wrong:** there is no file to move and nothing in CRM says where it went. The
  > document exists, its file record exists, and between them they name no file.
  > **How we know:** the record's own `mocd_filepath` is blank.
  > **What the tool does:** nothing. It is listed for someone to judge.

`Classifier` returns `Verdict.Review` and group 8 for this case instead of `Verdict.Skip` and
group 7.

### Say the number when there are many

In the eight services this is 29 rows — readable, and worth reading. Across every catalogue in
pre-prod it is roughly **37,000**, because two thirds of that environment is documents with no
file at all:

| | documents | of those, with a file path |
|---|---|---|
| dev | 4,510 | 2,354 |
| pre-prod | over 50,000 | 13,128 |

Thirty-seven thousand rows do not read as a list; they read as wallpaper. So the scan says the
number out loud rather than letting the rows speak for themselves:

> 37,412 documents have no file path at all. They are in the sheet as `review`. There is
> nothing this tool can do with them — no path to diagnose and no file to move.

The rows are still written. The count is what makes them comprehensible.

---

## 5. Three tabs, one per mode

Finished rows stay in the file — "Check it all" walks them, and the recheck marker lives on
them — but they leave the working sheet. And the rows awaiting deletion get a tab of their own,
because burying them among the rows still to fix hides the one list Delete old files acts on.

Each ledger file becomes three worksheets:

| tab | holds | the mode that reads it |
|---|---|---|
| `ledger` | documents still to fix | Repair run |
| `corrected` | corrected, old file waiting to be deleted | Delete old files |
| `finished` | nothing left to do | Check it all |

The rule, in order:

1. final state `corrected and pending the delete of old docs` → **`corrected`**
2. final state `old files deleted`, **or** verdict `done` with a blank final state (the
   already-right and corrected-by-sibling settlements) → **`finished`**
3. everything else → **`ledger`**

**This is presentation, not speed.** An `.xlsx` is a single zip archive and tabs are folders
inside it; writing any of it rebuilds all of it, so three tabs cost exactly what one costs. The
gain is that each mode has one place to look.

### The boundary

`LedgerWorkbook.Read()` reads all three tabs and returns one flat list. `Write()` partitions on
the rule above and writes three. **Nothing else in the tool notices** — the merge, the repair
run, Redo, Delete old files and Check it all all keep seeing one list of rows, exactly as now.
A mode is not restricted to its own tab; the table above says where a row is *shown*, not what a
mode is allowed to read.

Row numbers are per tab, so each reads 1, 2, 3 down the page — `LedgerOrder.Sorted` is applied to
each partition separately rather than once across the whole list. Row numbers were already
positional and unreliable, which is why `LedgerRow.Ref()` carries the document id; `Ref()` now
also names the tab when the row is not on `ledger`.

With finished rows on their own tab, `LedgerOrder`'s `Finished()` sort key no longer has
anything to separate on the working sheet. It stays, because it is what decides which tab a row
goes to, and the two rules must not be allowed to drift apart: the partition and the sort read
the same predicate.

---

## 6. Use the file as it is, or update it from CRM

The repair run always rescans. That means every entry into it reads every document in scope —
4,510 in dev, and far more across all catalogues — plus a catalogue-name lookup per row. When
the sheet is already in front of you and you simply want to carry on working through it, all of
that is spent for nothing.

So the repair run asks first:

```
  1  Use the ledger as it is    412 rows, last updated 2026-09-18 16:58
  2  Update it from CRM         reads every document in scope first
```

Choice 1 skips `LedgerBuilder` and `LedgerMerge` entirely and goes straight to the work menu of
§7. It still reads the sheet from disk, still replays the change journal, and still runs the
verdict guard of §8 — everything except asking CRM.

**It is safe to skip.** Before a single byte is uploaded the run already asks CRM, row by row,
whether that document is now filed correctly (`AlreadyCorrect`, the pre-run check). A sheet that
is a day stale cannot cause a double upload; it can only cause a row to be settled at the start
of the run instead of by the scan.

The question is asked after the scope question of §9 and before the work menu of §7.

---

## 7. Reloading the sheet mid-run

Edit the sheet after a rescan has written it and the run works from what it read before your
edits. There is no way to say "look again".

`NarrowToOneDocument` gains a third choice and becomes a loop:

```
  1  From the file          every row marked fix — 412 of 1,180
  2  One document           type its GUID
  3  Read the sheet again   pick up edits you just made
```

Choice 3:

- re-reads the workbook from disk and runs it through `Reconciled` (journal replay) and the
  verdict guard of §8, exactly as the rescan does
- reports what moved — *"83 rows changed: 80 fix → ignore, 3 review → fix"*
- returns to the same question with the counts refreshed

A reload **only reads**. It never writes the sheet back. The run still insists Excel is closed
before any document is worked on, because the ledger is written after each one.

`NarrowToOneDocument` returns both the working set and the (possibly re-read) whole set, so the
caller's `all` is replaced too. A small `WorkingSet(IReadOnlyList<LedgerRow> Working,
IReadOnlyList<LedgerRow> Whole)` record carries it.

### The other three modes get it too

Every mode already reads the sheet fresh from disk the moment it is chosen, so a sheet edited
*before* picking Delete old files is picked up as it is. That much works today.

The gap is **after** a mode has read the sheet and shown what it is about to do, while it waits
for the answer. That is the moment an operator thinks "hold on, that row should not be in
there", edits Excel — and the mode goes ahead on what it read a minute ago.

So the confirmation in Delete old files, Redo and Check it all becomes three-way:

```
  1  Go ahead
  2  Read the sheet again      pick up edits you just made
  3  Cancel
```

One helper, `AskWithReload`, used by all four modes, so the reload cannot mean one thing in one
place and something else in another. It re-reads, replays the journal, runs the verdict guard,
reports what moved, and asks again.

It matters most in Delete old files, which is the irreversible one.

---

## 8. The verdict you change by hand on a finished row

### The bug behind the request

**Delete old files reads the final state column, not the verdict.**

| verdict | final state | Delete old files |
|---|---|---|
| `done` | corrected and pending the delete of old docs | old file deleted |
| `ignore` | corrected and pending the delete of old docs | **old file still deleted** |
| `ignore` | ignore | nothing happens |

Setting the verdict to `ignore` on a finished row does nothing. People reasonably expect it to
stop the delete. It does not.

### The guard

On **every read-back of the sheet** — rescan merge, reload (§7), and the existing hand-edit
prompt — any row whose verdict has moved **off `done`** while its final state still records work
done is caught, grouped, and put to the operator:

1. **Put it back to `done`** — it is done; the final state is the record *(default)*
2. **Keep `ignore`, and stop the delete too** — also writes `ignore` into the final state, so
   Delete old files will not touch the old file
3. **Keep exactly what I typed** — verdict `ignore`, final state untouched, and the old file
   **will** still be deleted

Choice 2 is offered only when the final state says *corrected and pending the delete*. Once it
says *old files deleted* the file is gone and there is nothing left to stop.

### The exceptions

- `done` → `redo` is **never questioned**. That is what Redo is for.
- `done` → `fix` gets the same three-way guard with louder wording, because re-running a
  corrected row uploads a second copy and orphans the first.

### Bulk

Grouped, a few shown, asked once — and the existing `Session.Confirm(int)` warning fires when it
is more than one row:

> This changes the verdict on 83 row(s). It cannot be undone from inside the tool.
> Change all 83?

**Rejected:** making `ignore` in the verdict column stop the delete on its own. It is the more
forgiving behaviour, but then two columns control the same thing and neither is the record of
what happened.

---

## 9. Choosing the scope, and two ledger files

### What CRM actually holds

Measured 2026-09-17 against the live environments:

| | service catalogues | documents |
|---|---|---|
| dev | 54 | 4,510 |
| pre-prod | 51 | **over 50,000** (past the server's aggregate ceiling) |

Ten of dev's 151 document types carry **no service catalogue at all**. Their documents cannot be
reached through the catalogue route and will not appear even in an all-services scan.

### The question

Asked **once, right after the environment is chosen, before the main menu** — because the answer
governs every mode, not just the repair run:

```
  1  The services we work on     8 services      ← default
  2  Every service catalogue     asks CRM first
```

The choice is shown in the banner beside the environment, and a **Change services** entry sits
next to *Change environment* in the menu so it can be switched without restarting.

The last answer is remembered per environment and becomes that environment's default next time.
It is stored on `EnvironmentConfig` in `config.json` as `Scope` (`"ours"` or `"all"`), not on
`AppConfig` — dev may be surveying all 54 while pre-prod stays on the eight. A missing or
unreadable value means `"ours"`.

### The eight

The seven currently in `AppConfig.Default()` plus **Membership Managment**
(`6bcb221c-6c2b-f111-b119-005056010908`), which was removed from scope on 2026-09-13 and is
being added back at the operator's request on 2026-09-18. The "do not add it back without
asking" comment is replaced with a note saying when and why it returned.

### Choosing "every service catalogue"

It queries `mocd_servicecatalogues` **live** rather than using any stored list, reports what it
found, and confirms before reading a single document:

> 54 service catalogues, 151 document types, roughly 4,500 documents.
> 10 document types have no service catalogue, so their documents cannot be reached this way.
> Scan all of them?

This needs one new method on `ICrmReadClient`:

```csharp
/// <summary>Every mocd_servicecatalogue, for the all-services scope. Id and display name.</summary>
Task<IReadOnlyList<(Guid Id, string Name)>> GetServiceCataloguesAsync(CancellationToken ct) =>
    Task.FromResult<IReadOnlyList<(Guid, string)>>(Array.Empty<(Guid, string)>());
```

A default implementation, following the pattern `FindDocumentFilesByFileIdAsync` already uses on
this interface, so the existing test doubles keep compiling.

### Two files, and the sets do not overlap

```
repair-{env}.xlsx                  the 8 services
repair-{env}-other-services.xlsx   every catalogue that is not one of the 8
```

The second file holds **the others**, not "all" — so a document lives in exactly one file and
the two can never disagree about it.

- Choose the 8 → only the first file is opened, read and written.
- Choose all → both are open, both are refreshed by the scan, and the run works across both.

`repair-{env}.xlsx` keeps its existing name, so the current ledger carries on as the 8-services
file with nothing migrated. The other-services file is new and starts empty.

A small `LedgerSet` holds one or two `LedgerStore`s and routes a row to the file its service
catalogue belongs to. Everything above it — the merge, the modes — keeps seeing one flat list.

### Why two files and not more tabs in one

Measured, with §10's fix already applied:

| rows in one file | seconds per save |
|---|---|
| 2,000 | 0.6 |
| 50,000 | 11.4 |

The ledger is saved after every completed document. Put 45,000 other-service rows in the same
workbook and every document corrected in the 8 pays eleven seconds for rows nobody is looking
at. Two files keep the common case fast however large the other set grows.

Tabs would not help. An `.xlsx` is a single zip archive and a tab is a folder inside it, so
writing one tab rewrites the whole file — the cost follows the **file**, never the tab. That is
why §5's three tabs are a matter of clarity and this is a matter of speed: they are answers to
different questions and only one of them can be solved by dividing a workbook up.

### When a document changes service

A document type can be moved to another service, so a row can belong in the other file.

- On an **all-services** run both files are in hand, so the row moves and the move is reported:
  *"3 rows moved to the other-services file"*.
- On an **8-services** run the other file is not open. A row that has left the 8 is reported as
  out of scope by the existing `ReportGone` path — kept, nothing acts on it — and moves on the
  next all-services run.

### Out-of-scope rows collapse to one line

`ReportGone` currently prints a bullet per row, capped at ten. Out-of-scope rows become one
counted line; only genuinely missing documents are listed individually:

> 41,800 rows belong to services you did not scan this run — nothing will act on them.

---

## 10. Fixed column widths

`LedgerWorkbook.Write` ends with `sheet.Columns().AdjustToContents()`, which measures every cell
in every column to fit the widths. It runs on **every ledger write**, which is after every
completed document.

Measured with the real code, 29 columns:

| | 2,000 rows |
|---|---|
| as written today | **15.67s** |
| without `AdjustToContents` | **0.57s** |

Ninety-seven per cent of the time. On a 1,000-row ledger that is about eleven seconds per
corrected document — over an hour across a 400-document run, spent entirely on column widths.

It is replaced by fixed widths per column: narrow for the row number, the group and the two
edited columns; medium for names and verdicts; the capped 60 for paths, links, reasons and
notes. The sheet looks the same.

This is a defect in the current build, independent of everything else here.

---

## 11. The ledger is written to a temporary file and renamed into place

`LedgerWorkbook.Write` saves straight over the ledger. A process killed during that save — the
console window closed, the machine shut down — leaves a half-written workbook, and a half-written
workbook opens as nothing at all.

The window is small, because §10 takes the save down to a fraction of a second. It is not zero,
and it sits exactly where an impatient operator closes the window.

So the workbook is saved to `repair-dev.xlsx.tmp` and then moved over the real file. A move
within one folder is atomic on NTFS, so the ledger on disk is always either the whole previous
version or the whole new one, never part of either. A stray `.tmp` left by a kill is deleted on
the next write.

Cheap, and it makes killing the tool harmless at any instant.

---

## Testing

- **LedgerStore / LedgerWorkbook** — no `.csv` is written; an existing one is deleted once; the
  three tabs round-trip; a pending-delete row lands on `corrected`; a deleted one on `finished`;
  a `done` row with a blank final state on `finished`; row numbers restart per tab; a legacy
  single-tab workbook still reads; the save goes via `.tmp` and a stray `.tmp` is cleared.
- **LedgerColumns** — the local `[Column]` attribute yields the same headers in the same order
  as the CsvHelper attributes did.
- **LedgerMerge** — a new correct row is not added; an existing correct row is not reported as
  vanished; a row corrected outside the tool is not reported as vanished.
- **The one-time clearing** — each of the five rows of §2's table lands where it says; a row
  with a typed `fix` or `review` is never deleted; the clearing runs before the legacy mapping,
  so no untouched `skip` row reaches `finished`.
- **Classifier** — an empty `mocd_filepath` yields `Verdict.Review` and group 8; the count is
  reported when there are many.
- **RowVerdicts** — `skip` parses as `Done`; it is absent from `All`; nothing writes it.
- **AlreadyCorrect / RepairOneRow** — the always-right settlement writes `done`, not `skip`.
- **The verdict guard** — each of the three answers writes what it says; `done` → `redo` is not
  questioned; `done` → `fix` is; the bulk confirmation fires above one row.
- **Scope** — the 8 includes Membership Managment; the all-services scope queries CRM live; a
  row is routed to the file its catalogue belongs to; an 8-services run never opens the other
  file; out-of-scope rows are counted, not listed.
- **Reload** — re-reading picks up an edited verdict and returns to the same question; it never
  writes the sheet; all four modes use the same helper.
- **Use as it is** — choosing it calls neither `LedgerBuilder` nor `LedgerMerge`, and still
  replays the journal and runs the verdict guard.

## Not doing

- **Making `ignore` stop the delete on its own** (§8). More forgiving, but then two columns
  control the delete and neither is the record of what happened.
- **Writing the ledger less often than once per document.** The change journal would cover the
  corrections, but not the decisions that never touch CRM — settlements by the pre-run check,
  failures, notes, verdict changes agreed at a prompt. Those would be lost on a kill. §10 and
  §11 make the per-document write cheap and safe instead.
- **Splitting the other-services file into several files.** It would work — 11,000 rows saves in
  under three seconds against 11 for 45,000 — but the working file is 410 rows (§2), and the
  only file large enough to need it is one nobody will grind through row by row.
- **Using tabs to save time.** An `.xlsx` is one zip archive; writing any part rebuilds all of
  it. Three tabs cost exactly what one costs. The tabs in §5 are for clarity only.
- **Making CRM filter out the correct documents server-side.** It would mean one query per
  document type — 151 instead of batches of 20 — and would probably be slower, and documents
  with no file path would need special handling or be filtered away with them. Revisit only if
  the all-services scan proves painful.
- **Reaching documents whose document type has no service catalogue.** There is no catalogue
  route to them; the all-services scan says so rather than implying otherwise.
