# CSV Ledger Repair Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the seven-way menu and five kinds of report with one CSV ledger per environment, and four modes that read and write it.

**Architecture:** A `LedgerRow` is one document; `LedgerStore` reads and rewrites the whole CSV after every completed row. Four commands consume `IReadOnlyList<LedgerRow>` and return a summary — `RepairRun` (which delegates per-row work to `RepairOneRow`), `RedoRun`, `DeleteOldFiles`, `CheckItAll`. Corrections update the existing `mocd_documentfile` in place, so nothing is created and nothing is repointed; an append-only `ChangeJournal` carries the before-and-after that CRM no longer holds.

**Tech Stack:** C# / .NET 8, xUnit, CsvHelper (already a dependency), `System.Text.Json`.

## Global Constraints

- Spec: `docs/specs/2026-09-15-csv-ledger-repair-mode-design.md`. Read it before Task 1.
- Branch `feat/docfix-tool`. **Never `git push`** — commit locally only.
- Every commit message ends with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- `dotnet build -c Release` must be warning-free at the end of every task.
- CSV files are written UTF-8 **with** a BOM (`new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)`) so Excel reads Arabic file names.
- The ledger lives at `<AppConfig.DataRoot>\repair-<env>.csv`; the journal at `<DataRoot>\changes-<env>.jsonl`; the error log at `<DataRoot>\errors-<env>.txt`.
- The two operator vocabularies, verbatim: verdict is `fix` / `review` / `skip` / `ignore` / `redo`; final state is blank / `corrected and pending the delete of old docs` / `old files deleted` / `ignore` / `failed`. Anything else in either column is **unrecognised** and means "leave this row alone".
- Comparisons of both vocabularies are case-insensitive and trimmed.
- No code may reference Azure DevOps after Task 1.

## File Structure

**Created**

| file | responsibility |
|---|---|
| `src/MocdDocFix/Domain/RowVerdict.cs` | the verdict enum and its parse/format |
| `src/MocdDocFix/Domain/RowState.cs` | the final-state enum and its parse/format |
| `src/MocdDocFix/Domain/LedgerRow.cs` | one CSV row — 29 mapped columns |
| `src/MocdDocFix/Domain/CrmLinks.cs` | the two CRM deep links |
| `src/MocdDocFix/Storage/LedgerStore.cs` | read, full rewrite, once-per-sitting `.bak`, start-new |
| `src/MocdDocFix/Storage/ChangeJournal.cs` | append-only JSONL, before-and-after |
| `src/MocdDocFix/Storage/ErrorLog.cs` | `errors-<env>.txt` |
| `src/MocdDocFix/Commands/LedgerBuilder.cs` | CRM → classified `LedgerRow`s |
| `src/MocdDocFix/Ui/RunProgress.cs` | `WatchMode` and the per-row printing |
| `src/MocdDocFix/Commands/RepairOneRow.cs` | the six steps for one row |
| `src/MocdDocFix/Commands/RepairRun.cs` | the loop, the tallies, the halting |
| `src/MocdDocFix/Commands/RedoRun.cs` | the revert |
| `src/MocdDocFix/Commands/DeleteOldFiles.cs` | ledger-driven deletion of old files |
| `src/MocdDocFix/Commands/CheckItAll.cs` | ledger-driven read-only confirmation |

**Modified:** `Clients/CrmReadClient.cs`, `Clients/CrmWriteClient.cs`, `Domain/DocumentRow.cs`, `Config/AppConfig.cs`, `Cli/Wizard.cs`, `Cli/Session.cs`, `Cli/CommandLineOptions.cs`, `Program.cs`.

**Deleted** (Tasks 1 and 14): `Clients/AdoClient.cs`, `Domain/DocumentTypeAuthority.cs`, `Domain/LocalBacklogSearch.cs`, `Commands/DocumentTypeCheck.cs`, `Cli/AdoSetup.cs`, `Storage/DocumentTypeDecisions.cs`, `Storage/GroupedReportWriter.cs`, `Storage/GuidListWriter.cs`, `Storage/RepointedListWriter.cs`, `Storage/DocumentReportStore.cs`, `Storage/DocumentRecord.cs`, `Storage/Reporter.cs`, `Storage/StateStore.cs`, `Domain/MigrationState.cs`, `Commands/ScanCommand.cs`, `Commands/TargetedCommand.cs`, `Commands/MigrateCommand.cs`, `Commands/VerifyCommand.cs`, `Commands/OldFileCheckCommand.cs`, `Commands/BackupCommand.cs`, `Commands/DeleteCommand.cs`, `Commands/Reconciler.cs`, `Ui/StepGate.cs`, `Ui/CheckLines.cs`.

## How the test doubles actually work

Read this before writing any test. Getting it wrong is what broke the previous plan's test code.

- **`FakePrompts`** is non-interactive. `Confirm` is answered by `.Answer(ConfirmChoice.Yes, …)`; `YesNo` by `YesNoResponse` (a single bool) or `YesNoQueue`; `ReadLine` by `ReadLineResponse` or `ReadLineQueue` (which **throws** when it runs dry, naming the question). Every question asked lands in `Questions`. It implements only `Info` from `IPrompts` — `Say`, `Blank`, `Section`, `Field`, `Warn`, `Bullet`, `Title` are **extension methods in `Ui/Screen.cs`** that all funnel into `Info`, so assert against `Messages`, never against a `Say` call.
- **`FakeCrmReadClient`** returns a `{"stub":true,…}` JSON for any id not in `RawRecords`; put a real record in `RawRecords["mocd_documentfiles:" + id]` when the test cares. `FilesByPath` is empty unless set, so `FindDocumentFilesByPathAsync` finds nothing by default.
- **`FakeCrmWriteClient`** has no update method yet — Task 5 adds `UpdatedFiles` to it.
- **`FakeFileServiceClient`**: `Files[path] = (base64, hash)` seeds a download; `UploadResponder` must be set or every upload fails with "no upload responder configured"; `DeleteAsync` removes from `Files` unless `PretendToDelete` is set.
- **`Verifier.OurHash(byte[])`** is the local SHA; the *vendor* hash is whatever the fake returns.

Run a single test with:
`dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~ClassName.MethodName"`

---

### Task 1: Strip the DevOps cross-check

The app must still build, run and pass its remaining tests at the end of this task. Nothing new is added; the scan simply stops asking DevOps anything.

**Files:**
- Delete: `src/MocdDocFix/Clients/AdoClient.cs`, `src/MocdDocFix/Domain/DocumentTypeAuthority.cs`, `src/MocdDocFix/Domain/LocalBacklogSearch.cs`, `src/MocdDocFix/Commands/DocumentTypeCheck.cs`, `src/MocdDocFix/Cli/AdoSetup.cs`, `src/MocdDocFix/Storage/DocumentTypeDecisions.cs`
- Delete: `tests/MocdDocFix.Tests/DocumentTypeAuthorityTests.cs`, `AdoClientBodyTests.cs`, `LocalBacklogSearchTests.cs`, `ScanDevOpsTests.cs`, `DocumentTypeCheckTests.cs`, `LiveBacklogTests.cs`, `MigrateTypeCheckTests.cs`, `Fakes/FakeAdoClient.cs`
- Modify: `src/MocdDocFix/Config/AppConfig.cs`, `src/MocdDocFix/Cli/Session.cs`, `src/MocdDocFix/Commands/ScanCommand.cs`, `src/MocdDocFix/Commands/MigrateCommand.cs`, `src/MocdDocFix/Storage/DocumentRecord.cs`, `src/MocdDocFix/Storage/Reporter.cs`, `src/MocdDocFix/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: an `AdoVerdict`-free codebase. Later tasks assume no type named `AdoClient`, `IAdoClient`, `AdoOpinion`, `AdoVerdict`, `TypeRuling`, `AdoHit`, `AdoAttachment` or `DocumentTypeCheck` exists.

- [ ] **Step 1: Delete the six source files and eight test files**

```bash
cd /d/mocd-docfix
git rm -q src/MocdDocFix/Clients/AdoClient.cs \
          src/MocdDocFix/Domain/DocumentTypeAuthority.cs \
          src/MocdDocFix/Domain/LocalBacklogSearch.cs \
          src/MocdDocFix/Commands/DocumentTypeCheck.cs \
          src/MocdDocFix/Cli/AdoSetup.cs \
          src/MocdDocFix/Storage/DocumentTypeDecisions.cs
git rm -q tests/MocdDocFix.Tests/DocumentTypeAuthorityTests.cs \
          tests/MocdDocFix.Tests/AdoClientBodyTests.cs \
          tests/MocdDocFix.Tests/LocalBacklogSearchTests.cs \
          tests/MocdDocFix.Tests/ScanDevOpsTests.cs \
          tests/MocdDocFix.Tests/DocumentTypeCheckTests.cs \
          tests/MocdDocFix.Tests/LiveBacklogTests.cs \
          tests/MocdDocFix.Tests/MigrateTypeCheckTests.cs \
          tests/MocdDocFix.Tests/Fakes/FakeAdoClient.cs
```

- [ ] **Step 2: Build to get the exact list of break sites**

Run: `dotnet build 2>&1 | grep -E "error CS" | sort -u`

Expected: errors in `AppConfig.cs`, `Session.cs`, `ScanCommand.cs`, `MigrateCommand.cs`, `DocumentRecord.cs`, `Reporter.cs`, `Program.cs`. Work through them in the next three steps.

- [ ] **Step 3: Remove the `Ado` config**

In `src/MocdDocFix/Config/AppConfig.cs`, delete the `Ado` property and the whole `AdoConfig` class:

```csharp
    /// <summary>The DevOps backlog, consulted during the scan. Read-only.</summary>
    public AdoConfig Ado { get; set; } = new();
```

and everything from `/// <summary>` above `public sealed class AdoConfig` to that class's closing brace. Leave `Environments`, `ServiceCatalogues`, `DataRoot` and `Default()` untouched.

- [ ] **Step 4: Remove the third opinion from the scan**

In `src/MocdDocFix/Commands/ScanCommand.cs`:

- delete the `_devops` field, its constructor parameter and its assignment, and the `<param name="devops">` doc block;
- in `ClassifyAsync`, delete the `var ruling = …` statement and the whole `if (ruling.Verdict == AdoVerdict.Disagrees …)` block;
- in the `rows.Add(new ScanRow(…))` call, delete the three trailing arguments `AdoVerdict:`, `AdoService:` and `AdoEvidence:`;
- delete `DevOpsSummary()` and `DevOpsCouldNotTell()` from `ScanResult`.

In `src/MocdDocFix/Storage/Reporter.cs`, delete the three trailing properties of `ScanRow`:

```csharp
    /// <summary>What DevOps says about this document type: Agrees, Disagrees or CannotTell.</summary>
    string AdoVerdict = "",

    /// <summary>The service DevOps — or the operator, when asked — puts this document type under.</summary>
    string AdoService = "",

    /// <summary>The work items the answer rests on, so a reader can check it rather than believe it.</summary>
    string AdoEvidence = "");
```

leaving `string CrmLink);` as the record's last member.

In `src/MocdDocFix/Storage/DocumentRecord.cs`, delete the `private static string DevOps(ScanRow row)` method and the `("DevOps says", DevOps(row)),` line from `Points`.

- [ ] **Step 5: Remove the re-check from the upload step**

In `src/MocdDocFix/Commands/MigrateCommand.cs`, delete the `_checkTypeAsync` field, its constructor parameter and assignment and their doc comments, the `WhereDevOpsStandsAsync` method, the `TypeStanding` enum if it is declared in this file, and this block from `RunAsync`:

```csharp
            var standing = await WhereDevOpsStandsAsync(entry, catalogueName, ct);

            if (standing == TypeStanding.LeaveThisOne)
            {
                Skip("the DevOps check for its document type was not settled");
                continue;
            }

            if (standing == TypeStanding.StopTheRun)
            {
                haltReason = "You stopped the run over the DevOps check on " +
                             $"'{entry.DocumentTypeName ?? "(no document type)"}'.";
                break;
            }
```

- [ ] **Step 6: Unwire the session**

In `src/MocdDocFix/Cli/Session.cs`: delete the `_typeDecisions` and `_typeCheck` fields and their initialisation, the `IAdoClient? ado = null` constructor parameter and its `<param name="ado">` doc, the `CheckTypeAsync` method and its doc comment, and pass neither to `ScanCommand` nor to the two `new MigrateCommand(…)` calls. In the `ScanAsync` wizard action, delete the `if (result.DevOpsSummary() is { } devops)` block.

In `src/MocdDocFix/Program.cs`, delete any `AdoSetup` call and any `IAdoClient` argument passed to `new Session(…)`.

- [ ] **Step 7: Build and test**

Run: `dotnet build -c Release 2>&1 | grep -E "error|warning" ; dotnet test tests/MocdDocFix.Tests 2>&1 | tail -5`

Expected: no errors, no warnings, and a passing run. The total will be well below the previous 654 — around 560 — because eight test files were removed.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor: remove the DevOps cross-check entirely

mocd_servicecatalogue on the document type becomes the only authority on
which service owns a document. The CRM-internal cross-check against the
parent request stays -- that is CRM asking itself, not DevOps.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: The two vocabularies and the ledger row

**Files:**
- Create: `src/MocdDocFix/Domain/RowVerdict.cs`, `src/MocdDocFix/Domain/RowState.cs`, `src/MocdDocFix/Domain/LedgerRow.cs`, `src/MocdDocFix/Domain/CrmLinks.cs`
- Test: `tests/MocdDocFix.Tests/LedgerRowTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum RowVerdict { Fix, Review, Skip, Ignore, Redo, Unrecognised }`
  - `static class RowVerdicts` with `const string Fix/Review/Skip/Ignore/Redo`, `RowVerdict Parse(string?)`, `string Text(RowVerdict)`
  - `enum RowState { NotStarted, Corrected, Deleted, Ignore, Failed, Unrecognised }`
  - `static class RowStates` with `const string Corrected = "corrected and pending the delete of old docs"`, `Deleted = "old files deleted"`, `Ignore = "ignore"`, `Failed = "failed"`, `RowState Parse(string?)`, `string Text(RowState)`
  - `sealed class LedgerRow` — 29 settable properties, plus `[Ignore] RowVerdict Verdict()` and `[Ignore] RowState State()` **methods** (not properties, to avoid clashing with the `Verdict` and `FinalState` string columns)
  - `static class CrmLinks` with `string Document(string crmUrl, Guid id)` and `string DocumentFile(string crmUrl, Guid id)`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/LedgerRowTests.cs`:

```csharp
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

public class LedgerRowTests
{
    [Theory]
    [InlineData("fix", RowVerdict.Fix)]
    [InlineData("FIX", RowVerdict.Fix)]
    [InlineData("  Fix  ", RowVerdict.Fix)]
    [InlineData("review", RowVerdict.Review)]
    [InlineData("skip", RowVerdict.Skip)]
    [InlineData("ignore", RowVerdict.Ignore)]
    [InlineData("redo", RowVerdict.Redo)]
    public void Every_verdict_the_operator_may_type_is_understood(string cell, RowVerdict expected) =>
        Assert.Equal(expected, RowVerdicts.Parse(cell));

    /// <summary>
    /// The safety rule: a typo must never be read as the value it nearly is. "fixx" acting as
    /// "fix" would upload a file the operator had tried to exclude.
    /// </summary>
    [Theory]
    [InlineData("fixx")]
    [InlineData("f")]
    [InlineData("done")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_unrecognised_rather_than_the_value_it_resembles(string? cell) =>
        Assert.Equal(RowVerdict.Unrecognised, RowVerdicts.Parse(cell));

    [Theory]
    [InlineData("corrected and pending the delete of old docs", RowState.Corrected)]
    [InlineData("CORRECTED AND PENDING THE DELETE OF OLD DOCS", RowState.Corrected)]
    [InlineData("old files deleted", RowState.Deleted)]
    [InlineData("ignore", RowState.Ignore)]
    [InlineData("failed", RowState.Failed)]
    public void Every_final_state_the_steps_write_is_understood(string cell, RowState expected) =>
        Assert.Equal(expected, RowStates.Parse(cell));

    /// <summary>Blank is a real value — it means the row has not been started.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_final_state_means_not_started(string? cell) =>
        Assert.Equal(RowState.NotStarted, RowStates.Parse(cell));

    [Fact]
    public void A_final_state_nobody_recognises_is_not_mistaken_for_not_started() =>
        Assert.Equal(RowState.Unrecognised, RowStates.Parse("nearly done"));

    /// <summary>Round-tripping matters: the steps write Text(), the operator reads it, Parse() sees it again.</summary>
    [Theory]
    [InlineData(RowState.Corrected)]
    [InlineData(RowState.Deleted)]
    [InlineData(RowState.Ignore)]
    [InlineData(RowState.Failed)]
    public void What_a_step_writes_is_what_the_next_run_reads(RowState state) =>
        Assert.Equal(state, RowStates.Parse(RowStates.Text(state)));

    [Fact]
    public void The_row_reads_its_own_two_cells()
    {
        var row = new LedgerRow { Verdict = "fix", FinalState = RowStates.Text(RowState.Corrected) };

        Assert.Equal(RowVerdict.Fix, row.Verdict2());
        Assert.Equal(RowState.Corrected, row.State());
    }

    [Fact]
    public void The_two_crm_links_point_at_the_right_entities()
    {
        var id = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");

        Assert.Equal(
            $"https://crm.example/main.aspx?etn=mocd_document&pagetype=entityrecord&id={id}",
            CrmLinks.Document("https://crm.example/", id));

        Assert.Equal(
            $"https://crm.example/main.aspx?etn=mocd_documentfile&pagetype=entityrecord&id={id}",
            CrmLinks.DocumentFile("https://crm.example", id));
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~LedgerRowTests"`
Expected: FAIL — `error CS0246: The type or namespace name 'RowVerdict' could not be found`.

- [ ] **Step 3: Write `RowVerdict.cs`**

```csharp
namespace MocdDocFix.Domain;

/// <summary>
/// What the operator's verdict cell says, once parsed. Anything the vocabulary does not contain
/// is <see cref="Unrecognised"/> — deliberately its own case rather than a default, so a typo is
/// reported at the end of a run instead of being read as the value it nearly is.
/// </summary>
public enum RowVerdict { Fix, Review, Skip, Ignore, Redo, Unrecognised }

public static class RowVerdicts
{
    public const string Fix = "fix";
    public const string Review = "review";
    public const string Skip = "skip";
    public const string Ignore = "ignore";
    public const string Redo = "redo";

    public static RowVerdict Parse(string? cell) => (cell ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Fix => RowVerdict.Fix,
        Review => RowVerdict.Review,
        Skip => RowVerdict.Skip,
        Ignore => RowVerdict.Ignore,
        Redo => RowVerdict.Redo,
        _ => RowVerdict.Unrecognised
    };

    public static string Text(RowVerdict verdict) => verdict switch
    {
        RowVerdict.Fix => Fix,
        RowVerdict.Review => Review,
        RowVerdict.Skip => Skip,
        RowVerdict.Ignore => Ignore,
        RowVerdict.Redo => Redo,
        _ => string.Empty
    };
}
```

- [ ] **Step 4: Write `RowState.cs`**

```csharp
namespace MocdDocFix.Domain;

/// <summary>
/// How far a row has got. <see cref="NotStarted"/> is a blank cell and is a real answer;
/// <see cref="Unrecognised"/> is something else entirely and must never be confused with it,
/// because the delete step acts on exactly one of these values.
/// </summary>
public enum RowState { NotStarted, Corrected, Deleted, Ignore, Failed, Unrecognised }

public static class RowStates
{
    /// <summary>The only value the delete step acts on. Written verbatim; do not abbreviate.</summary>
    public const string Corrected = "corrected and pending the delete of old docs";

    public const string Deleted = "old files deleted";
    public const string Ignore = "ignore";
    public const string Failed = "failed";

    public static RowState Parse(string? cell)
    {
        var text = (cell ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return RowState.NotStarted;

        return text switch
        {
            Corrected => RowState.Corrected,
            Deleted => RowState.Deleted,
            Ignore => RowState.Ignore,
            Failed => RowState.Failed,
            _ => RowState.Unrecognised
        };
    }

    public static string Text(RowState state) => state switch
    {
        RowState.Corrected => Corrected,
        RowState.Deleted => Deleted,
        RowState.Ignore => Ignore,
        RowState.Failed => Failed,
        _ => string.Empty
    };
}
```

- [ ] **Step 5: Write `CrmLinks.cs`**

```csharp
namespace MocdDocFix.Domain;

/// <summary>
/// Deep links into CRM. Both live here rather than on the writers that print them, because
/// every mode prints at least one and two spellings of the same URL is one too many.
/// </summary>
public static class CrmLinks
{
    public static string Document(string crmUrl, Guid id) => Link(crmUrl, "mocd_document", id);

    public static string DocumentFile(string crmUrl, Guid id) => Link(crmUrl, "mocd_documentfile", id);

    private static string Link(string crmUrl, string entity, Guid id) =>
        $"{crmUrl.TrimEnd('/')}/main.aspx?etn={entity}&pagetype=entityrecord&id={id}";
}
```

- [ ] **Step 6: Write `LedgerRow.cs`**

```csharp
using CsvHelper.Configuration.Attributes;

namespace MocdDocFix.Domain;

/// <summary>
/// One document, as one line of the ledger. A mutable class rather than a record: the whole
/// file is rewritten after every completed row, and the steps set cells on the instance they
/// were handed.
///
/// The header names are the operator's, not the code's — they are read in Excel. Changing one
/// silently orphans the column in every ledger already on disk, so do not rename without
/// migrating.
/// </summary>
public sealed class LedgerRow
{
    [Name("row")] public int Row { get; set; }
    [Name("doc id")] public Guid DocId { get; set; }
    [Name("doc name")] public string DocName { get; set; } = string.Empty;

    /// <summary>Filled once the row has been backed up, so the bytes are one click away.</summary>
    [Name("backup path")] public string BackupPath { get; set; } = string.Empty;

    [Name("doc file id")] public Guid DocFileId { get; set; }
    [Name("doc file name")] public string DocFileName { get; set; } = string.Empty;
    [Name("doc type name")] public string DocTypeName { get; set; } = string.Empty;
    [Name("service catalogue id")] public string ServiceCatalogueId { get; set; } = string.Empty;
    [Name("service catalogue name")] public string ServiceCatalogueName { get; set; } = string.Empty;
    [Name("correct service catalogue id")] public string CorrectServiceCatalogueId { get; set; } = string.Empty;
    [Name("correct service catalogue name")] public string CorrectServiceCatalogueName { get; set; } = string.Empty;
    [Name("old file path")] public string OldFilePath { get; set; } = string.Empty;
    [Name("new file path predicted")] public string NewFilePathPredicted { get; set; } = string.Empty;
    [Name("new file path")] public string NewFilePath { get; set; } = string.Empty;

    /// <summary>Corrected copies a revert has abandoned, semicolon-separated. Nothing deletes these.</summary>
    [Name("superseded paths")] public string SupersededPaths { get; set; } = string.Empty;

    // The four fields a correction overwrites, as they were. A blank cell means the original
    // record held nothing there — it does not mean "not recorded".
    [Name("old category")] public string OldCategory { get; set; } = string.Empty;
    [Name("old hash")] public string OldHash { get; set; } = string.Empty;
    [Name("old file name")] public string OldFileName { get; set; } = string.Empty;
    [Name("old file id")] public string OldFileId { get; set; } = string.Empty;

    [Name("verdict")] public string Verdict { get; set; } = string.Empty;
    [Name("group")] public int Group { get; set; }
    [Name("reason of bug")] public string ReasonOfBug { get; set; } = string.Empty;
    [Name("solution")] public string Solution { get; set; } = string.Empty;
    [Name("final state")] public string FinalState { get; set; } = string.Empty;
    [Name("error")] public string Error { get; set; } = string.Empty;
    [Name("notes")] public string Notes { get; set; } = string.Empty;
    [Name("crm link of doc")] public string CrmLinkOfDoc { get; set; } = string.Empty;
    [Name("crm link of doc file")] public string CrmLinkOfDocFile { get; set; } = string.Empty;

    /// <summary>portal or plugin — which code path created the record, from FileRecordCopier.StyleOf.</summary>
    [Name("way of upload")] public string WayOfUpload { get; set; } = string.Empty;

    /// <summary>
    /// The verdict cell, parsed. A method rather than a property because the column itself is
    /// called Verdict, and CsvHelper maps by property.
    /// </summary>
    [Ignore] public RowVerdict Verdict2() => RowVerdicts.Parse(Verdict);

    [Ignore] public RowState State() => RowStates.Parse(FinalState);
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~LedgerRowTests"`
Expected: PASS, 24 tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(ledger): the row, and the two vocabularies the operator edits

A typo in either column parses to Unrecognised rather than to the value
it resembles, so a mistyped verdict can never cause a write.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `LedgerStore`

**Files:**
- Create: `src/MocdDocFix/Storage/LedgerStore.cs`
- Test: `tests/MocdDocFix.Tests/LedgerStoreTests.cs`

**Interfaces:**
- Consumes: `LedgerRow` (Task 2).
- Produces: `sealed class LedgerStore` with
  `LedgerStore(string path)`, `string Path { get; }`, `bool Exists { get; }`,
  `IReadOnlyList<LedgerRow> Read()`, `void Write(IReadOnlyList<LedgerRow> rows)`,
  `string StartNewKeepingOld()`.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/LedgerStoreTests.cs`:

```csharp
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class LedgerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-ledger-" + Guid.NewGuid());

    public LedgerStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private LedgerStore Store() => new(Path.Combine(_dir, "repair-dev.csv"));

    private static LedgerRow Row(int number) => new()
    {
        Row = number,
        DocId = Guid.Parse($"a3f1b2c4-0000-0000-0000-{number:D12}"),
        DocName = "Board of Director's Decision",
        DocFileId = Guid.Parse($"7c20a1f4-0000-0000-0000-{number:D12}"),
        DocFileName = "cert.jpg",
        OldFilePath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg",
        Verdict = RowVerdicts.Fix,
        Group = 2,
        ReasonOfBug = "Path has 'docTypeCatalogue', the variable name rather than its value"
    };

    [Fact]
    public void A_written_ledger_reads_back_unchanged()
    {
        var store = Store();
        store.Write(new[] { Row(1), Row(2) });

        var back = store.Read();

        Assert.Equal(2, back.Count);
        Assert.Equal(Row(1).DocId, back[0].DocId);
        Assert.Equal("Board of Director's Decision", back[0].DocName);
        Assert.Equal(@"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg", back[0].OldFilePath);
        Assert.Equal(RowVerdict.Fix, back[0].Verdict2());
        Assert.Equal(2, back[1].Row);
    }

    /// <summary>Excel will not show Arabic file names correctly without the BOM.</summary>
    [Fact]
    public void The_file_is_written_with_a_byte_order_mark()
    {
        var store = Store();
        store.Write(new[] { Row(1) });

        var first = File.ReadAllBytes(store.Path).Take(3).ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, first);
    }

    /// <summary>
    /// The point of the whole design: what the operator types in Excel is what the next run acts
    /// on. A rewrite must carry their cells through untouched.
    /// </summary>
    [Fact]
    public void An_edit_made_by_hand_survives_a_rewrite()
    {
        var store = Store();
        store.Write(new[] { Row(1) });

        var edited = store.Read().ToList();
        edited[0].Verdict = RowVerdicts.Ignore;
        edited[0].FinalState = RowStates.Text(RowState.Ignore);
        store.Write(edited);

        var back = store.Read();
        Assert.Equal(RowVerdict.Ignore, back[0].Verdict2());
        Assert.Equal(RowState.Ignore, back[0].State());
    }

    /// <summary>
    /// A blank cell must come back blank, not as null and not as the string "null". Redo writes
    /// these values back into CRM, and a portal record genuinely has no old file id.
    /// </summary>
    [Fact]
    public void A_blank_old_value_stays_blank()
    {
        var store = Store();
        var row = Row(1);
        row.OldFileId = string.Empty;
        row.OldCategory = string.Empty;
        store.Write(new[] { row });

        var back = store.Read();
        Assert.Equal(string.Empty, back[0].OldFileId);
        Assert.Equal(string.Empty, back[0].OldCategory);
    }

    [Fact]
    public void Reading_a_ledger_that_is_not_there_gives_nothing_rather_than_throwing()
    {
        var store = Store();
        Assert.False(store.Exists);
        Assert.Empty(store.Read());
    }

    /// <summary>
    /// The backup is taken once per sitting, not once per row. Taken per row it would leave four
    /// hundred copies and the earliest -- the only one worth having -- would be lost among them.
    /// </summary>
    [Fact]
    public void The_backup_copy_is_taken_once_however_many_times_the_ledger_is_rewritten()
    {
        var store = Store();
        store.Write(new[] { Row(1) });
        store.Write(new[] { Row(1), Row(2) });
        store.Write(new[] { Row(1), Row(2), Row(3) });

        Assert.Single(Directory.GetFiles(_dir, "*.bak.csv"));
    }

    /// <summary>The first write has nothing to copy, so it takes no backup.</summary>
    [Fact]
    public void A_ledger_that_did_not_exist_yet_gets_no_backup_copy()
    {
        Store().Write(new[] { Row(1) });
        Assert.Empty(Directory.GetFiles(_dir, "*.bak.csv"));
    }

    [Fact]
    public void Starting_a_new_ledger_keeps_the_old_one_under_a_dated_name()
    {
        var store = Store();
        store.Write(new[] { Row(1) });

        var kept = store.StartNewKeepingOld();

        Assert.True(File.Exists(kept));
        Assert.False(store.Exists);
        Assert.Equal(1, new LedgerStore(kept).Read().Count);
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~LedgerStoreTests"`
Expected: FAIL — `'LedgerStore' could not be found`.

- [ ] **Step 3: Write `LedgerStore.cs`**

```csharp
using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// The ledger on disk. One CSV per environment, rewritten in full every time a row finishes.
///
/// Rewriting the whole file for one changed cell is deliberate. The operator has the file open
/// in Excel between runs, so it must be a plain well-formed CSV at every instant, not an
/// append-log that needs replaying — and at a few hundred rows the cost is not measurable.
/// A crash therefore loses at most the row in flight.
/// </summary>
public sealed class LedgerStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Taken before the first write of a sitting, never again. The ledger is the only route back
    /// once a correction has overwritten a CRM record, so the state it was in when this session
    /// started is worth one copy — and only one, or the useful earliest copy is buried.
    /// </summary>
    private bool _backedUpThisSitting;

    public LedgerStore(string path) => Path = path;

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    public IReadOnlyList<LedgerRow> Read()
    {
        if (!Exists) return Array.Empty<LedgerRow>();

        using var reader = new StreamReader(Path, Utf8);
        using var csv = new CsvReader(reader, Config());
        return csv.GetRecords<LedgerRow>().ToList();
    }

    public void Write(IReadOnlyList<LedgerRow> rows)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        BackUpOnce();

        using var writer = new StreamWriter(Path, append: false, Utf8);
        using var csv = new CsvWriter(writer, Config());
        csv.WriteRecords(rows);
    }

    /// <summary>Renames the current ledger out of the way. Returns where it was kept.</summary>
    public string StartNewKeepingOld()
    {
        var kept = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        File.Move(Path, kept);
        _backedUpThisSitting = false;
        return kept;
    }

    private void BackUpOnce()
    {
        if (_backedUpThisSitting || !Exists) return;

        File.Copy(Path, System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}-{DateTime.Now:yyyyMMdd-HHmmss}.bak.csv"));

        _backedUpThisSitting = true;
    }

    /// <summary>
    /// A missing column is ignored rather than fatal, so a ledger written by an earlier build
    /// still opens — the cells it lacks simply come back empty.
    /// </summary>
    private static CsvConfiguration Config() => new(CultureInfo.InvariantCulture)
    {
        MissingFieldFound = null,
        HeaderValidated = null
    };
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~LedgerStoreTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(ledger): read, rewrite, and one backup copy per sitting

The whole file is rewritten when a row finishes, so it is a well-formed
CSV at every instant -- the operator has it open in Excel between runs.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: `ChangeJournal` and `ErrorLog`

**Files:**
- Create: `src/MocdDocFix/Storage/ChangeJournal.cs`, `src/MocdDocFix/Storage/ErrorLog.cs`
- Test: `tests/MocdDocFix.Tests/ChangeJournalTests.cs`

**Interfaces:**
- Consumes: `LedgerRow` (Task 2).
- Produces:
  - `sealed record RecordValues(string? Path, string? Category, string? Hash, string? FileName, string? FileId)`
  - `sealed record ChangeEntry(DateTimeOffset At, Guid Doc, Guid Record, string Action, RecordValues? Old, RecordValues? New)`
  - `static class ChangeActions` with `const string Corrected = "corrected"`, `Reverted = "reverted"`, `Deleted = "deleted"`
  - `sealed class ChangeJournal` with `ChangeJournal(string path)`, `void Append(ChangeEntry)`, `IReadOnlyList<ChangeEntry> Read()`
  - `sealed class ErrorLog` with `ErrorLog(string path)`, `void Append(int number, int total, LedgerRow row, string step, string detail)`, `string Path { get; }`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/ChangeJournalTests.cs`:

```csharp
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class ChangeJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-journal-" + Guid.NewGuid());

    public ChangeJournalTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string JournalPath => Path.Combine(_dir, "changes-dev.jsonl");

    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

    private static ChangeEntry Corrected() => new(
        DateTimeOffset.UtcNow, Doc, Record, ChangeActions.Corrected,
        new RecordValues(@"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg",
            "docTypeCatalogue", "9f86d081", "a3f1.jpg", null),
        new RecordValues(@"DigitalServices\7c20a1f4\20260915\b2c3.jpg",
            "7c20a1f4", "5e884898", "b2c3.jpg", null));

    [Fact]
    public void A_correction_round_trips()
    {
        var journal = new ChangeJournal(JournalPath);
        journal.Append(Corrected());

        var back = journal.Read();

        Assert.Single(back);
        Assert.Equal(ChangeActions.Corrected, back[0].Action);
        Assert.Equal(Doc, back[0].Doc);
        Assert.Equal("docTypeCatalogue", back[0].Old!.Category);
        Assert.Equal(@"DigitalServices\7c20a1f4\20260915\b2c3.jpg", back[0].New!.Path);
        Assert.Null(back[0].Old!.FileId);
    }

    [Fact]
    public void Appending_never_rewrites_what_is_already_there()
    {
        var journal = new ChangeJournal(JournalPath);
        journal.Append(Corrected());
        journal.Append(Corrected() with { Action = ChangeActions.Reverted });
        journal.Append(Corrected() with { Action = ChangeActions.Deleted });

        Assert.Equal(
            new[] { ChangeActions.Corrected, ChangeActions.Reverted, ChangeActions.Deleted },
            journal.Read().Select(e => e.Action));
    }

    /// <summary>
    /// The reason this file exists rather than trusting the CSV: a process killed mid-write
    /// leaves a half line, and everything before it must still be readable.
    /// </summary>
    [Fact]
    public void A_torn_last_line_costs_that_line_and_nothing_else()
    {
        var journal = new ChangeJournal(JournalPath);
        journal.Append(Corrected());
        journal.Append(Corrected() with { Action = ChangeActions.Reverted });
        File.AppendAllText(JournalPath, "{\"At\":\"2026-09-15T10:32:0");

        var back = journal.Read();

        Assert.Equal(2, back.Count);
        Assert.Equal(ChangeActions.Reverted, back[1].Action);
    }

    [Fact]
    public void Reading_a_journal_that_is_not_there_gives_nothing() =>
        Assert.Empty(new ChangeJournal(JournalPath).Read());

    [Fact]
    public void The_error_log_says_which_row_which_step_and_why()
    {
        var log = new ErrorLog(Path.Combine(_dir, "errors-dev.txt"));

        log.Append(14, 431,
            new LedgerRow { DocId = Doc, DocName = "Good Conduct Certificate", DocFileName = "id.png" },
            "upload", "POST /api/File/Upload -> 413 Payload Too Large");

        var text = File.ReadAllText(log.Path);

        Assert.Contains("[ 14/431 ]", text);
        Assert.Contains("id.png", text);
        Assert.Contains(Doc.ToString(), text);
        Assert.Contains("step: upload", text);
        Assert.Contains("413", text);
    }

    [Fact]
    public void The_error_log_keeps_every_failure_rather_than_the_last()
    {
        var log = new ErrorLog(Path.Combine(_dir, "errors-dev.txt"));
        var row = new LedgerRow { DocId = Doc, DocFileName = "id.png" };

        log.Append(1, 2, row, "upload", "first");
        log.Append(2, 2, row, "record update", "second");

        var text = File.ReadAllText(log.Path);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~ChangeJournalTests"`
Expected: FAIL — `'ChangeJournal' could not be found`.

- [ ] **Step 3: Write `ChangeJournal.cs`**

```csharp
using System.Text.Json;

namespace MocdDocFix.Storage;

/// <summary>The five fields of a mocd_documentfile that a correction overwrites.</summary>
public sealed record RecordValues(string? Path, string? Category, string? Hash, string? FileName, string? FileId);

/// <param name="Action">One of <see cref="ChangeActions"/>.</param>
public sealed record ChangeEntry(
    DateTimeOffset At, Guid Doc, Guid Record, string Action, RecordValues? Old, RecordValues? New);

public static class ChangeActions
{
    public const string Corrected = "corrected";
    public const string Reverted = "reverted";
    public const string Deleted = "deleted";
}

/// <summary>
/// Every change this tool makes to a mocd_documentfile, with the values before and after,
/// appended as it happens and never rewritten.
///
/// It exists because a correction overwrites the old record rather than replacing it, so CRM
/// stops holding the before-state. The ledger holds it too, but the ledger is rewritten
/// constantly and is edited by hand in Excel; this is the copy that no mistake can reach.
/// </summary>
public sealed class ChangeJournal
{
    private readonly string _path;

    public ChangeJournal(string path) => _path = path;

    public void Append(ChangeEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    public IReadOnlyList<ChangeEntry> Read()
    {
        var entries = new List<ChangeEntry>();
        if (!File.Exists(_path)) return entries;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // A process killed mid-write leaves a half line. Skipping it keeps every complete
            // line before it readable, which is the whole reason this is JSONL and not JSON.
            try
            {
                if (JsonSerializer.Deserialize<ChangeEntry>(line) is { } entry) entries.Add(entry);
            }
            catch (JsonException) { }
        }

        return entries;
    }
}
```

- [ ] **Step 4: Write `ErrorLog.cs`**

```csharp
using System.Text;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// What broke, at length. The ledger's error column holds a sentence that fits a spreadsheet
/// cell; this holds the request, the response and the step, which is what is actually needed to
/// find out why.
/// </summary>
public sealed class ErrorLog
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    public ErrorLog(string path) => Path = path;

    public string Path { get; }

    public void Append(int number, int total, LedgerRow row, string step, string detail)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var text = new StringBuilder();
        text.AppendLine(new string('-', 78));
        text.AppendLine($"[ {number}/{total} ]  {row.DocFileName}   {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"  document     {row.DocId}");
        text.AppendLine($"  record       {row.DocFileId}");
        text.AppendLine($"  name         {row.DocName}");
        text.AppendLine($"  step: {step}");
        text.AppendLine();

        foreach (var line in detail.Split('\n')) text.AppendLine("  " + line.TrimEnd());
        text.AppendLine();

        File.AppendAllText(Path, text.ToString(), Utf8);
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~ChangeJournalTests"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(ledger): the change journal and the error log

The journal is the copy of the before-state that no hand-edit can reach:
appended as each change happens, never rewritten, and a torn final line
costs that line alone.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: The CRM clients learn the four old values and the in-place update

**Files:**
- Modify: `src/MocdDocFix/Domain/DocumentRow.cs`, `src/MocdDocFix/Clients/CrmReadClient.cs`, `src/MocdDocFix/Clients/CrmWriteClient.cs`
- Modify: `tests/MocdDocFix.Tests/Fakes/FakeCrmWriteClient.cs`
- Test: `tests/MocdDocFix.Tests/CrmWriteClientTests.cs` (add to it)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `DocumentRow` gains three defaulted properties: `string? OldCategory = null`, `Guid? VendorFileId = null`, `string? VendorFileName = null`.
  - `ICrmWriteClient` gains `Task UpdateDocumentFileAsync(Guid recordId, IReadOnlyDictionary<string, object?> attributes, CancellationToken ct)`.
  - `FakeCrmWriteClient` gains `List<Updated> UpdatedFiles`, where `record Updated(Guid RecordId, IReadOnlyDictionary<string, object?> Attributes)` exposes `FilePath`, `Hash`, `Category`, `FileName`, `FileId` and `bool Wrote(string attribute)`; plus `string? UpdateRefusal`.

- [ ] **Step 1: Write the failing test**

First read `tests/MocdDocFix.Tests/Fakes/FakeHttpMessageHandler.cs` and the existing `CreateDocumentFileAsync` tests in `tests/MocdDocFix.Tests/CrmWriteClientTests.cs`, and match whatever they use to assert on a sent request. Then append inside the existing class, adapting the three assertion lines marked below to that handler's actual API:

```csharp
    /// <summary>
    /// The write the whole redesign turns on. A correction updates the record the document
    /// already points at, so there is no create and no repoint — and a PATCH to the wrong URL
    /// would create a second row instead of changing this one.
    /// </summary>
    [Fact]
    public async Task Updating_a_document_file_patches_that_record_and_nothing_else()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        var client = new CrmWriteClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://crm.example/api/") });
        var record = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

        await client.UpdateDocumentFileAsync(record, new Dictionary<string, object?>
        {
            ["mocd_filepath"] = @"DigitalServices\cat\20260915\b2c3.jpg",
            ["mocd_category"] = "cat",
            ["mocd_hash"] = "5e884898"
        }, CancellationToken.None);

        // --- adapt these three to FakeHttpMessageHandler's own API ---
        var sent = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal($"https://crm.example/api/mocd_documentfiles({record})", sent.RequestUri!.ToString());
    }

    [Fact]
    public async Task An_update_that_crm_rejects_is_raised_rather_than_swallowed()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"bad attribute"}}""");

        var client = new CrmWriteClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://crm.example/api/") });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UpdateDocumentFileAsync(Guid.NewGuid(),
                new Dictionary<string, object?> { ["mocd_filepath"] = "x" }, CancellationToken.None));

        Assert.Contains("bad attribute", thrown.Message);
    }
```

Add `using System.Net;` and `using MocdDocFix.Tests.Fakes;` at the top only if they are not already there.

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~CrmWriteClientTests"`

Expected: FAIL — `'CrmWriteClient' does not contain a definition for 'UpdateDocumentFileAsync'`.

- [ ] **Step 3: Add the update to the write client**

In `src/MocdDocFix/Clients/CrmWriteClient.cs`, add to the `ICrmWriteClient` interface:

```csharp
    /// <summary>
    /// Changes the named attributes on an existing mocd_documentfile, leaving every other column
    /// alone. This is how a correction is applied: the document already points at this record,
    /// so nothing is created and nothing is repointed.
    /// </summary>
    Task UpdateDocumentFileAsync(Guid recordId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct);
```

and to the `CrmWriteClient` class, beside `RepointDocumentAsync`:

```csharp
    public Task UpdateDocumentFileAsync(Guid recordId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"mocd_documentfiles({recordId})",
            new Dictionary<string, object?>(attributes), ct);
```

- [ ] **Step 4: Teach the fake to record updates**

In `tests/MocdDocFix.Tests/Fakes/FakeCrmWriteClient.cs`, add beside the `Created` record:

```csharp
    /// <param name="Attributes">Only the columns the correction changed, not the whole record.</param>
    public record Updated(Guid RecordId, IReadOnlyDictionary<string, object?> Attributes)
    {
        public string? FilePath => Attributes.TryGetValue("mocd_filepath", out var v) ? v as string : null;
        public string? Hash => Attributes.TryGetValue("mocd_hash", out var v) ? v as string : null;
        public string? Category => Attributes.TryGetValue("mocd_category", out var v) ? v as string : null;
        public string? FileName => Attributes.TryGetValue("mocd_filename", out var v) ? v as string : null;
        public string? FileId => Attributes.TryGetValue("mocd_fileid", out var v) ? v as string : null;

        /// <summary>Whether the column was in the payload at all — blank and absent differ.</summary>
        public bool Wrote(string attribute) => Attributes.ContainsKey(attribute);
    }

    public List<Updated> UpdatedFiles { get; } = new();

    /// <summary>When set, the next update throws with this message — a CRM refusal.</summary>
    public string? UpdateRefusal { get; set; }

    public Task UpdateDocumentFileAsync(Guid recordId,
        IReadOnlyDictionary<string, object?> attributes, CancellationToken ct)
    {
        if (UpdateRefusal is not null) throw new InvalidOperationException(UpdateRefusal);

        UpdatedFiles.Add(new Updated(recordId, attributes));
        return Task.CompletedTask;
    }
```

- [ ] **Step 5: Widen `DocumentRow`**

In `src/MocdDocFix/Domain/DocumentRow.cs`, replace `DateTimeOffset ModifiedOn)` with the following. They are defaulted so every existing construction site still compiles:

```csharp
    DateTimeOffset ModifiedOn,

    /// <summary>mocd_category as the old record holds it. Often the junk that is the bug.</summary>
    string? OldCategory = null,

    /// <summary>mocd_fileid — the vendor's own id. Null on a portal-created record, always.</summary>
    Guid? VendorFileId = null,

    /// <summary>mocd_filename — the vendor's name for the file, distinct from mocd_name.</summary>
    string? VendorFileName = null)
```

- [ ] **Step 6: Select the three new columns and map them**

In `src/MocdDocFix/Clients/CrmReadClient.cs`, widen the documentfile expand:

```csharp
    private static readonly string Expand =
        "mocd_documentfile($select=mocd_filepath,mocd_hash,mocd_name,mocd_mediatype," +
        "mocd_category,mocd_fileid,mocd_filename)," +
        "mocd_documenttype($select=mocd_name,_mocd_servicecatalogue_value)," +
        string.Join(",", CrossCheckNavigations.Select(n => $"{n}($select=_mocd_servicecatalogue_value)"));
```

Then find the single place that constructs a row — search the file for `new DocumentRow(` — and add the three arguments, reading from the expanded `mocd_documentfile` object exactly as the neighbouring `mocd_filepath` and `mocd_hash` reads already do. `mocd_fileid` arrives as a string: parse it with `Guid.TryParse` and pass `null` when it is absent or unparseable.

- [ ] **Step 7: Build and run the whole suite**

Run: `dotnet build -c Release 2>&1 | grep -E "error|warning" ; dotnet test tests/MocdDocFix.Tests 2>&1 | tail -5`

Expected: no errors, no warnings, all passing.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(crm): update a documentfile in place, and read the four old values

mocd_category, mocd_fileid and mocd_filename join the expand, because a
revert must put back exactly what a correction overwrote -- and a blank
one of those means the original record held nothing there.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: `LedgerBuilder`

**Files:**
- Create: `src/MocdDocFix/Commands/LedgerBuilder.cs`
- Test: `tests/MocdDocFix.Tests/LedgerBuilderTests.cs`

**Interfaces:**
- Consumes: `LedgerRow`, `RowVerdicts`, `CrmLinks` (Task 2); `DocumentRow.OldCategory` / `VendorFileId` / `VendorFileName` (Task 5); the existing `Classifier.Classify`, `FilePathParser.Parse`, `Verdict`.
- Produces: `sealed class LedgerBuilder` with `LedgerBuilder(ICrmReadClient crm, IReadOnlyList<Guid> catalogues, string crmUrl)` and `Task<IReadOnlyList<LedgerRow>> BuildAsync(CancellationToken ct)`.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/LedgerBuilderTests.cs`:

```csharp
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class LedgerBuilderTests
{
    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");
    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");

    private static DocumentRow Document(string? path, Guid? vendorFileId = null,
        string? oldCategory = "docTypeCatalogue") =>
        new(DocumentId: Doc,
            DocumentName: "A Copy of Board of Director's Decision",
            DocumentFileId: Record,
            FilePath: path,
            FileName: "cert.jpg",
            MediaType: "image/jpeg",
            Hash: "9f86d081",
            DocumentTypeId: Guid.NewGuid(),
            DocumentTypeName: "Board Decision",
            DocTypeCatalogueId: Correct,
            CrossCheckCatalogueId: null,
            CrossCheckSource: null,
            ModifiedOn: DateTimeOffset.UtcNow,
            OldCategory: oldCategory,
            VendorFileId: vendorFileId,
            VendorFileName: "a3f1.jpg");

    private static FakeCrmReadClient Crm(params DocumentRow[] documents)
    {
        var crm = new FakeCrmReadClient();
        crm.Documents.AddRange(documents);
        crm.KnownCatalogues.Add(Correct.ToString());
        crm.CatalogueNames[Correct.ToString()] = "Employee Appointment Request";
        return crm;
    }

    private static LedgerBuilder Builder(FakeCrmReadClient crm) =>
        new(crm, new[] { Correct }, "https://crm.example/");

    private const string BrokenPath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg";

    [Fact]
    public async Task A_broken_path_becomes_a_fix_row_carrying_everything_a_correction_needs()
    {
        var rows = await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Row);
        Assert.Equal(Doc, row.DocId);
        Assert.Equal("A Copy of Board of Director's Decision", row.DocName);
        Assert.Equal(Record, row.DocFileId);
        Assert.Equal(RowVerdict.Fix, row.Verdict2());
        Assert.Equal(2, row.Group);
        Assert.Equal(Correct.ToString(), row.CorrectServiceCatalogueId);
        Assert.Equal("Employee Appointment Request", row.CorrectServiceCatalogueName);
        Assert.Equal(RowState.NotStarted, row.State());
        Assert.Equal(string.Empty, row.BackupPath);
        Assert.Equal(string.Empty, row.NewFilePath);
    }

    /// <summary>The four values a revert writes back, taken from the record rather than the path.</summary>
    [Fact]
    public async Task The_old_values_come_off_the_record()
    {
        var row = (await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None))[0];

        Assert.Equal("docTypeCatalogue", row.OldCategory);
        Assert.Equal("9f86d081", row.OldHash);
        Assert.Equal("a3f1.jpg", row.OldFileName);
        Assert.Equal(string.Empty, row.OldFileId);
    }

    /// <summary>A portal record has no mocd_fileid at all, and that blank is meaningful.</summary>
    [Fact]
    public async Task A_portal_record_and_a_plugin_record_are_told_apart()
    {
        var portal = (await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None))[0];
        Assert.Equal("portal", portal.WayOfUpload);
        Assert.Equal(string.Empty, portal.OldFileId);

        var pluginId = Guid.Parse("11111111-0000-0000-0000-000000000001");
        var plugin = (await Builder(Crm(Document(BrokenPath, vendorFileId: pluginId)))
            .BuildAsync(CancellationToken.None))[0];
        Assert.Equal("plugin", plugin.WayOfUpload);
        Assert.Equal(pluginId.ToString(), plugin.OldFileId);
    }

    [Fact]
    public async Task The_predicted_path_names_the_correct_catalogue_and_keeps_the_extension()
    {
        var row = (await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None))[0];

        Assert.StartsWith($@"DigitalServices\{Correct}\", row.NewFilePathPredicted);
        Assert.EndsWith(@"\(new id).jpg", row.NewFilePathPredicted);
        Assert.Contains(DateTime.Now.ToString("yyyyMMdd"), row.NewFilePathPredicted);
    }

    /// <summary>
    /// Every document in scope gets a row — the correct ones too. The verdict column is what
    /// separates them, and without the correct ones there is no denominator.
    /// </summary>
    [Fact]
    public async Task An_already_correct_document_still_gets_a_row_marked_skip()
    {
        var crm = Crm(Document($@"DigitalServices\{Correct}\20250509\a3f1.jpg"));

        var row = Assert.Single(await Builder(crm).BuildAsync(CancellationToken.None));

        Assert.Equal(RowVerdict.Skip, row.Verdict2());
        Assert.Equal(7, row.Group);
    }

    [Fact]
    public async Task A_document_with_no_file_path_gets_a_row_marked_skip()
    {
        var row = Assert.Single(await Builder(Crm(Document(null))).BuildAsync(CancellationToken.None));

        Assert.Equal(RowVerdict.Skip, row.Verdict2());
        Assert.Equal(string.Empty, row.OldFilePath);
    }

    [Fact]
    public async Task A_malformed_path_needs_a_human_and_says_so()
    {
        var crm = Crm(Document(@"DigitalServices\a3f1.jpg"));

        var row = Assert.Single(await Builder(crm).BuildAsync(CancellationToken.None));

        Assert.Equal(RowVerdict.Review, row.Verdict2());
        Assert.Equal(6, row.Group);
        Assert.NotEqual(string.Empty, row.ReasonOfBug);
        Assert.NotEqual(string.Empty, row.Solution);
    }

    [Fact]
    public async Task Both_crm_links_are_written()
    {
        var row = (await Builder(Crm(Document(BrokenPath))).BuildAsync(CancellationToken.None))[0];

        Assert.Equal(CrmLinks.Document("https://crm.example/", Doc), row.CrmLinkOfDoc);
        Assert.Equal(CrmLinks.DocumentFile("https://crm.example/", Record), row.CrmLinkOfDocFile);
    }

    [Fact]
    public async Task Rows_are_numbered_from_one_in_the_order_they_are_written()
    {
        var crm = Crm(Document(BrokenPath), Document(BrokenPath), Document(BrokenPath));

        var rows = await Builder(crm).BuildAsync(CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Row));
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~LedgerBuilderTests"`

Expected: FAIL — `'LedgerBuilder' could not be found`.

- [ ] **Step 3: Write `LedgerBuilder.cs`**

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <summary>
/// CRM to ledger. Every document under the configured services gets a row — the correct ones
/// and the legacy ones with no file at all, not only the broken ones, because the verdict
/// column is what separates them and a census without a denominator cannot be read.
///
/// It reads and classifies. It writes nothing, to CRM or to disk; the caller decides where the
/// rows go.
/// </summary>
public sealed class LedgerBuilder
{
    private readonly ICrmReadClient _crm;
    private readonly IReadOnlyList<Guid> _catalogues;
    private readonly string _crmUrl;

    public LedgerBuilder(ICrmReadClient crm, IReadOnlyList<Guid> catalogues, string crmUrl)
    {
        _crm = crm;
        _catalogues = catalogues;
        _crmUrl = crmUrl;
    }

    public async Task<IReadOnlyList<LedgerRow>> BuildAsync(CancellationToken ct)
    {
        var documents = await _crm.GetInScopeDocumentsAsync(_catalogues, ct);
        var rows = new List<LedgerRow>(documents.Count);
        var number = 1;

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(await RowFor(document, number++, ct));
        }

        return rows;
    }

    private async Task<LedgerRow> RowFor(DocumentRow document, int number, CancellationToken ct)
    {
        var parsed = FilePathParser.Parse(document.FilePath);

        // Resolved live against mocd_servicecatalogue; the client caches, so repeated ids cost
        // one query between them.
        var filedUnderName = parsed.CategorySegment is { } segment
            ? await _crm.GetServiceCatalogueNameAsync(segment, ct)
            : null;

        var serviceName = document.DocTypeCatalogueId is { } catalogue
            ? await _crm.GetServiceCatalogueNameAsync(catalogue.ToString(), ct)
            : null;

        var classification = Classifier.Classify(
            parsed, document.DocTypeCatalogueId, document.CrossCheckCatalogueId,
            _ => filedUnderName is not null);

        var correctName = classification.CorrectCatalogueId is { } correct
            ? await _crm.GetServiceCatalogueNameAsync(correct.ToString(), ct)
            : null;

        return new LedgerRow
        {
            Row = number,
            DocId = document.DocumentId,
            DocName = document.DocumentName,
            DocFileId = document.DocumentFileId,
            DocFileName = document.FileName ?? string.Empty,
            DocTypeName = document.DocumentTypeName,
            ServiceCatalogueId = document.DocTypeCatalogueId?.ToString() ?? string.Empty,
            ServiceCatalogueName = serviceName ?? string.Empty,
            CorrectServiceCatalogueId = classification.CorrectCatalogueId?.ToString() ?? string.Empty,
            CorrectServiceCatalogueName = correctName ?? string.Empty,
            OldFilePath = document.FilePath ?? string.Empty,
            NewFilePathPredicted = Predict(classification.CorrectCatalogueId, document.Extension),

            // Off the record, not off the path. These are what a revert writes back, so they
            // must be what the record actually holds — blanks included.
            OldCategory = document.OldCategory ?? string.Empty,
            OldHash = document.Hash ?? string.Empty,
            OldFileName = document.VendorFileName ?? string.Empty,
            OldFileId = document.VendorFileId?.ToString() ?? string.Empty,

            Verdict = VerdictFor(classification.Verdict),
            Group = classification.Group,
            ReasonOfBug = classification.Reason,
            Solution = classification.Solution,
            FinalState = string.Empty,
            CrmLinkOfDoc = CrmLinks.Document(_crmUrl, document.DocumentId),
            CrmLinkOfDocFile = CrmLinks.DocumentFile(_crmUrl, document.DocumentFileId),
            WayOfUpload = document.VendorFileId is null ? "portal" : "plugin"
        };
    }

    /// <summary>
    /// The shape the corrected path will take. The file id and the date folder are the server's
    /// to choose, so the id is written literally rather than guessed at.
    /// </summary>
    private static string Predict(Guid? correct, string extension) =>
        correct is null
            ? string.Empty
            : $@"DigitalServices\{correct}\{DateTime.Now:yyyyMMdd}\(new id){extension}";

    private static string VerdictFor(Verdict verdict) => verdict switch
    {
        Verdict.Fix => RowVerdicts.Fix,
        Verdict.Review => RowVerdicts.Review,
        _ => RowVerdicts.Skip
    };
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~LedgerBuilderTests"`

Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(ledger): build the census from CRM

Every document in the seven services gets a row, correct ones included --
the verdict column separates them, and a census without a denominator
cannot be read. The four old values come off the record, not the path.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: `RunProgress` and the three watch modes

**Files:**
- Create: `src/MocdDocFix/Ui/RunProgress.cs`
- Test: `tests/MocdDocFix.Tests/RunProgressTests.cs`

**Interfaces:**
- Consumes: `LedgerRow`, `RowStates` (Task 2); `IPrompts` and the `Screen` extension methods.
- Produces:
  - `enum WatchMode { Watch, Quiet, Unattended }` in namespace `MocdDocFix.Ui`
  - `sealed class RunProgress` with `RunProgress(IPrompts prompts, WatchMode mode)`, `WatchMode Mode { get; }`, `bool AsksTheEyeCheck { get; }`, `void StartRow(int number, int total, LedgerRow row)`, `void Step(string name, string detail = "")`, `void Finished(LedgerRow row)`, `void Skipped(LedgerRow row, string why)`, `void Failed(LedgerRow row, string why)`, `void BetweenRows()`.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/RunProgressTests.cs`:

```csharp
using MocdDocFix.Domain;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class RunProgressTests
{
    private static LedgerRow Row() => new()
    {
        Row = 12,
        DocId = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001"),
        DocName = "Board of Director's Decision",
        DocFileName = "cert.jpg"
    };

    private static (FakePrompts Prompts, RunProgress Progress) At(WatchMode mode)
    {
        var prompts = new FakePrompts();
        return (prompts, new RunProgress(prompts, mode));
    }

    private static string All(FakePrompts prompts) => string.Join("\n", prompts.Messages);

    [Fact]
    public void Watch_prints_every_step()
    {
        var (prompts, progress) = At(WatchMode.Watch);

        progress.StartRow(12, 431, Row());
        progress.Step("backing up", @"backup\dev\cert.jpg__a3f1");
        progress.Step("uploading", @"...\20260915\b2c3.jpg");
        progress.Finished(Row());

        var text = All(prompts);
        Assert.Contains("12/431", text);
        Assert.Contains("cert.jpg", text);
        Assert.Contains("backing up", text);
        Assert.Contains("uploading", text);
        Assert.Contains("FINISHED", text);
    }

    /// <summary>
    /// Quiet is one line per document. Printing each step over four hundred rows is thousands of
    /// lines of scrollback, and the failures are lost in it.
    /// </summary>
    [Fact]
    public void Quiet_prints_the_document_and_the_outcome_but_not_the_steps()
    {
        var (prompts, progress) = At(WatchMode.Quiet);

        progress.StartRow(12, 431, Row());
        progress.Step("backing up", @"backup\dev\cert.jpg__a3f1");
        progress.Step("uploading");
        progress.Finished(Row());

        var text = All(prompts);
        Assert.Contains("12/431", text);
        Assert.Contains("cert.jpg", text);
        Assert.Contains("FINISHED", text);
        Assert.DoesNotContain("backing up", text);
        Assert.DoesNotContain("uploading", text);
    }

    [Fact]
    public void Unattended_prints_as_little_as_quiet_does()
    {
        var (prompts, progress) = At(WatchMode.Unattended);

        progress.StartRow(12, 431, Row());
        progress.Step("uploading");
        progress.Finished(Row());

        Assert.DoesNotContain("uploading", All(prompts));
    }

    /// <summary>The only difference that can reach CRM: whether the operator is asked at all.</summary>
    [Theory]
    [InlineData(WatchMode.Watch, true)]
    [InlineData(WatchMode.Quiet, true)]
    [InlineData(WatchMode.Unattended, false)]
    public void Only_unattended_skips_the_eye_check(WatchMode mode, bool asks) =>
        Assert.Equal(asks, At(mode).Progress.AsksTheEyeCheck);

    /// <summary>
    /// Watch pauses so each document can be read. It is a bare enter, not the watch question
    /// being asked again — that was settled before the loop started.
    /// </summary>
    [Fact]
    public void Watch_pauses_between_documents()
    {
        var (prompts, progress) = At(WatchMode.Watch);
        prompts.ReadLineQueue = new Queue<string>(new[] { "" });

        progress.BetweenRows();

        Assert.Single(prompts.Questions);
    }

    [Theory]
    [InlineData(WatchMode.Quiet)]
    [InlineData(WatchMode.Unattended)]
    public void Nothing_else_pauses_between_documents(WatchMode mode)
    {
        var (prompts, progress) = At(mode);

        progress.BetweenRows();

        Assert.Empty(prompts.Questions);
    }

    /// <summary>A failure must be visible in every mode — it is the one thing nobody may miss.</summary>
    [Theory]
    [InlineData(WatchMode.Watch)]
    [InlineData(WatchMode.Quiet)]
    [InlineData(WatchMode.Unattended)]
    public void A_failure_is_printed_whatever_the_mode(WatchMode mode)
    {
        var (prompts, progress) = At(mode);

        progress.StartRow(14, 431, Row());
        progress.Failed(Row(), "upload rejected: 413");

        var text = All(prompts);
        Assert.Contains("413", text);
        Assert.Contains("cert.jpg", text);
    }

    [Fact]
    public void A_skipped_row_says_why_it_was_skipped()
    {
        var (prompts, progress) = At(WatchMode.Quiet);

        progress.Skipped(Row(), "verdict is review");

        Assert.Contains("verdict is review", All(prompts));
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~RunProgressTests"`

Expected: FAIL — `'WatchMode' could not be found`.

- [ ] **Step 3: Write `RunProgress.cs`**

```csharp
using MocdDocFix.Domain;

namespace MocdDocFix.Ui;

/// <summary>
/// How closely the operator wants to watch the loop. Asked once when the mode is entered and
/// never again while it runs.
///
/// It changes what is printed and what stops. It never changes what is written to CRM or to the
/// ledger, with one exception that is the whole point of <see cref="Unattended"/>: whether the
/// operator is shown the two copies and asked.
/// </summary>
public enum WatchMode
{
    /// <summary>Every step, then a pause to read it before the next document begins.</summary>
    Watch,

    /// <summary>One line per document. The eye-check is the only interruption.</summary>
    Quiet,

    /// <summary>One line per document, and nothing is asked. The automated checks decide.</summary>
    Unattended
}

/// <summary>
/// The per-document account on screen. All three modes go through here, so they cannot drift
/// into printing different things about the same event.
/// </summary>
public sealed class RunProgress
{
    private readonly IPrompts _prompts;

    public RunProgress(IPrompts prompts, WatchMode mode)
    {
        _prompts = prompts;
        Mode = mode;
    }

    public WatchMode Mode { get; }

    /// <summary>False only in <see cref="WatchMode.Unattended"/>.</summary>
    public bool AsksTheEyeCheck => Mode != WatchMode.Unattended;

    public void StartRow(int number, int total, LedgerRow row)
    {
        _prompts.Blank();
        _prompts.Info($"  [ {number}/{total} ]  {Short(row.DocName, 48)}", Tone.Strong);
        _prompts.Info($"              doc {row.DocId}   {row.DocFileName}", Tone.Muted);
    }

    /// <summary>One step of the six. Printed in Watch only.</summary>
    public void Step(string name, string detail = "")
    {
        if (Mode != WatchMode.Watch) return;

        var dots = new string('.', Math.Max(1, 24 - name.Length));
        _prompts.Info($"      {name} {dots} ok   {detail}".TrimEnd(), Tone.Muted);
    }

    public void Finished(LedgerRow row) =>
        _prompts.Info(Mode == WatchMode.Watch
            ? $"              FINISHED — {RowStates.Corrected}"
            : $"              {row.DocFileName}  FINISHED", Tone.Good);

    public void Skipped(LedgerRow row, string why) =>
        _prompts.Info($"              {row.DocFileName}  skipped — {why}", Tone.Muted);

    /// <summary>Printed in every mode. A failure is the one thing nobody may miss.</summary>
    public void Failed(LedgerRow row, string why) =>
        _prompts.Info($"              {row.DocFileName}  FAILED — {why}", Tone.Danger);

    /// <summary>
    /// The pause after a document in Watch. A bare enter to move on — not the watch question
    /// being asked again, which was settled before the loop started.
    /// </summary>
    public void BetweenRows()
    {
        if (Mode != WatchMode.Watch) return;
        _prompts.ReadLine("  enter for the next one");
    }

    private static string Short(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~RunProgressTests"`

Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(ui): watch, quiet or unattended

The choice changes what is printed and what stops, never what is written
-- with the single exception that is the point of unattended: whether the
operator is shown the two copies and asked.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: `RepairOneRow` — the six steps for one document

**Files:**
- Create: `src/MocdDocFix/Commands/RepairOneRow.cs`
- Test: `tests/MocdDocFix.Tests/RepairOneRowTests.cs`

**Interfaces:**
- Consumes: `LedgerRow`, `RowStates` (Task 2); `ChangeJournal`, `RecordValues`, `ChangeActions` (Task 4); `ICrmWriteClient.UpdateDocumentFileAsync` and `FakeCrmWriteClient.UpdatedFiles` (Task 5); `RunProgress`, `WatchMode` (Task 7). Also the existing `BackupStore.Save` / `SaveNew` / `Folder`, `FileRecordCopier.StyleOf` / `ExtensionFor` / `ApplicationIdFor`, `Verifier`, `FilePathParser`, `IFileOpener`.
- Produces:
  - `sealed record RowOutcome(bool Corrected, string? FailedStep, string? Failure)` with `static RowOutcome Ok()`, `static RowOutcome Declined()`, `static RowOutcome Broke(string step, string why)`, and `bool Failed => Failure is not null`.
  - `sealed class RepairOneRow` with the constructor
    `RepairOneRow(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write, BackupStore backups, ChangeJournal journal, IPrompts prompts, IFileOpener opener, RunProgress progress, string env)`
    and `Task<RowOutcome> RunAsync(LedgerRow row, CancellationToken ct)`.

**What this task does not do:** it never decides whether a row *should* be worked on — the verdict is Task 9's business — and it never writes the ledger to disk. It mutates the `LedgerRow` it is handed and returns.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/RepairOneRowTests.cs`:

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class RepairOneRowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-one-" + Guid.NewGuid());

    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");
    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");
    private static readonly Guid NewFileId = Guid.Parse("b2c3d4e5-0000-0000-0000-000000000001");

    private const string OldPath = @"DigitalServices\docTypeCatalogue\20250509\a3f10000-0000-0000-0000-000000000001.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260915\{NewFileId}.jpg";

    private static readonly byte[] Bytes = { 1, 2, 3, 4, 5 };
    private static readonly string Base64 = Convert.ToBase64String(Bytes);

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();
    private readonly FakePrompts _prompts = new();
    private readonly RecordingOpener _opener = new();
    private BackupStore _backups = null!;
    private ChangeJournal _journal = null!;

    public RepairOneRowTests()
    {
        Directory.CreateDirectory(_dir);
        _backups = new BackupStore(Path.Combine(_dir, "backup"));
        _journal = new ChangeJournal(Path.Combine(_dir, "changes-dev.jsonl"));

        _files.Files[OldPath] = (Base64, "9f86d081");
        _files.UploadResponder = _ => new ApiResponse<FileData>(
            true, null, new FileData(NewFileId, NewPath, "9f86d081", $"{NewFileId}.jpg", "image/jpeg", null), null);

        // The round-trip download of the new copy must come back identical to the backup.
        _files.Files[NewPath] = (Base64, "9f86d081");

        // A portal record: mocd_fileid absent, which is how portal and plugin are told apart.
        _read.RawRecords[$"mocd_documentfiles:{Record}"] =
            $$"""{"mocd_documentfileid":"{{Record}}","mocd_filepath":"{{OldPath.Replace("\\", "\\\\")}}","mocd_hash":"9f86d081","mocd_mediatype":"image/jpeg","mocd_category":"docTypeCatalogue","mocd_name":"cert.jpg"}""";
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class RecordingOpener : IFileOpener
    {
        public List<string> Opened { get; } = new();
        public void Open(string path) => Opened.Add(path);
    }

    private static LedgerRow Row() => new()
    {
        Row = 12,
        DocId = Doc,
        DocName = "Board of Director's Decision",
        DocFileId = Record,
        DocFileName = "cert.jpg",
        CorrectServiceCatalogueId = Correct.ToString(),
        OldFilePath = OldPath,
        OldCategory = "docTypeCatalogue",
        OldHash = "9f86d081",
        OldFileName = "a3f10000-0000-0000-0000-000000000001.jpg",
        OldFileId = string.Empty,
        Verdict = RowVerdicts.Fix,
        WayOfUpload = "portal"
    };

    private RepairOneRow Subject(WatchMode mode = WatchMode.Quiet) =>
        new(_files, _read, _write, _backups, _journal, _prompts, _opener,
            new RunProgress(_prompts, mode), "dev");

    /// <summary>The happy path, end to end: backed up, uploaded, checked, record updated in place.</summary>
    [Fact]
    public async Task A_clean_row_is_corrected_and_the_record_is_updated_in_place()
    {
        _prompts.Answer(ConfirmChoice.Yes);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.Corrected);
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Equal(NewPath, row.NewFilePath);
        Assert.NotEqual(string.Empty, row.BackupPath);
        Assert.Equal(string.Empty, row.Error);

        var update = Assert.Single(_write.UpdatedFiles);
        Assert.Equal(Record, update.RecordId);
        Assert.Equal(NewPath, update.FilePath);
        Assert.Equal(Correct.ToString(), update.Category);
    }

    /// <summary>
    /// The thing that makes this design work: nothing is created and nothing is repointed, so a
    /// document keeps the record it already had.
    /// </summary>
    [Fact]
    public async Task Nothing_is_created_and_nothing_is_repointed()
    {
        _prompts.Answer(ConfirmChoice.Yes);

        await Subject().RunAsync(Row(), CancellationToken.None);

        Assert.Empty(_write.CreatedFiles);
        Assert.Empty(_write.Links);
    }

    /// <summary>
    /// mocd_fileid and mocd_filename are written only where the old record used them. Adding
    /// mocd_fileid to a portal record would change its shape, which is the thing the copier
    /// exists to avoid.
    /// </summary>
    [Fact]
    public async Task A_portal_record_does_not_gain_a_file_id_it_never_had()
    {
        _prompts.Answer(ConfirmChoice.Yes);

        await Subject().RunAsync(Row(), CancellationToken.None);

        var update = Assert.Single(_write.UpdatedFiles);
        Assert.False(update.Wrote("mocd_fileid"));
        Assert.True(update.Wrote("mocd_filepath"));
        Assert.True(update.Wrote("mocd_category"));
        Assert.True(update.Wrote("mocd_hash"));
    }

    [Fact]
    public async Task A_plugin_record_keeps_its_file_id_and_gets_the_new_one()
    {
        _read.RawRecords[$"mocd_documentfiles:{Record}"] =
            $$"""{"mocd_documentfileid":"{{Record}}","mocd_fileid":"11111111-0000-0000-0000-000000000001","mocd_filename":"a3f1.jpg","mocd_mediatype":"image/jpeg","mocd_category":"docTypeCatalogue"}""";

        _prompts.Answer(ConfirmChoice.Yes);
        var row = Row();
        row.OldFileId = "11111111-0000-0000-0000-000000000001";
        row.WayOfUpload = "plugin";

        await Subject().RunAsync(row, CancellationToken.None);

        var update = Assert.Single(_write.UpdatedFiles);
        Assert.Equal(NewFileId.ToString(), update.FileId);
        Assert.True(update.Wrote("mocd_filename"));
    }

    /// <summary>A no to the eye-check must leave CRM exactly as it was.</summary>
    [Fact]
    public async Task Saying_the_copies_do_not_match_writes_nothing_to_crm()
    {
        _prompts.Answer(ConfirmChoice.No);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.False(outcome.Corrected);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Equal(RowState.NotStarted, row.State());
        Assert.NotEqual(string.Empty, row.Notes);
    }

    [Fact]
    public async Task Both_copies_are_opened_before_the_operator_is_asked()
    {
        _prompts.Answer(ConfirmChoice.Yes);

        await Subject().RunAsync(Row(), CancellationToken.None);

        Assert.Equal(2, _opener.Opened.Count);
    }

    /// <summary>Unattended asks nothing and opens nothing; the four checks decide on their own.</summary>
    [Fact]
    public async Task Unattended_corrects_the_row_without_asking_or_opening_anything()
    {
        var row = Row();

        var outcome = await Subject(WatchMode.Unattended).RunAsync(row, CancellationToken.None);

        Assert.True(outcome.Corrected);
        Assert.Empty(_prompts.Questions);
        Assert.Empty(_opener.Opened);
        Assert.Single(_write.UpdatedFiles);
    }

    [Fact]
    public async Task An_upload_the_server_refuses_fails_the_row_and_writes_nothing_to_crm()
    {
        _files.UploadResponder = _ => ApiResponse<FileData>.Fail("413 Payload Too Large");
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal("upload", outcome.FailedStep);
        Assert.Contains("413", outcome.Failure);
        Assert.Empty(_write.UpdatedFiles);
    }

    /// <summary>
    /// The decisive check. A corrupted round trip means the copy on the server is not the file
    /// we backed up, so CRM must not be pointed at it.
    /// </summary>
    [Fact]
    public async Task A_new_copy_that_does_not_come_back_identical_fails_the_row()
    {
        _files.Files[NewPath] = (Convert.ToBase64String(new byte[] { 9, 9, 9 }), "9f86d081");
        _prompts.Answer(ConfirmChoice.Yes);

        var outcome = await Subject().RunAsync(Row(), CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Empty(_write.UpdatedFiles);
    }

    /// <summary>
    /// CRM accepting a PATCH is not the same as CRM having stored it. The record is read back,
    /// and only then is the row called corrected.
    /// </summary>
    [Fact]
    public async Task A_record_that_does_not_hold_the_new_path_afterwards_fails_the_row()
    {
        _prompts.Answer(ConfirmChoice.Yes);

        // The read-back returns the OLD record, unchanged -- a write CRM silently did not take.
        var subject = Subject();
        var row = Row();

        var outcome = await subject.RunAsync(row, CancellationToken.None);

        // The fake read client returns whatever RawRecords holds, which the update did not
        // change, so the read-back still names the old path.
        Assert.True(outcome.Failed);
        Assert.Equal("read the record back", outcome.FailedStep);
        Assert.Equal(RowState.NotStarted, row.State());
    }

    [Fact]
    public async Task The_correction_is_journalled_with_the_values_before_and_after()
    {
        _prompts.Answer(ConfirmChoice.Yes);

        await Subject().RunAsync(Row(), CancellationToken.None);

        var entry = _journal.Read().SingleOrDefault(e => e.Action == ChangeActions.Corrected);
        Assert.NotNull(entry);
        Assert.Equal(Doc, entry!.Doc);
        Assert.Equal(Record, entry.Record);
        Assert.Equal(OldPath, entry.Old!.Path);
        Assert.Equal("docTypeCatalogue", entry.Old.Category);
        Assert.Equal(NewPath, entry.New!.Path);
    }

    [Fact]
    public async Task A_file_the_server_does_not_have_fails_at_the_backup_step()
    {
        _files.Files.Remove(OldPath);
        var row = Row();

        var outcome = await Subject().RunAsync(row, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal("backup", outcome.FailedStep);
        Assert.Equal(string.Empty, row.NewFilePath);
        Assert.Empty(_files.Uploads);
    }
}
```

**Note for the implementer on the two read-back tests.** `FakeCrmReadClient.GetRawRecordAsync` returns whatever is in `RawRecords` and is not affected by `FakeCrmWriteClient`. So with the fakes as they stand, the read-back after an update finds the *old* path — which is exactly the failure `A_record_that_does_not_hold_the_new_path_afterwards_fails_the_row` asserts. For every test that expects a **successful** correction, the read-back must be made to succeed. Do this by wiring the two fakes together in the test constructor, after `_write` is created:

```csharp
        // CRM would hold what was just written; the fakes are independent, so mirror it.
        _write.OnUpdated = (id, attributes) =>
            _read.RawRecords[$"mocd_documentfiles:{id}"] =
                $$"""{"mocd_documentfileid":"{{id}}","mocd_filepath":"{{((string?)attributes["mocd_filepath"])!.Replace("\\", "\\\\")}}"}""";
```

and add to `FakeCrmWriteClient`, beside `OnCreated`:

```csharp
    /// <summary>Called with what an update wrote, so a test can mirror it into the read client.</summary>
    public Action<Guid, IReadOnlyDictionary<string, object?>>? OnUpdated { get; set; }
```

invoking it at the end of `UpdateDocumentFileAsync`. Then `A_record_that_does_not_hold_the_new_path_afterwards_fails_the_row` sets `_write.OnUpdated = null;` as its first line to restore the un-mirrored behaviour.

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~RepairOneRowTests"`

Expected: FAIL — `'RepairOneRow' could not be found`.

- [ ] **Step 3: Write `RepairOneRow.cs`**

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;
using MocdDocFix.Verification;
using System.Text.Json;

namespace MocdDocFix.Commands;

/// <param name="FailedStep">Which of the six broke, for the error log and the ledger.</param>
public sealed record RowOutcome(bool Corrected, string? FailedStep, string? Failure)
{
    public static RowOutcome Ok() => new(true, null, null);

    /// <summary>The operator said the two copies do not match. Not a failure — a decision.</summary>
    public static RowOutcome Declined() => new(false, null, null);

    public static RowOutcome Broke(string step, string why) => new(false, step, why);

    public bool Failed => Failure is not null;
}

/// <summary>
/// One document, corrected. Download and back up, upload under the right catalogue, check the
/// copy four ways, show the operator, update the record the document already points at, and
/// read it back.
///
/// The record is updated in place: nothing is created and nothing is repointed. That is what
/// makes the change journal and the ledger the only route back, so every field this overwrites
/// is journalled with the value it had.
///
/// It mutates the row it is given and returns. Writing the ledger to disk is the loop's job.
/// </summary>
public sealed class RepairOneRow
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly ChangeJournal _journal;
    private readonly IPrompts _prompts;
    private readonly IFileOpener _opener;
    private readonly RunProgress _progress;
    private readonly string _env;

    public RepairOneRow(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write,
        BackupStore backups, ChangeJournal journal, IPrompts prompts, IFileOpener opener,
        RunProgress progress, string env)
    {
        _files = files;
        _read = read;
        _write = write;
        _backups = backups;
        _journal = journal;
        _prompts = prompts;
        _opener = opener;
        _progress = progress;
        _env = env;
    }

    public async Task<RowOutcome> RunAsync(LedgerRow row, CancellationToken ct)
    {
        if (!Guid.TryParse(row.CorrectServiceCatalogueId, out var correct))
            return RowOutcome.Broke("check", "No correct service catalogue on this row.");

        // ---- 1. back up ----

        var download = await _files.DownloadAsync(row.OldFilePath, ct);
        if (!download.Success || download.Data?.File is null)
            return RowOutcome.Broke("backup", $"The old file could not be downloaded: {download.Message}");

        var bytes = Convert.FromBase64String(download.Data.File);
        var extension = Path.GetExtension(row.OldFilePath);

        var oldRecordJson = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocFileId, ct);

        var saved = _backups.Save(row.DocId, row.DocFileId, extension, bytes, row.DocFileName);
        _backups.SaveCrmImage(row.DocFileId, row.DocId, row.OldFilePath, _env,
            await _read.GetRawRecordAsync("mocd_documents", row.DocId, ct) ?? "",
            oldRecordJson ?? "",
            await _read.GetDocumentAnnotationsAsync(row.DocId, ct) ?? "");

        row.BackupPath = _backups.Folder(row.DocId, row.DocFileName).Root;
        _progress.Step("backing up", row.BackupPath);

        // ---- 2. upload, in the shape the old record was created in ----

        var style = FileRecordCopier.StyleOf(oldRecordJson);

        var upload = await _files.UploadAsync(new UploadRequest(
            Category: correct.ToString(),
            FileName: row.DocFileName.Length > 0 ? row.DocFileName : $"{row.DocFileId}{extension}",
            File: Convert.ToBase64String(bytes),
            MediaType: ReadString(oldRecordJson, "mocd_mediatype") ?? "application/octet-stream",
            Extension: FileRecordCopier.ExtensionFor(oldRecordJson, extension),
            ApplicationId: FileRecordCopier.ApplicationIdFor(style, row.DocId)), ct);

        if (!upload.Success || upload.Data is null)
            return RowOutcome.Broke("upload", upload.Message ?? "the file server gave no reason");

        var newFile = upload.Data;
        _progress.Step("uploading", newFile.FilePath);

        // ---- 3. the four checks ----

        // The vendor's id for the OLD file is the stem of its path, not the CRM record's key --
        // they are equal only on portal-created records.
        var oldVendorId = Guid.TryParse(FilePathParser.Parse(row.OldFilePath).FileStem, out var stem)
            ? stem : Guid.Empty;

        var checks = new List<CheckResult>
        {
            Verifier.UploadHashMatches(row.OldHash, newFile.Hash),
            Verifier.IsGenuinelyNew(row.OldFilePath, newFile.FilePath, oldVendorId, newFile.FileId),
            Verifier.PathIsFixed(FilePathParser.Parse(newFile.FilePath), correct, newFile.FileId)
        };

        if (checks.All(c => c.Passed))
        {
            var back = await _files.DownloadAsync(newFile.FilePath, ct);
            checks.Add(!back.Success || back.Data?.File is null
                ? new CheckResult("round-trip", false,
                    $"The new file could not be downloaded back: {back.Message}", false)
                : Verifier.RoundTrip(bytes, Convert.FromBase64String(back.Data.File)));
        }

        var report = new VerificationReport(checks);
        if (!report.AllPassed)
            return RowOutcome.Broke("verify",
                string.Join(" | ", report.Failures.Select(f => $"{f.Name}: {f.Detail}")));

        _progress.Step("checked", string.Join(", ", checks.Select(c => c.Name)));

        // ---- 4. the operator's own eyes, unless nobody is watching ----

        var staged = _backups.SaveNew(row.DocId, newFile.FileId, extension, bytes);

        if (_progress.AsksTheEyeCheck)
        {
            _opener.Open(saved.LocalPath);
            _opener.Open(staged.LocalPath);

            if (_prompts.Confirm("Do these two files look the same?") != ConfirmChoice.Yes)
            {
                row.Notes = Note(row.Notes,
                    $"not corrected {DateTimeOffset.Now:yyyy-MM-dd HH:mm} — you said the copies " +
                    $"did not match; the uploaded copy is at {newFile.FilePath}");
                return RowOutcome.Declined();
            }
        }

        // ---- 5. update the record the document already points at ----

        var before = new RecordValues(row.OldFilePath, row.OldCategory, row.OldHash,
            Blank(row.OldFileName), Blank(row.OldFileId));

        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mocd_filepath"] = newFile.FilePath,
            ["mocd_category"] = correct.ToString(),
            ["mocd_hash"] = newFile.Hash
        };

        // Only where the old record used them. Adding either to a portal record would change
        // its shape, which is the one thing a correction must not do.
        if (ReadString(oldRecordJson, "mocd_fileid") is not null)
            attributes["mocd_fileid"] = newFile.FileId.ToString();

        if (ReadString(oldRecordJson, "mocd_filename") is not null)
            attributes["mocd_filename"] = LeafOf(newFile.FilePath) ?? newFile.FileName;

        try
        {
            await _write.UpdateDocumentFileAsync(row.DocFileId, attributes, ct);
        }
        catch (Exception problem)
        {
            return RowOutcome.Broke("record update", problem.Message);
        }

        _progress.Step("updating the record", row.DocFileId.ToString());

        // ---- 6. read it back. CRM accepting a PATCH is not CRM having stored it ----

        var after = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocFileId, ct);
        var stored = ReadString(after, "mocd_filepath");

        if (!FilePaths.Same(stored, newFile.FilePath))
            return RowOutcome.Broke("read the record back",
                $"mocd_filepath on {row.DocFileId} is '{stored ?? "null"}', not {newFile.FilePath}");

        _progress.Step("reading it back");

        // ---- done ----

        _journal.Append(new ChangeEntry(
            DateTimeOffset.UtcNow, row.DocId, row.DocFileId, ChangeActions.Corrected,
            before,
            new RecordValues(newFile.FilePath, correct.ToString(), newFile.Hash,
                attributes.TryGetValue("mocd_filename", out var n) ? n as string : null,
                attributes.TryGetValue("mocd_fileid", out var f) ? f as string : null)));

        row.NewFilePath = newFile.FilePath;
        row.FinalState = RowStates.Text(RowState.Corrected);
        row.Error = string.Empty;

        return RowOutcome.Ok();
    }

    /// <summary>Appends to the notes cell without discarding what is already in it.</summary>
    private static string Note(string existing, string addition) =>
        existing.Length == 0 ? addition : $"{existing}; {addition}";

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    private static string? LeafOf(string path)
    {
        var leaf = path.Replace('/', '\\').Split('\\').LastOrDefault();
        return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
    }

    private static string? ReadString(string? json, string attribute)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(attribute, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~RepairOneRowTests"`

Expected: PASS, 12 tests.

If `A_record_that_does_not_hold_the_new_path_afterwards_fails_the_row` fails because the mirroring in the constructor makes the read-back succeed, add `_write.OnUpdated = null;` as that test's first line — see the note under Step 1.

- [ ] **Step 5: Build clean**

Run: `dotnet build -c Release 2>&1 | grep -E "error|warning"`

Expected: nothing.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(repair): the six steps for one document

Back up, upload under the right catalogue, check four ways, show the
operator, update the record the document already points at, read it back.
Nothing is created and nothing is repointed, so every field the update
overwrites is journalled with the value it had.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: `RepairRun` — the loop, the tallies and the halting

**Files:**
- Create: `src/MocdDocFix/Commands/RepairRun.cs`
- Test: `tests/MocdDocFix.Tests/RepairRunTests.cs`

**Interfaces:**
- Consumes: `LedgerRow`, `RowVerdict`, `RowVerdicts`, `RowState`, `RowStates` (Task 2); `LedgerStore` (Task 3); `ErrorLog` (Task 4); `RunProgress` (Task 7); `RepairOneRow`, `RowOutcome` (Task 8).
- Produces:
  - `sealed record SkipTally(string Why, int Count)`
  - `sealed record RepairSummary(int Corrected, int Declined, int Failed, bool Stopped, IReadOnlyList<SkipTally> Skips, IReadOnlyList<string> Unrecognised)`
  - `sealed class RepairRun` with `RepairRun(LedgerStore ledger, RepairOneRow one, RunProgress progress, IPrompts prompts, ErrorLog errors)` and `Task<RepairSummary> RunAsync(IReadOnlyList<LedgerRow> working, IReadOnlyList<LedgerRow> wholeLedger, CancellationToken ct)`.

**Why two lists.** The operator may choose to work on one document rather than the whole file (Task 13), and the loop rewrites the ledger after every row. Writing only the rows it is working on would truncate a four-hundred-row ledger to one. So `working` is what the loop iterates and `wholeLedger` is what gets written; `LedgerRow` is a mutable class, so the rows in `working` are the same objects as those inside `wholeLedger` and mutating them is enough. When the whole file is being worked on, the caller passes the same list twice.

**Design note for the implementer.** `RepairOneRow` is a concrete class, not an interface, and these tests need to control what it returns. Rather than introducing an interface for one call site, the tests build a **real** `RepairOneRow` over the existing fakes and steer it by how those fakes are configured — the same way `RepairOneRowTests` does. Copy that test class's constructor set-up verbatim as the starting point.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/RepairRunTests.cs`:

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class RepairRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-run-" + Guid.NewGuid());

    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();
    private readonly FakePrompts _prompts = new();
    private readonly NullOpener _opener = new();
    private readonly BackupStore _backups;
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;
    private readonly ErrorLog _errors;

    public RepairRunTests()
    {
        Directory.CreateDirectory(_dir);
        _backups = new BackupStore(Path.Combine(_dir, "backup"));
        _journal = new ChangeJournal(Path.Combine(_dir, "changes-dev.jsonl"));
        _ledger = new LedgerStore(Path.Combine(_dir, "repair-dev.csv"));
        _errors = new ErrorLog(Path.Combine(_dir, "errors-dev.txt"));

        // CRM would hold what was just written; the fakes are independent, so mirror it.
        _write.OnUpdated = (id, attributes) =>
            _read.RawRecords[$"mocd_documentfiles:{id}"] =
                $$"""{"mocd_documentfileid":"{{id}}","mocd_filepath":"{{((string?)attributes["mocd_filepath"])!.Replace("\\", "\\\\")}}"}""";
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class NullOpener : IFileOpener { public void Open(string path) { } }

    /// <summary>Gives a row a real old file on the server and a record CRM can answer about.</summary>
    private LedgerRow Fixable(int number)
    {
        var doc = Guid.Parse($"a3f1b2c4-0000-0000-0000-{number:D12}");
        var record = Guid.Parse($"2a1c51a3-0000-0000-0000-{number:D12}");
        var newId = Guid.Parse($"b2c3d4e5-0000-0000-0000-{number:D12}");
        var oldPath = $@"DigitalServices\docTypeCatalogue\20250509\a3f10000-0000-0000-0000-{number:D12}.jpg";
        var newPath = $@"DigitalServices\{Correct}\20260915\{newId}.jpg";
        var bytes = new byte[] { 1, 2, 3, (byte)number };
        var base64 = Convert.ToBase64String(bytes);

        _files.Files[oldPath] = (base64, "hash" + number);
        _files.Files[newPath] = (base64, "hash" + number);
        _read.RawRecords[$"mocd_documentfiles:{record}"] =
            $$"""{"mocd_documentfileid":"{{record}}","mocd_mediatype":"image/jpeg"}""";

        return new LedgerRow
        {
            Row = number,
            DocId = doc,
            DocName = "Document " + number,
            DocFileId = record,
            DocFileName = $"cert{number}.jpg",
            CorrectServiceCatalogueId = Correct.ToString(),
            OldFilePath = oldPath,
            OldHash = "hash" + number,
            OldCategory = "docTypeCatalogue",
            Verdict = RowVerdicts.Fix,
            WayOfUpload = "portal"
        };
    }

    /// <summary>The upload responder has to answer differently per row, keyed on the bytes.</summary>
    private void UploadsSucceed()
    {
        _files.UploadResponder = request =>
        {
            var number = Convert.FromBase64String(request.File)[3];
            var newId = Guid.Parse($"b2c3d4e5-0000-0000-0000-{number:D12}");
            var newPath = $@"DigitalServices\{Correct}\20260915\{newId}.jpg";
            return new ApiResponse<FileData>(true, null,
                new FileData(newId, newPath, request.File is null ? null : "hash" + number,
                    $"{newId}.jpg", "image/jpeg", null), null);
        };
    }

    private RepairRun Subject(WatchMode mode = WatchMode.Unattended)
    {
        var progress = new RunProgress(_prompts, mode);
        return new RepairRun(_ledger,
            new RepairOneRow(_files, _read, _write, _backups, _journal, _prompts, _opener, progress, "dev"),
            progress, _prompts, _errors);
    }

    [Fact]
    public async Task Every_fix_row_is_corrected_and_counted()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2), Fixable(3) };

        var summary = await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.Equal(3, summary.Corrected);
        Assert.Equal(0, summary.Failed);
        Assert.False(summary.Stopped);
        Assert.All(rows, r => Assert.Equal(RowState.Corrected, r.State()));
    }

    /// <summary>
    /// The ledger is rewritten as each row finishes, not once at the end. An interruption must
    /// lose at most the row in flight.
    /// </summary>
    [Fact]
    public async Task The_ledger_on_disk_is_current_after_every_row()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2) };

        await Subject().RunAsync(rows, rows, CancellationToken.None);

        var onDisk = _ledger.Read();
        Assert.Equal(2, onDisk.Count);
        Assert.All(onDisk, r => Assert.Equal(RowState.Corrected, r.State()));
        Assert.All(onDisk, r => Assert.NotEqual(string.Empty, r.NewFilePath));
    }

    [Theory]
    [InlineData(RowVerdicts.Review)]
    [InlineData(RowVerdicts.Skip)]
    [InlineData(RowVerdicts.Ignore)]
    [InlineData(RowVerdicts.Redo)]
    public async Task Only_fix_rows_are_touched(string verdict)
    {
        UploadsSucceed();
        var row = Fixable(1);
        row.Verdict = verdict;

        var summary = await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Corrected);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Empty(_files.Uploads);
        Assert.Equal(RowState.NotStarted, row.State());
    }

    /// <summary>
    /// The safety rule made visible. A typo is not acted on, and it is not silent either — it is
    /// named at the end so the operator finds out before assuming the row was done.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_verdict_is_left_alone_and_reported()
    {
        UploadsSucceed();
        var row = Fixable(1);
        row.Verdict = "fixx";

        var summary = await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Corrected);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Contains(summary.Unrecognised, u => u.Contains("fixx"));
        Assert.Contains(summary.Unrecognised, u => u.Contains("1"));
    }

    /// <summary>A row already done is not done again, however its verdict still reads.</summary>
    [Fact]
    public async Task A_row_already_corrected_is_not_uploaded_a_second_time()
    {
        UploadsSucceed();
        var row = Fixable(1);
        row.FinalState = RowStates.Text(RowState.Corrected);

        var summary = await Subject().RunAsync(new[] { row }, new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Corrected);
        Assert.Empty(_files.Uploads);
        Assert.Contains(summary.Skips, s => s.Why.Contains("already"));
    }

    [Fact]
    public async Task A_failure_marks_the_row_writes_the_error_log_and_asks()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2) };
        _files.Files.Remove(rows[0].OldFilePath);          // row 1 cannot be backed up
        _prompts.YesNoQueue = new Queue<bool>(new[] { true });   // carry on

        var summary = await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Corrected);
        Assert.Equal(RowState.Failed, rows[0].State());
        Assert.NotEqual(string.Empty, rows[0].Error);
        Assert.True(File.Exists(_errors.Path));
        Assert.Contains("cert1.jpg", File.ReadAllText(_errors.Path));
        Assert.Contains(_prompts.Questions, q => q.Contains("carry on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Saying stop ends the run there, with everything before it recorded.</summary>
    [Fact]
    public async Task Answering_stop_ends_the_run_and_leaves_the_earlier_rows_intact()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1), Fixable(2), Fixable(3) };
        _files.Files.Remove(rows[1].OldFilePath);          // row 2 breaks
        _prompts.YesNoQueue = new Queue<bool>(new[] { false });  // stop

        var summary = await Subject().RunAsync(rows, rows, CancellationToken.None);

        Assert.True(summary.Stopped);
        Assert.Equal(1, summary.Corrected);
        Assert.Equal(RowState.Corrected, rows[0].State());
        Assert.Equal(RowState.Failed, rows[1].State());
        Assert.Equal(RowState.NotStarted, rows[2].State());

        // And the ledger says the same, because it was written as each row finished.
        Assert.Equal(RowState.Corrected, _ledger.Read()[0].State());
    }

    /// <summary>Unattended is unattended, not unstoppable.</summary>
    [Fact]
    public async Task Unattended_still_halts_on_a_failure_and_asks()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1) };
        _files.Files.Remove(rows[0].OldFilePath);
        _prompts.YesNoQueue = new Queue<bool>(new[] { false });

        var summary = await Subject(WatchMode.Unattended).RunAsync(rows, rows, CancellationToken.None);

        Assert.True(summary.Stopped);
        Assert.NotEmpty(_prompts.Questions);
    }

    /// <summary>
    /// The choice is asked once, before the loop. Whichever was chosen, the same input must
    /// produce the same ledger — the mode changes what is printed, never what is written.
    /// </summary>
    [Fact]
    public async Task Quiet_and_unattended_write_the_same_ledger_for_the_same_input()
    {
        UploadsSucceed();
        var unattended = new[] { Fixable(1), Fixable(2) };
        await Subject(WatchMode.Unattended).RunAsync(unattended, unattended, CancellationToken.None);
        var first = _ledger.Read().Select(r => (r.Row, r.FinalState, r.NewFilePath)).ToList();

        // A second run of the same input under Quiet, with the operator agreeing each time.
        File.Delete(_ledger.Path);
        _files.Uploads.Clear();
        _write.UpdatedFiles.Clear();
        _prompts.Answer(ConfirmChoice.Yes, ConfirmChoice.Yes);
        var quiet = new[] { Fixable(1), Fixable(2) };
        await Subject(WatchMode.Quiet).RunAsync(quiet, quiet, CancellationToken.None);

        Assert.Equal(first, _ledger.Read().Select(r => (r.Row, r.FinalState, r.NewFilePath)).ToList());
    }

    [Fact]
    public async Task A_declined_eye_check_is_counted_apart_from_a_failure()
    {
        UploadsSucceed();
        var rows = new[] { Fixable(1) };
        _prompts.Answer(ConfirmChoice.No);

        var summary = await Subject(WatchMode.Quiet).RunAsync(rows, rows, CancellationToken.None);

        Assert.Equal(1, summary.Declined);
        Assert.Equal(0, summary.Failed);
        Assert.False(summary.Stopped);
        Assert.Equal(RowState.NotStarted, rows[0].State());
    }

    /// <summary>
    /// Working on one document must not truncate the ledger. The loop rewrites the file after
    /// every row, so if it wrote only what it was iterating, choosing one document out of four
    /// hundred would destroy the other three hundred and ninety-nine — and with them the only
    /// record of which old files are still waiting to be deleted.
    /// </summary>
    [Fact]
    public async Task Working_on_one_document_leaves_every_other_row_in_the_ledger()
    {
        UploadsSucceed();
        var all = new[] { Fixable(1), Fixable(2), Fixable(3) };
        var justOne = new[] { all[1] };

        var summary = await Subject().RunAsync(justOne, all, CancellationToken.None);

        Assert.Equal(1, summary.Corrected);

        var onDisk = _ledger.Read();
        Assert.Equal(3, onDisk.Count);
        Assert.Equal(RowState.NotStarted, onDisk[0].State());
        Assert.Equal(RowState.Corrected, onDisk[1].State());
        Assert.Equal(RowState.NotStarted, onDisk[2].State());
    }

    [Fact]
    public async Task An_empty_ledger_is_not_an_error()
    {
        var summary = await Subject().RunAsync(Array.Empty<LedgerRow>(), Array.Empty<LedgerRow>(), CancellationToken.None);

        Assert.Equal(0, summary.Corrected);
        Assert.False(summary.Stopped);
        Assert.Empty(_prompts.Questions);
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~RepairRunTests"`

Expected: FAIL — `'RepairRun' could not be found`.

- [ ] **Step 3: Write `RepairRun.cs`**

```csharp
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="Why">Said as it should appear on screen, without a count.</param>
public sealed record SkipTally(string Why, int Count);

/// <param name="Declined">
/// Rows where the operator said the two copies did not match. Counted apart from failures on
/// purpose: nothing went wrong, a person decided.
/// </param>
/// <param name="Unrecognised">
/// Verdict cells nobody recognises, with their row numbers. Named at the end so a typo is
/// found before the operator assumes the row was done.
/// </param>
public sealed record RepairSummary(
    int Corrected, int Declined, int Failed, bool Stopped,
    IReadOnlyList<SkipTally> Skips, IReadOnlyList<string> Unrecognised);

/// <summary>
/// The loop. It decides which rows are worked on, keeps the ledger on disk current, and stops
/// to ask when something breaks. The work itself is <see cref="RepairOneRow"/>'s.
///
/// The ledger is rewritten the moment a row finishes rather than once at the end, so an
/// interruption — a crash, a Ctrl-C, a stopped run — loses at most the row in flight.
/// </summary>
public sealed class RepairRun
{
    private readonly LedgerStore _ledger;
    private readonly RepairOneRow _one;
    private readonly RunProgress _progress;
    private readonly IPrompts _prompts;
    private readonly ErrorLog _errors;

    public RepairRun(LedgerStore ledger, RepairOneRow one, RunProgress progress,
        IPrompts prompts, ErrorLog errors)
    {
        _ledger = ledger;
        _one = one;
        _progress = progress;
        _prompts = prompts;
        _errors = errors;
    }

    /// <param name="working">The rows the loop acts on — the whole ledger, or one document.</param>
    /// <param name="wholeLedger">
    /// What gets written back. Never <paramref name="working"/> when the operator has narrowed to
    /// one document: the ledger is rewritten after every row, and writing only the working set
    /// would truncate four hundred rows to one. LedgerRow is a mutable class, so the rows in
    /// <paramref name="working"/> are the same objects as those inside this list.
    /// </param>
    public async Task<RepairSummary> RunAsync(IReadOnlyList<LedgerRow> working,
        IReadOnlyList<LedgerRow> wholeLedger, CancellationToken ct)
    {
        int corrected = 0, declined = 0, failed = 0;
        var stopped = false;
        var unrecognised = new List<string>();
        var skips = new Dictionary<string, int>(StringComparer.Ordinal);

        void Skip(LedgerRow row, string why)
        {
            skips[why] = skips.TryGetValue(why, out var n) ? n + 1 : 1;
            _progress.Skipped(row, why);
        }

        for (var i = 0; i < working.Count && !stopped; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = working[i];

            // An unrecognised verdict means "leave this row alone" -- but loudly, at the end.
            if (row.Verdict2() == RowVerdict.Unrecognised)
            {
                unrecognised.Add($"row {row.Row}: '{row.Verdict}'");
                Skip(row, "its verdict is not one this tool understands");
                continue;
            }

            if (row.Verdict2() != RowVerdict.Fix)
            {
                Skip(row, $"its verdict is {RowVerdicts.Text(row.Verdict2())}");
                continue;
            }

            if (row.State() is RowState.Corrected or RowState.Deleted)
            {
                Skip(row, "it was already corrected in an earlier run");
                continue;
            }

            _progress.StartRow(i + 1, working.Count, row);

            var outcome = await _one.RunAsync(row, ct);

            if (outcome.Corrected)
            {
                corrected++;
                _progress.Finished(row);
            }
            else if (outcome.Failed)
            {
                failed++;

                row.FinalState = RowStates.Text(RowState.Failed);
                row.Error = Short(outcome.Failure!);

                _errors.Append(i + 1, working.Count, row, outcome.FailedStep!, outcome.Failure!);
                _progress.Failed(row, row.Error);
            }
            else
            {
                declined++;
            }

            // Written before the question, so a stop here still leaves the ledger current.
            _ledger.Write(wholeLedger);

            if (outcome.Failed && !KeepGoing()) { stopped = true; break; }

            _progress.BetweenRows();
        }

        return new RepairSummary(corrected, declined, failed, stopped,
            skips.Select(s => new SkipTally(s.Key, s.Value)).ToList(), unrecognised);
    }

    /// <summary>
    /// Asked after every failure, in every watch mode. Unattended is unattended, not
    /// unstoppable: a run that ploughs on through an unexplained error is how one bad
    /// assumption reaches four hundred documents.
    /// </summary>
    private bool KeepGoing()
    {
        _prompts.Blank();
        _prompts.Say("That document was not corrected. Its row says failed, and the full detail " +
                     "is in the error log.", Tone.Warn);

        return _prompts.YesNo("  Carry on with the next document?", defaultYes: false, Tone.Warn);
    }

    /// <summary>The ledger's error column is read in a spreadsheet cell; the log has it in full.</summary>
    private static string Short(string detail) =>
        detail.Length <= 200 ? detail : detail[..197] + "…";
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~RepairRunTests"`

Expected: PASS, 15 tests.

- [ ] **Step 5: Build clean and run everything**

Run: `dotnet build -c Release 2>&1 | grep -E "error|warning" ; dotnet test tests/MocdDocFix.Tests 2>&1 | tail -5`

Expected: nothing from the first, all passing from the second.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(repair): the loop, the tallies and the halting

The ledger is rewritten as each row finishes, so an interruption loses at
most the row in flight. An unrecognised verdict is left alone and named at
the end, and a failure stops and asks in every watch mode.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: `RedoRun` — put the record back the way it was

**Files:**
- Create: `src/MocdDocFix/Commands/RedoRun.cs`
- Test: `tests/MocdDocFix.Tests/RedoRunTests.cs`

**Interfaces:**
- Consumes: `LedgerRow`, `RowVerdict`, `RowVerdicts`, `RowState`, `RowStates` (Task 2); `LedgerStore` (Task 3); `ChangeJournal`, `RecordValues`, `ChangeActions` (Task 4); `ICrmWriteClient.UpdateDocumentFileAsync` (Task 5). Also `BackupStore.Folder(...)` and the `crm.json` the backup step writes.
- Produces:
  - `sealed record RedoSummary(int Reverted, int Refused, IReadOnlyList<string> Reasons)`
  - `sealed class RedoRun` with `RedoRun(IFileServiceClient files, ICrmWriteClient write, BackupStore backups, ChangeJournal journal, LedgerStore ledger, IPrompts prompts)` and `Task<RedoSummary> RunAsync(IReadOnlyList<LedgerRow> rows, CancellationToken ct)`.

**What the snapshot is.** The backup step writes `crm.json` in the document's `old\` folder — a `CrmImage` whose `DocumentFileRaw` property holds the complete old `mocd_documentfile` as raw JSON. That is the authority for a revert. The ledger's columns 16–19 are compared against it and any difference is reported, but **the snapshot is what gets written**: it is the record as it actually was, so it cannot be wrong.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/RedoRunTests.cs`:

```csharp
using System.Text.Json;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class RedoRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-redo-" + Guid.NewGuid());

    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");
    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

    private const string OldPath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260915\b2c3.jpg";

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmWriteClient _write = new();
    private readonly FakePrompts _prompts = new();
    private readonly BackupStore _backups;
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;

    public RedoRunTests()
    {
        Directory.CreateDirectory(_dir);
        _backups = new BackupStore(Path.Combine(_dir, "backup"));
        _journal = new ChangeJournal(Path.Combine(_dir, "changes-dev.jsonl"));
        _ledger = new LedgerStore(Path.Combine(_dir, "repair-dev.csv"));

        // The old file is still on the server -- which is what makes a revert possible at all.
        _files.Files[OldPath] = (Convert.ToBase64String(new byte[] { 1, 2, 3 }), "9f86d081");
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>Writes the crm.json the backup step would have written, holding the old record.</summary>
    private void Snapshot(string category = "docTypeCatalogue", string? fileId = null,
        string? fileName = "a3f1.jpg", string hash = "9f86d081")
    {
        var record = new Dictionary<string, object?>
        {
            ["mocd_documentfileid"] = Record.ToString(),
            ["mocd_filepath"] = OldPath,
            ["mocd_category"] = category,
            ["mocd_hash"] = hash
        };
        if (fileId is not null) record["mocd_fileid"] = fileId;
        if (fileName is not null) record["mocd_filename"] = fileName;

        _backups.SaveCrmImage(Record, Doc, OldPath, "dev",
            documentRaw: "{}",
            documentFileRaw: JsonSerializer.Serialize(record),
            annotationsRaw: """{"value":[]}""");
    }

    private LedgerRow Row() => new()
    {
        Row = 12,
        DocId = Doc,
        DocName = "Board of Director's Decision",
        DocFileId = Record,
        DocFileName = "cert.jpg",
        BackupPath = _backups.Folder(Doc, "cert.jpg").Root,
        OldFilePath = OldPath,
        NewFilePath = NewPath,
        OldCategory = "docTypeCatalogue",
        OldHash = "9f86d081",
        OldFileName = "a3f1.jpg",
        OldFileId = string.Empty,
        Verdict = RowVerdicts.Redo,
        FinalState = RowStates.Text(RowState.Corrected),
        WayOfUpload = "portal"
    };

    private RedoRun Subject() => new(_files, _write, _backups, _journal, _ledger, _prompts);

    [Fact]
    public async Task The_record_goes_back_to_the_values_the_snapshot_holds()
    {
        Snapshot();
        var row = Row();

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Reverted);
        var update = Assert.Single(_write.UpdatedFiles);
        Assert.Equal(Record, update.RecordId);
        Assert.Equal(OldPath, update.FilePath);
        Assert.Equal("docTypeCatalogue", update.Category);
        Assert.Equal("9f86d081", update.Hash);
    }

    /// <summary>Back to the start: queued to be corrected again, with the history kept.</summary>
    [Fact]
    public async Task The_row_returns_to_fix_with_a_blank_final_state_and_a_note()
    {
        Snapshot();
        var row = Row();

        await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(RowVerdict.Fix, row.Verdict2());
        Assert.Equal(RowState.NotStarted, row.State());
        Assert.Equal(string.Empty, row.NewFilePath);
        Assert.Contains("reverted", row.Notes, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The abandoned copy is not deleted and not forgotten: nothing in CRM points at it, so its
    /// path is the only way anyone will ever find it again.
    /// </summary>
    [Fact]
    public async Task The_abandoned_copy_moves_into_superseded_paths_and_is_not_deleted()
    {
        Snapshot();
        var row = Row();

        await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Contains(NewPath, row.SupersededPaths);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Reverting_twice_keeps_both_abandoned_paths()
    {
        Snapshot();
        var row = Row();
        row.SupersededPaths = @"DigitalServices\x\20260901\first.jpg";

        await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Contains("first.jpg", row.SupersededPaths);
        Assert.Contains(NewPath, row.SupersededPaths);
    }

    /// <summary>
    /// The guard. Once the old file is off the server there is nothing to go back to, and
    /// repointing CRM at a path that no longer exists would break the document for good.
    /// </summary>
    [Fact]
    public async Task A_row_whose_old_file_is_gone_from_the_server_is_refused()
    {
        Snapshot();
        _files.Files.Remove(OldPath);
        var row = Row();

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Reverted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Contains(summary.Reasons, r => r.Contains("no longer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_row_whose_old_files_are_already_deleted_is_refused_without_asking_the_server()
    {
        Snapshot();
        var row = Row();
        row.FinalState = RowStates.Text(RowState.Deleted);

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Refused);
        Assert.Empty(_write.UpdatedFiles);
    }

    [Theory]
    [InlineData(RowVerdicts.Fix)]
    [InlineData(RowVerdicts.Review)]
    [InlineData(RowVerdicts.Skip)]
    [InlineData(RowVerdicts.Ignore)]
    public async Task Only_redo_rows_are_acted_on(string verdict)
    {
        Snapshot();
        var row = Row();
        row.Verdict = verdict;

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Reverted);
        Assert.Equal(0, summary.Refused);
        Assert.Empty(_write.UpdatedFiles);
    }

    /// <summary>
    /// The snapshot is the record as it actually was, so it wins. The operator is told their
    /// edit was not used rather than left to assume it was.
    /// </summary>
    [Fact]
    public async Task Where_the_ledger_and_the_snapshot_disagree_the_snapshot_is_written_and_it_is_said()
    {
        Snapshot(category: "docTypeCatalogue");
        var row = Row();
        row.OldCategory = "something-the-operator-typed";

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Reverted);
        Assert.Equal("docTypeCatalogue", Assert.Single(_write.UpdatedFiles).Category);
        Assert.Contains(summary.Reasons, r =>
            r.Contains("old category", StringComparison.OrdinalIgnoreCase) &&
            r.Contains("something-the-operator-typed"));
    }

    /// <summary>A revert writes back exactly the fields a correction overwrote — no more.</summary>
    [Fact]
    public async Task A_portal_record_gets_back_no_file_id_because_it_never_had_one()
    {
        Snapshot(fileId: null);
        var row = Row();

        await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.False(Assert.Single(_write.UpdatedFiles).Wrote("mocd_fileid"));
    }

    [Fact]
    public async Task A_plugin_record_gets_its_own_file_id_back()
    {
        Snapshot(fileId: "11111111-0000-0000-0000-000000000001");
        var row = Row();
        row.OldFileId = "11111111-0000-0000-0000-000000000001";

        await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal("11111111-0000-0000-0000-000000000001",
            Assert.Single(_write.UpdatedFiles).FileId);
    }

    [Fact]
    public async Task A_row_with_no_snapshot_on_disk_is_refused_rather_than_guessed_at()
    {
        var row = Row();   // no Snapshot() call

        var summary = await Subject().RunAsync(new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Refused);
        Assert.Empty(_write.UpdatedFiles);
        Assert.Contains(summary.Reasons, r => r.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_revert_is_journalled()
    {
        Snapshot();

        await Subject().RunAsync(new[] { Row() }, CancellationToken.None);

        var entry = Assert.Single(_journal.Read());
        Assert.Equal(ChangeActions.Reverted, entry.Action);
        Assert.Equal(NewPath, entry.Old!.Path);
        Assert.Equal(OldPath, entry.New!.Path);
    }

    [Fact]
    public async Task The_ledger_is_written_after_each_row()
    {
        Snapshot();

        await Subject().RunAsync(new[] { Row() }, CancellationToken.None);

        Assert.Equal(RowVerdict.Fix, _ledger.Read()[0].Verdict2());
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~RedoRunTests"`

Expected: FAIL — `'RedoRun' could not be found`.

- [ ] **Step 3: Write `RedoRun.cs`**

```csharp
using System.Text.Json;
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="Reasons">One line per refusal, and one per disagreement that was overridden.</param>
public sealed record RedoSummary(int Reverted, int Refused, IReadOnlyList<string> Reasons);

/// <summary>
/// Puts a corrected record back the way it was.
///
/// This is the route back that CRM used to hold by itself: before this design a correction
/// created a new record and repointed, so the old record survived untouched. Now the old record
/// is overwritten, and the only copies of what it held are the ledger and the crm.json the
/// backup step wrote. The snapshot wins where they disagree — it is the record as it actually
/// was, so it cannot be wrong — and the disagreement is reported so the operator knows their
/// edit was not used.
///
/// It refuses any row whose old file is no longer on the server. Pointing CRM at a path that
/// does not exist would break the document for good, and a ledger can go stale.
/// </summary>
public sealed class RedoRun
{
    private readonly IFileServiceClient _files;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;
    private readonly IPrompts _prompts;

    public RedoRun(IFileServiceClient files, ICrmWriteClient write, BackupStore backups,
        ChangeJournal journal, LedgerStore ledger, IPrompts prompts)
    {
        _files = files;
        _write = write;
        _backups = backups;
        _journal = journal;
        _ledger = ledger;
        _prompts = prompts;
    }

    public async Task<RedoSummary> RunAsync(IReadOnlyList<LedgerRow> rows, CancellationToken ct)
    {
        int reverted = 0, refused = 0;
        var reasons = new List<string>();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (row.Verdict2() != RowVerdict.Redo) continue;

            var problem = await RevertAsync(row, reasons, ct);

            if (problem is null)
            {
                reverted++;
                _prompts.Info($"  [ row {row.Row} ]  {row.DocFileName}  RESTORED", Tone.Good);
            }
            else
            {
                refused++;
                reasons.Add($"row {row.Row}: {problem}");
                _prompts.Info($"  [ row {row.Row} ]  {row.DocFileName}  REFUSED — {problem}", Tone.Warn);
            }

            _ledger.Write(rows);
        }

        return new RedoSummary(reverted, refused, reasons);
    }

    /// <returns>Null when the row was reverted, or why it was refused.</returns>
    private async Task<string?> RevertAsync(LedgerRow row, List<string> reasons, CancellationToken ct)
    {
        // Once the old file is deleted there is nothing to go back to. Checked before the
        // server is asked, because the answer is already known.
        if (row.State() == RowState.Deleted)
            return "its old file has already been deleted, so there is nothing to go back to";

        if (row.OldFilePath.Length == 0) return "the ledger has no old file path for it";

        // The ledger can be stale -- somebody may have removed the file outside this tool.
        var still = await _files.DownloadAsync(row.OldFilePath, ct);
        if (!still.Success)
            return $"its old file is no longer on the server at {row.OldFilePath}";

        var snapshot = ReadSnapshot(row);
        if (snapshot is null)
            return "there is no crm.json snapshot in its backup folder, and the old values will " +
                   "not be guessed at";

        // The ledger's own columns are compared but not used. Saying so matters: an operator who
        // edited a cell must not be left thinking it took effect.
        Compare(row, snapshot, reasons);

        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mocd_filepath"] = snapshot.Path,
            ["mocd_category"] = snapshot.Category,
            ["mocd_hash"] = snapshot.Hash
        };

        // Exactly the fields a correction overwrote, and no others. A correction never fills a
        // field that was empty, so nothing here needs clearing.
        if (snapshot.FileId is not null) attributes["mocd_fileid"] = snapshot.FileId;
        if (snapshot.FileName is not null) attributes["mocd_filename"] = snapshot.FileName;

        try
        {
            await _write.UpdateDocumentFileAsync(row.DocFileId, attributes, ct);
        }
        catch (Exception broke)
        {
            return $"CRM refused the update: {broke.Message}";
        }

        _journal.Append(new ChangeEntry(
            DateTimeOffset.UtcNow, row.DocId, row.DocFileId, ChangeActions.Reverted,
            new RecordValues(row.NewFilePath, row.CorrectServiceCatalogueId, null, null, null),
            snapshot));

        // The abandoned copy is kept and recorded. Nothing in CRM points at it now, so its path
        // is the only way anyone will find it again.
        if (row.NewFilePath.Length > 0)
            row.SupersededPaths = row.SupersededPaths.Length == 0
                ? row.NewFilePath
                : $"{row.SupersededPaths};{row.NewFilePath}";

        row.NewFilePath = string.Empty;
        row.Verdict = RowVerdicts.Fix;
        row.FinalState = string.Empty;
        row.Error = string.Empty;
        row.Notes = row.Notes.Length == 0
            ? $"reverted {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"
            : $"{row.Notes}; reverted {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";

        return null;
    }

    /// <summary>The old record, out of the crm.json the backup step wrote beside the bytes.</summary>
    private RecordValues? ReadSnapshot(LedgerRow row)
    {
        var path = Path.Combine(_backups.Folder(row.DocId, row.DocFileName).OldDir, "crm.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var image = JsonDocument.Parse(File.ReadAllText(path));

            if (!image.RootElement.TryGetProperty(nameof(CrmImage.DocumentFileRaw), out var raw) ||
                raw.GetString() is not { Length: > 0 } json)
                return null;

            using var record = JsonDocument.Parse(json);

            return new RecordValues(
                Text(record, "mocd_filepath"),
                Text(record, "mocd_category"),
                Text(record, "mocd_hash"),
                Text(record, "mocd_filename"),
                Text(record, "mocd_fileid"));
        }
        catch (JsonException) { return null; }
    }

    private static void Compare(LedgerRow row, RecordValues snapshot, List<string> reasons)
    {
        Differ("old file path", row.OldFilePath, snapshot.Path);
        Differ("old category", row.OldCategory, snapshot.Category);
        Differ("old hash", row.OldHash, snapshot.Hash);
        Differ("old file name", row.OldFileName, snapshot.FileName);
        Differ("old file id", row.OldFileId, snapshot.FileId);

        void Differ(string column, string inLedger, string? inSnapshot)
        {
            var snap = inSnapshot ?? string.Empty;
            if (string.Equals(inLedger, snap, StringComparison.OrdinalIgnoreCase)) return;

            reasons.Add($"row {row.Row}: the csv's {column} says '{inLedger}' and the snapshot " +
                        $"says '{snap}' — the snapshot was written, the csv was not used");
        }
    }

    private static string? Text(JsonDocument record, string attribute) =>
        record.RootElement.TryGetProperty(attribute, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~RedoRunTests"`

Expected: PASS, 16 tests.

If `ReadSnapshot` finds nothing, check what `BackupStore.SaveCrmImage` actually serialises — the property name in the JSON must match `nameof(CrmImage.DocumentFileRaw)`. Read `src/MocdDocFix/Storage/BackupStore.cs` around `SaveCrmImage` and use the name it writes.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(redo): put a corrected record back the way it was

The snapshot wins where it and the ledger disagree -- it is the record as
it actually was -- and the disagreement is reported so an edited cell is
never silently ignored. Any row whose old file has left the server is
refused rather than pointed at a path that is not there.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: `DeleteOldFiles`

**Files:**
- Create: `src/MocdDocFix/Commands/DeleteOldFiles.cs`
- Test: `tests/MocdDocFix.Tests/DeleteOldFilesTests.cs`

**Interfaces:**
- Consumes: `LedgerRow`, `RowState`, `RowStates` (Task 2); `LedgerStore` (Task 3); `ChangeJournal` (Task 4). Also `ICrmReadClient.GetRawRecordAsync` and `FindDocumentFilesByPathAsync`, `IFileServiceClient.DeleteAsync`, `FilePaths.Same`.
- Produces:
  - `sealed record DeleteSummary(int Deleted, int Refused, bool Aborted, IReadOnlyList<string> Reasons)`
  - `sealed class DeleteOldFiles` with `DeleteOldFiles(IFileServiceClient files, ICrmReadClient read, ChangeJournal journal, LedgerStore ledger, IPrompts prompts)` and `Task<DeleteSummary> RunAsync(IReadOnlyList<LedgerRow> rows, bool isProduction, CancellationToken ct)`.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/DeleteOldFilesTests.cs`:

```csharp
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class DeleteOldFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-del-" + Guid.NewGuid());

    private static readonly Guid Doc = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001");
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");
    private static readonly Guid Correct = Guid.Parse("7c20a1f4-0000-0000-0000-000000000001");

    private const string OldPath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260915\b2c3.jpg";

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakePrompts _prompts = new();
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;

    public DeleteOldFilesTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new ChangeJournal(Path.Combine(_dir, "changes-dev.jsonl"));
        _ledger = new LedgerStore(Path.Combine(_dir, "repair-dev.csv"));

        _files.Files[OldPath] = ("AQID", "9f86d081");

        // CRM says the record now holds the new path -- the correction landed.
        _read.RawRecords[$"mocd_documentfiles:{Record}"] =
            $$"""{"mocd_documentfileid":"{{Record}}","mocd_filepath":"{{NewPath.Replace("\\", "\\\\")}}"}""";
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static LedgerRow Row() => new()
    {
        Row = 12,
        DocId = Doc,
        DocFileId = Record,
        DocFileName = "cert.jpg",
        OldFilePath = OldPath,
        NewFilePath = NewPath,
        Verdict = RowVerdicts.Fix,
        FinalState = RowStates.Text(RowState.Corrected)
    };

    private DeleteOldFiles Subject() => new(_files, _read, _journal, _ledger, _prompts);

    private Task<DeleteSummary> Run(params LedgerRow[] rows)
    {
        _prompts.YesNoResponse = true;
        return Subject().RunAsync(rows, isProduction: false, CancellationToken.None);
    }

    [Fact]
    public async Task An_eligible_row_has_its_old_file_removed_and_is_marked_deleted()
    {
        var row = Row();

        var summary = await Run(row);

        Assert.Equal(1, summary.Deleted);
        Assert.Contains(OldPath, _files.Deleted);
        Assert.Equal(RowState.Deleted, row.State());
    }

    /// <summary>
    /// The correction updated the record rather than replacing it, so there is no orphaned CRM
    /// row to remove. Deleting one would delete the record the document still points at.
    /// </summary>
    [Fact]
    public async Task No_crm_record_is_deleted_only_the_file()
    {
        await Run(Row());

        Assert.Single(_files.Deleted);
        // Nothing here has an ICrmWriteClient at all -- proven by construction.
    }

    [Theory]
    [InlineData("")]
    [InlineData(RowStates.Deleted)]
    [InlineData(RowStates.Ignore)]
    [InlineData(RowStates.Failed)]
    [InlineData("nearly done")]
    public async Task Only_the_one_final_state_is_eligible(string state)
    {
        var row = Row();
        row.FinalState = state;

        var summary = await Run(row);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(_files.Deleted);
    }

    /// <summary>
    /// The ledger's belief is not evidence. CRM is asked, live, whether the record really does
    /// hold the new path -- deleting the old file otherwise leaves a document that opens nothing.
    /// </summary>
    [Fact]
    public async Task A_record_that_still_names_the_old_path_is_refused()
    {
        _read.RawRecords[$"mocd_documentfiles:{Record}"] =
            $$"""{"mocd_documentfileid":"{{Record}}","mocd_filepath":"{{OldPath.Replace("\\", "\\\\")}}"}""";

        var summary = await Run(Row());

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
        Assert.Contains(summary.Reasons, r => r.Contains("still", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One file referenced by two records is rare and real. Deleting it breaks the other.</summary>
    [Fact]
    public async Task A_file_another_record_also_points_at_is_refused()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { Record, Guid.NewGuid() };

        var summary = await Run(Row());

        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
        Assert.Contains(summary.Reasons, r => r.Contains("another", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The row's own record referencing it is expected, not a reason to refuse.</summary>
    [Fact]
    public async Task The_rows_own_record_referencing_the_old_path_is_not_a_second_reference()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { Record };

        var summary = await Run(Row());

        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task A_delete_the_server_refuses_leaves_the_row_alone()
    {
        _files.DeleteRefusal = "423 Locked";
        var row = Row();

        var summary = await Run(row);

        Assert.Equal(1, summary.Refused);
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Contains(summary.Reasons, r => r.Contains("423"));
    }

    [Fact]
    public async Task The_deletion_is_journalled()
    {
        await Run(Row());

        var entry = Assert.Single(_journal.Read());
        Assert.Equal(ChangeActions.Deleted, entry.Action);
        Assert.Equal(OldPath, entry.Old!.Path);
    }

    [Fact]
    public async Task Nothing_is_deleted_until_the_operator_agrees()
    {
        _prompts.YesNoResponse = false;

        var summary = await Subject().RunAsync(new[] { Row() }, isProduction: false, CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Production_is_confirmed_by_typing_the_word()
    {
        _prompts.YesNoResponse = true;
        _prompts.TypedWordResponse = "no";

        var summary = await Subject().RunAsync(new[] { Row() }, isProduction: true, CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task The_ledger_is_written_after_each_deletion()
    {
        await Run(Row());

        Assert.Equal(RowState.Deleted, _ledger.Read()[0].State());
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DeleteOldFilesTests"`

Expected: FAIL — `'DeleteOldFiles' could not be found`.

- [ ] **Step 3: Write `DeleteOldFiles.cs`**

```csharp
using System.Text.Json;
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="Aborted">The operator did not agree to the step, so nothing was attempted.</param>
public sealed record DeleteSummary(
    int Deleted, int Refused, bool Aborted, IReadOnlyList<string> Reasons);

/// <summary>
/// Removes the old file of every row the repair run finished, and nothing else.
///
/// Because a correction updated the record rather than replacing it, there is no orphaned CRM
/// row to delete — the record the document points at is the same one it always pointed at, now
/// naming the new file. So this step touches the file server only.
///
/// Two checks stand between a row and an irreversible deletion, and both ask a system rather
/// than the ledger: does the record really hold the new path now, and does any other record
/// still name the old one.
/// </summary>
public sealed class DeleteOldFiles
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ChangeJournal _journal;
    private readonly LedgerStore _ledger;
    private readonly IPrompts _prompts;

    public DeleteOldFiles(IFileServiceClient files, ICrmReadClient read, ChangeJournal journal,
        LedgerStore ledger, IPrompts prompts)
    {
        _files = files;
        _read = read;
        _journal = journal;
        _ledger = ledger;
        _prompts = prompts;
    }

    public async Task<DeleteSummary> RunAsync(
        IReadOnlyList<LedgerRow> rows, bool isProduction, CancellationToken ct)
    {
        var eligible = rows.Where(r => r.State() == RowState.Corrected).ToList();
        var reasons = new List<string>();

        if (eligible.Count == 0)
            return new DeleteSummary(0, 0, false,
                new[] { $"No row says \"{RowStates.Corrected}\", so there is nothing to delete." });

        if (!Agreed(eligible.Count, isProduction))
            return new DeleteSummary(0, 0, true, reasons);

        int deleted = 0, refused = 0;

        foreach (var row in eligible)
        {
            ct.ThrowIfCancellationRequested();

            var problem = await DeleteOneAsync(row, ct);

            if (problem is null)
            {
                deleted++;
                _prompts.Info($"  [ row {row.Row} ]  {row.DocFileName}  old file deleted", Tone.Good);
            }
            else
            {
                refused++;
                reasons.Add($"row {row.Row}: {problem}");
                _prompts.Info($"  [ row {row.Row} ]  {row.DocFileName}  REFUSED — {problem}", Tone.Warn);
            }

            _ledger.Write(rows);
        }

        return new DeleteSummary(deleted, refused, false, reasons);
    }

    /// <returns>Null when the old file was deleted, or why it was refused.</returns>
    private async Task<string?> DeleteOneAsync(LedgerRow row, CancellationToken ct)
    {
        if (row.OldFilePath.Length == 0) return "the ledger has no old file path for it";
        if (row.NewFilePath.Length == 0) return "the ledger has no new file path for it";

        // CRM, live. The ledger's belief that the correction landed is not evidence that it did.
        var record = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocFileId, ct);
        var holds = ReadString(record, "mocd_filepath");

        if (!FilePaths.Same(holds, row.NewFilePath))
            return $"its record still names '{holds ?? "nothing"}', not the new file — the " +
                   "correction did not land, so the old file is still the one in use";

        // One file referenced by two records is rare and real, and deleting it breaks the other.
        var referencing = await _read.FindDocumentFilesByPathAsync(row.OldFilePath, ct);
        if (referencing.Any(id => id != row.DocFileId))
            return "another mocd_documentfile still points at the old file";

        var gone = await _files.DeleteAsync(row.OldFilePath, ct);
        if (!gone.Success)
            return $"the file server would not delete it: {gone.Message}";

        _journal.Append(new ChangeEntry(
            DateTimeOffset.UtcNow, row.DocId, row.DocFileId, ChangeActions.Deleted,
            new RecordValues(row.OldFilePath, row.OldCategory, row.OldHash,
                row.OldFileName.Length == 0 ? null : row.OldFileName,
                row.OldFileId.Length == 0 ? null : row.OldFileId),
            null));

        row.FinalState = RowStates.Text(RowState.Deleted);
        return null;
    }

    private bool Agreed(int count, bool isProduction)
    {
        _prompts.Section("Delete old files", Tone.Danger);
        _prompts.Say($"{count} row(s) say \"{RowStates.Corrected}\". For each one, the OLD file " +
                     "is removed from the file server.");
        _prompts.Blank();
        _prompts.Warn("This cannot be undone.", Tone.Danger);
        _prompts.Blank();
        _prompts.Bullet("No CRM record is deleted. The correction updated the record the document " +
                        "already pointed at, so there is no orphan to remove.", Tone.Muted);
        _prompts.Bullet("Each row is re-checked against CRM immediately before its file goes.",
            Tone.Muted);
        _prompts.Bullet("Your local backup keeps the bytes, but a restored file gets a new id and " +
                        "today's date folder — it cannot go back to its old path.", Tone.Muted);
        _prompts.Blank();

        if (!_prompts.YesNo("  Delete the old files now?", defaultYes: false, Tone.Danger))
            return false;

        return !isProduction || _prompts.TypedWord(
            "  This is PRODUCTION. Type DELETE to confirm", "DELETE");
    }

    private static string? ReadString(string? json, string attribute)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(attribute, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~DeleteOldFilesTests"`

Expected: PASS, 15 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(delete): remove old files, driven by the ledger

One job only: the file leaves the server. The correction updated the
record rather than replacing it, so there is no orphaned CRM row to
delete. CRM is asked live whether the correction really landed before
anything irreversible happens.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 12: `CheckItAll`

**Files:**
- Create: `src/MocdDocFix/Commands/CheckItAll.cs`
- Test: `tests/MocdDocFix.Tests/CheckItAllTests.cs`

**Interfaces:**
- Consumes: `LedgerRow`, `RowState`, `RowStates` (Task 2). Also `IFileServiceClient.DownloadAsync` and `ICrmReadClient.FindDocumentFilesByPathAsync`.
- Produces:
  - `sealed record CheckSummary(int Checked, int AsExpected, int NotAsExpected, IReadOnlyList<string> Problems)`
  - `sealed class CheckItAll` with `CheckItAll(IFileServiceClient files, ICrmReadClient read)` and `Task<CheckSummary> RunAsync(IReadOnlyList<LedgerRow> rows, CancellationToken ct)`.

**It writes nothing.** Not to CRM, not to the file server, not to the ledger. It reports.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/CheckItAllTests.cs`:

```csharp
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class CheckItAllTests
{
    private static readonly Guid Record = Guid.Parse("2a1c51a3-0000-0000-0000-000000000001");
    private const string OldPath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg";
    private const string NewPath = @"DigitalServices\7c20a1f4\20260915\b2c3.jpg";

    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();

    private CheckItAll Subject() => new(_files, _read);

    private static LedgerRow Row(string finalState) => new()
    {
        Row = 12,
        DocId = Guid.Parse("a3f1b2c4-0000-0000-0000-000000000001"),
        DocFileId = Record,
        DocFileName = "cert.jpg",
        OldFilePath = OldPath,
        NewFilePath = NewPath,
        FinalState = finalState
    };

    [Fact]
    public async Task A_deleted_row_whose_old_file_is_really_gone_is_as_expected()
    {
        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Deleted) }, CancellationToken.None);

        Assert.Equal(1, summary.Checked);
        Assert.Equal(1, summary.AsExpected);
        Assert.Empty(summary.Problems);
    }

    /// <summary>The whole point of this mode: a file the ledger says is gone, and is not.</summary>
    [Fact]
    public async Task A_deleted_row_whose_old_file_is_still_there_is_reported()
    {
        _files.Files[OldPath] = ("AQID", "9f86d081");

        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Deleted) }, CancellationToken.None);

        Assert.Equal(1, summary.NotAsExpected);
        Assert.Contains(summary.Problems, p =>
            p.Contains("still on the file server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_deleted_row_something_in_crm_still_refers_to_is_reported()
    {
        _read.FilesByPath[OldPath] = new List<Guid> { Guid.NewGuid() };

        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Deleted) }, CancellationToken.None);

        Assert.Equal(1, summary.NotAsExpected);
        Assert.Contains(summary.Problems, p =>
            p.Contains("still refer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_corrected_row_whose_old_file_is_still_waiting_is_as_expected()
    {
        _files.Files[OldPath] = ("AQID", "9f86d081");

        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Corrected) }, CancellationToken.None);

        Assert.Equal(1, summary.AsExpected);
        Assert.Empty(summary.Problems);
    }

    /// <summary>
    /// A row awaiting deletion whose old file has already gone means somebody deleted it outside
    /// this tool — worth knowing, because Redo can no longer help that row.
    /// </summary>
    [Fact]
    public async Task A_corrected_row_whose_old_file_has_vanished_is_reported()
    {
        var summary = await Subject().RunAsync(
            new[] { Row(RowStates.Corrected) }, CancellationToken.None);

        Assert.Equal(1, summary.NotAsExpected);
        Assert.Contains(summary.Problems, p =>
            p.Contains("already gone", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData(RowStates.Ignore)]
    [InlineData(RowStates.Failed)]
    public async Task Rows_that_were_never_corrected_are_not_checked(string state)
    {
        var summary = await Subject().RunAsync(new[] { Row(state) }, CancellationToken.None);

        Assert.Equal(0, summary.Checked);
        Assert.Empty(summary.Problems);
    }

    [Fact]
    public async Task It_writes_nothing_anywhere()
    {
        _files.Files[OldPath] = ("AQID", "9f86d081");

        await Subject().RunAsync(
            new[] { Row(RowStates.Deleted), Row(RowStates.Corrected) }, CancellationToken.None);

        Assert.Empty(_files.Deleted);
        Assert.Empty(_files.Uploads);
    }

    [Fact]
    public async Task Counts_add_up_across_a_mixed_ledger()
    {
        _files.Files[OldPath] = ("AQID", "9f86d081");

        var summary = await Subject().RunAsync(new[]
        {
            Row(RowStates.Corrected),   // old file still there -- as expected
            Row(RowStates.Deleted),     // old file still there -- NOT as expected
            Row(RowStates.Ignore)       // not checked at all
        }, CancellationToken.None);

        Assert.Equal(2, summary.Checked);
        Assert.Equal(1, summary.AsExpected);
        Assert.Equal(1, summary.NotAsExpected);
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~CheckItAllTests"`

Expected: FAIL — `'CheckItAll' could not be found`.

- [ ] **Step 3: Write `CheckItAll.cs`**

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

public sealed record CheckSummary(
    int Checked, int AsExpected, int NotAsExpected, IReadOnlyList<string> Problems);

/// <summary>
/// Asks both systems whether the ledger is telling the truth about the old files.
///
/// A row saying the old file was deleted should have nothing on the server at that path and
/// nothing in CRM referring to it. A row awaiting deletion should still have its old file
/// exactly where it was. Anything else is worth knowing about — a file deleted outside this
/// tool leaves a row Redo can no longer help, and a file the ledger believes is gone but is not
/// is an orphan nobody is counting.
///
/// It writes nothing. Not to CRM, not to the file server, not to the ledger.
/// </summary>
public sealed class CheckItAll
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;

    public CheckItAll(IFileServiceClient files, ICrmReadClient read)
    {
        _files = files;
        _read = read;
    }

    public async Task<CheckSummary> RunAsync(IReadOnlyList<LedgerRow> rows, CancellationToken ct)
    {
        int checkedRows = 0, asExpected = 0, notAsExpected = 0;
        var problems = new List<string>();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var state = row.State();
            if (state is not (RowState.Deleted or RowState.Corrected)) continue;
            if (row.OldFilePath.Length == 0) continue;

            checkedRows++;

            var onServer = (await _files.DownloadAsync(row.OldFilePath, ct)).Success;
            var found = new List<string>();

            if (state == RowState.Deleted)
            {
                if (onServer)
                    found.Add($"row {row.Row} ({row.DocFileName}): the ledger says the old file " +
                              $"was deleted, but it is still on the file server at {row.OldFilePath}");

                var referencing = await _read.FindDocumentFilesByPathAsync(row.OldFilePath, ct);
                if (referencing.Count > 0)
                    found.Add($"row {row.Row} ({row.DocFileName}): {referencing.Count} " +
                              "mocd_documentfile record(s) still refer to the old path");
            }
            else if (!onServer)
            {
                found.Add($"row {row.Row} ({row.DocFileName}): the row is awaiting the delete " +
                          "step, but its old file is already gone from the file server — " +
                          "something removed it outside this tool, and Redo can no longer " +
                          "restore this row");
            }

            if (found.Count == 0) asExpected++;
            else { notAsExpected++; problems.AddRange(found); }
        }

        return new CheckSummary(checkedRows, asExpected, notAsExpected, problems);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~CheckItAllTests"`

Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(check): confirm the old files really are as the ledger says

A row that says deleted should have nothing at that path and nothing in
CRM naming it; a row awaiting deletion should still have its old file
where it was. Reads only.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 13: The menu and the wiring

This is where the new modes become reachable. The old pipeline is left in place but unreferenced; Task 14 removes it.

**Files:**
- Rewrite: `src/MocdDocFix/Cli/Wizard.cs`
- Modify: `src/MocdDocFix/Cli/Session.cs`
- Rewrite: `tests/MocdDocFix.Tests/WizardTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–12.
- Produces:
  - `sealed record StepOutcome(string Headline, IReadOnlyList<string> Details)` with `static StepOutcome Of(string headline, params string[] details)` — the existing type, carried unchanged into the rewritten `Wizard.cs`. Every mode returns this; there is no second outcome type.
  - `sealed record LedgerActions(Func<CancellationToken, Task<StepOutcome>> RepairAsync, Func<CancellationToken, Task<StepOutcome>> DeleteAsync, Func<CancellationToken, Task<StepOutcome>> RedoAsync, Func<CancellationToken, Task<StepOutcome>> CheckAsync, Func<IReadOnlyList<string>, Task<StepOutcome>>? LookAsync = null)`
  - `Wizard` keeps its `RunAsync(CancellationToken)` and `WizardExit` and gains the seven-entry menu.
  - `Session` gains three private members — `WatchMode AskHowCloselyToWatch()`, `Task<IReadOnlyList<LedgerRow>> OpenLedgerAsync(bool mayRebuild, CancellationToken ct)`, `IReadOnlyList<LedgerRow> NarrowToOneDocument(IReadOnlyList<LedgerRow> rows)` — and one public one, `LedgerActions Actions(CancellationToken outer)`. The method is **not** called `LedgerActions`: a method whose name matches its return type compiles but reads as a constructor call at every use site.

- [ ] **Step 1: Write the failing test**

Replace the whole of `tests/MocdDocFix.Tests/WizardTests.cs`:

```csharp
using MocdDocFix.Cli;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class WizardTests
{
    private readonly List<string> _ran = new();

    private LedgerActions Actions() => new(
        RepairAsync: _ => { _ran.Add("repair"); return Task.FromResult(StepOutcome.Of("repaired")); },
        DeleteAsync: _ => { _ran.Add("delete"); return Task.FromResult(StepOutcome.Of("deleted")); },
        RedoAsync: _ => { _ran.Add("redo"); return Task.FromResult(StepOutcome.Of("reverted")); },
        CheckAsync: _ => { _ran.Add("check"); return Task.FromResult(StepOutcome.Of("checked")); },
        LookAsync: _ => { _ran.Add("look"); return Task.FromResult(StepOutcome.Of("looked")); });

    private Wizard Subject(FakePrompts prompts) =>
        new(prompts, "dev", isProduction: false,
            "https://crm.example", "https://files.example", Actions());

    /// <summary>Menu answers are typed 1-based numbers; the fake is non-interactive.</summary>
    private static FakePrompts Choosing(params string[] answers)
    {
        var prompts = new FakePrompts { ReadLineQueue = new Queue<string>(answers) };
        prompts.YesNoResponse = true;      // the confirm-your-choice question
        return prompts;
    }

    [Theory]
    [InlineData("1", "repair")]
    [InlineData("2", "delete")]
    [InlineData("3", "redo")]
    [InlineData("4", "check")]
    [InlineData("5", "look")]
    public async Task Each_entry_runs_its_own_mode(string typed, string expected)
    {
        // "look" then needs a path, and every path ends by choosing Quit.
        var answers = expected == "look"
            ? new[] { typed, "somepath.jpg", "7" }
            : new[] { typed, "7" };

        await Subject(Choosing(answers)).RunAsync(CancellationToken.None);

        Assert.Contains(expected, _ran);
    }

    [Fact]
    public async Task Changing_environment_leaves_the_wizard_saying_so()
    {
        var exit = await Subject(Choosing("6")).RunAsync(CancellationToken.None);

        Assert.Equal(WizardExit.ChangeEnvironment, exit);
        Assert.Empty(_ran);
    }

    [Fact]
    public async Task Quitting_runs_nothing()
    {
        var exit = await Subject(Choosing("7")).RunAsync(CancellationToken.None);

        Assert.Equal(WizardExit.Finished, exit);
        Assert.Empty(_ran);
    }

    /// <summary>
    /// The menu is seven entries and no more. A stray eighth would shift every number the
    /// operator has learned.
    /// </summary>
    [Fact]
    public async Task The_menu_offers_exactly_seven_choices()
    {
        var prompts = Choosing("7");

        await Subject(prompts).RunAsync(CancellationToken.None);

        var text = string.Join("\n", prompts.Messages);
        Assert.Contains("1 to 7", text);
    }

    [Fact]
    public async Task Production_is_named_in_the_banner()
    {
        var prompts = Choosing("7");

        await new Wizard(prompts, "prod", isProduction: true,
            "https://crm.example", "https://files.example", Actions())
            .RunAsync(CancellationToken.None);

        Assert.Contains(prompts.Messages, m => m.Contains("PRODUCTION"));
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~WizardTests"`

Expected: FAIL — `'LedgerActions' could not be found`, and `Wizard` has no such constructor.

- [ ] **Step 3: Rewrite `Wizard.cs`**

Replace the whole file:

```csharp
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>What a mode reports back, so the wizard can show it before returning to the menu.</summary>
public sealed record StepOutcome(string Headline, IReadOnlyList<string> Details)
{
    public static StepOutcome Of(string headline, params string[] details) => new(headline, details);
}

/// <param name="RepairAsync">
/// Build or open the ledger, then work through it. The only mode that uploads.
/// </param>
/// <param name="DeleteAsync">Remove the old files of rows the repair run finished.</param>
/// <param name="RedoAsync">Put reverted rows' records back the way they were.</param>
/// <param name="CheckAsync">Confirm the old files are as the ledger says. Reads only.</param>
public sealed record LedgerActions(
    Func<CancellationToken, Task<StepOutcome>> RepairAsync,
    Func<CancellationToken, Task<StepOutcome>> DeleteAsync,
    Func<CancellationToken, Task<StepOutcome>> RedoAsync,
    Func<CancellationToken, Task<StepOutcome>> CheckAsync,
    Func<IReadOnlyList<string>, Task<StepOutcome>>? LookAsync = null);

public enum WizardExit { Finished, ChangeEnvironment }

/// <summary>
/// The front end: seven entries, one ledger behind all of them.
///
/// Everything that used to be four separate ways in — targeted, full, just report, check it
/// all — is now entry 1, because the ledger is what tells those apart. The verdict column
/// chooses which documents are worked on and the final state column records what happened, so
/// there is nothing left for a mode to mean.
/// </summary>
public sealed class Wizard
{
    private readonly IPrompts _prompts;
    private readonly Asker _asker;
    private readonly string _envName;
    private readonly bool _isProduction;
    private readonly string _crmUrl;
    private readonly string _fileServerUrl;
    private readonly LedgerActions _actions;

    public Wizard(IPrompts prompts, string envName, bool isProduction,
        string crmUrl, string fileServerUrl, LedgerActions actions)
    {
        _prompts = prompts;
        _asker = new Asker(prompts);
        _envName = envName;
        _isProduction = isProduction;
        _crmUrl = crmUrl;
        _fileServerUrl = fileServerUrl;
        _actions = actions;
    }

    public async Task<WizardExit> RunAsync(CancellationToken ct)
    {
        Banner();

        while (!ct.IsCancellationRequested)
        {
            var mode = _asker.Ask("What do you want to do?", new[]
            {
                new Choice("Repair run", "build the ledger, then work through it",
                    "Reads every document in the seven services and writes one CSV — the " +
                    "ledger. You look at it, edit the verdict column where you disagree, and " +
                    "it works through the rows marked fix: back up, upload under the correct " +
                    "catalogue, check the copy four ways, show you both, and update the record " +
                    "the document already points at. Nothing is created and nothing is " +
                    "repointed. It does not delete anything."),

                new Choice("Delete old files", "of rows the repair run finished",
                    "Reads the ledger and takes only rows whose final state says \"corrected " +
                    "and pending the delete of old docs\". For each one the OLD file is removed " +
                    "from the file server. No CRM record is deleted — the correction updated " +
                    "the record rather than replacing it, so there is no orphan. IRREVERSIBLE."),

                new Choice("Redo", "put records back the way they were",
                    "Reads the ledger and acts on rows where you typed redo in the verdict " +
                    "column, as long as their old files have not been deleted. It writes the " +
                    "old path, category, hash and name back into the same record, from the " +
                    "snapshot saved in the backup folder. The corrected copy stays on the " +
                    "server with nothing pointing at it, and its path is recorded."),

                new Choice("Check it all", "confirm the old files really are gone",
                    "Walks the ledger and asks both systems the plain question: for every row " +
                    "that says its old files were deleted, is the file actually off the server " +
                    "and is nothing in CRM still naming it — and for every row still awaiting " +
                    "the delete step, is its old file still there. Reads only."),

                new Choice("Is this file still there?", "check one path or documentfile id",
                    "Give it an old file path, or the id of a mocd_documentfile, and it asks " +
                    "the file server whether the file is on disk and CRM whether any record " +
                    "still refers to it. It changes nothing, in either system.",
                    Enabled: _actions.LookAsync is not null,
                    DisabledNote: "this build was not given a look-up action."),

                new Choice("Change environment", $"currently {_envName}"),

                new Choice("Quit", "stop here")
            }, defaultIndex: 0, allowBack: false, confirm: true);

            switch (mode.Kind == AnswerKind.Chosen ? mode.Index : 6)
            {
                case 0: Report("Repair run", await _actions.RepairAsync(ct)); break;
                case 1: Report("Delete old files", await _actions.DeleteAsync(ct)); break;
                case 2: Report("Redo", await _actions.RedoAsync(ct)); break;
                case 3: Report("Check it all", await _actions.CheckAsync(ct)); break;
                case 4: await LookUpAsync(); break;
                case 5: return WizardExit.ChangeEnvironment;
                default:
                    _prompts.Blank();
                    _prompts.Say("Nothing further was done. Bye.");
                    return WizardExit.Finished;
            }
        }

        return WizardExit.Finished;
    }

    private void Banner()
    {
        _prompts.Title("MoCD — document file path remediation");
        _prompts.Blank();
        _prompts.Field("Environment", _envName + (_isProduction ? "   *** PRODUCTION ***" : ""),
            _isProduction ? Tone.Danger : Tone.Normal);
        _prompts.Field("CRM", _crmUrl, Tone.Muted);
        _prompts.Field("File server", _fileServerUrl, Tone.Muted);
        _prompts.Blank();
        _prompts.Say("Type ? at any question for a fuller explanation, b to go back, q to quit.",
            Tone.Muted);
    }

    private async Task LookUpAsync()
    {
        if (_actions.LookAsync is not { } look) return;

        _prompts.Section("Is this file still there?");
        _prompts.Say("Give an old file path, or the id of a mocd_documentfile record. Several " +
                     "at once, separated by commas.");
        _prompts.Blank();

        var asked = _prompts.ReadLine("  Path or id")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.Equals("q", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (asked.Count == 0)
        {
            _prompts.Blank();
            _prompts.Say("Nothing to look up.", Tone.Muted);
            return;
        }

        Report("Look-up", await look(asked));
    }

    private void Report(string name, StepOutcome outcome)
    {
        _prompts.Section(name, Tone.Good);
        _prompts.Say(outcome.Headline);

        if (outcome.Details.Count == 0) return;

        _prompts.Blank();
        foreach (var line in outcome.Details) _prompts.Info("    " + line, Tone.Muted);
    }
}
```

- [ ] **Step 4: Wire the session**

In `src/MocdDocFix/Cli/Session.cs`, add these fields beside the existing ones and build them in the constructor, keeping the existing `_read`, `_write`, `_files`, `_backups`, `_opener` and `_prompts`:

```csharp
    private readonly LedgerStore _ledger;
    private readonly ChangeJournal _journal;
    private readonly ErrorLog _errors;
    private readonly LedgerBuilder _builder;
```

```csharp
        _ledger = new LedgerStore(Path.Combine(appConfig.DataRoot, $"repair-{envName}.csv"));
        _journal = new ChangeJournal(Path.Combine(appConfig.DataRoot, $"changes-{envName}.jsonl"));
        _errors = new ErrorLog(Path.Combine(appConfig.DataRoot, $"errors-{envName}.txt"));
        _builder = new LedgerBuilder(_read, appConfig.ServiceCatalogues, env.CrmUrl);
```

Then replace the whole `WizardActions(...)` method with:

```csharp
    /// <summary>
    /// Opens the ledger for a mode that is about to work from it. Re-read from disk every time,
    /// so an edit made in Excel since the last run takes effect — that is the whole point of the
    /// file.
    /// </summary>
    private async Task<IReadOnlyList<LedgerRow>> OpenLedgerAsync(bool mayRebuild, CancellationToken ct)
    {
        if (_ledger.Exists)
        {
            var existing = _ledger.Read();
            var corrected = existing.Count(r => r.State() == RowState.Corrected);
            var done = existing.Count(r => r.State() == RowState.Deleted);

            _prompts.Section("There is already a ledger for this environment");
            _prompts.Field("file", _ledger.Path, Tone.Muted);
            _prompts.Say($"{existing.Count} row(s); {corrected} corrected and awaiting the delete " +
                         $"step, {done} finished.");
            _prompts.Blank();

            if (!mayRebuild) return existing;

            var carryOn = _prompts.YesNo("  Carry on with it?", defaultYes: true);
            if (carryOn) return existing;

            var kept = _ledger.StartNewKeepingOld();
            _prompts.Say($"Kept the old one as {kept}.", Tone.Muted);
        }

        if (!mayRebuild)
        {
            _prompts.Say("There is no ledger yet. Run a repair run first — every other mode " +
                         "works from it.", Tone.Warn);
            return Array.Empty<LedgerRow>();
        }

        _prompts.Blank();
        _prompts.Say("Reading CRM. This writes nothing.", Tone.Muted);

        var rows = await _builder.BuildAsync(ct);
        _ledger.Write(rows);

        _prompts.Say($"{rows.Count} document(s) written to {_ledger.Path}", Tone.Good);
        return rows;
    }

    /// <summary>
    /// Work through the whole ledger, or just one document the operator names.
    ///
    /// One document takes exactly the same six steps as any other row — it is the same loop over
    /// a list of one — so there is no second code path that could behave differently from the
    /// one the operator has watched four hundred times.
    /// </summary>
    private IReadOnlyList<LedgerRow> NarrowToOneDocument(IReadOnlyList<LedgerRow> rows)
    {
        var how = new Asker(_prompts).Ask("What do you want to work on?", new[]
        {
            new Choice("From the file", $"every row marked fix — {rows.Count(r => r.Verdict2() == RowVerdict.Fix)} of {rows.Count}",
                "Works down the ledger in order, acting on every row whose verdict says fix and " +
                "walking past the rest."),
            new Choice("One document", "type its GUID",
                "The same six steps, for the single row whose doc id you give. Useful for " +
                "re-trying one document without opening the whole run.")
        }, defaultIndex: 0);

        if (how.Kind != AnswerKind.Chosen) return Array.Empty<LedgerRow>();
        if (how.Index == 0) return rows;

        _prompts.Blank();
        var typed = _prompts.ReadLine("  Document GUID").Trim();

        if (!Guid.TryParse(typed, out var wanted))
        {
            _prompts.Say($"'{typed}' is not a GUID.", Tone.Warn);
            return Array.Empty<LedgerRow>();
        }

        var found = rows.Where(r => r.DocId == wanted).ToList();

        if (found.Count == 0)
        {
            // Not in the ledger means the ledger is older than the document, or the document is
            // outside the seven services. Either way, guessing is worse than saying so.
            _prompts.Say($"No row in the ledger has doc id {wanted}. If the document is new, " +
                         "start a fresh ledger; if it belongs to a service this tool is not " +
                         "scoped to, it will never appear.", Tone.Warn);
            return Array.Empty<LedgerRow>();
        }

        _prompts.Say($"Row {found[0].Row} — {found[0].DocName}", Tone.Muted);
        return found;
    }

    /// <summary>
    /// Asked once per entry into the repair run, and never again while the loop runs. One answer
    /// governs every document in the ledger.
    /// </summary>
    private WatchMode AskHowCloselyToWatch()
    {
        var answer = new Asker(_prompts).Ask("How closely do you want to watch?", new[]
        {
            new Choice("Watch", "every step, and a pause after each document",
                "The full step-by-step account of each document, then a bare enter before the " +
                "next one begins. For the first few, or for production."),
            new Choice("Quiet", "one line per document",
                "One line each, straight through. The question about the two copies is the only " +
                "thing that interrupts it. This is the one to use over hundreds of rows."),
            new Choice("Unattended", "nothing is asked at all",
                "One line each, and you are never shown the two copies. The four automated " +
                "checks decide on their own — including a round-trip download compared byte for " +
                "byte against your backup. It still stops and asks on an error. Use it once you " +
                "have read the ledger and agree with it.")
        }, defaultIndex: 1);

        return answer.Kind == AnswerKind.Chosen
            ? (WatchMode)answer.Index
            : WatchMode.Quiet;
    }

    public LedgerActions Actions(CancellationToken outer) => new(
        RepairAsync: async ct =>
        {
            var all = await OpenLedgerAsync(mayRebuild: true, ct);
            if (all.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            var rows = NarrowToOneDocument(all);
            if (rows.Count == 0) return StepOutcome.Of("Nothing to work on.");

            WarnAboutInPlace();

            var mode = AskHowCloselyToWatch();
            var progress = new RunProgress(_prompts, mode);

            var summary = await new RepairRun(_ledger,
                    new RepairOneRow(_files, _read, _write, _backups, _journal, _prompts,
                        _opener, progress, _envName),
                    progress, _prompts, _errors)
                .RunAsync(rows, all, ct);

            var details = new List<string> { $"ledger → {_ledger.Path}" };

            foreach (var skip in summary.Skips) details.Add($"skipped: {skip.Count} — {skip.Why}");

            if (summary.Declined > 0)
                details.Add($"{summary.Declined} left alone because you said the copies did not match");

            if (summary.Failed > 0) details.Add($"errors → {_errors.Path}");

            foreach (var odd in summary.Unrecognised.Take(10))
                details.Add($"VERDICT NOT UNDERSTOOD — {odd}");

            if (summary.Stopped) details.Add("THE RUN WAS STOPPED at your request.");

            return new StepOutcome(
                $"{summary.Corrected} corrected, {summary.Declined} left alone, " +
                $"{summary.Failed} failed.", details);
        },

        DeleteAsync: async ct =>
        {
            var rows = await OpenLedgerAsync(mayRebuild: false, ct);
            if (rows.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            var summary = await new DeleteOldFiles(_files, _read, _journal, _ledger, _prompts)
                .RunAsync(rows, _env.IsProduction, ct);

            return new StepOutcome(
                summary.Aborted
                    ? "Nothing was deleted."
                    : $"{summary.Deleted} old file(s) deleted, {summary.Refused} refused.",
                summary.Reasons);
        },

        RedoAsync: async ct =>
        {
            var rows = await OpenLedgerAsync(mayRebuild: false, ct);
            if (rows.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            var summary = await new RedoRun(_files, _write, _backups, _journal, _ledger, _prompts)
                .RunAsync(rows, ct);

            return new StepOutcome(
                $"{summary.Reverted} record(s) put back, {summary.Refused} refused.",
                summary.Reasons);
        },

        CheckAsync: async ct =>
        {
            var rows = await OpenLedgerAsync(mayRebuild: false, ct);
            if (rows.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            var summary = await new CheckItAll(_files, _read).RunAsync(rows, ct);

            return new StepOutcome(
                summary.NotAsExpected == 0
                    ? $"{summary.Checked} checked, all as the ledger says."
                    : $"{summary.Checked} checked, {summary.AsExpected} correct, " +
                      $"{summary.NotAsExpected} NOT AS EXPECTED.",
                summary.Problems);
        },

        LookAsync: async asked =>
        {
            var reports = await new LookupCommand(_files, _read, _backups, _prompts)
                .RunAsync(asked, outer);

            return new StepOutcome($"{reports.Count} looked up. Nothing was changed.",
                reports.Select(Summarise).ToList());
        });

    /// <summary>
    /// Said once, on entering the repair run — not per document, which would train the operator
    /// to skip past it.
    /// </summary>
    private void WarnAboutInPlace()
    {
        _prompts.Section("Before this starts", Tone.Warn);
        _prompts.Say("Corrections are written into the existing mocd_documentfile record. A " +
                     "portal-created record's id equals its original file id, and after a " +
                     "correction it no longer will.");
        _prompts.Blank();
        _prompts.Bullet("Nothing in CRM reads a file path off the record id — DownloadDocument " +
                        "takes a FilePath, and its callers read mocd_filepath off the record — " +
                        "so this breaks the convention, not any code path.", Tone.Muted);
        _prompts.Bullet("There is no new record and nothing is repointed.", Tone.Muted);
        _prompts.Bullet("The ledger and the backup folder are the only route back. Do not delete " +
                        "them.", Tone.Muted);
        _prompts.Blank();
    }
```

`LookupCommand`'s constructor currently takes a `StateStore`, which Task 14 deletes. Free it of that now, in this task, because the wiring above already calls the four-argument form:

- remove the `StateStore` parameter and its field from `LookupCommand`;
- remove every member of `LookupReport` fed from it, and every line that populates them — what this tool's own notes said about a file is the ledger's business now;
- leave the file-server answer and the CRM answer exactly as they are. Those two are the whole point of the look-up.

Then update `tests/MocdDocFix.Tests/LookupCommandTests.cs` to construct it without the state store and delete any assertion about the removed members.

Finally, in the same file, delete `_pending`, `_hashes`, `HashOf`, `DeleteOneAsync`, `ScanAsync`, `WriteEachDocument`, `Pickable`, `Targeted` and `RunDirectAsync`'s `scan` / `backup` / `migrate` / `delete` / `targeted` cases — Task 14 replaces the command line.

- [ ] **Step 5: Run the wizard tests**

Run: `dotnet test tests/MocdDocFix.Tests --filter "FullyQualifiedName~WizardTests"`

Expected: PASS, 9 tests.

- [ ] **Step 6: Build**

Run: `dotnet build 2>&1 | grep -E "error CS" | sort -u`

Expected: errors only in the files Task 14 deletes — `Program.cs`, `CommandLineOptions.cs`, and anything still referencing `ScanCommand`, `MigrateCommand`, `BackupCommand`, `DeleteCommand`, `VerifyCommand`, `TargetedCommand`, `OldFileCheckCommand` or `StateStore`. If any error is in a file **not** on Task 14's delete list, fix it here.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(cli): seven entries, one ledger behind all of them

Targeted, full, just-report and check-it-all collapse into the repair run:
the verdict column chooses which documents are worked on and the final
state column records what happened, so there is nothing left for a mode to
mean. The watch question is asked once, before the loop.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 14: Remove everything superseded

The build does not pass until this task is finished. Do it in one sitting.

**Files:**
- Delete (source): `Storage/GroupedReportWriter.cs`, `Storage/GuidListWriter.cs`, `Storage/RepointedListWriter.cs`, `Storage/DocumentReportStore.cs`, `Storage/DocumentRecord.cs`, `Storage/Reporter.cs`, `Storage/StateStore.cs`, `Domain/MigrationState.cs`, `Commands/ScanCommand.cs`, `Commands/TargetedCommand.cs`, `Commands/MigrateCommand.cs`, `Commands/VerifyCommand.cs`, `Commands/OldFileCheckCommand.cs`, `Commands/BackupCommand.cs`, `Commands/DeleteCommand.cs`, `Commands/Reconciler.cs`, `Ui/StepGate.cs`, `Ui/CheckLines.cs`
- Delete (tests): `ReporterTests.cs`, `GroupedReportWriterTests.cs`, `GuidListWriterTests.cs`, `DocumentReportStoreTests.cs`, `ScanCommandTests.cs`, `TargetedCommandTests.cs`, `TargetedReportTests.cs`, `MigrateCommandTests.cs`, `VerifyCommandTests.cs`, `OldFileCheckTests.cs`, `BackupCommandTests.cs`, `DeleteCommandTests.cs`, `PipelineDeleteStepTests.cs`, `StateStoreTests.cs`, `CheckLinesTests.cs`
- Modify: `src/MocdDocFix/Cli/CommandLineOptions.cs`, `src/MocdDocFix/Program.cs`, `src/MocdDocFix/Commands/LookupCommand.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–13.
- Produces: a build with no reference to any deleted type, and a command line offering `repair`, `delete`, `redo` and `check`.

**Do not delete:** `Verification/*` (the `Verifier` is used by `RepairOneRow`), `Storage/BackupStore.cs`, `Storage/DocumentFolder.cs`, `Commands/LookupCommand.cs`, `Domain/Classifier.cs`, `Domain/DocumentGroups.cs`, `Domain/Classification.cs`, `Domain/Verdict.cs`.

- [ ] **Step 1: Delete the source files**

```bash
cd /d/mocd-docfix
git rm -q src/MocdDocFix/Storage/GroupedReportWriter.cs \
          src/MocdDocFix/Storage/GuidListWriter.cs \
          src/MocdDocFix/Storage/RepointedListWriter.cs \
          src/MocdDocFix/Storage/DocumentReportStore.cs \
          src/MocdDocFix/Storage/DocumentRecord.cs \
          src/MocdDocFix/Storage/Reporter.cs \
          src/MocdDocFix/Storage/StateStore.cs \
          src/MocdDocFix/Domain/MigrationState.cs \
          src/MocdDocFix/Commands/ScanCommand.cs \
          src/MocdDocFix/Commands/TargetedCommand.cs \
          src/MocdDocFix/Commands/MigrateCommand.cs \
          src/MocdDocFix/Commands/VerifyCommand.cs \
          src/MocdDocFix/Commands/OldFileCheckCommand.cs \
          src/MocdDocFix/Commands/BackupCommand.cs \
          src/MocdDocFix/Commands/DeleteCommand.cs \
          src/MocdDocFix/Commands/Reconciler.cs \
          src/MocdDocFix/Ui/StepGate.cs \
          src/MocdDocFix/Ui/CheckLines.cs
```

- [ ] **Step 2: Delete the test files**

```bash
git rm -q tests/MocdDocFix.Tests/ReporterTests.cs \
          tests/MocdDocFix.Tests/GroupedReportWriterTests.cs \
          tests/MocdDocFix.Tests/GuidListWriterTests.cs \
          tests/MocdDocFix.Tests/DocumentReportStoreTests.cs \
          tests/MocdDocFix.Tests/ScanCommandTests.cs \
          tests/MocdDocFix.Tests/TargetedCommandTests.cs \
          tests/MocdDocFix.Tests/TargetedReportTests.cs \
          tests/MocdDocFix.Tests/MigrateCommandTests.cs \
          tests/MocdDocFix.Tests/VerifyCommandTests.cs \
          tests/MocdDocFix.Tests/OldFileCheckTests.cs \
          tests/MocdDocFix.Tests/BackupCommandTests.cs \
          tests/MocdDocFix.Tests/DeleteCommandTests.cs \
          tests/MocdDocFix.Tests/PipelineDeleteStepTests.cs \
          tests/MocdDocFix.Tests/StateStoreTests.cs \
          tests/MocdDocFix.Tests/CheckLinesTests.cs
```

- [ ] **Step 3: Rewrite the command line**

In `src/MocdDocFix/Cli/CommandLineOptions.cs`, replace the command list and `Usage` so the four verbs are `repair`, `delete`, `redo` and `check`. Keep `--env`, `--dry-run` and the config flags exactly as they are; delete `--docs`, `--docs-file`, `--force-review` and the `Identifiers` / `ForceReview` members, which only a targeted run used.

In `src/MocdDocFix/Program.cs` and `Session.RunDirectAsync`, replace the switch with the four verbs, each calling the matching member of `Session.Actions(ct)` and printing its `Headline` and `Details`. `--dry-run` applies to `repair`, `delete` and `redo`: print what would run and return 0 without calling the action.

- [ ] **Step 4: Build until it is clean**

Run: `dotnet build 2>&1 | grep -E "error CS" | sort -u`

Expected: nothing. Work through whatever appears — every remaining error is a reference to something deleted in Steps 1 and 2.

- [ ] **Step 5: Run everything**

Run: `dotnet build -c Release 2>&1 | grep -E "error|warning" ; dotnet test tests/MocdDocFix.Tests 2>&1 | tail -6`

Expected: no errors, no warnings, all passing. The total will be roughly 330 — far below the 654 this branch started at, because fifteen test files went with their subjects and the new modes are covered by ten focused files instead.

- [ ] **Step 6: Prove the app still starts**

Run: `dotnet run --project src/MocdDocFix -- --help`

Expected: usage text naming `repair`, `delete`, `redo` and `check`, and no others.

- [ ] **Step 7: Check nothing dead is left**

Run: `grep -rn "AdoClient\|DocumentTypeCheck\|MigrateCommand\|StateStore\|MigrationState\|DocumentReportStore\|GroupedReportWriter\|Reconciler\|StepGate" src tests --include=*.cs`

Expected: no output.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor: delete everything the ledger replaced

Five kinds of report, the five-step pipeline and the state store all go.
The ledger is the report, the work queue, the progress record and the
route back, and there is nothing else to read.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Done

At this point:

- one CSV per environment, at `<DataRoot>\repair-<env>.csv`, with the append-only journal and the error log beside it;
- seven menu entries, four of them ledger-driven;
- corrections applied in place, with `Redo` as the route back and `Check it all` as the proof;
- no reference to Azure DevOps anywhere in the tree.

**Do not `git push`.** The branch is `feat/docfix-tool`; ask before pushing.
