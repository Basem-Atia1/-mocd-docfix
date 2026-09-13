# Guided wizard, revised scope, and grouped reporting

**Status:** Approved
**Date:** 2026-09-13
**Supersedes parts of:** `2026-09-10-document-filepath-remediation-design.md` sections 2, 4.3, 5.3, 8

This document records four changes agreed with the operator on 2026-09-13, after the first
live dev scan (526 documents) showed what the corruption actually looks like.

---

## 1. Why

The tool works, but three things were wrong for the operator's real task:

1. **Scope was too wide.** Membership Managment was in the eight in-scope services; it should
   not be. Seven services remain.
2. **It refused to fix a whole class of genuinely broken files.** Files whose path holds a
   *real but different* service catalogue id were held back for human review. The parent
   request corroborates the document type in every one of these cases, so the caution bought
   nothing and left 41 files unfixed.
3. **The front end was a menu, not a guide.** A numbered menu that exits when you pick an
   environment that has not been set up is not a guided tool. The operator asked for
   question-and-answer, for it to ask at every step, and for it never to break on a valid choice.

---

## 2. Scope — seven services

`AppConfig.Default()` lists exactly these. Membership Managment is removed.

| Service catalogue id | Name |
|---|---|
| `cd97bf8d-bea8-f011-b116-005056010908` | Employee Appointment Request |
| `3ff27d73-653e-f111-b119-005056010908` | General Assembly Meeting Request |
| `d2744b68-aa50-f111-b119-005056010908` | GAM - Nomination List Request |
| `24db2387-c15d-f111-b119-005056010908` | GAM - Attendance |
| `35105602-2b5f-f111-b119-005056010908` | GAM - Update (Reschedule) |
| `d8155dcc-635e-f111-b119-005056010908` | GAM - Minutes of Meeting |
| `930f636a-077a-f111-b119-005056010908` | By-Laws Amendment Requests |

This is configuration, not code: the list lives in `config.json` and `AppConfig.Default()` only
seeds it. Three of the seven have no documents in dev; that is expected, not an error.

**Verified 2026-09-13:** re-filtering the 526-row dev scan against this list leaves 408 rows, and
the only excluded catalogue is Membership Managment (118 rows). No out-of-scope catalogue appears.

---

## 3. Classification — seven groups

The rule is unchanged and remains the only rule:

```
mocd_document -> mocd_documenttype -> mocd_servicecatalogue     = the CORRECT value
                                            compare
DigitalServices\<segment>\<yyyyMMdd>\<fileGuid>.<ext>           = the value IN THE PATH
```

The parent request's catalogue (`mocd_emaprequest`, `mocd_gamrequest`,
`mocd_bylawsamendmentrequest`) is read as a **cross-check only**, never as the source of the
value written.

`Classification` gains an `int Group` (1-7). Group membership is decided in this order - the
order matters, because group 6 must win over group 5:

| # | Condition | Verdict | Live dev count |
|---|---|---|---|
| 7 | No file path, or segment already equals the correct catalogue | `Skip` | 88 |
| 6 | A human must decide: the parent request and the document type name different catalogues, **or** the path is malformed | `Review` | 7 |
| 5 | Segment parses as a GUID and resolves to a real catalogue, but not the correct one | **`Fix`** | 41 |
| 1 | Segment is a GUID that is not a service catalogue (a document type id) | `Fix` | 59 |
| 2 | Segment starts with the literal `docType` | `Fix` | 57 |
| 4 | No segment at all - the date folder sits directly under the root | `Fix` | 23 |
| 3 | Anything else - a name such as `Document`, `boardDecision`, `string`, `Test` | `Fix` | 133 |

**Invariant:** a row's group decides whether it is fixed. Groups 1-5 are always `Fix`, groups 6
and 7 are never `Fix`. Nothing may be `Review` inside a group the report calls fixable, because the
operator reads the group heading and expects it to be true of every row beneath it.

That is why malformed paths (doubled separators, or a segment count that is neither 3 nor 4) are
group 6 rather than being scattered through groups 1-5 as exceptions. Group 6 is therefore not
only the cross-check conflict: it is **everything the tool refuses to decide**, with the reason
line saying which of the two applies. There are no malformed paths in the dev data, so group 6's
live count is the 7 conflicts alone.

### 3.1 Group 5 changes verdict - the reasoning

Previously `Review`. Now `Fix`.

A segment that resolves to a real service is ambiguous on its face: either the file is misfiled,
or the *document type* is wrong and the file is where it belongs. The tie is broken by the third
piece of evidence. In all 41 dev cases the parent request agrees with the document type and only
the path disagrees:

```
path segment : 732030c4-895c-f011-b112-005056010908  = Opening New Bank Account Certificate Request
document type: A Copy of Certificate of Good Conduct and Behavior
its catalogue: cd97bf8d-bea8-f011-b116-005056010908  = Employee Appointment Request
parent record: mocd_employeeappintmentrequest        -> Employee Appointment Request
```

Two authorities against one. Group 5 therefore carries no more risk than groups 1-4, and gets the
same treatment - including the full backup, the byte-for-byte verification, and the operator
seeing both files before CRM is repointed.

Where the parent request **does not** agree, the row is group 6 and is never touched.

### 3.2 Group 6 is parked, not solved

Seven dev rows have a document type pointing at one catalogue and a parent request pointing at
another - for example a *Medical Examination Certificate* (Employee Appointment) attached to a
`mocd_gamrequest`. The data cannot say which is wrong, and the path (`docType`) offers no third
opinion. The tool reports them with both values and touches neither. Deciding them is deferred to
a later session; the operator asked to be reminded.

---

## 4. `DocumentGroups` - one source of truth for group text

A static table in `MocdDocFix.Domain`, keyed by group number, holding:

- `Number` (1-7)
- `ShortLabel` - one line, for menus: *"path holds a document type id"*
- `WhatIsInThePath` - what the segment actually contains
- `WhyItIsWrong` - the defect, in terms of the data
- `HowWeKnow` - which record supplies the correct value
- `WhatTheToolDoes` - the remedy, or why there is none
- `WillBeFixed` - bool

The grouped report, the wizard's group picker, and the CSV's `GroupLabel` column all read this
table, so they can never drift apart. Per-row detail (the actual segment, the actual correct id)
still comes from `Classification.Reason` and `.Solution`, which stay per-row.

---

## 5. Reports

Every scan writes three files to `<DataRoot>\<env>\reports\`:

| File | Purpose |
|---|---|
| `scan-<env>-<stamp>.csv` | one row per document, as today, plus `Group` and `GroupLabel` |
| `groups-<env>-<stamp>.txt` | **new** - readable, grouped, with each group's full reason |
| `review-<env>-<stamp>.csv` | as today - groups 6 and 7 only, for a human |

The `.txt` is the operator's working document. Shape:

```
MoCD document file path remediation - dev - 2026-09-13 13:59
Scope: 7 services (Employee Appointment .. By-Laws Amendment), Membership Managment excluded.
408 documents in scope.

====================================================================================
GROUP 1 - 59 files - WILL BE FIXED
  What is in the path:  a document type id, where a service catalogue id belongs
  Why it is wrong:      ...
  How we know:          ...
  What the tool does:   ...
====================================================================================

  --- Employee Appointment Request  (59 files) ---
        15  A Copy of Board of Director's Decision
              15  under: b967792d-e40b-f111-b117-005056010908
            e.g.  Application Summary.png
                  DigitalServices\b967792d-...\20260624\3b79bd74-....png
                  document   b9d4cd2f-fe1d-f111-b119-005056010908
                  file rec.  3b79bd74-139d-43d5-9616-cb8959c4c4e6
...
SUMMARY
     59  fix:YES   1 - path holds a document type id
     ...
    313  TOTAL to fix (groups 1-5)
```

Sorted group -> service -> document type -> segment, descending by count, so the biggest problem
is always at the top of its section.

---

## 6. The wizard

`GuidedMenu` is replaced by `Wizard`: a sequence of questions, each one a `Question` with a
prompt, numbered `Choice` values, and an optional typed answer. Every question accepts:

| Input | Effect |
|---|---|
| a number | choose that option |
| Enter | accept the default, where one is shown in `[brackets]` |
| `?` | print the long explanation of every choice, then re-ask |
| `b` | go back to the previous question |
| `q` | quit, after confirming if anything is part-done |

Unrecognised input re-asks. **No input at any question can end the program by accident.**

### 6.1 Environment - never dead-ends

The question lists every environment the config knows about plus the four well-known names, each
with its status: `ready`, `not set up yet`, or `*** PRODUCTION ***`.

- Choosing a **ready** environment continues.
- Choosing a **not set up** environment offers: set it up now / pick another / quit. It does not
  exit. Setup asks for the file-service base URL, CRM URL, CRM domain, CRM user, CRM password and
  the file-service API key; offers to test the connection; writes non-secrets to `config.json` and
  secrets to DPAPI; then returns to the flow with that environment selected.
- Choosing **prod** explains that production needs `--confirm-production` on the command line and
  the name typed in full, then returns to the list. Production can never be entered from a
  numbered choice alone. This preserves the original spec's section 7.1 gate.
- A resolution failure later on (missing secret, unreachable CRM, bad credentials) is caught,
  explained in one sentence, and returns the operator to this question. No stack traces.

### 6.2 Mode

```
  How do you want to work?
    1  Targeted      pick specific files and run them one at a time   [default]
    2  Full          work through a whole group
    3  Just report   scan and write the report files, change nothing
```

Targeted is the default because the operator's immediate task is testing on individual files.

### 6.3 Picking files without typing GUIDs

In targeted mode:

```
  How do you want to pick them?
    1  Choose from the last scan        408 files, grouped
    2  Type document GUIDs or file names
```

Option 1 reads the most recent `scan-<env>-*.csv`, offers the groups with their counts, then lists
that group's files numbered. The operator answers with numbers (`1,3,5`), a range (`1-10`), or
`all`. If no scan exists for this environment, the wizard says so and offers to run one first.

Option 2 is the existing typed-identifier path, unchanged.

### 6.4 A gate at every step

The five steps are **scan -> back up -> upload corrected -> verify and repoint -> delete old**.
No step runs into the next. Each finishes by reporting what it did and asking:

```
  Step 2 of 5 - Backup - done
    23 files downloaded and verified against CRM
     0 quarantined
    saved to D:\mocd-docfix-data\dev\backups\

    1  Continue to step 3 (upload corrected copies)
    2  Show me the details first
    3  Stop here - nothing else runs
```

"Show me the details" prints the per-file outcome then re-asks. "Stop here" leaves state on disk
so the run can be resumed later; it is always safe, because the old files are never touched until
step 5.

Existing safety gates are kept on top of this, not replaced:

- anything contacting the file server announces the URL and asks first;
- step 4 shows the operator the old and the new file and asks before each repoint;
- step 5 re-verifies each file against CRM immediately before deleting, and requires the
  environment name typed in full.

---

## 7. Error handling

| Situation | Behaviour |
|---|---|
| Environment not configured | offer to set it up; never exit |
| Secret missing | name which one, offer setup, return to the environment question |
| CRM unreachable / 401 | one-sentence explanation, return to the environment question |
| File server unreachable | report, mark the file `Failed` in state, continue to the next file |
| No previous scan when one is needed | say so, offer to run a scan now |
| Unrecognised menu input | re-ask; never fall through |

Unhandled exceptions are caught at the top of the wizard loop, printed as one line plus the log
file path, and the wizard returns to its main question rather than terminating.

---

## 8. Testing

Everything here is testable without a network, because `IPrompts` is already injected.

- `Classifier` - one test per group, including the ordering guarantee that a cross-check conflict
  (group 6) beats a real-but-different segment (group 5).
- `DocumentGroups` - every group 1-7 present, `WillBeFixed` true for 1-5 and false for 6-7.
- `GroupedReportWriter` - grouping, ordering, counts, and that each group's four reason lines
  appear.
- `Wizard` - with a scripted `IPrompts`: `?` re-asks, `b` goes back, bad input re-asks,
  an unconfigured environment offers setup instead of exiting, prod is refused without the flag,
  and each step gate stops when told to stop.
- `FilePicker` - `1,3,5`, `1-10`, `all`, out-of-range, and empty input.

Live dev verification after implementation: re-scan and confirm 408 rows, 313 to fix, the group
counts above, and that the `.txt` matches.

---

## 9. Out of scope

- Group 6's seven conflicts - parked by agreement, to be revisited.
- The `UploadDocument.cs` CRM plugin defect that keeps producing broken paths. It is the source
  of the ongoing By-Laws corruption and must be raised separately; this tool only cleans up after
  it.
- Any environment other than dev being used for real. The wizard can configure them; nobody has
  said to run them.
