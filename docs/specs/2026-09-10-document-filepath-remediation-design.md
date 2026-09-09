# Document File Path Remediation Console App — Design

**Status:** Draft for review
**Date:** 2026-09-10
**Author:** Basem Atia (with Claude)
**Scope:** MoCD NPO Phase 2 — Employee Appointment through By-Laws Amendment

---

## 1. Problem

Files uploaded through the portal are stored on an external vendor file server at a path
built from a `Category` value the caller supplies:

```
DigitalServices\{Category}\{yyyyMMdd}\{fileGuid}.{ext}
```

`Category` is supposed to be the **service catalogue GUID**. For a long period the portal
sent the **document type** identifier instead — in several different formats — or sent
nothing at all. The result is thousands of files filed under the wrong folder.

The path is recorded in `mocd_documentfile.mocd_filepath`. It cannot be corrected in place:
the vendor's upload endpoint accepts no path, so fixing a file means **re-uploading it with
the correct `Category`** and repointing CRM at the new copy.

### 1.1 Evidence

Measured on `mocd-pre-prod`, 2026-09-09, over the 2352 in-scope documents:

| Count | Path segment 2 holds | Verdict |
|------:|---|---|
| 879 | a document type **name** (`goodConductCertificate`, `academicQualificationCertificate`, `boardDecision`, `paymentReceipt`, …) | broken |
| 318 | `docType` + document type GUID with dashes stripped (`docType2746f51e7e3ef111b119005056010908`) | broken |
| 259 | the literal `0` | broken |
| 257 | a GUID — resolves to either a catalogue or a document type, see §4.3 | broken / ambiguous |
| 25 | *nothing* — the date became segment 2 (`DigitalServices\20260517\file.pdf`) | broken |
| 403 | the correct service catalogue GUID | correct |
| 211 | no file path at all (legacy annotation-era rows) | out of scope |

**1738 of 2141 path-bearing documents (81%) are wrong.**

Resolved examples confirming the root cause:

- `2746f51e-7e3e-f111-b119-005056010908` → `mocd_documenttype` "Other Documents"
- `9b1121f4-e30b-f111-b117-005056010908` → `mocd_documenttype` "A Medical Examination Certificate"
- `9a39aa75-9933-f111-b119-005056010908` → `mocd_servicecatalogue` "Request to Join NPO"
- `732030c4-895c-f011-b112-005056010908` → `mocd_servicecatalogue` "OBA Request"

### 1.2 The defect is still live

Broken files are still being created. By date folder:

| Month | total | correct | broken |
|---|---:|---:|---:|
| 2026-03 | 297 | 0 | 297 |
| 2026-04 | 652 | 0 | 652 |
| 2026-05 | 461 | 0 | 461 |
| 2026-06 | 103 | 0 | 103 |
| 2026-07 | 207 | 70 | 137 |
| 2026-08 | 135 | 114 | 21 |
| 2026-09 | 286 | 219 | **67** |

The portal fix landed around July 2026 but did not close every path. Two producers remain:

1. **`Ministerial Decision - By-Laws Amendment`** — 15 files, ongoing to 2026-09-07, all with
   an empty `Category` (no segment at all).
2. **`Payment Receipt` / `Certificate of Good Conduct` / `Board of Directors' decision to Join
   NPO`** — 73 files receiving catalogue `9a39aa75…` ("Request to Join NPO"). See §4.3 — these
   may not be defects.

**Consequence for this design:** the tool is a **repeatable** remediation job, not a one-shot
migration. It must rescan on every run and must never reuse a stale scan.

**Recommended alongside:** raise the two live producers as defects in their own right. This
tool cleans up; it does not stop the source.

---

## 2. Scope

Files whose document type's `mocd_servicecatalogue` is one of:

| Service catalogue GUID | Service |
|---|---|
| `cd97bf8d-bea8-f011-b116-005056010908` | Employee Appointment Request |
| `6bcb221c-6c2b-f111-b119-005056010908` | Membership Managment |
| `3ff27d73-653e-f111-b119-005056010908` | General Assembly Meeting Request |
| `d2744b68-aa50-f111-b119-005056010908` | GAM – Nomination List Request |
| `24db2387-c15d-f111-b119-005056010908` | GAM – Attendance |
| `35105602-2b5f-f111-b119-005056010908` | GAM – Update (Reschedule) |
| `d8155dcc-635e-f111-b119-005056010908` | GAM – Minutes of Meeting |
| `930f636a-077a-f111-b119-005056010908` | By-Laws Amendment Requests |

The list is **configuration, not code** — see §7.

> `d2744b68…` (Nomination List) has no constant in `Constants.cs`. Worth adding there separately.

**Out of scope:** every other service; documents with no `mocd_filepath`; document types with no
service catalogue (nothing to write).

---

## 3. Endpoint behaviour (verified in source)

### 3.1 The two layers

`FileService` (`MoCD.EServices.Data.CRM/CRM Data Services/FileServerServices/FileService.cs`)
talks **only** to the vendor. It contains no `OrganizationService` and writes nothing to CRM.
The CRM records are created by the **caller**, `DocumentDataService.UploadDocument`, at lines
70 and 97.

**This tool calls the vendor endpoints directly and performs its own CRM writes.** It must not
call `api/Document/...`, which would create a duplicate `mocd_document`. No existing endpoint
does what we need — "create a documentfile and repoint an existing document" — so that work is
ours regardless.

### 3.2 Upload — `POST {API_BASE_URL}/api/File/Upload`

Header `Apikey`. Body:

```json
{ "Category": "...", "FileName": "...", "File": "<base64>",
  "MediaType": "...", "Extension": "...", "ApplicationId": "<guid>" }
```

Returns `{ Success, Message, Errors, Data: { FileId, FilePath, Hash, FileName, MediaType, ... } }`.

**No path field.** The vendor assigns `FileId`, the `yyyyMMdd` folder (today's date), and the
filename. `Category` is the only lever the caller has.

### 3.3 Download — `GET {API_BASE_URL}/api/File/Download?path={filePath}`

Header `Apikey`. Returns base64 content in `Data.File`.

The path is concatenated **raw and unencoded** (`FileService.cs:65`) and contains backslashes.
The tool must reproduce this byte-for-byte rather than URL-encoding; confirm on one dev file
before any bulk run.

### 3.4 Delete — `GET {API_BASE_URL}/api/File/Delete?path={filePath}`

**Delete is a GET.** No confirmation, no soft delete, trivially replayable by a retry, proxy, or
pasted URL. This endpoint is treated as hazardous throughout this design.

### 3.5 Config keys

`API_BASE_URL`, `FILE_UPLOAD_END_POINT` (`/api/File/Upload`), `FILE_GET_END_POINT`
(`/api/File/Download?path=`), `FILE_DELETE_END_POINT` (`/api/File/Delete?path=`), `API_KEY`.

---

## 4. Data model and classification

### 4.1 Established invariants

- `mocd_documentfile.mocd_documentfileid` == vendor `FileId` == the path's filename stem.
  Verified on every sampled row. A mismatch means the row is already inconsistent.
- `mocd_documentfile.mocd_fileid` is **NULL everywhere**. Production code never writes it; the
  only assignment in the repo is `TestProject\MGR Documents\DocumentHandler.cs:90`.
- `mocd_documentfile.mocd_category` is **unreliable** — written from the raw caller value with
  no fallback applied, so it is NULL on rows whose path segment is the literal `DigitalServices`.
  Never use it to decide correctness.
- `mocd_documentfile.mocd_filesize` is NULL on 2116 of 2141 rows. Not usable for validation.
- `mocd_hash` is **vendor-computed and content-derived**. Two independently uploaded copies of
  `test2.jpg` share hash `e57d1555e2197c964daa9fd57e197b7b` across different GUIDs, dates, and
  folders. The algorithm is *presumed* MD5 (32 lowercase hex) but **must not be relied on** —
  see §6.1.

### 4.2 Deciding the correct catalogue

**Primary authority:** `mocd_document` → `mocd_documenttype` → `mocd_servicecatalogue`.

**Cross-check, where available:** the parent request's own `mocd_servicecatalogue`.
Only three parent entities carry that field:

| Parent lookup | docs | agree with doc type | disagree |
|---|---:|---:|---:|
| `mocd_employeeappintmentrequest` | 540 | 529 | 2 |
| `mocd_gamrequest` | 277 | 277 | 0 |
| `mocd_BylawsAmendmentRequestId` | 214 | 214 | 0 |
| **total** | **1031** | **1020 (99.8%)** | **2** |

`mocd_nporelationship`, `mocd_gam`, `mocd_ministerialdecision` have **no** service catalogue
field. `mocd_nporequest` is populated on **0** in-scope documents. So the cross-check covers
1031 of 2352 documents (44%); for the rest the document type is the only authority.

### 4.3 Verdicts

```
FIX     path segment 2 is not a valid mocd_servicecatalogue id
        (name string, docType<hex>, numeric, absent, or a document type GUID)
        AND the document type has a service catalogue
        AND the cross-check, if available, agrees
        → 1555 documents

REVIEW  path segment 2 IS a valid mocd_servicecatalogue id, but a different one (183)
        OR the cross-check disagrees with the document type (2 known)
        → reported, never modified

SKIP    already correct (403) / no file path (211) / document type has no catalogue
```

**The catalogue check is a live lookup, never a list.** Every GUID-shaped segment is resolved
against `mocd_servicecatalogue` at scan time. Hardcoding known-bad GUIDs would be wrong — the
set is not closed, and one of the values below is a service inside our own scope.

Resolution of the 257 GUID-shaped mismatches:

| Segment resolves to | Files | Verdict |
|---|---:|---|
| `9a39aa75…` catalogue "Request to Join NPO" | 143 | REVIEW |
| `732030c4…` catalogue "OBA Request" | 39 | REVIEW |
| `3ff27d73…` catalogue **"General Assembly Meeting Request"** | 1 | REVIEW |
| `9b1121f4…` doc type "A Medical Examination Certificate" | 34 | FIX |
| `b967792d…` doc type "A Copy of Board of Director's Decision" | 15 | FIX |
| `2ad80a05…` doc type "A License / Permit to Practice Profession" | 7 | FIX |
| `ab5fd6b8…` doc type "A Copy of Academic Qualification Certificate" | 6 | FIX |
| `c38b872c…` doc type "A Copy of Certificate of Good Conduct and Behavior" | 6 | FIX |
| `4d4fd045…` doc type "A Copy of NPO Manager's Decision" | 3 | FIX |
| `5a52e1d6…` doc type "A Copy of Experience Certificates" | 3 | FIX |

**Sibling-service case.** The single `3ff27d73…` file is a **GAM Attendance** document
(`24db2387…`) stored under **GAM Request** (`3ff27d73…`). Both are valid catalogues and both are
in scope (§2). An attendance list genuinely belongs to the meeting journey, so the path may be
right and the document type's lookup wrong — the same ambiguity as §4.3's Payment Receipt
example, but arising *within* our own service set. It is REVIEW, not FIX.

**Why REVIEW exists.** Example `2c9d5572-a77b-f111-b10f-00505601095a`: the file sits under
`9a39aa75…` ("Request to Join NPO"); its document type "Payment Receipt" points at
`6bcb221c…` ("Membership Managment"). The document hangs off a `mocd_nporelationship` —
one contact joining one NPO — which carries no catalogue, so there is no third opinion.
The path may well be right and the document type record wrong. These files are reported for a
human decision and never touched automatically.

---

## 5. Phases

Each phase is a separate command. Phases 1, 2 and 4 write nothing to any server.

| # | Phase | Server writes | Reversible |
|---|---|---|---|
| 1 | **Scan** — two stages: gather in scope, then classify; emit the report (§5.0) | none | — |
| 2 | **Backup** — download every FIX file, verify, snapshot its CRM records, write restore manifest | none (local only) | — |
| 3 | **Migrate** — upload corrected, verify, create documentfile, repoint document | **yes** | ✅ fully |
| 4 | **Report** — old/new links for verification | none | — |
| 5 | **Delete** — gated: re-verify, then delete old file + old row. Separate invocation in bulk mode; typed `DELETE` at end of run in targeted mode (§5.3) | **yes** | ❌ **irreversible** |

### 5.0 Scan is two distinct stages, and reports both

**Stage one — gather in scope.** Every `mocd_document` whose `mocd_documenttype` points at one of
the eight service catalogues in §2, with its documentfile, document type and cross-check
catalogue. This is the denominator, and it is reported as such.

**Stage two — find the corrupted subset.** Each of those is classified (§4.3). Only then do we
know what needs work.

Both numbers appear at the top of the run and in the report, so the size of the problem is always
stated against the size of the population rather than on its own:

```
In scope (document types across the 8 services) ......... 2352
  with a file path ...................................... 2141
    already correct ..................................... 403
    BROKEN — will be fixed .............................. 1555
    AMBIGUOUS — needs a human decision .................. 183
  no file path (legacy records, out of scope) ........... 211
```

The same two-stage shape applies in targeted mode; the difference is only that stage one is the
identifiers you supplied instead of the whole population.

### 5.1 The restore manifest cannot restore a path

The manifest and local copies protect against **losing the bytes**. They are not an undo:
re-uploading produces a new `FileId` and today's date folder, so a restored file never returns
to its original path.

**The real rollback is that the old file still exists.** Until phase 5 runs, reverting is just
repointing `mocd_document.mocd_documentfile` back to the old row — no upload, no path change,
no data movement.

Therefore **phase 5 is the only irreversible step in the operation**, it is a separate command,
and it should be run only after phase 4 has been reviewed.

### 5.2 Per-document sequence in phase 3

```
1  read old bytes from the local backup
2  upload to vendor with the correct Category
3  download the new file back
4  run the verification gate (§6)          ── fails → stop, nothing written
5  show operator both files + verdict      ── §6.3
6  operator confirms
7  create mocd_documentfile  (Id = new FileId, path, hash, name, mediatype, category)
8  repoint mocd_document.mocd_documentfile → new row
9  re-read the document and confirm the change stuck
10 record state = repointed
   ── old vendor file and old documentfile row remain untouched ──
```

At no point between steps 1 and 10 does fewer than one good copy exist.

### 5.3 Operating modes

Both modes run the **same code**, the same verification gate (§6), and the same phases. They
differ only in how the work set is chosen and how deletion is reached.

#### Targeted mode — one or more identifiers supplied by the operator

The primary mode for verification, spot fixes, and proving the process before any bulk run.

```
docfix targeted --env dev --docs <id>[,<id>,...]
docfix targeted --env dev --docs-file ids.txt
docfix targeted                                   # prompts for both
```

**Accepted identifiers**, mixed freely in one run — each is resolved to a document, and the tool
reports which kind it matched:

| Input | Resolution |
|---|---|
| `mocd_document` GUID | direct |
| `mocd_documentfile` GUID | → its document via `_mocd_documentfile_value` |
| file name (`5b05398a-….jpg` or `Report.pdf`) | → documentfile by filename stem (§4.1) or `mocd_name` |

A file name may match more than one document. The tool lists every match and asks which to act
on rather than guessing.

**Per identifier the tool:**

1. Resolves it and prints what it found — document name, document type, service, current path.
2. **Classifies it and says plainly whether it is broken**, with the reason:
   ```
   VERDICT  BROKEN — path segment "goodConductCertificate" is a document type name,
                     not a service catalogue id
   correct catalogue  cd97bf8d-bea8-f011-b116-005056010908  (Employee Appointment Request)
   cross-check        mocd_emaprequest agrees
   ```
   - `SKIP` (already correct, no path, or no catalogue) → reports and moves on, changes nothing.
   - `REVIEW` (§4.3) → prints both candidate catalogues with their names and the reason it is
     ambiguous, then **requires an explicit `--force-review` flag** to proceed. Without it, the
     file is reported and skipped. A plain `y` is never enough to act on an ambiguous file.
   - `FIX` → continues.
3. Runs backup → upload → verify → operator confirmation (§6.3) → repoint.

**Deletion in targeted mode** happens at the end of the run, once, for all files handled — after
a summary listing every old path to be removed. It requires typing the word `DELETE`, not `y`.
This is the one convenience over bulk mode: the operator is present and watching a handful of
files, so a separate invocation is friction without safety benefit. The confirmation is still a
typed word, and every check in §6.2 is re-run against live state immediately before each delete.

#### Bulk mode — the full in-scope sweep

```
docfix scan     --env dev
docfix backup   --env dev
docfix migrate  --env dev
docfix delete   --env dev --confirm-count <n>
```

Work set is every `FIX` document from a **fresh scan** (§1.2 — never a stale one). Deletion is a
**separate invocation**, deliberately unreachable from the migrate run, and is expected to happen
after the phase 4 report has been reviewed.

#### Environment selection

Applies identically to both modes: `--env <name>`, or an interactive menu when omitted. The
chosen environment is echoed at the top of every run and written into every artefact filename and
log line, so no output is ever ambiguous about which system it came from. Production gating is
in §7.1 — `prod` is never selectable by pressing Enter.

---

## 6. Verification gate

### 6.1 Do not assume the hash algorithm

The stored hashes look like MD5, and `DocumentDataService.cs:523` has a commented-out
`ComputeMD5Hash`. But the vendor's implementation is unseen — if they hash the base64 text
rather than the raw bytes, a locally computed MD5 would never match and the tool would reject
every file.

Compare **like with like** only:

- vendor's old hash ↔ vendor's new hash (their algorithm, both sides)
- our hash of old bytes ↔ our hash of new bytes (our algorithm, both sides)
- the raw bytes themselves

All three hold regardless of what the vendor computes.

### 6.2 The six checks

| # | Check | On failure |
|---|---|---|
| 1 | Backup integrity: the hash the download reports for the **old** file == `mocd_hash` in CRM | quarantine this file, exclude from run |
| 2 | Upload response `Data.Hash` == old `mocd_hash` | stop this file |
| 3 | Round-trip: download new file; `len(new) == len(old)`, `ourHash(new) == ourHash(old)`, **and `new == old` byte for byte** | stop this file |
| 4 | New path segment 2 (lowercased) == correct catalogue GUID; filename stem == `Data.FileId` | stop this file |
| 5 | `newFilePath != oldFilePath` **and** `newFileId != oldFileId` | **halt the entire run** |
| 6 | Re-read document: `_mocd_documentfile_value` == new file id; new row's path and hash correct | **halt the entire run** |

Check 3 is the real proof. `Success: true` means the vendor accepted the request, not that a
file exists at that path and can be read; storage faults surface on read-back.

Check 5 guards against content-hash deduplication. If the vendor returned the *existing* file
for identical bytes, we would repoint to the old file and phase 5 would then destroy the only
copy. Current data says they do not dedupe (two `test2.jpg` rows, same hash, different GUIDs and
paths) — which is exactly why this is an assertion rather than a comment.

Checks 5 and 6 halt everything because they indicate a broken assumption, not a bad file.

### 6.3 Operator confirmation

Every file, in both bulk and test mode. The tool writes both files to disk, opens them in the
default viewer, and prints the machine verdict at the same moment:

```
[412 / 1555]  Certificate of Good Conduct — Employee Appointment Request
  document  2c9d5572-a77b-f111-b10f-00505601095a
  OLD  DigitalServices\goodConductCertificate\20260330\5b05398a-….jpg
  NEW  DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\a41c0b77-….jpg

  bytes        350,208 == 350,208                 OK
  content      byte-for-byte identical            OK
  vendor hash  e57d1555… == e57d1555…             OK
  new path     catalogue segment correct          OK
  CRM view action on new path: readable           OK

  opened both files in your viewer
  Repoint document to the new file?  [y / n / skip / quit]
```

Nothing is written to CRM until `y`.

**Viewing the new file needs no CRM record.** `ViewDocumentJS.js:397` → `DownloadDocument()`
reads the lookup only to discover the path, then POSTs `{FilePath}` to the CRM custom action
`mocd_DownloadDocument`. The action takes a path and nothing else, so the tool can call it with
the new path directly — proving the CRM View button will work on the file before we link it.

### 6.4 Annotations — no impact

Annotations are created only for establishment logo document types
(`DocumentDataService.cs:142-150`) and attach to **`mocd_document`**, not the documentfile.
Live counts: 477 on `mocd_document` (427 with a file), 23 on `mocd_documentfile` (**0** with a
file). Only **15** of 2352 in-scope documents have one.

Because they hang off the document — which we keep — and not the documentfile we swap, the
migration does not affect them. For those 15 they are a free third copy.

> Separate pre-existing bug: `openDocumentPopup()` (`ViewDocumentJS.js:43`) searches
> `objecttypecode eq 'mocd_documentfile'` while the code writes to `mocd_document`, so it can
> never find an attachment. Worth reporting; out of scope here.

---

## 7. Configuration and environments

Environment chosen at startup: `dev` / `test` / `preprod` / `prod`. Settings persist to a local
config file, prompted once per environment on first use.

```json
{
  "environments": {
    "dev": {
      "fileServiceBaseUrl": "http://...",
      "apiKey": "<secret>",
      "crmUrl": "https://devdigitalplatform.mocd.gov.ae/MoCD",
      "crmAuth": "ntlm",
      "isProduction": false
    }
  },
  "serviceCatalogues": ["cd97bf8d-…", "6bcb221c-…", "…"],
  "backupRoot": "D:\\mocd-fix\\backup"
}
```

**Only `dev` is configured for now.** The others are prompted for when first selected.

**Secrets are not stored in plaintext** alongside the config — use the OS credential store, or
prompt per run.

### 7.1 Production gating

- `prod` cannot be selected from a menu by pressing Enter — the operator types the word `prod`
  in full.
- Phase 3 and phase 5 against a `isProduction: true` environment additionally require an
  explicit confirmation flag on the command line.
- Phase 5 (delete) against production requires typing the count of files to be deleted.
- Every run against production is logged to a separate audit file.

---

## 8. Artefacts

| File | Phase | Contents |
|---|---|---|
| `scan-{env}-{timestamp}.csv` | 1 | document id, documentfile id, file name, document type, service, old path, path segment, correct catalogue, verdict, **reason**, **solution**, cross-check source, CRM link |
| `backup/{oldFileGuid}.{ext}` | 2 | the original bytes |
| `restore-manifest-{timestamp}.jsonl` | 2 | one line per file — everything needed to rebuild the file **and its CRM records**, see §8.2 |
| `state-{env}.jsonl` | 3 | per-document state: `pending → backed-up → uploaded → verified → repointed → deleted`, with timestamps and the new ids |
| `migration-report-{timestamp}.csv` | 4 | old + new documentfile id, old + new path, old + new CRM links, verification results |
| `review-{timestamp}.csv` | 1 | the REVIEW set, with both candidate catalogues and their names |
| `quarantine-{timestamp}.csv` | 2 | files failing check 1, with both hashes |
| `audit-{env}.log` | all | every API call and CRM write, timestamped |

State is written **before** each irreversible step and updated after, so an interrupted run
resumes rather than repeats. Re-running is safe by design: files already `repointed` are skipped.

### 8.1 The scan report states the reason *and* the solution

Every row carries two human-readable columns, not just a verdict:

- **Reason** — why it is wrong, in terms of the actual data.
  *"Path segment 'goodConductCertificate' is a document type name, not a service catalogue id."*
- **Solution** — precisely what the tool will do about it, or why it will not.
  *"Re-upload the file with Category = cd97bf8d-… (Employee Appointment Request), create a new
  mocd_documentfile, repoint the document, then delete the old file."*

For `REVIEW` the solution column says what a human must decide, and for `SKIP` it says why no
action is needed. **This report is produced in every mode**, including targeted — a targeted run
writes a scan report for the identifiers it was given before it touches anything, so there is
always a record of what was found and what was intended.

### 8.2 The manifest backs up the CRM records too, not only the bytes

Re-uploading bytes is not enough to restore a document: the `mocd_documentfile` row and the
document's link to it also have to be rebuildable. So the manifest line for each file carries
**a complete snapshot of the CRM side taken before any write**:

| Group | Contents |
|---|---|
| The bytes | local backup path, size, our SHA-256 |
| The old documentfile | **the entire `mocd_documentfile` record as raw JSON** — every attribute, not a chosen subset — plus its id, path and vendor hash called out for convenience |
| The old document link | document id, document type id and name, the `_mocd_documentfile_value` that was in place before we changed it |
| The upload contract | file name, media type, extension, the original `Category`, and the correct catalogue we intend to use |

Snapshotting the whole documentfile row rather than named fields means a restore does not depend
on us having predicted which attributes matter — `mocd_filesize`, `mocd_extension`,
`mocd_fileid` and anything a future customisation adds all come along.

The limit from §5.1 still applies: this restores the record and the content, **not the original
path**. A restored file lands under a new FileId and today's date folder.

---

## 9. Error handling

- **Any check fails** → that file stops where it is and is logged with the failing check and both
  values. The old file is untouched in every failure path.
- **Checks 5 or 6 fail** → halt the whole run; a shared assumption is wrong.
- **Network / VPN loss** → the state file allows a clean resume. (VPN dropped once during design
  research; assume it will happen mid-run.)
- **`modifiedon` changed** between scan and write → skip the document and report it; someone else
  is editing the record.
- **Disk space** → check free space against the estimate before phase 2 and abort early if short.
  `mocd_filesize` is unusable (NULL on 99% of rows); the 25 populated rows average 342 KB, max
  1.95 MB, suggesting roughly **600 MB for 1555 files** — a guess the tool should verify by
  tracking the running total.
- **`--dry-run`** on every phase: performs all reads and verifications, writes nothing.

---

## 10. Testing

- **Targeted mode (§5.3)** is the verification vehicle — one or more identifiers, all five phases,
  confirmation at each step. It shares its code with the bulk path, so exercising it on a couple
  of documents genuinely proves the bulk path rather than a parallel one.
- **Unit tests** — path parsing across all six observed shapes, including the doubled-backslash
  case (`DigitalServices\\POD\\20250911\\…`); classification rules; the `docType<32hex>` →
  dashed-GUID transformation.
- **Integration, dev only** — full round trip on a disposable document, ending with delete;
  verifies the unencoded `?path=` behaviour of §3.3.
- **Rehearsal** — full phase 1 and 2 against pre-prod (read-only) before any production run.

---

## 11. Open questions

1. **Is "Request to Join NPO" a separate service, or the joining journey inside Membership
   Managment?** Decides whether the 143 REVIEW files are correctly filed (and the document type
   records are misconfigured) or should be moved. Business decision.
2. **Can the workstation reach the vendor file service directly** in each environment? The
   staging value in `Web.config` is `http://mocdstgdpfs01.mocd.gov.ae:83`; dev will differ.
3. **Is the UNC share reachable?** `ViewDocumentJS.js:120` leaks `\\mocd.gov.ae\DPFS\DigitalServices\…`.
   If reachable, phase 2 could copy files directly instead of pulling 1555 downloads through the
   API — faster, and it preserves the original folder structure in the backup. Verify via the API
   regardless.
4. **`ApplicationId`** is part of the upload contract but never set by `DocumentDataService`.
   Confirm whether the vendor requires it and what it should be on re-upload.
5. **Should the tool also fix the two live producers?** Recommended: no — file them as defects.
   Cleaning up while the source still writes broken paths means this job runs forever.

---

## 12. Out of scope

- Fixing the portal / API defects that create broken paths.
- The 1216 legacy documents with no `mocd_filepath` (annotation-era records).
- Services outside the eight listed in §2.
- Any write to Azure DevOps.
