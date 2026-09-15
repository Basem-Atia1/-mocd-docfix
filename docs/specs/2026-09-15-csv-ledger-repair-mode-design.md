# The CSV ledger, and one mode that works from it

**Date:** 2026-09-15
**Status:** Approved, not yet implemented

## Why

The tool has grown seven ways in and five kinds of report. A full run writes a grouped
report, a GUID list, a migration report, a verify report and a folder of numbered text files
per document — and an operator who wants to know what is left to do has to read four of them
and hold the answer in their head. Meanwhile the DevOps cross-check stops runs to ask
questions the operator cannot answer from the screen.

This replaces all of it with one file. A CSV holding every document in scope, written once at
the start, edited in place as the work happens, and read back before every step so the
operator's own corrections count. It is the report, the work queue, the progress record and
the route back, and there is nothing else to read.

## What is removed

**The DevOps cross-check, entirely.** `AdoClient`, `DocumentTypeAuthority`,
`LocalBacklogSearch`, `DocumentTypeCheck`, `AdoSetup`, `DocumentTypeDecisions`, the `Ado`
block in `AppConfig`, and the seven test files that cover them. No ADO connection, no reading
of the knowledge-base copy, no saved type decisions.

`mocd_servicecatalogue` on the document type becomes the only authority on which service owns
a document. The CRM-internal cross-check stays — the parent request (`mocd_gamrequest`,
`mocd_employeeappintmentrequest`, `mocd_BylawsAmendmentRequestId`) is still compared against
the document type, and a disagreement is still group 6. That is CRM asking itself, not DevOps.

**Every report but the ledger.** `GroupedReportWriter`, `GuidListWriter`,
`RepointedListWriter`, `DocumentReportStore`, `DocumentRecord`, `Reporter`. The per-document
`01-check.txt` … `06-old-file.txt` folders go with them.

**The commands whose job is now a column or a step.** `ScanCommand`, `TargetedCommand`,
`MigrateCommand`, `VerifyCommand`, `OldFileCheckCommand`, `BackupCommand` (it becomes step 1
of the repair loop), `DeleteCommand` (it becomes `DeleteOldFiles`).

**`Reconciler`.** It exists to correct a state file that disagrees with CRM, and it does so by
detecting that a document points at *some other* record — which no longer ever happens,
because nothing is repointed. `Check it all` answers the same question against the ledger.

**`MigrationState`, `StateStore` and `StateRecord`.** The `final state` column records where a
row got to, and `ChangeJournal` (§ Durability) keeps the append-only history. The JSONL
mechanism is carried over; the type is not.

**`StepGate` and `CheckLines`.** The first gated the five-step pipeline, which is gone; the
second printed a scan row, which `RunProgress` now does.

**Kept unchanged:** `Classifier` (it decides verdict and group), `BackupStore`,
`DocumentFolder`, `FileServiceClient`, `CrmReadClient`, `Verifier`, `CheckResult`,
`VerificationReport`, `FileRecordCopier`, `FilePathParser`, `FilePaths`, `DocumentGroups`,
`LookupCommand`, `Asker`, `Prompts`, `Screen`, `Selection`, `FileOpener`.

## The ledger

`<DataRoot>\repair-<env>.csv`. UTF-8 with a BOM, so Excel opens Arabic file names correctly.

### Columns

| # | header | written by | operator edits |
|---|---|---|---|
| 1 | `row` | scan | |
| 2 | `doc id` | scan | |
| 3 | `doc name` | scan | |
| 4 | `backup path` | backup | |
| 5 | `doc file id` | scan | |
| 6 | `doc file name` | scan | |
| 7 | `doc type name` | scan | |
| 8 | `service catalogue id` | scan | |
| 9 | `service catalogue name` | scan | |
| 10 | `correct service catalogue id` | scan | |
| 11 | `correct service catalogue name` | scan | |
| 12 | `old file path` | scan | |
| 13 | `new file path predicted` | scan | |
| 14 | `new file path` | upload | |
| 15 | `superseded paths` | redo | |
| 16 | `old category` | scan | |
| 17 | `old hash` | scan | |
| 18 | `old file name` | scan | |
| 19 | `old file id` | scan | |
| 20 | `verdict` | scan | **yes** |
| 21 | `group` | scan | |
| 22 | `reason of bug` | scan | |
| 23 | `solution` | scan | |
| 24 | `final state` | the steps | **yes** |
| 25 | `error` | on failure | |
| 26 | `notes` | redo | |
| 27 | `crm link of doc` | scan | |
| 28 | `crm link of doc file` | scan | |
| 29 | `way of upload` | scan | |

`backup path` sits at column 4, near the front, so the folder holding a document's bytes is
reachable without scrolling. It is blank until that row is backed up.

`new file path predicted` is the shape the corrected path will take —
`DigitalServices\<correct catalogue>\<today, yyyyMMdd>\(new id).<ext>` — with the file id the
server will mint shown literally as `(new id)`, because it is not knowable until the upload
returns.

`way of upload` is `portal` or `plugin`, read from the old record the way
`FileRecordCopier.StyleOf` reads it: a record with `mocd_fileid` was made by the plugin, one
without by the portal. This requires `mocd_fileid` to be added to the `mocd_documentfile`
expand in `CrmReadClient.Expand`, which today selects only
`mocd_filepath,mocd_hash,mocd_name,mocd_mediatype`.

Columns 16–19 are the old record's values for the four fields a correction overwrites. They
are what makes a revert possible, and they are written from the record itself at scan time —
so a blank cell means the original held nothing there.

### `verdict` — whether the loop touches the row

| value | written by | effect |
|---|---|---|
| `fix` | scan | worked on: back up, upload, check, update the record |
| `review` | scan | skipped and tallied. The path is malformed, or the parent request and the document type disagree. The operator reads `reason of bug` and sets it to `fix` or `ignore` |
| `skip` | scan | skipped. Already filed correctly, or no file path at all |
| `ignore` | operator | never touched, by any mode, whatever the other columns say |
| `redo` | operator | ignored by the repair run; acted on by the Redo mode |
| anything else | operator, by typo | treated as `ignore`, and listed at the end of the run as unrecognised so it cannot fail silently |

### `final state` — where the row got to

| value | written by | meaning |
|---|---|---|
| *(blank)* | — | not started |
| `corrected and pending the delete of old docs` | repair run | new file uploaded, every check passed, record updated and read back. The old file is still on the server. **The only value the delete step acts on** |
| `old files deleted` | delete | the old file is gone from the file server, confirmed. Terminal |
| `ignore` | operator | the old file is kept forever; the delete step walks past it |
| `failed` | any step | it broke here. `error` says how, `errors-<env>.txt` says it in full. Not retried until the operator clears it |

### How it is read and written

Every mode **re-reads the CSV from disk** before it starts, so hand edits made since the last
run take effect. Every mode **rewrites it in full the moment a row finishes**, so an
interruption loses at most the row in flight.

On entering a mode that writes, if `repair-<env>.csv` already exists it reports how many rows
it holds and how many are already corrected, then asks: carry on with it, or start a new one.
Starting a new one renames the existing file with a timestamp; it is never overwritten.

## Durability

The ledger is no longer a report. Because a correction overwrites the old
`mocd_documentfile` in place, the record of what a document used to be exists nowhere in CRM
and nowhere on the file server — only in this CSV and in `record.json` under the backup
folder. Two guards follow from that.

**`<DataRoot>\changes-<env>.jsonl` — an append-only journal.** One line per change, written
as it happens, never rewritten and never edited:

```json
{"at":"2026-09-15T10:32:04Z","doc":"a3f1b2c4-…","record":"2a1c51a3-…",
 "action":"corrected",
 "old":{"path":"DigitalServices\\docTypeCatalogue\\20250509\\a3f1….jpg",
        "category":"docTypeCatalogue","hash":"9f86d081…","filename":"a3f1….jpg","fileid":null},
 "new":{"path":"DigitalServices\\7c20a1f4-…\\20260915\\b2c3….jpg",
        "category":"7c20a1f4-…","hash":"5e884898…","filename":"b2c3….jpg","fileid":null}}
```

`action` is one of `corrected`, `reverted`, `deleted`. This is `StateStore` extended to carry
before-and-after values; the append-only JSONL mechanism already exists and is reused. It
lets the CSV be rebuilt if it is lost, survives a torn write (a bad last line is skipped and
the rest still reads), and keeps the history that a hand-edit in Excel would otherwise
destroy.

**`<DataRoot>\repair-<env>-<yyyyMMdd-HHmmss>.bak.csv`.** A copy of the ledger taken before
the first write of each sitting.

## The menu

```
  1  Repair run                  build the ledger, then work through it
  2  Delete old files            the old files of corrected rows
  3  Redo                        put records back the way they were
  4  Check it all                confirm the old files really are gone
  5  Is this file still there?   one path or documentfile id
  6  Change environment          currently dev
  7  Quit
```

Entries 1–4 of the old menu — Targeted, Full, Just report, Check it all — collapse into
entry 1. `Check it all` survives as entry 4 with a new job (§ Check it all).

## Repair run

### Entering

It prints the in-place warning once, not per file:

> Corrections are written into the existing `mocd_documentfile` record. A portal-created
> record's id equals its original file id, and after a correction it no longer will. Nothing
> in CRM reads a file path off the record id — `DownloadDocument` takes a FilePath, and both
> callers read `mocd_filepath` off the record — so this breaks the convention, not any code
> path. There is no new record and nothing is repointed.

Then it reads CRM for every document under the seven configured service catalogues —
correct ones, broken ones, and legacy ones with no file path — classifies each with
`Classifier`, and writes the ledger. It prints the path and asks:

- **work from the file**, or
- **one document** — the operator types a GUID.

### Per row

Working from the file, in ledger order, for every row whose `verdict` is `fix`:

```
[ 12/431 ]  Board of Director's Decision
            doc a3f1b2c4-…   cert.jpg
    backing up ……………………… ok   backup\dev\cert.jpg__a3f1…\
    uploading ………………………… ok   …\7c20a1f4\20250915\b2c3….jpg
    hash matches …………………… ok
    genuinely new ………………… ok
    path is fixed ………………… ok
    round-trip ……………………… ok
  > do these two files look the same? (y/n)
    updating the record ……… ok
    reading it back ……………… ok
            FINISHED — corrected and pending the delete of old docs
```

The steps, in order:

1. **Back up.** Download the old file, save it under
   `backup\<env>\<file name>__<doc id>\old\`, save the complete old `mocd_documentfile` as
   `record.json` beside it. Write `backup path`.
2. **Upload** the same bytes with `Category` = the correct catalogue, sending the extension
   and applicationId in the style the old record used (`FileRecordCopier.ExtensionFor`,
   `ApplicationIdFor`). Write `new file path`.
3. **Check**, with the existing `Verifier`: the vendor's hash of the new copy matches the
   old, the new file is genuinely a different file at a different path, the new path parses
   to the correct catalogue, and a round-trip download of the new file is byte-identical to
   the backup.
4. **Show the operator** both copies — `_opener.Open` on each — and ask *do these two files
   look the same?* A no leaves everything as it is: the new copy stays on the server
   unreferenced, the record is untouched, and the row's final state stays blank with the
   reason in `notes`.
5. **Update the record in place.** `mocd_filepath`, `mocd_category` and `mocd_hash` always;
   `mocd_filename` and `mocd_fileid` only where the old record held them. No new record. No
   repoint — the document already points at this record.
6. **Read the record back** and confirm `mocd_filepath` holds the new path. Only then write
   `final state = corrected and pending the delete of old docs`, append the `corrected` line
   to the journal, and rewrite the CSV.

The eye-check at step 4 is the only question inside the loop. The operator chose what happens
to every row when they entered the mode, so nothing else is asked twice.

Rows whose verdict is `review`, `skip`, `ignore`, `redo` or unrecognised are not touched. The
run ends with a tally of each, and unrecognised verdicts are listed with their row numbers.

### One document

The same six steps for a single row, found in the ledger by document GUID. If the GUID is not
in the ledger the run says so and offers to refresh the ledger from CRM rather than acting on
a document it has no record of.

## Redo

Acts on rows where `verdict = redo` **and** `final state` is not `old files deleted`. Once the
old file is off the server there is nothing to go back to, and such a row is reported as
refused rather than silently skipped.

Per row:

1. **Ask the file server whether the old file is still at `old file path`.** If it is not,
   refuse the row, say so, and move on. Nothing is written. This guards against a ledger that
   has gone stale because somebody removed the file outside the tool.
2. **Read `record.json`** from the backup folder. Compare its four values against the ledger's
   columns 16–19 and report any field where they differ. **The snapshot is what gets
   written** — it is the record as it actually was, so it cannot be wrong — and the report
   makes clear that the CSV's value was not used.
3. **Write the old values back** into the same record: exactly the fields a correction
   overwrote, with the values they held. A correction never fills a field that was empty
   (`mocd_filename` and `mocd_fileid` are written only where the old record used them), so
   nothing needs clearing and no field goes from filled to empty.
4. **Update the ledger:** clear `new file path` and append its value to `superseded paths`,
   semicolon-separated; set `verdict = fix`; clear `final state`; stamp `notes` with
   `reverted <date time>`. Append the `reverted` line to the journal.

The corrected copy stays on the file server with nothing referring to it. Its path is in
`superseded paths` so it can be cleaned up by hand; no mode deletes it, because no mode
deletes a file it is not certain about.

## Delete old files

Reads the ledger and takes only rows whose `final state` is exactly
`corrected and pending the delete of old docs`. A row saying `ignore`, `failed`, `old files
deleted` or nothing at all is not eligible.

Because the correction updated the record rather than replacing it, there is no orphaned CRM
record to remove. The step has one job: **delete the old file from the file server.**

Before each deletion, two checks against CRM:

- the row's own `mocd_documentfile` now holds the **new** path, read live — not the ledger's
  belief about it;
- no *other* `mocd_documentfile` references the old path
  (`FindDocumentFilesByPathAsync`). One file referenced by two records is rare and real, and
  deleting it would break the other record.

A row failing either check is refused, with the reason, and its final state is left alone. A
row that passes is deleted, its final state becomes `old files deleted`, and a `deleted` line
goes into the journal.

Production is confirmed once before the step runs, as it is today.

## Check it all

Reads only. Walks the ledger and asks both systems what is actually true:

- rows saying `old files deleted` — confirm the old file is gone from the file server and no
  `mocd_documentfile` still references the old path;
- rows saying `corrected and pending the delete of old docs` — confirm the old file is still
  there, and that the record holds the new path.

It reports counts for each and lists every row where the systems disagree with the ledger. It
writes nothing and repairs nothing.

## Errors and halting

Any failure in any step of any mode:

1. the row's `final state` becomes `failed` and `error` gets the short reason;
2. the full detail — step, request, response, exception — is appended to
   `<DataRoot>\errors-<env>.txt`;
3. the CSV is rewritten immediately;
4. the run **stops and asks**: continue with the next row, or stop here.

Stopping is clean: everything before the failed row is recorded, and re-entering the mode
picks up from the ledger.

## Shape

**New files**

| file | responsibility |
|---|---|
| `Domain/LedgerRow.cs` | one row — every column, and the two vocabularies as constants |
| `Domain/LedgerVerdict.cs`, `Domain/FinalState.cs` | parsing a cell to a known value, with unrecognised as its own case |
| `Storage/LedgerStore.cs` | read, write, rewrite-on-row-completion, `.bak` on first write |
| `Storage/ChangeJournal.cs` | the append-only JSONL, before-and-after |
| `Storage/ErrorLog.cs` | `errors-<env>.txt` |
| `Commands/LedgerBuilder.cs` | CRM → classified rows → ledger; refresh of an existing one |
| `Commands/RepairRun.cs` | the loop and its six steps |
| `Commands/RedoRun.cs` | the revert |
| `Commands/DeleteOldFiles.cs` | ledger-driven; replaces the eligibility half of `DeleteCommand` |
| `Commands/CheckItAll.cs` | ledger-driven read-only confirmation |
| `Ui/RunProgress.cs` | the per-row progress block |

**Changed**

- `Cli/Wizard.cs` — the new seven-entry menu; the five-step pipeline and its gates go.
- `Cli/Session.cs` — wiring: no ADO, no report writers, the new stores and commands.
- `Clients/CrmReadClient.cs` — add `mocd_fileid` to the `mocd_documentfile` expand.
- `Clients/CrmWriteClient.cs` — an update-in-place method for `mocd_documentfile`. The
  existing `CreateDocumentFileAsync` and `RepointDocumentAsync` are no longer called by any
  mode and go with `MigrateCommand`.
- `Config/AppConfig.cs` — the `Ado` block goes.
- `Cli/CommandLineOptions.cs`, `Program.cs` — the direct commands follow the new modes.

`RepairRun` is the one at risk of growing too large. If the six steps plus the progress
printing push it past roughly 300 lines, the per-row work moves to `Commands/RepairOneRow.cs`
and `RepairRun` keeps only the loop, the ledger and the halting.

## Testing

Test-first, one test file per class, as the project already does.

**`LedgerStoreTests`** — a written ledger reads back identically; a hand-edited verdict
survives a rewrite; a blank old-value cell stays blank rather than becoming null-ish; a
rewrite after each row leaves a readable file if the process stops between rows; the `.bak`
is taken once per sitting, not once per row.

**`LedgerRowTests`** — `fix`, `review`, `skip`, `ignore`, `redo` parse; `FIX ` and `Fix`
parse; `fixx` is unrecognised and is treated as `ignore` rather than as `fix`.

**`RepairRunTests`** (fake CRM, fake file service, fake prompts) — a `fix` row goes end to
end and ends `corrected and pending the delete of old docs`; a `review` row is untouched; a
`no` to the eye-check writes nothing to CRM and leaves the final state blank; an upload
failure writes `failed`, the error column, the error log, and asks; answering *stop* ends the
run with earlier rows intact; the record update writes `mocd_fileid` for a plugin row and not
for a portal row.

**`RedoRunTests`** — a row whose old file is gone is refused and nothing is written; a
disagreement between ledger and `record.json` is reported and the snapshot is what is written;
after a revert the row is `verdict=fix`, blank final state, and the new path has moved into
`superseded paths`; a row at `old files deleted` is refused.

**`DeleteOldFilesTests`** — only `corrected and pending the delete of old docs` is eligible;
a second `mocd_documentfile` sharing the old path refuses the deletion; a record that does not
hold the new path refuses the deletion.

**`CheckItAllTests`** — a deleted-but-still-present old file is reported; nothing is written.

**`ChangeJournalTests`** — a `corrected` line round-trips; a torn final line is skipped and
the rest still reads; the ledger's old-value columns can be rebuilt from the journal.

Surviving unchanged: `ClassifierTests`, `VerifierTests`, `FileRecordCopierTests`,
`FilePathParserTests`, `FilePathsTests`, `CrmReadClientTests`, `CrmWriteClientTests`,
`FileServiceClientTests`, `BackupStoreTests`, `LookupCommandTests`, `AskerTests`,
`ConsolePromptsTests`, `ScreenTests`, `SelectionTests`, `ConfigStoreTests`,
`EnvironmentPickerTests`, `DocumentGroupsTests`, `DocumentFolderTests`.

Rewritten rather than deleted: `WizardTests`, for the seven-entry menu.

Deleted with their subjects: `DocumentTypeAuthorityTests`, `AdoClientBodyTests`,
`LocalBacklogSearchTests`, `ScanDevOpsTests`, `DocumentTypeCheckTests`, `LiveBacklogTests`,
`MigrateTypeCheckTests`, `MigrateCommandTests`, `TargetedCommandTests`, `TargetedReportTests`,
`VerifyCommandTests`, `OldFileCheckTests`, `ReporterTests`, `GroupedReportWriterTests`,
`GuidListWriterTests`, `DocumentReportStoreTests`, `ScanCommandTests`, `BackupCommandTests`,
`DeleteCommandTests`, `PipelineDeleteStepTests`, `StateStoreTests`, `Fakes/FakeAdoClient.cs`.

## Risks accepted

**Nothing can contradict CRM any more.** With the DevOps cross-check gone, a document type
whose `mocd_servicecatalogue` is wrong will send its files confidently to the wrong service.
The `verdict` column is the only place this can be caught, by a human reading the ledger.
Accepted deliberately: the cross-check was stopping runs to ask questions the operator could
not answer at the keyboard.

**In-place modification is the only route back.** A correction overwrites the old record, so
CRM no longer remembers the before-state. The ledger, the journal and `record.json` are it.
Mitigated by the journal and the `.bak` copies, not eliminated by them.

**The portal id convention breaks.** After a correction, a portal-created record's id no
longer equals the file id in its path. Verified on 2026-09-15 against the CRM checkout that
nothing derives a path from the record id: `DownloadDocument.cs` takes a `FilePath` input
parameter, and both callers — `ViewDocumentJS.js:406` and `SaveDocumentFileAsNote.cs:35` —
read `mocd_filepath` off the record by record id. The convention is cosmetic; no code path
depends on it.

## Out of scope

- Deleting the superseded copies a revert leaves on the file server. Their paths are
  recorded; removing them is a manual decision.
- Rebuilding a lost ledger from the journal. The journal is written so that this is possible;
  the command to do it is not built until it is needed.
- Any write to Azure DevOps. There is no longer any read from it either.
