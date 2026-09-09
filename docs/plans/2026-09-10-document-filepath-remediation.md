# Document File Path Remediation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a console tool that finds MoCD documents whose vendor file path carries the wrong service catalogue segment, backs them up locally, re-uploads them under the correct catalogue, repoints CRM, and — only after explicit confirmation — deletes the old files.

**Architecture:** A .NET 8 console app with no Xrm SDK. Two HTTP clients: one for the Dataverse Web API over NTLM (`/api/data/v9.1/`), one for the vendor file service (`/api/File/*`). Pure, fully-testable domain logic (path parsing, classification, verification) sits behind those clients so the risky code is covered by unit tests. Five phases — scan, backup, migrate, report, delete — each a separate command, with deletion isolated as the only irreversible step.

**Tech Stack:** .NET 8, C# 12, xUnit, CsvHelper, `System.Text.Json`, Windows DPAPI (`System.Security.Cryptography.ProtectedData`) for secrets.

**Spec:** `docs/specs/2026-09-10-document-filepath-remediation-design.md` — read it before starting. Section references below (§n) point at it.

## Global Constraints

- **Target framework:** `net8.0`. Windows-only is acceptable (DPAPI, `UseShellExecute`).
- **Repository:** `D:\mocd-docfix`. Never modify `DigitalServicesPlatform` or `mocd-knowledge-base`.
- **Data root:** `D:\mocd-docfix-data` — downloads, reports, state and logs live **outside the repo**. Never write citizen documents inside the working tree.
- **No Xrm SDK.** CRM is reached only through the OData Web API with NTLM.
- **Read-only until phase 3.** Commands `scan` and `backup` must issue zero writes to CRM or the file service.
- **Never call `api/Document/...`** on the EServices API — it would create a duplicate `mocd_document` (§3.1). Only the vendor endpoints `/api/File/Upload`, `/api/File/Download`, `/api/File/Delete` are used.
- **Path strings are sent raw and unencoded** onto `?path=`, matching `FileService.cs:65` (§3.3). Do not URL-encode.
- **Never assume the vendor hash is MD5** (§6.1). Compare vendor-hash to vendor-hash, our-hash to our-hash, bytes to bytes.
- **Never hardcode "known bad" catalogue GUIDs.** Whether a path segment is a real catalogue is resolved live against `mocd_servicecatalogue` (§4.3).
- **Secrets** (`API_KEY`, CRM password) are never written to disk in plaintext and never committed.
- **TDD.** Every task writes a failing test first. Commit after each task.

---

## File Structure

```
D:\mocd-docfix\
├─ MocdDocFix.sln
├─ .gitignore                                   (already present)
├─ docs\
│  ├─ specs\2026-09-10-document-filepath-remediation-design.md
│  └─ plans\2026-09-10-document-filepath-remediation.md
├─ src\MocdDocFix\
│  ├─ MocdDocFix.csproj
│  ├─ Program.cs                                command routing
│  ├─ Domain\
│  │  ├─ FilePathParts.cs                       parsed path (T1)
│  │  ├─ FilePathParser.cs                      parsing, all 6 shapes (T1)
│  │  ├─ Verdict.cs / Classification.cs         (T2)
│  │  ├─ Classifier.cs                          FIX / REVIEW / SKIP (T2)
│  │  ├─ DocumentRow.cs                         one document + its file + catalogues (T5)
│  │  └─ MigrationState.cs                      state enum (T8)
│  ├─ Config\
│  │  ├─ AppConfig.cs / EnvironmentConfig.cs    (T3)
│  │  ├─ ConfigStore.cs                         load/save/prompt (T3)
│  │  └─ SecretStore.cs                         DPAPI wrapper (T3)
│  ├─ Clients\
│  │  ├─ FileServiceClient.cs                   vendor upload/download/delete (T4)
│  │  ├─ FileServiceModels.cs                   request/response records (T4)
│  │  ├─ CrmReadClient.cs                       queries, identifier resolution (T5)
│  │  └─ CrmWriteClient.cs                      create documentfile, repoint (T6)
│  ├─ Verification\
│  │  ├─ CheckResult.cs / VerificationReport.cs (T7)
│  │  └─ Verifier.cs                            the six checks (T7)
│  ├─ Storage\
│  │  ├─ StateStore.cs                          resumable jsonl state (T8)
│  │  ├─ BackupStore.cs                         local files + manifest (T9)
│  │  └─ Reporter.cs                            CSV artefacts (T10)
│  ├─ Commands\
│  │  ├─ ScanCommand.cs      (T11)
│  │  ├─ BackupCommand.cs    (T12)
│  │  ├─ MigrateCommand.cs   (T13)
│  │  ├─ DeleteCommand.cs    (T14)
│  │  └─ TargetedCommand.cs  (T15)
│  └─ Ui\
│     ├─ Console Prompts.cs → Prompts.cs        confirmations, typed words (T13)
│     └─ FileOpener.cs                          open both files in viewer (T13)
└─ tests\MocdDocFix.Tests\
   ├─ MocdDocFix.Tests.csproj
   ├─ FilePathParserTests.cs      ClassifierTests.cs
   ├─ ConfigStoreTests.cs         FileServiceClientTests.cs
   ├─ CrmReadClientTests.cs       CrmWriteClientTests.cs
   ├─ VerifierTests.cs            StateStoreTests.cs
   ├─ BackupStoreTests.cs         ReporterTests.cs
   ├─ ScanCommandTests.cs         MigrateCommandTests.cs
   ├─ DeleteCommandTests.cs       TargetedCommandTests.cs
   └─ Fakes\FakeHttpMessageHandler.cs
```

---

## Task 1: Solution scaffolding and the path parser

The parser is the foundation — every later decision reads its output. It handles all six shapes
observed in production (§1.1), including the doubled-backslash case.

**Files:**
- Create: `MocdDocFix.sln`, `src/MocdDocFix/MocdDocFix.csproj`, `tests/MocdDocFix.Tests/MocdDocFix.Tests.csproj`
- Create: `src/MocdDocFix/Domain/FilePathParts.cs`, `src/MocdDocFix/Domain/FilePathParser.cs`
- Test: `tests/MocdDocFix.Tests/FilePathParserTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `FilePathParts` record and `FilePathParser.Parse(string? raw) → FilePathParts`.
  Later tasks read `CategorySegment` (null when absent), `SegmentCount`, `FileStem`, `Extension`,
  `HasDoubledSeparators`.

- [ ] **Step 1: Create the solution and projects**

```bash
cd /d/mocd-docfix
dotnet new sln -n MocdDocFix
dotnet new console -o src/MocdDocFix -f net8.0
dotnet new xunit  -o tests/MocdDocFix.Tests -f net8.0
dotnet sln add src/MocdDocFix/MocdDocFix.csproj tests/MocdDocFix.Tests/MocdDocFix.Tests.csproj
dotnet add tests/MocdDocFix.Tests/MocdDocFix.Tests.csproj reference src/MocdDocFix/MocdDocFix.csproj
dotnet build
```

Expected: `Build succeeded`.

- [ ] **Step 2: Enable nullable and implicit usings**

Edit `src/MocdDocFix/MocdDocFix.csproj` so the `<PropertyGroup>` reads:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net8.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <RootNamespace>MocdDocFix</RootNamespace>
</PropertyGroup>
```

- [ ] **Step 3: Write the failing test**

Create `tests/MocdDocFix.Tests/FilePathParserTests.cs`:

```csharp
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

public class FilePathParserTests
{
    [Fact]
    public void Parses_the_correct_four_segment_shape()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg");

        Assert.Equal("DigitalServices", p.Root);
        Assert.Equal("cd97bf8d-bea8-f011-b116-005056010908", p.CategorySegment);
        Assert.Equal("20260330", p.DateSegment);
        Assert.Equal("5b05398a-b0bc-4c26-a6a6-40b7e0ece187", p.FileStem);
        Assert.Equal(".jpg", p.Extension);
        Assert.Equal(4, p.SegmentCount);
        Assert.False(p.HasDoubledSeparators);
    }

    [Fact]
    public void Parses_the_document_type_name_shape()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg");

        Assert.Equal("goodConductCertificate", p.CategorySegment);
        Assert.Equal("20260330", p.DateSegment);
    }

    [Fact]
    public void Parses_the_docType_prefixed_shape()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\docType2746f51e7e3ef111b119005056010908\20260518\028b1307-7972-43e0-8566-d1687f2e4d63.pdf");

        Assert.Equal("docType2746f51e7e3ef111b119005056010908", p.CategorySegment);
        Assert.Equal(".pdf", p.Extension);
    }

    [Fact]
    public void Three_segment_shape_has_no_category_and_still_finds_the_date()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\20260707\090d2179-20be-4ec3-a558-a182adc5f38d.doc");

        Assert.Null(p.CategorySegment);
        Assert.Equal("20260707", p.DateSegment);
        Assert.Equal(3, p.SegmentCount);
    }

    [Fact]
    public void Numeric_category_is_kept_verbatim()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\0\20260423\c91918d7-5698-4d08-b227-0005d35e76db.png");

        Assert.Equal("0", p.CategorySegment);
    }

    [Fact]
    public void Doubled_separators_are_collapsed_and_flagged()
    {
        var p = FilePathParser.Parse(
            @"DigitalServices\\POD\\20250911\\e9733bfd-07e9-4060-a7bf-31e162412ce5.png");

        Assert.True(p.HasDoubledSeparators);
        Assert.Equal("POD", p.CategorySegment);
        Assert.Equal("20250911", p.DateSegment);
        Assert.Equal(4, p.SegmentCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_yields_an_empty_parse(string? raw)
    {
        var p = FilePathParser.Parse(raw);

        Assert.Equal(0, p.SegmentCount);
        Assert.Null(p.CategorySegment);
        Assert.Equal(string.Empty, p.FileStem);
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test --filter FilePathParserTests`
Expected: FAIL — `The type or namespace name 'FilePathParser' could not be found`.

- [ ] **Step 5: Write `FilePathParts`**

Create `src/MocdDocFix/Domain/FilePathParts.cs`:

```csharp
namespace MocdDocFix.Domain;

/// <summary>
/// A vendor file path broken into its parts. The canonical shape is
/// DigitalServices\{Category}\{yyyyMMdd}\{fileGuid}.{ext} but production
/// contains several malformed variants (see spec section 1.1).
/// </summary>
public sealed record FilePathParts(
    string Raw,
    string Root,
    string? CategorySegment,
    string? DateSegment,
    string FileStem,
    string Extension,
    int SegmentCount,
    bool HasDoubledSeparators)
{
    public static FilePathParts Empty(string raw) =>
        new(raw, string.Empty, null, null, string.Empty, string.Empty, 0, false);

    /// <summary>True when the path has the expected number of segments.</summary>
    public bool IsWellFormed => SegmentCount == 4 && !HasDoubledSeparators;
}
```

- [ ] **Step 6: Write `FilePathParser`**

Create `src/MocdDocFix/Domain/FilePathParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace MocdDocFix.Domain;

public static class FilePathParser
{
    private static readonly Regex DateSegment = new(@"^\d{8}$", RegexOptions.Compiled);

    public static FilePathParts Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return FilePathParts.Empty(raw ?? string.Empty);

        var doubled = raw.Contains(@"\\", StringComparison.Ordinal);

        var segments = raw
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        if (segments.Length == 0)
            return FilePathParts.Empty(raw);

        var root = segments[0];
        var last = segments[^1];
        var extension = Path.GetExtension(last);
        var stem = Path.GetFileNameWithoutExtension(last);

        // The date is the last segment before the file name that looks like yyyyMMdd.
        string? date = null;
        for (var i = segments.Length - 2; i >= 1; i--)
        {
            if (DateSegment.IsMatch(segments[i])) { date = segments[i]; break; }
        }

        // Category is segment 1 only when it is not itself the date.
        string? category = null;
        if (segments.Length >= 3 && !DateSegment.IsMatch(segments[1]))
            category = segments[1];

        return new FilePathParts(raw, root, category, date, stem, extension, segments.Length, doubled);
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter FilePathParserTests`
Expected: PASS — `Passed! - Failed: 0, Passed: 9`.

- [ ] **Step 8: Commit**

```bash
cd /d/mocd-docfix
git add .gitignore docs MocdDocFix.sln src tests
git commit -m "feat: solution scaffolding and vendor file path parser

Parses all six path shapes observed in production, including the
three-segment form with no category and the doubled-separator form."
```

---

## Task 2: The classifier

Turns a parsed path plus catalogue facts into FIX / REVIEW / SKIP. Pure logic — whether a GUID is
a real catalogue arrives as an injected predicate, so this is fully unit-testable and the
"never hardcode known-bad GUIDs" constraint is structurally enforced.

**Files:**
- Create: `src/MocdDocFix/Domain/Verdict.cs`, `src/MocdDocFix/Domain/Classification.cs`, `src/MocdDocFix/Domain/Classifier.cs`
- Test: `tests/MocdDocFix.Tests/ClassifierTests.cs`

**Interfaces:**
- Consumes: `FilePathParts` (T1).
- Produces:
  - `enum Verdict { Fix, Review, Skip }`
  - `record Classification(Verdict Verdict, string Reason, string Solution, Guid? CorrectCatalogueId, string? CurrentSegment)`
  - `Classifier.Classify(FilePathParts path, Guid? docTypeCatalogue, Guid? crossCheckCatalogue, Func<string, bool> isKnownCatalogue) → Classification`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/ClassifierTests.cs`:

```csharp
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

public class ClassifierTests
{
    private static readonly Guid EmployeeAppointment = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid GamAttendance       = Guid.Parse("24db2387-c15d-f111-b119-005056010908");
    private static readonly Guid GamRequest          = Guid.Parse("3ff27d73-653e-f111-b119-005056010908");
    private static readonly Guid RequestToJoinNpo    = Guid.Parse("9a39aa75-9933-f111-b119-005056010908");

    /// <summary>Only these GUIDs are real service catalogues in these tests.</summary>
    private static bool IsCatalogue(string segment) =>
        Guid.TryParse(segment, out var g) &&
        (g == EmployeeAppointment || g == GamAttendance || g == GamRequest || g == RequestToJoinNpo);

    private static Classification Classify(string path, Guid? docTypeCat, Guid? crossCheck = null) =>
        Classifier.Classify(FilePathParser.Parse(path), docTypeCat, crossCheck, IsCatalogue);

    [Fact]
    public void Correct_catalogue_is_skipped()
    {
        var r = Classify(
            @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260330\a.jpg", EmployeeAppointment);

        Assert.Equal(Verdict.Skip, r.Verdict);
        Assert.Contains("already correct", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalogue_match_is_case_insensitive()
    {
        var r = Classify(
            @"DigitalServices\CD97BF8D-BEA8-F011-B116-005056010908\20260330\a.jpg", EmployeeAppointment);

        Assert.Equal(Verdict.Skip, r.Verdict);
    }

    [Fact]
    public void Document_type_name_segment_is_fixed()
    {
        var r = Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment);

        Assert.Equal(Verdict.Fix, r.Verdict);
        Assert.Equal(EmployeeAppointment, r.CorrectCatalogueId);
        Assert.Equal("goodConductCertificate", r.CurrentSegment);
    }

    [Fact]
    public void DocType_prefixed_segment_is_fixed()
    {
        var r = Classify(
            @"DigitalServices\docType2746f51e7e3ef111b119005056010908\20260518\a.pdf", GamRequest);

        Assert.Equal(Verdict.Fix, r.Verdict);
    }

    [Fact]
    public void Numeric_segment_is_fixed()
    {
        var r = Classify(@"DigitalServices\0\20260423\a.png", EmployeeAppointment);

        Assert.Equal(Verdict.Fix, r.Verdict);
    }

    [Fact]
    public void Missing_category_segment_is_fixed()
    {
        var r = Classify(@"DigitalServices\20260707\a.doc", GamRequest);

        Assert.Equal(Verdict.Fix, r.Verdict);
        Assert.Null(r.CurrentSegment);
    }

    [Fact]
    public void A_different_but_real_catalogue_is_review_not_fix()
    {
        // GAM Attendance document stored under GAM Request — both are valid catalogues.
        var r = Classify(
            @"DigitalServices\3ff27d73-653e-f111-b119-005056010908\20260518\a.pdf", GamAttendance);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Contains("valid service catalogue", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_foreign_but_real_catalogue_is_review()
    {
        var r = Classify(
            @"DigitalServices\9a39aa75-9933-f111-b119-005056010908\20260709\a.jpg", GamAttendance);

        Assert.Equal(Verdict.Review, r.Verdict);
    }

    [Fact]
    public void Cross_check_disagreement_is_review_even_when_segment_is_junk()
    {
        var r = Classify(
            @"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment, GamRequest);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Contains("cross-check", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cross_check_agreement_still_fixes()
    {
        var r = Classify(
            @"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment, EmployeeAppointment);

        Assert.Equal(Verdict.Fix, r.Verdict);
    }

    [Fact]
    public void No_document_type_catalogue_is_skipped_as_unfixable()
    {
        var r = Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", null);

        Assert.Equal(Verdict.Skip, r.Verdict);
        Assert.Contains("no service catalogue", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_file_path_is_skipped()
    {
        var r = Classify("", EmployeeAppointment);

        Assert.Equal(Verdict.Skip, r.Verdict);
        Assert.Contains("no file path", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doubled_separator_path_is_review_not_fix()
    {
        var r = Classify(@"DigitalServices\\POD\\20250911\\a.png", EmployeeAppointment);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Contains("malformed", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_fix_states_the_remedy_including_the_catalogue_it_will_use()
    {
        var r = Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment);

        Assert.Contains("Re-upload", r.Solution);
        Assert.Contains(EmployeeAppointment.ToString(), r.Solution);
        Assert.Contains("repoint", r.Solution, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_verdict_carries_a_non_empty_solution()
    {
        _ = IsCatalogue("");   // keep the helper referenced

        var cases = new[]
        {
            Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment),
            Classify($@"DigitalServices\{EmployeeAppointment}\20260330\a.jpg", EmployeeAppointment),
            Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", null),
            Classify($@"DigitalServices\{GamRequest}\20260518\a.pdf", GamAttendance),
            Classify("", EmployeeAppointment)
        };

        Assert.All(cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Solution)));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter ClassifierTests`
Expected: FAIL — `'Classifier' does not exist`.

- [ ] **Step 3: Write the domain types**

Create `src/MocdDocFix/Domain/Verdict.cs`:

```csharp
namespace MocdDocFix.Domain;

public enum Verdict
{
    /// <summary>Unambiguously wrong and we know the correct catalogue. Safe to migrate.</summary>
    Fix,

    /// <summary>Wrong-looking but possibly correct. Reported for a human, never modified.</summary>
    Review,

    /// <summary>Already correct, or nothing to write. No action.</summary>
    Skip
}
```

Create `src/MocdDocFix/Domain/Classification.cs`:

```csharp
namespace MocdDocFix.Domain;

/// <param name="Reason">Why it is wrong, in terms of the actual data.</param>
/// <param name="Solution">
/// What the tool will do about it, or what a human must decide. Spec section 8.1 — every
/// report states the remedy, not only the diagnosis.
/// </param>
public sealed record Classification(
    Verdict Verdict,
    string Reason,
    string Solution,
    Guid? CorrectCatalogueId,
    string? CurrentSegment);
```

- [ ] **Step 4: Write the classifier**

Create `src/MocdDocFix/Domain/Classifier.cs`:

```csharp
namespace MocdDocFix.Domain;

public static class Classifier
{
    /// <param name="isKnownCatalogue">
    /// Resolves a path segment against the live mocd_servicecatalogue table. Injected so the
    /// set is never hardcoded — see spec section 4.3.
    /// </param>
    public static Classification Classify(
        FilePathParts path,
        Guid? docTypeCatalogue,
        Guid? crossCheckCatalogue,
        Func<string, bool> isKnownCatalogue)
    {
        if (path.SegmentCount == 0)
            return new Classification(Verdict.Skip,
                "No file path on the document file record.",
                "Nothing to do — this is a legacy record with no file on the vendor server.",
                null, null);

        if (docTypeCatalogue is null)
            return new Classification(Verdict.Skip,
                "The document type has no service catalogue, so there is no correct value to write.",
                "Cannot be fixed by this tool. Set mocd_servicecatalogue on the document type " +
                "in CRM first, then re-scan.",
                null, path.CategorySegment);

        var correct = docTypeCatalogue.Value;

        // Already correct?
        if (path.CategorySegment is not null &&
            Guid.TryParse(path.CategorySegment, out var segmentGuid) &&
            segmentGuid == correct)
        {
            return new Classification(Verdict.Skip,
                "Path already correct — matches the document type's catalogue.",
                "No action needed.",
                correct, path.CategorySegment);
        }

        // Malformed structure we should not rewrite blindly.
        if (path.HasDoubledSeparators || (path.SegmentCount != 4 && path.SegmentCount != 3))
        {
            return new Classification(Verdict.Review,
                $"Path is malformed ({path.SegmentCount} segments" +
                (path.HasDoubledSeparators ? ", doubled separators" : "") + ").",
                "Not touched automatically — the path does not have the expected shape, so " +
                "rewriting it could lose information. Inspect this record by hand.",
                correct, path.CategorySegment);
        }

        // The two authorities disagree — see spec section 4.2.
        if (crossCheckCatalogue is not null && crossCheckCatalogue.Value != correct)
        {
            return new Classification(Verdict.Review,
                $"Cross-check conflict: the parent request says {crossCheckCatalogue.Value} " +
                $"but the document type says {correct}.",
                "Not touched automatically — the two authorities disagree, so we cannot tell " +
                "which catalogue is right. A human must decide which one applies.",
                null, path.CategorySegment);
        }

        // The segment is a real catalogue, just a different one — may already be right.
        if (path.CategorySegment is not null && isKnownCatalogue(path.CategorySegment))
        {
            return new Classification(Verdict.Review,
                $"Path segment '{path.CategorySegment}' is a valid service catalogue, but not the " +
                $"document type's ({correct}). It may be correctly filed.",
                $"Not touched automatically — the file may already be in the right place. Decide " +
                $"whether the file belongs under '{path.CategorySegment}' (leave it, and correct " +
                $"the document type's catalogue in CRM) or under {correct} (re-run with " +
                $"--force-review to move it).",
                correct, path.CategorySegment);
        }

        var describe = path.CategorySegment is null
            ? "no category segment at all"
            : $"'{path.CategorySegment}', which is not a service catalogue id";

        return new Classification(Verdict.Fix,
            $"Path has {describe}; correct catalogue is {correct}.",
            $"Re-upload the file with Category = {correct}, create a new mocd_documentfile with " +
            $"the vendor's new FileId, repoint the document at it, then delete the old file.",
            correct, path.CategorySegment);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter ClassifierTests`
Expected: PASS — `Failed: 0, Passed: 15`.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 24 tests.

- [ ] **Step 7: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Domain tests/MocdDocFix.Tests/ClassifierTests.cs
git commit -m "feat: classifier for FIX / REVIEW / SKIP verdicts

Catalogue membership is an injected predicate resolved live against
mocd_servicecatalogue, never a hardcoded list. A segment that is a valid
but different catalogue is REVIEW, including sibling services in scope."
```

---

## Task 3: Configuration, environments and secrets

Four environments, prompted once each, persisted outside the repo. Secrets are DPAPI-encrypted
per Windows user and never written in plaintext (§7).

**Files:**
- Create: `src/MocdDocFix/Config/AppConfig.cs`, `EnvironmentConfig.cs`, `ResolvedEnvironment.cs`, `SecretStore.cs`, `ConfigStore.cs`
- Modify: `src/MocdDocFix/MocdDocFix.csproj` (add `System.Security.Cryptography.ProtectedData`)
- Test: `tests/MocdDocFix.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `record EnvironmentConfig(string FileServiceBaseUrl, string CrmUrl, string CrmDomain, string CrmUser, bool IsProduction)`
  - `record AppConfig(Dictionary<string,EnvironmentConfig> Environments, List<Guid> ServiceCatalogues, string DataRoot)`
  - `record ResolvedEnvironment(string Name, string FileServiceBaseUrl, string UploadUrl, string DownloadUrlPrefix, string DeleteUrlPrefix, string ApiKey, string CrmUrl, string CrmDomain, string CrmUser, string CrmPassword, bool IsProduction)`
  - `interface ISecretStore { string? Get(string key); void Set(string key, string value); }`
  - `class InMemorySecretStore : ISecretStore`, `class DpapiSecretStore : ISecretStore`
  - `class ConfigStore { AppConfig Load(); void Save(AppConfig); ResolvedEnvironment Resolve(string envName); }`

- [ ] **Step 1: Add the DPAPI package**

```bash
cd /d/mocd-docfix
dotnet add src/MocdDocFix/MocdDocFix.csproj package System.Security.Cryptography.ProtectedData
```

- [ ] **Step 2: Write the failing test**

Create `tests/MocdDocFix.Tests/ConfigStoreTests.cs`:

```csharp
using MocdDocFix.Config;
using Xunit;

namespace MocdDocFix.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-cfg-" + Guid.NewGuid());
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Load_on_a_missing_file_returns_defaults_with_the_eight_catalogues()
    {
        var store = new ConfigStore(ConfigPath, new InMemorySecretStore());

        var cfg = store.Load();

        Assert.Equal(8, cfg.ServiceCatalogues.Count);
        Assert.Contains(Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), cfg.ServiceCatalogues);
        Assert.Contains(Guid.Parse("930f636a-077a-f111-b119-005056010908"), cfg.ServiceCatalogues);
        Assert.Empty(cfg.Environments);
    }

    [Fact]
    public void Save_then_load_round_trips_an_environment()
    {
        var store = new ConfigStore(ConfigPath, new InMemorySecretStore());
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig(
            "http://devfiles.mocd.gov.ae:83",
            "https://devdigitalplatform.mocd.gov.ae/MoCD",
            "MOCD", "svc.docfix", IsProduction: false);

        store.Save(cfg);
        var reloaded = new ConfigStore(ConfigPath, new InMemorySecretStore()).Load();

        Assert.True(reloaded.Environments.ContainsKey("dev"));
        Assert.Equal("MOCD", reloaded.Environments["dev"].CrmDomain);
        Assert.False(reloaded.Environments["dev"].IsProduction);
    }

    [Fact]
    public void Secrets_are_never_written_into_the_config_file()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("dev:apiKey", "SUPER-SECRET-KEY");
        secrets.Set("dev:crmPassword", "hunter2");
        var store = new ConfigStore(ConfigPath, secrets);
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig("http://x", "https://y", "MOCD", "u", false);
        store.Save(cfg);

        var text = File.ReadAllText(ConfigPath);

        Assert.DoesNotContain("SUPER-SECRET-KEY", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void Resolve_builds_the_endpoint_urls_and_injects_secrets()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("dev:apiKey", "KEY123");
        secrets.Set("dev:crmPassword", "PW123");
        var store = new ConfigStore(ConfigPath, secrets);
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig(
            "http://devfiles.mocd.gov.ae:83", "https://crm/MoCD", "MOCD", "svc", false);
        store.Save(cfg);

        var env = store.Resolve("dev");

        Assert.Equal("http://devfiles.mocd.gov.ae:83/api/File/Upload", env.UploadUrl);
        Assert.Equal("http://devfiles.mocd.gov.ae:83/api/File/Download?path=", env.DownloadUrlPrefix);
        Assert.Equal("http://devfiles.mocd.gov.ae:83/api/File/Delete?path=", env.DeleteUrlPrefix);
        Assert.Equal("KEY123", env.ApiKey);
        Assert.Equal("PW123", env.CrmPassword);
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_does_not_double_up()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("dev:apiKey", "K");
        secrets.Set("dev:crmPassword", "P");
        var store = new ConfigStore(ConfigPath, secrets);
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig("http://devfiles/", "https://crm/MoCD", "D", "u", false);
        store.Save(cfg);

        Assert.Equal("http://devfiles/api/File/Upload", store.Resolve("dev").UploadUrl);
    }

    [Fact]
    public void Resolve_throws_for_an_unknown_environment()
    {
        var store = new ConfigStore(ConfigPath, new InMemorySecretStore());

        var ex = Assert.Throws<InvalidOperationException>(() => store.Resolve("prod"));

        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_throws_when_a_secret_is_missing()
    {
        var store = new ConfigStore(ConfigPath, new InMemorySecretStore());
        var cfg = store.Load();
        cfg.Environments["dev"] = new EnvironmentConfig("http://x", "https://y", "D", "u", false);
        store.Save(cfg);

        var ex = Assert.Throws<InvalidOperationException>(() => store.Resolve("dev"));

        Assert.Contains("apiKey", ex.Message);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --filter ConfigStoreTests`
Expected: FAIL — `'ConfigStore' does not exist`.

- [ ] **Step 4: Write the config records**

Create `src/MocdDocFix/Config/EnvironmentConfig.cs`:

```csharp
namespace MocdDocFix.Config;

/// <summary>Non-secret settings for one environment. Secrets live in ISecretStore.</summary>
public sealed record EnvironmentConfig(
    string FileServiceBaseUrl,
    string CrmUrl,
    string CrmDomain,
    string CrmUser,
    bool IsProduction);
```

Create `src/MocdDocFix/Config/AppConfig.cs`:

```csharp
namespace MocdDocFix.Config;

public sealed class AppConfig
{
    public Dictionary<string, EnvironmentConfig> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The eight in-scope services (spec section 2). Configuration, not code.</summary>
    public List<Guid> ServiceCatalogues { get; set; } = new();

    /// <summary>Root for downloads, reports, state and logs. Outside the repo by design.</summary>
    public string DataRoot { get; set; } = @"D:\mocd-docfix-data";

    public static AppConfig Default() => new()
    {
        ServiceCatalogues = new List<Guid>
        {
            Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), // Employee Appointment Request
            Guid.Parse("6bcb221c-6c2b-f111-b119-005056010908"), // Membership Managment
            Guid.Parse("3ff27d73-653e-f111-b119-005056010908"), // General Assembly Meeting Request
            Guid.Parse("d2744b68-aa50-f111-b119-005056010908"), // GAM - Nomination List Request
            Guid.Parse("24db2387-c15d-f111-b119-005056010908"), // GAM - Attendance
            Guid.Parse("35105602-2b5f-f111-b119-005056010908"), // GAM - Update (Reschedule)
            Guid.Parse("d8155dcc-635e-f111-b119-005056010908"), // GAM - Minutes of Meeting
            Guid.Parse("930f636a-077a-f111-b119-005056010908"), // By-Laws Amendment Requests
        }
    };
}
```

Create `src/MocdDocFix/Config/ResolvedEnvironment.cs`:

```csharp
namespace MocdDocFix.Config;

/// <summary>An environment with its secrets and fully-built endpoint URLs.</summary>
public sealed record ResolvedEnvironment(
    string Name,
    string FileServiceBaseUrl,
    string UploadUrl,
    string DownloadUrlPrefix,
    string DeleteUrlPrefix,
    string ApiKey,
    string CrmUrl,
    string CrmDomain,
    string CrmUser,
    string CrmPassword,
    bool IsProduction);
```

- [ ] **Step 5: Write the secret stores**

Create `src/MocdDocFix/Config/SecretStore.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MocdDocFix.Config;

public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
}

/// <summary>For tests only.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _values[key] = value;
}

/// <summary>
/// Secrets encrypted with Windows DPAPI, scoped to the current user. The file is unreadable
/// by any other Windows account and never leaves the machine.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _path;
    private Dictionary<string, string> _cache;

    public DpapiSecretStore(string path)
    {
        _path = path;
        _cache = Read();
    }

    public string? Get(string key) => _cache.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value)
    {
        _cache[key] = value;
        Write(_cache);
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var protectedBytes = File.ReadAllBytes(_path);
        var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var json = Encoding.UTF8.GetString(plain);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void Write(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(values);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, protectedBytes);
    }
}
```

- [ ] **Step 6: Write the config store**

Create `src/MocdDocFix/Config/ConfigStore.cs`:

```csharp
using System.Text.Json;

namespace MocdDocFix.Config;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _configPath;
    private readonly ISecretStore _secrets;

    public ConfigStore(string configPath, ISecretStore secrets)
    {
        _configPath = configPath;
        _secrets = secrets;
    }

    /// <summary>Default location: %APPDATA%\mocd-docfix\ — outside the repo.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mocd-docfix");

    public AppConfig Load()
    {
        if (!File.Exists(_configPath)) return AppConfig.Default();

        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath));
        if (cfg is null) return AppConfig.Default();
        if (cfg.ServiceCatalogues.Count == 0) cfg.ServiceCatalogues = AppConfig.Default().ServiceCatalogues;
        return cfg;
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, Json));
    }

    public ResolvedEnvironment Resolve(string envName)
    {
        var cfg = Load();
        if (!cfg.Environments.TryGetValue(envName, out var env))
            throw new InvalidOperationException(
                $"Environment '{envName}' is not configured. Run 'docfix config {envName}' to set it up.");

        var apiKey = _secrets.Get($"{envName}:apiKey")
            ?? throw new InvalidOperationException($"Missing secret 'apiKey' for environment '{envName}'.");
        var crmPassword = _secrets.Get($"{envName}:crmPassword")
            ?? throw new InvalidOperationException($"Missing secret 'crmPassword' for environment '{envName}'.");

        var baseUrl = env.FileServiceBaseUrl.TrimEnd('/');

        return new ResolvedEnvironment(
            Name: envName,
            FileServiceBaseUrl: baseUrl,
            UploadUrl: $"{baseUrl}/api/File/Upload",
            DownloadUrlPrefix: $"{baseUrl}/api/File/Download?path=",
            DeleteUrlPrefix: $"{baseUrl}/api/File/Delete?path=",
            ApiKey: apiKey,
            CrmUrl: env.CrmUrl.TrimEnd('/'),
            CrmDomain: env.CrmDomain,
            CrmUser: env.CrmUser,
            CrmPassword: crmPassword,
            IsProduction: env.IsProduction);
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter ConfigStoreTests`
Expected: PASS — `Failed: 0, Passed: 7`.

- [ ] **Step 8: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Config src/MocdDocFix/MocdDocFix.csproj tests/MocdDocFix.Tests/ConfigStoreTests.cs
git commit -m "feat: environment configuration with DPAPI-protected secrets

Config lives in %APPDATA%\\mocd-docfix, secrets are encrypted per Windows
user, and neither the API key nor the CRM password is ever written to the
config file. The eight in-scope catalogues ship as defaults."
```

---

## Task 4: Vendor file service client

Upload, download and delete against the vendor. Two behaviours here are deliberate and must not
be "improved": the path goes onto the query string **unencoded** (§3.3), and delete is a **GET**
(§3.4).

**Files:**
- Create: `src/MocdDocFix/Clients/FileServiceModels.cs`, `src/MocdDocFix/Clients/FileServiceClient.cs`
- Create: `tests/MocdDocFix.Tests/Fakes/FakeHttpMessageHandler.cs`
- Test: `tests/MocdDocFix.Tests/FileServiceClientTests.cs`

**Interfaces:**
- Consumes: `ResolvedEnvironment` (T3).
- Produces:
  - `record ApiResponse<T>(bool Success, string? Message, T? Data, List<string>? Errors)`
  - `record FileData(Guid FileId, string FilePath, string? Hash, string? FileName, string? MediaType, string? File)`
  - `record UploadRequest(string Category, string FileName, string File, string MediaType, string Extension, Guid ApplicationId)`
  - `interface IFileServiceClient` with
    `Task<ApiResponse<FileData>> DownloadAsync(string filePath, CancellationToken ct)`,
    `Task<ApiResponse<FileData>> UploadAsync(UploadRequest request, CancellationToken ct)`,
    `Task<ApiResponse<bool>> DeleteAsync(string filePath, CancellationToken ct)`

- [ ] **Step 1: Write the fake HTTP handler**

Create `tests/MocdDocFix.Tests/Fakes/FakeHttpMessageHandler.cs`:

```csharp
using System.Net;

namespace MocdDocFix.Tests.Fakes;

/// <summary>Records every request and replies with a queued response.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public FakeHttpMessageHandler Enqueue(HttpStatusCode status, string json)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

        return _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)
              { Content = new StringContent("no response queued") };
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/MocdDocFix.Tests/FileServiceClientTests.cs`:

```csharp
using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Config;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class FileServiceClientTests
{
    private const string OldPath =
        @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg";

    private static ResolvedEnvironment Env() => new(
        "dev", "http://files", "http://files/api/File/Upload",
        "http://files/api/File/Download?path=", "http://files/api/File/Delete?path=",
        "KEY123", "https://crm/MoCD", "MOCD", "svc", "pw", IsProduction: false);

    private static (FileServiceClient client, FakeHttpMessageHandler handler) Build()
    {
        var handler = new FakeHttpMessageHandler();
        return (new FileServiceClient(new HttpClient(handler), Env()), handler);
    }

    [Fact]
    public async Task Download_sends_the_path_unencoded_with_the_api_key()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK,
            """{"Success":true,"Message":null,"Errors":null,"Data":{"FileId":"00000000-0000-0000-0000-000000000001","FilePath":"p","Hash":"abc","File":"QUJD"}}""");

        var result = await client.DownloadAsync(OldPath, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("QUJD", result.Data!.File);

        var uri = handler.Requests[0].RequestUri!.OriginalString;
        Assert.Equal("http://files/api/File/Download?path=" + OldPath, uri);
        Assert.Contains(@"\", uri, StringComparison.Ordinal);          // backslashes survive
        Assert.DoesNotContain("%5C", uri, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("KEY123", handler.Requests[0].Headers.GetValues("Apikey").Single());
    }

    [Fact]
    public async Task Upload_posts_the_expected_body_shape()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK,
            """{"Success":true,"Data":{"FileId":"11111111-1111-1111-1111-111111111111","FilePath":"new","Hash":"h"}}""");

        var request = new UploadRequest(
            Category: "cd97bf8d-bea8-f011-b116-005056010908",
            FileName: "cert.jpg", File: "QUJD", MediaType: "image/jpeg",
            Extension: ".jpg", ApplicationId: Guid.Empty);

        var result = await client.UploadAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result.Data!.FileId);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);

        var body = handler.RequestBodies[0];
        Assert.Contains("\"Category\":\"cd97bf8d-bea8-f011-b116-005056010908\"", body);
        Assert.Contains("\"FileName\":\"cert.jpg\"", body);
        Assert.Contains("\"File\":\"QUJD\"", body);
    }

    [Fact]
    public async Task Delete_uses_GET_because_that_is_what_the_vendor_exposes()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"Success":true,"Data":true}""");

        var result = await client.DeleteAsync(OldPath, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("http://files/api/File/Delete?path=" + OldPath,
                     handler.Requests[0].RequestUri!.OriginalString);
    }

    [Fact]
    public async Task A_non_success_status_becomes_a_failed_ApiResponse_not_an_exception()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");

        var result = await client.DownloadAsync(OldPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("500", result.Message);
        Assert.NotEmpty(result.Errors!);
    }

    [Fact]
    public async Task Unparseable_json_becomes_a_failed_ApiResponse()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, "<html>not json</html>");

        var result = await client.DownloadAsync(OldPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("parse", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --filter FileServiceClientTests`
Expected: FAIL — `'FileServiceClient' does not exist`.

- [ ] **Step 4: Write the models**

Create `src/MocdDocFix/Clients/FileServiceModels.cs`:

```csharp
using System.Text.Json.Serialization;

namespace MocdDocFix.Clients;

public sealed record ApiResponse<T>(
    [property: JsonPropertyName("Success")] bool Success,
    [property: JsonPropertyName("Message")] string? Message,
    [property: JsonPropertyName("Data")] T? Data,
    [property: JsonPropertyName("Errors")] List<string>? Errors)
{
    public static ApiResponse<T> Fail(string message) => new(false, message, default, new List<string> { message });
}

public sealed record FileData(
    [property: JsonPropertyName("FileId")] Guid FileId,
    [property: JsonPropertyName("FilePath")] string FilePath,
    [property: JsonPropertyName("Hash")] string? Hash,
    [property: JsonPropertyName("FileName")] string? FileName,
    [property: JsonPropertyName("MediaType")] string? MediaType,
    [property: JsonPropertyName("File")] string? File);

/// <summary>
/// The upload contract. There is no path field — the vendor assigns FileId, the yyyyMMdd
/// folder and the file name. Category is the only lever we have (spec section 3.2).
/// ApplicationId is sent as Guid.Empty to match current production behaviour, where
/// DocumentDataService never sets it.
/// </summary>
public sealed record UploadRequest(
    [property: JsonPropertyName("Category")] string Category,
    [property: JsonPropertyName("FileName")] string FileName,
    [property: JsonPropertyName("File")] string File,
    [property: JsonPropertyName("MediaType")] string MediaType,
    [property: JsonPropertyName("Extension")] string Extension,
    [property: JsonPropertyName("ApplicationId")] Guid ApplicationId);
```

- [ ] **Step 5: Write the client**

Create `src/MocdDocFix/Clients/FileServiceClient.cs`:

```csharp
using System.Text;
using System.Text.Json;
using MocdDocFix.Config;

namespace MocdDocFix.Clients;

public interface IFileServiceClient
{
    Task<ApiResponse<FileData>> DownloadAsync(string filePath, CancellationToken ct);
    Task<ApiResponse<FileData>> UploadAsync(UploadRequest request, CancellationToken ct);
    Task<ApiResponse<bool>> DeleteAsync(string filePath, CancellationToken ct);
}

public sealed class FileServiceClient : IFileServiceClient
{
    private readonly HttpClient _http;
    private readonly ResolvedEnvironment _env;

    public FileServiceClient(HttpClient http, ResolvedEnvironment env)
    {
        _http = http;
        _env = env;
    }

    public Task<ApiResponse<FileData>> DownloadAsync(string filePath, CancellationToken ct) =>
        SendAsync<FileData>(HttpMethod.Get, _env.DownloadUrlPrefix + filePath, content: null, ct);

    public Task<ApiResponse<bool>> DeleteAsync(string filePath, CancellationToken ct) =>
        // GET, deliberately — that is what the vendor exposes (spec section 3.4).
        SendAsync<bool>(HttpMethod.Get, _env.DeleteUrlPrefix + filePath, content: null, ct);

    public Task<ApiResponse<FileData>> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        var body = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        return SendAsync<FileData>(HttpMethod.Post, _env.UploadUrl, body, ct);
    }

    private async Task<ApiResponse<T>> SendAsync<T>(
        HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        try
        {
            // UriKind.Absolute with DontEscape semantics: build the Uri from the raw string so
            // backslashes in ?path= survive exactly as FileService.cs:65 sends them.
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Absolute));
            request.Headers.Add("Apikey", _env.ApiKey);
            if (content is not null) request.Content = content;

            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return ApiResponse<T>.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(text)}");

            try
            {
                var parsed = JsonSerializer.Deserialize<ApiResponse<T>>(text);
                return parsed ?? ApiResponse<T>.Fail("Could not parse the response body (null).");
            }
            catch (JsonException ex)
            {
                return ApiResponse<T>.Fail($"Could not parse the response body: {ex.Message}. Body: {Truncate(text)}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ApiResponse<T>.Fail($"Request failed: {ex.Message}");
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter FileServiceClientTests`
Expected: PASS — `Failed: 0, Passed: 5`.

> If the unencoded-path assertion fails because `Uri` normalises backslashes, set
> `handler`-side comparison against `request.RequestUri.OriginalString` (already used above).
> Should `Uri` still escape them, fall back to a `DelegatingHandler` that rewrites the
> request URI from a stored raw string — but verify against dev first (§3.3), because the
> live app sends them unencoded and it works.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 36 tests.

- [ ] **Step 8: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Clients tests/MocdDocFix.Tests/FileServiceClientTests.cs tests/MocdDocFix.Tests/Fakes
git commit -m "feat: vendor file service client (upload, download, delete)

Paths go onto the query string unencoded and delete uses GET, both matching
FileService.cs exactly. Transport and parse failures return a failed
ApiResponse rather than throwing."
```

---

## Task 5: CRM read client

Reads in-scope documents with their file, document type and cross-check catalogue in one query,
resolves the three identifier kinds a user can type, and answers "is this GUID a real catalogue".

All OData shapes below were verified live against `mocd-pre-prod`. Note the By-Laws navigation
property is `mocd_BylawsAmendmentRequestId` with that exact casing — the lower-case form returns
`Could not find a property named 'mocd_bylawsamendmentrequestid'`.

**Files:**
- Create: `src/MocdDocFix/Domain/DocumentRow.cs`, `src/MocdDocFix/Clients/CrmHttp.cs`, `src/MocdDocFix/Clients/CrmReadClient.cs`
- Test: `tests/MocdDocFix.Tests/CrmReadClientTests.cs`

**Interfaces:**
- Consumes: `ResolvedEnvironment` (T3), `FakeHttpMessageHandler` (T4).
- Produces:
  - `record DocumentRow(...)` — see Step 4 for the exact members.
  - `static HttpClient CrmHttp.Create(ResolvedEnvironment env)`
  - `interface ICrmReadClient` with
    `Task<IReadOnlyList<DocumentRow>> GetInScopeDocumentsAsync(IReadOnlyList<Guid> catalogues, CancellationToken ct)`,
    `Task<IReadOnlyList<DocumentRow>> ResolveIdentifierAsync(string identifier, CancellationToken ct)`,
    `Task<bool> IsServiceCatalogueAsync(string candidate, CancellationToken ct)`,
    `Task<DateTimeOffset?> GetDocumentModifiedOnAsync(Guid documentId, CancellationToken ct)`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/CrmReadClientTests.cs`:

```csharp
using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Config;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class CrmReadClientTests
{
    private static ResolvedEnvironment Env() => new(
        "dev", "http://files", "http://files/api/File/Upload",
        "http://files/api/File/Download?path=", "http://files/api/File/Delete?path=",
        "KEY", "https://crm/MoCD", "MOCD", "svc", "pw", IsProduction: false);

    private static (CrmReadClient client, FakeHttpMessageHandler handler) Build()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://crm/MoCD/api/data/v9.1/") };
        return (new CrmReadClient(http, Env()), handler);
    }

    private const string OneDocument = """
    {"value":[{
      "mocd_documentid":"2c9d5572-a77b-f111-b10f-00505601095a",
      "mocd_name":"cert.jpg",
      "modifiedon":"2026-07-09T10:11:12Z",
      "mocd_documentfile":{
        "mocd_documentfileid":"98f9e3b2-867d-49ed-9af4-57cc938ee1f9",
        "mocd_filepath":"DigitalServices\\goodConductCertificate\\20260330\\98f9e3b2-867d-49ed-9af4-57cc938ee1f9.jpg",
        "mocd_hash":"e57d1555e2197c964daa9fd57e197b7b",
        "mocd_name":"cert.jpg",
        "mocd_mediatype":"image/jpeg"},
      "mocd_documenttype":{
        "mocd_documenttypeid":"6e79d291-722b-f111-b119-005056010908",
        "mocd_name":"Payment Receipt",
        "_mocd_servicecatalogue_value":"6bcb221c-6c2b-f111-b119-005056010908"},
      "mocd_employeeappintmentrequest":null,
      "mocd_gamrequest":null,
      "mocd_BylawsAmendmentRequestId":null
    }]}
    """;

    [Fact]
    public async Task GetInScopeDocuments_maps_a_row_completely()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, OneDocument);

        var rows = await client.GetInScopeDocumentsAsync(
            new[] { Guid.Parse("6bcb221c-6c2b-f111-b119-005056010908") }, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"), row.DocumentId);
        Assert.Equal(Guid.Parse("98f9e3b2-867d-49ed-9af4-57cc938ee1f9"), row.DocumentFileId);
        Assert.Equal("e57d1555e2197c964daa9fd57e197b7b", row.Hash);
        Assert.Equal("image/jpeg", row.MediaType);
        Assert.Equal("Payment Receipt", row.DocumentTypeName);
        Assert.Equal(Guid.Parse("6bcb221c-6c2b-f111-b119-005056010908"), row.DocTypeCatalogueId);
        Assert.Null(row.CrossCheckCatalogueId);
        Assert.EndsWith(".jpg", row.FilePath);
    }

    [Fact]
    public async Task The_filter_ors_every_catalogue_and_expands_the_cross_checks()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");

        await client.GetInScopeDocumentsAsync(
            new[] { Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
                    Guid.Parse("930f636a-077a-f111-b119-005056010908") }, CancellationToken.None);

        var url = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("mocd_documenttype/_mocd_servicecatalogue_value eq cd97bf8d-bea8-f011-b116-005056010908", url);
        Assert.Contains(" or ", url);
        Assert.Contains("mocd_employeeappintmentrequest($select=_mocd_servicecatalogue_value)", url);
        Assert.Contains("mocd_gamrequest($select=_mocd_servicecatalogue_value)", url);
        Assert.Contains("mocd_BylawsAmendmentRequestId($select=_mocd_servicecatalogue_value)", url);
    }

    [Fact]
    public async Task Cross_check_catalogue_and_source_are_picked_up_from_whichever_parent_has_one()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """
        {"value":[{
          "mocd_documentid":"11111111-1111-1111-1111-111111111111",
          "mocd_name":"a.pdf","modifiedon":"2026-07-09T10:11:12Z",
          "mocd_documentfile":{"mocd_documentfileid":"22222222-2222-2222-2222-222222222222",
            "mocd_filepath":"DigitalServices\\0\\20260423\\x.pdf","mocd_hash":"h","mocd_name":"a.pdf","mocd_mediatype":"application/pdf"},
          "mocd_documenttype":{"mocd_documenttypeid":"33333333-3333-3333-3333-333333333333",
            "mocd_name":"Board Decision","_mocd_servicecatalogue_value":"cd97bf8d-bea8-f011-b116-005056010908"},
          "mocd_employeeappintmentrequest":{"_mocd_servicecatalogue_value":"cd97bf8d-bea8-f011-b116-005056010908"},
          "mocd_gamrequest":null,"mocd_BylawsAmendmentRequestId":null}]}
        """);

        var rows = await client.GetInScopeDocumentsAsync(new[] { Guid.NewGuid() }, CancellationToken.None);

        Assert.Equal(Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), rows[0].CrossCheckCatalogueId);
        Assert.Equal("mocd_employeeappintmentrequest", rows[0].CrossCheckSource);
    }

    [Fact]
    public async Task Paging_follows_odata_nextLink()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK,
            """{"value":[],"@odata.nextLink":"https://crm/MoCD/api/data/v9.1/mocd_documents?$skiptoken=abc"}""");
        handler.Enqueue(HttpStatusCode.OK, OneDocument);

        var rows = await client.GetInScopeDocumentsAsync(new[] { Guid.NewGuid() }, CancellationToken.None);

        Assert.Single(rows);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("$skiptoken=abc", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task IsServiceCatalogue_is_true_on_200_and_false_on_404()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"mocd_name":"GAM Request"}""");
        handler.Enqueue(HttpStatusCode.NotFound, """{"error":{"message":"Does Not Exist"}}""");

        Assert.True(await client.IsServiceCatalogueAsync("3ff27d73-653e-f111-b119-005056010908", CancellationToken.None));
        Assert.False(await client.IsServiceCatalogueAsync("9b1121f4-e30b-f111-b117-005056010908", CancellationToken.None));
    }

    [Fact]
    public async Task IsServiceCatalogue_is_false_for_a_non_guid_without_calling_the_server()
    {
        var (client, handler) = Build();

        Assert.False(await client.IsServiceCatalogueAsync("goodConductCertificate", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IsServiceCatalogue_caches_so_repeated_segments_cost_one_call()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"mocd_name":"GAM Request"}""");

        await client.IsServiceCatalogueAsync("3ff27d73-653e-f111-b119-005056010908", CancellationToken.None);
        await client.IsServiceCatalogueAsync("3ff27d73-653e-f111-b119-005056010908", CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResolveIdentifier_treats_a_document_guid_as_a_document()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, OneDocument);

        var rows = await client.ResolveIdentifierAsync("2c9d5572-a77b-f111-b10f-00505601095a", CancellationToken.None);

        Assert.Single(rows);
        var url = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("mocd_documentid eq 2c9d5572-a77b-f111-b10f-00505601095a", url);
    }

    [Fact]
    public async Task ResolveIdentifier_falls_back_to_documentfile_then_file_name()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[]}""");   // not a document id
        handler.Enqueue(HttpStatusCode.OK, OneDocument);          // matched as a documentfile id

        var rows = await client.ResolveIdentifierAsync("98f9e3b2-867d-49ed-9af4-57cc938ee1f9", CancellationToken.None);

        Assert.Single(rows);
        var second = Uri.UnescapeDataString(handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("_mocd_documentfile_value eq 98f9e3b2-867d-49ed-9af4-57cc938ee1f9", second);
    }

    [Fact]
    public async Task ResolveIdentifier_matches_a_plain_file_name()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, OneDocument);

        var rows = await client.ResolveIdentifierAsync("cert.jpg", CancellationToken.None);

        Assert.Single(rows);
        var url = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("mocd_documentfile/mocd_name eq 'cert.jpg'", url);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter CrmReadClientTests`
Expected: FAIL — `'CrmReadClient' does not exist`.

- [ ] **Step 3: Write the NTLM HttpClient factory**

Create `src/MocdDocFix/Clients/CrmHttp.cs`:

```csharp
using System.Net;
using MocdDocFix.Config;

namespace MocdDocFix.Clients;

public static class CrmHttp
{
    /// <summary>
    /// On-prem Dataverse is v9.1 maximum — v9.2 returns HTTP 501. NTLM is the only
    /// supported scheme here.
    /// </summary>
    public static HttpClient Create(ResolvedEnvironment env)
    {
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(env.CrmUser, env.CrmPassword, env.CrmDomain),
            PreAuthenticate = true
        };

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{env.CrmUrl}/api/data/v9.1/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        http.DefaultRequestHeaders.Add("OData-Version", "4.0");
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        return http;
    }
}
```

- [ ] **Step 4: Write `DocumentRow`**

Create `src/MocdDocFix/Domain/DocumentRow.cs`:

```csharp
namespace MocdDocFix.Domain;

/// <summary>One document with everything needed to classify and migrate it.</summary>
public sealed record DocumentRow(
    Guid DocumentId,
    string DocumentName,
    Guid DocumentFileId,
    string? FilePath,
    string? FileName,
    string? MediaType,
    string? Hash,
    Guid DocumentTypeId,
    string DocumentTypeName,
    Guid? DocTypeCatalogueId,
    Guid? CrossCheckCatalogueId,
    string? CrossCheckSource,
    DateTimeOffset ModifiedOn)
{
    public string Extension => string.IsNullOrEmpty(FileName) ? string.Empty : Path.GetExtension(FileName);
}
```

- [ ] **Step 5: Write the read client**

Create `src/MocdDocFix/Clients/CrmReadClient.cs`:

```csharp
using System.Text.Json;
using MocdDocFix.Config;
using MocdDocFix.Domain;

namespace MocdDocFix.Clients;

public interface ICrmReadClient
{
    Task<IReadOnlyList<DocumentRow>> GetInScopeDocumentsAsync(IReadOnlyList<Guid> catalogues, CancellationToken ct);
    Task<IReadOnlyList<DocumentRow>> ResolveIdentifierAsync(string identifier, CancellationToken ct);
    Task<bool> IsServiceCatalogueAsync(string candidate, CancellationToken ct);
    Task<DateTimeOffset?> GetDocumentModifiedOnAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// The complete record as raw JSON — every attribute, not a chosen subset. Used by the
    /// backup phase to snapshot the CRM side before any write (spec section 8.2).
    /// </summary>
    Task<string?> GetRawRecordAsync(string entitySet, Guid id, CancellationToken ct);
}

public sealed class CrmReadClient : ICrmReadClient
{
    // Only these three parent entities carry mocd_servicecatalogue (spec section 4.2).
    // Casing matters: the lower-case by-laws form is rejected by the server.
    private static readonly string[] CrossCheckNavigations =
    {
        "mocd_employeeappintmentrequest",
        "mocd_gamrequest",
        "mocd_BylawsAmendmentRequestId"
    };

    private const string Select = "mocd_documentid,mocd_name,modifiedon";

    private static readonly string Expand =
        "mocd_documentfile($select=mocd_filepath,mocd_hash,mocd_name,mocd_mediatype)," +
        "mocd_documenttype($select=mocd_name,_mocd_servicecatalogue_value)," +
        string.Join(",", CrossCheckNavigations.Select(n => $"{n}($select=_mocd_servicecatalogue_value)"));

    private readonly HttpClient _http;
    private readonly ResolvedEnvironment _env;
    private readonly Dictionary<string, bool> _catalogueCache = new(StringComparer.OrdinalIgnoreCase);

    public CrmReadClient(HttpClient http, ResolvedEnvironment env)
    {
        _http = http;
        _env = env;
    }

    public Task<IReadOnlyList<DocumentRow>> GetInScopeDocumentsAsync(
        IReadOnlyList<Guid> catalogues, CancellationToken ct)
    {
        var filter = string.Join(" or ",
            catalogues.Select(c => $"mocd_documenttype/_mocd_servicecatalogue_value eq {c}"));
        return QueryAsync($"mocd_documents?$select={Select}&$filter={filter}&$expand={Expand}", ct);
    }

    public async Task<IReadOnlyList<DocumentRow>> ResolveIdentifierAsync(string identifier, CancellationToken ct)
    {
        identifier = identifier.Trim();

        if (Guid.TryParse(identifier, out var id))
        {
            var asDocument = await QueryAsync(
                $"mocd_documents?$select={Select}&$filter=mocd_documentid eq {id}&$expand={Expand}", ct);
            if (asDocument.Count > 0) return asDocument;

            return await QueryAsync(
                $"mocd_documents?$select={Select}&$filter=_mocd_documentfile_value eq {id}&$expand={Expand}", ct);
        }

        // A file name. The path's file stem is the documentfile id (spec section 4.1), so strip
        // any extension and try that as a GUID first; otherwise match mocd_name.
        var stem = Path.GetFileNameWithoutExtension(identifier);
        if (Guid.TryParse(stem, out var stemId))
        {
            var byStem = await QueryAsync(
                $"mocd_documents?$select={Select}&$filter=_mocd_documentfile_value eq {stemId}&$expand={Expand}", ct);
            if (byStem.Count > 0) return byStem;
        }

        var escaped = identifier.Replace("'", "''");
        return await QueryAsync(
            $"mocd_documents?$select={Select}&$filter=mocd_documentfile/mocd_name eq '{escaped}'&$expand={Expand}", ct);
    }

    public async Task<bool> IsServiceCatalogueAsync(string candidate, CancellationToken ct)
    {
        if (!Guid.TryParse(candidate, out var id)) return false;
        if (_catalogueCache.TryGetValue(candidate, out var cached)) return cached;

        using var response = await _http.GetAsync($"mocd_servicecatalogues({id})?$select=mocd_name", ct);
        var exists = response.IsSuccessStatusCode;
        _catalogueCache[candidate] = exists;
        return exists;
    }

    public async Task<string?> GetRawRecordAsync(string entitySet, Guid id, CancellationToken ct)
    {
        // No $select — we want every attribute, so a restore does not depend on us having
        // predicted which ones matter.
        using var response = await _http.GetAsync($"{entitySet}({id})", ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
    }

    public async Task<DateTimeOffset?> GetDocumentModifiedOnAsync(Guid documentId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"mocd_documents({documentId})?$select=modifiedon", ct);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("modifiedon", out var m) && m.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(m.GetString()!)
            : null;
    }

    private async Task<IReadOnlyList<DocumentRow>> QueryAsync(string url, CancellationToken ct)
    {
        var rows = new List<DocumentRow>();
        string? next = url;

        while (next is not null)
        {
            using var response = await _http.GetAsync(next, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"CRM query failed: HTTP {(int)response.StatusCode}. {body}");

            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("value", out var value))
                foreach (var element in value.EnumerateArray())
                    rows.Add(Map(element));

            next = json.RootElement.TryGetProperty("@odata.nextLink", out var link)
                ? link.GetString()
                : null;
        }

        return rows;
    }

    private static DocumentRow Map(JsonElement e)
    {
        var file = Child(e, "mocd_documentfile");
        var type = Child(e, "mocd_documenttype");

        Guid? crossCheck = null;
        string? crossSource = null;
        foreach (var nav in CrossCheckNavigations)
        {
            var parent = Child(e, nav);
            var value = GuidOrNull(parent, "_mocd_servicecatalogue_value");
            if (value is not null) { crossCheck = value; crossSource = nav; break; }
        }

        return new DocumentRow(
            DocumentId: GuidOrNull(e, "mocd_documentid") ?? Guid.Empty,
            DocumentName: StringOrNull(e, "mocd_name") ?? string.Empty,
            DocumentFileId: GuidOrNull(file, "mocd_documentfileid") ?? Guid.Empty,
            FilePath: StringOrNull(file, "mocd_filepath"),
            FileName: StringOrNull(file, "mocd_name"),
            MediaType: StringOrNull(file, "mocd_mediatype"),
            Hash: StringOrNull(file, "mocd_hash"),
            DocumentTypeId: GuidOrNull(type, "mocd_documenttypeid") ?? Guid.Empty,
            DocumentTypeName: StringOrNull(type, "mocd_name") ?? string.Empty,
            DocTypeCatalogueId: GuidOrNull(type, "_mocd_servicecatalogue_value"),
            CrossCheckCatalogueId: crossCheck,
            CrossCheckSource: crossSource,
            ModifiedOn: StringOrNull(e, "modifiedon") is { } m ? DateTimeOffset.Parse(m) : default);
    }

    private static JsonElement? Child(JsonElement? parent, string name) =>
        parent is { } p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child : null;

    private static string? StringOrNull(JsonElement? e, string name) =>
        e is { } p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static Guid? GuidOrNull(JsonElement? e, string name) =>
        StringOrNull(e, name) is { } s && Guid.TryParse(s, out var g) ? g : null;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter CrmReadClientTests`
Expected: PASS — `Failed: 0, Passed: 10`.

- [ ] **Step 7: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Clients/CrmHttp.cs src/MocdDocFix/Clients/CrmReadClient.cs \
        src/MocdDocFix/Domain/DocumentRow.cs tests/MocdDocFix.Tests/CrmReadClientTests.cs
git commit -m "feat: CRM read client over the Dataverse Web API with NTLM

One query returns the document, its file, its document type catalogue and
the cross-check catalogue from whichever of the three parent entities has
one. Catalogue membership is resolved live and cached."
```

---

## Task 6: CRM write client

Three writes, each verified by reading back. Creating the documentfile with a **client-specified
primary key** is essential: `mocd_documentfileid` must equal the vendor's `FileId` (§4.1).

**Files:**
- Create: `src/MocdDocFix/Clients/CrmWriteClient.cs`
- Test: `tests/MocdDocFix.Tests/CrmWriteClientTests.cs`

**Interfaces:**
- Consumes: `CrmHttp` (T5), `FileData` (T4).
- Produces: `interface ICrmWriteClient` with
  `Task CreateDocumentFileAsync(Guid fileId, string filePath, string? hash, string? name, string? mediaType, string? category, CancellationToken ct)`,
  `Task RepointDocumentAsync(Guid documentId, Guid newFileId, CancellationToken ct)`,
  `Task<Guid?> GetDocumentFileLinkAsync(Guid documentId, CancellationToken ct)`,
  `Task DeleteDocumentFileAsync(Guid fileId, CancellationToken ct)`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/CrmWriteClientTests.cs`:

```csharp
using System.Net;
using MocdDocFix.Clients;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class CrmWriteClientTests
{
    private static readonly Guid NewFileId  = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");

    private static (CrmWriteClient client, FakeHttpMessageHandler handler) Build()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://crm/MoCD/api/data/v9.1/") };
        return (new CrmWriteClient(http), handler);
    }

    [Fact]
    public async Task CreateDocumentFile_posts_the_vendor_FileId_as_the_primary_key()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        await client.CreateDocumentFileAsync(
            NewFileId,
            @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\a41c0b77-1111-2222-3333-444444444444.jpg",
            "e57d1555e2197c964daa9fd57e197b7b", "cert.jpg", "image/jpeg",
            "cd97bf8d-bea8-f011-b116-005056010908", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("mocd_documentfiles", handler.Requests[0].RequestUri!.ToString());

        var body = handler.RequestBodies[0];
        Assert.Contains("\"mocd_documentfileid\":\"a41c0b77-1111-2222-3333-444444444444\"", body);
        Assert.Contains("\"mocd_hash\":\"e57d1555e2197c964daa9fd57e197b7b\"", body);
        Assert.Contains("\"mocd_mediatype\":\"image/jpeg\"", body);
    }

    [Fact]
    public async Task RepointDocument_patches_with_an_odata_bind()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        await client.RepointDocumentAsync(DocumentId, NewFileId, CancellationToken.None);

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Contains($"mocd_documents({DocumentId})", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains($"\"mocd_documentfile@odata.bind\":\"/mocd_documentfiles({NewFileId})\"",
                        handler.RequestBodies[0]);
    }

    [Fact]
    public async Task GetDocumentFileLink_reads_back_the_lookup()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK,
            $$"""{"_mocd_documentfile_value":"{{NewFileId}}"}""");

        var linked = await client.GetDocumentFileLinkAsync(DocumentId, CancellationToken.None);

        Assert.Equal(NewFileId, linked);
    }

    [Fact]
    public async Task GetDocumentFileLink_returns_null_when_the_lookup_is_empty()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, """{"_mocd_documentfile_value":null}""");

        Assert.Null(await client.GetDocumentFileLinkAsync(DocumentId, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteDocumentFile_issues_a_DELETE()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        await client.DeleteDocumentFileAsync(NewFileId, CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Contains($"mocd_documentfiles({NewFileId})", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task A_failed_write_throws_with_the_server_message()
    {
        var (client, handler) = Build();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"duplicate key"}}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RepointDocumentAsync(DocumentId, NewFileId, CancellationToken.None));

        Assert.Contains("duplicate key", ex.Message);
        Assert.Contains("400", ex.Message);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter CrmWriteClientTests`
Expected: FAIL — `'CrmWriteClient' does not exist`.

- [ ] **Step 3: Write the write client**

Create `src/MocdDocFix/Clients/CrmWriteClient.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace MocdDocFix.Clients;

public interface ICrmWriteClient
{
    Task CreateDocumentFileAsync(Guid fileId, string filePath, string? hash, string? name,
        string? mediaType, string? category, CancellationToken ct);
    Task RepointDocumentAsync(Guid documentId, Guid newFileId, CancellationToken ct);
    Task<Guid?> GetDocumentFileLinkAsync(Guid documentId, CancellationToken ct);
    Task DeleteDocumentFileAsync(Guid fileId, CancellationToken ct);
}

public sealed class CrmWriteClient : ICrmWriteClient
{
    private readonly HttpClient _http;

    public CrmWriteClient(HttpClient http) => _http = http;

    /// <summary>
    /// Creates the row with the vendor's FileId as its primary key, preserving the invariant
    /// mocd_documentfileid == vendor FileId == the path's file stem (spec section 4.1).
    /// </summary>
    public Task CreateDocumentFileAsync(Guid fileId, string filePath, string? hash, string? name,
        string? mediaType, string? category, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["mocd_documentfileid"] = fileId,
            ["mocd_filepath"] = filePath,
            ["mocd_hash"] = hash,
            ["mocd_name"] = name,
            ["mocd_mediatype"] = mediaType,
            ["mocd_category"] = category
        };

        return SendAsync(HttpMethod.Post, "mocd_documentfiles", payload, ct);
    }

    public Task RepointDocumentAsync(Guid documentId, Guid newFileId, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["mocd_documentfile@odata.bind"] = $"/mocd_documentfiles({newFileId})"
        };

        return SendAsync(HttpMethod.Patch, $"mocd_documents({documentId})", payload, ct);
    }

    public async Task<Guid?> GetDocumentFileLinkAsync(Guid documentId, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"mocd_documents({documentId})?$select=_mocd_documentfile_value", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not read document {documentId}: HTTP {(int)response.StatusCode}. {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("_mocd_documentfile_value", out var v) &&
               v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g)
            ? g : null;
    }

    public Task DeleteDocumentFileAsync(Guid fileId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"mocd_documentfiles({fileId})", payload: null, ct);

    private async Task SendAsync(HttpMethod method, string url, Dictionary<string, object?>? payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"CRM {method} {url} failed: HTTP {(int)response.StatusCode}. {body}");
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter CrmWriteClientTests`
Expected: PASS — `Failed: 0, Passed: 6`.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 52 tests.

- [ ] **Step 6: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Clients/CrmWriteClient.cs tests/MocdDocFix.Tests/CrmWriteClientTests.cs
git commit -m "feat: CRM write client for documentfile create, repoint and delete

The new documentfile row is created with the vendor FileId as its primary
key, keeping mocd_documentfileid == FileId == path file stem. Every failed
write throws with the server's own message."
```

---

## Task 7: The verification gate

The safety-critical component. Six checks (§6.2), all pure functions so they are fully covered by
unit tests. Two rules encoded structurally:

- **Never assume the vendor's hash algorithm** (§6.1). Vendor hashes are only ever compared to
  vendor hashes; our own hash is SHA-256 and only ever compared to our own.
- **Checks 5 and 6 halt the whole run**, not just the file — they mean an assumption is wrong.

**Files:**
- Create: `src/MocdDocFix/Verification/CheckResult.cs`, `VerificationReport.cs`, `Verifier.cs`
- Test: `tests/MocdDocFix.Tests/VerifierTests.cs`

**Interfaces:**
- Consumes: `FilePathParts` (T1).
- Produces:
  - `record CheckResult(string Name, bool Passed, string Detail, bool HaltsRun)`
  - `record VerificationReport(IReadOnlyList<CheckResult> Checks)` with `AllPassed`, `MustHalt`, `Failures`
  - `static class Verifier` with `OurHash`, `BackupIntegrity`, `UploadHashMatches`, `RoundTrip`,
    `PathIsFixed`, `IsGenuinelyNew`, `CrmTookTheChange`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/VerifierTests.cs`:

```csharp
using System.Text;
using MocdDocFix.Domain;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

public class VerifierTests
{
    private static readonly Guid Correct   = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid NewFileId = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid OldFileId = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");

    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("the file contents");

    private const string NewPath =
        @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\a41c0b77-1111-2222-3333-444444444444.jpg";
    private const string OldPath =
        @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg";

    // --- check 1 -----------------------------------------------------------

    [Fact]
    public void Backup_integrity_passes_when_the_two_vendor_hashes_agree()
        => Assert.True(Verifier.BackupIntegrity("abc123", "abc123").Passed);

    [Fact]
    public void Backup_integrity_is_case_insensitive()
        => Assert.True(Verifier.BackupIntegrity("ABC123", "abc123").Passed);

    [Fact]
    public void Backup_integrity_fails_when_CRM_and_the_file_server_disagree()
    {
        var r = Verifier.BackupIntegrity("abc123", "def456");

        Assert.False(r.Passed);
        Assert.False(r.HaltsRun);                       // quarantine this file only
        Assert.Contains("abc123", r.Detail);
        Assert.Contains("def456", r.Detail);
    }

    [Fact]
    public void Backup_integrity_fails_when_either_hash_is_missing()
    {
        Assert.False(Verifier.BackupIntegrity(null, "abc").Passed);
        Assert.False(Verifier.BackupIntegrity("abc", null).Passed);
    }

    // --- check 2 -----------------------------------------------------------

    [Fact]
    public void Upload_hash_must_equal_the_old_vendor_hash()
    {
        Assert.True(Verifier.UploadHashMatches("h1", "h1").Passed);
        Assert.False(Verifier.UploadHashMatches("h1", "h2").Passed);
    }

    // --- check 3 -----------------------------------------------------------

    [Fact]
    public void Round_trip_passes_only_on_byte_for_byte_equality()
    {
        var r = Verifier.RoundTrip(Bytes, Bytes.ToArray());

        Assert.True(r.Passed);
        Assert.Contains("byte-for-byte", r.Detail);
    }

    [Fact]
    public void Round_trip_fails_on_a_length_difference()
    {
        var r = Verifier.RoundTrip(Bytes, Bytes.Take(5).ToArray());

        Assert.False(r.Passed);
        Assert.Contains("length", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Round_trip_fails_on_a_single_flipped_byte_of_the_same_length()
    {
        var corrupted = Bytes.ToArray();
        corrupted[3] ^= 0xFF;

        Assert.False(Verifier.RoundTrip(Bytes, corrupted).Passed);
    }

    [Fact]
    public void Our_hash_is_stable_and_content_derived()
    {
        Assert.Equal(Verifier.OurHash(Bytes), Verifier.OurHash(Bytes.ToArray()));
        Assert.NotEqual(Verifier.OurHash(Bytes), Verifier.OurHash(Encoding.UTF8.GetBytes("different")));
        Assert.Equal(64, Verifier.OurHash(Bytes).Length);      // SHA-256 hex
    }

    // --- check 4 -----------------------------------------------------------

    [Fact]
    public void Path_is_fixed_when_the_segment_matches_and_the_stem_is_the_file_id()
        => Assert.True(Verifier.PathIsFixed(FilePathParser.Parse(NewPath), Correct, NewFileId).Passed);

    [Fact]
    public void Path_is_fixed_ignores_catalogue_casing()
    {
        var upper = NewPath.Replace("cd97bf8d-bea8-f011-b116-005056010908",
                                    "CD97BF8D-BEA8-F011-B116-005056010908");

        Assert.True(Verifier.PathIsFixed(FilePathParser.Parse(upper), Correct, NewFileId).Passed);
    }

    [Fact]
    public void Path_is_fixed_fails_when_the_vendor_filed_it_somewhere_else()
    {
        var wrong = FilePathParser.Parse(
            @"DigitalServices\DigitalServices\20260910\a41c0b77-1111-2222-3333-444444444444.jpg");

        var r = Verifier.PathIsFixed(wrong, Correct, NewFileId);

        Assert.False(r.Passed);
        Assert.Contains("DigitalServices", r.Detail);
    }

    [Fact]
    public void Path_is_fixed_fails_when_the_file_stem_is_not_the_returned_file_id()
    {
        var mismatched = FilePathParser.Parse(
            @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\99999999-9999-9999-9999-999999999999.jpg");

        var r = Verifier.PathIsFixed(mismatched, Correct, NewFileId);

        Assert.False(r.Passed);
        Assert.Contains("stem", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // --- check 5 (halts the run) ------------------------------------------

    [Fact]
    public void Genuinely_new_passes_for_a_distinct_path_and_id()
    {
        var r = Verifier.IsGenuinelyNew(OldPath, NewPath, OldFileId, NewFileId);

        Assert.True(r.Passed);
        Assert.True(r.HaltsRun);          // the flag describes the consequence of failure
    }

    [Fact]
    public void Genuinely_new_fails_and_halts_when_the_vendor_returns_the_same_path()
    {
        var r = Verifier.IsGenuinelyNew(OldPath, OldPath, OldFileId, NewFileId);

        Assert.False(r.Passed);
        Assert.True(r.HaltsRun);
        Assert.Contains("deduplicat", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Genuinely_new_fails_and_halts_when_the_vendor_returns_the_same_file_id()
    {
        var r = Verifier.IsGenuinelyNew(OldPath, NewPath, OldFileId, OldFileId);

        Assert.False(r.Passed);
        Assert.True(r.HaltsRun);
    }

    // --- check 6 (halts the run) ------------------------------------------

    [Fact]
    public void Crm_took_the_change_passes_when_the_lookup_reads_back_as_the_new_file()
        => Assert.True(Verifier.CrmTookTheChange(NewFileId, NewFileId).Passed);

    [Fact]
    public void Crm_took_the_change_fails_and_halts_when_the_lookup_is_stale_or_empty()
    {
        Assert.False(Verifier.CrmTookTheChange(NewFileId, OldFileId).Passed);
        Assert.True(Verifier.CrmTookTheChange(NewFileId, OldFileId).HaltsRun);
        Assert.False(Verifier.CrmTookTheChange(NewFileId, null).Passed);
    }

    // --- report ------------------------------------------------------------

    [Fact]
    public void Report_summarises_pass_fail_and_halt()
    {
        var ok    = new CheckResult("a", true,  "fine", false);
        var soft  = new CheckResult("b", false, "bad",  false);
        var hard  = new CheckResult("c", false, "very bad", true);

        Assert.True(new VerificationReport(new[] { ok }).AllPassed);
        Assert.False(new VerificationReport(new[] { ok, soft }).AllPassed);
        Assert.False(new VerificationReport(new[] { ok, soft }).MustHalt);
        Assert.True(new VerificationReport(new[] { ok, hard }).MustHalt);
        Assert.Equal(2, new VerificationReport(new[] { ok, soft, hard }).Failures.Count());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter VerifierTests`
Expected: FAIL — `'Verifier' does not exist`.

- [ ] **Step 3: Write the result types**

Create `src/MocdDocFix/Verification/CheckResult.cs`:

```csharp
namespace MocdDocFix.Verification;

/// <param name="HaltsRun">
/// True when a failure of this check means a shared assumption is wrong, so the whole run
/// must stop rather than just this document.
/// </param>
public sealed record CheckResult(string Name, bool Passed, string Detail, bool HaltsRun);
```

Create `src/MocdDocFix/Verification/VerificationReport.cs`:

```csharp
namespace MocdDocFix.Verification;

public sealed record VerificationReport(IReadOnlyList<CheckResult> Checks)
{
    public bool AllPassed => Checks.All(c => c.Passed);
    public bool MustHalt => Checks.Any(c => !c.Passed && c.HaltsRun);
    public IEnumerable<CheckResult> Failures => Checks.Where(c => !c.Passed);
}
```

- [ ] **Step 4: Write the verifier**

Create `src/MocdDocFix/Verification/Verifier.cs`:

```csharp
using System.Security.Cryptography;
using MocdDocFix.Domain;

namespace MocdDocFix.Verification;

/// <summary>
/// The six checks of spec section 6.2. Comparisons are always like-for-like: vendor hash to
/// vendor hash, our hash to our hash, bytes to bytes. We never assume the vendor's algorithm.
/// </summary>
public static class Verifier
{
    /// <summary>Our own hash. SHA-256, never compared against a vendor hash.</summary>
    public static string OurHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Check 1 — CRM's stored hash agrees with what the file server reports today.</summary>
    public static CheckResult BackupIntegrity(string? crmHash, string? serverHash)
    {
        if (string.IsNullOrWhiteSpace(crmHash) || string.IsNullOrWhiteSpace(serverHash))
            return new CheckResult("backup-integrity", false,
                $"A hash is missing (CRM='{crmHash ?? "null"}', server='{serverHash ?? "null"}').", false);

        var same = string.Equals(crmHash, serverHash, StringComparison.OrdinalIgnoreCase);
        return new CheckResult("backup-integrity", same,
            same ? $"CRM and file server agree ({crmHash})."
                 : $"CRM hash '{crmHash}' does not match the file server's '{serverHash}'. " +
                   "This record was already inconsistent — quarantined.",
            false);
    }

    /// <summary>Check 2 — the vendor computed the same hash for the bytes we just uploaded.</summary>
    public static CheckResult UploadHashMatches(string? oldVendorHash, string? newVendorHash)
    {
        if (string.IsNullOrWhiteSpace(oldVendorHash) || string.IsNullOrWhiteSpace(newVendorHash))
            return new CheckResult("upload-hash", false,
                $"A vendor hash is missing (old='{oldVendorHash ?? "null"}', new='{newVendorHash ?? "null"}').", false);

        var same = string.Equals(oldVendorHash, newVendorHash, StringComparison.OrdinalIgnoreCase);
        return new CheckResult("upload-hash", same,
            same ? $"Vendor hash unchanged ({oldVendorHash})."
                 : $"Vendor hash changed: '{oldVendorHash}' → '{newVendorHash}'. Content differs.",
            false);
    }

    /// <summary>
    /// Check 3 — the new file downloads and is byte-identical. This is the real proof:
    /// Success=true only means the upload was accepted, not that a readable file exists.
    /// </summary>
    public static CheckResult RoundTrip(byte[] oldBytes, byte[] newBytes)
    {
        if (oldBytes.Length != newBytes.Length)
            return new CheckResult("round-trip", false,
                $"Length differs: {oldBytes.Length:N0} vs {newBytes.Length:N0} bytes.", false);

        if (!oldBytes.AsSpan().SequenceEqual(newBytes))
            return new CheckResult("round-trip", false,
                $"Same length ({oldBytes.Length:N0}) but content differs. " +
                $"our hash {OurHash(oldBytes)} vs {OurHash(newBytes)}.", false);

        return new CheckResult("round-trip", true,
            $"Downloaded copy is byte-for-byte identical ({oldBytes.Length:N0} bytes, {OurHash(newBytes)}).", false);
    }

    /// <summary>Check 4 — the new path really is under the correct catalogue.</summary>
    public static CheckResult PathIsFixed(FilePathParts newPath, Guid correctCatalogue, Guid newFileId)
    {
        if (newPath.CategorySegment is null ||
            !Guid.TryParse(newPath.CategorySegment, out var segment) ||
            segment != correctCatalogue)
        {
            return new CheckResult("path-fixed", false,
                $"New path segment is '{newPath.CategorySegment ?? "(absent)"}', expected {correctCatalogue}. " +
                $"Path: {newPath.Raw}", false);
        }

        if (!Guid.TryParse(newPath.FileStem, out var stem) || stem != newFileId)
        {
            return new CheckResult("path-fixed", false,
                $"File stem '{newPath.FileStem}' does not equal the returned FileId {newFileId}.", false);
        }

        return new CheckResult("path-fixed", true, $"Filed under {correctCatalogue}.", false);
    }

    /// <summary>
    /// Check 5 — HALTS THE RUN. Guards against content-hash deduplication handing us back the
    /// existing file: we would repoint to the old file and the delete pass would then destroy
    /// the only copy.
    /// </summary>
    public static CheckResult IsGenuinelyNew(string oldPath, string newPath, Guid oldFileId, Guid newFileId)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return new CheckResult("genuinely-new", false,
                $"The vendor returned the SAME path ({newPath}). It may be deduplicating by content hash. " +
                "Stopping — continuing risks deleting the only copy.", true);

        if (oldFileId == newFileId)
            return new CheckResult("genuinely-new", false,
                $"The vendor returned the SAME FileId ({newFileId}). Possible deduplication. Stopping.", true);

        return new CheckResult("genuinely-new", true, $"New file {newFileId} is distinct from {oldFileId}.", true);
    }

    /// <summary>Check 6 — HALTS THE RUN. The document really points at the new file.</summary>
    public static CheckResult CrmTookTheChange(Guid expectedFileId, Guid? actualLinkedFileId)
    {
        var ok = actualLinkedFileId == expectedFileId;
        return new CheckResult("crm-repointed", ok,
            ok ? $"Document now points at {expectedFileId}."
               : $"After the update the document points at '{actualLinkedFileId?.ToString() ?? "null"}', " +
                 $"expected {expectedFileId}. Stopping.",
            true);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter VerifierTests`
Expected: PASS — `Failed: 0, Passed: 19`.

- [ ] **Step 6: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Verification tests/MocdDocFix.Tests/VerifierTests.cs
git commit -m "feat: six-check verification gate

Vendor hashes compare only to vendor hashes, our SHA-256 only to itself,
and the decisive check is byte-for-byte equality after downloading the new
file back. Deduplication and failed-repoint checks halt the whole run."
```

---

## Task 8: Resumable state store

1555 documents means an interruption is likely — the VPN dropped during the research for this
spec. Append-only JSONL, last record per document wins, so a restart resumes and never
double-uploads (§8, §9).

**Files:**
- Create: `src/MocdDocFix/Domain/MigrationState.cs`, `src/MocdDocFix/Storage/StateStore.cs`
- Test: `tests/MocdDocFix.Tests/StateStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum MigrationState { Pending, BackedUp, Uploaded, Verified, Repointed, Deleted, Quarantined, Failed }`
  - `record StateRecord(Guid DocumentId, MigrationState State, DateTimeOffset At, Guid? NewFileId, string? NewFilePath, string? Detail)`
  - `class StateStore { void Append(StateRecord); IReadOnlyDictionary<Guid,StateRecord> LoadLatest(); bool IsAtLeast(Guid, MigrationState); }`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/StateStoreTests.cs`:

```csharp
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-state-" + Guid.NewGuid());
    private string Path_ => Path.Combine(_dir, "state-dev.jsonl");

    public StateStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static StateRecord Rec(Guid id, MigrationState s, Guid? newFileId = null) =>
        new(id, s, DateTimeOffset.UtcNow, newFileId, null, null);

    [Fact]
    public void LoadLatest_on_a_missing_file_is_empty()
        => Assert.Empty(new StateStore(Path_).LoadLatest());

    [Fact]
    public void Append_then_load_returns_the_record()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(Path_);

        store.Append(Rec(id, MigrationState.BackedUp));

        Assert.Equal(MigrationState.BackedUp, store.LoadLatest()[id].State);
    }

    [Fact]
    public void The_last_record_for_a_document_wins()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(Path_);

        store.Append(Rec(id, MigrationState.BackedUp));
        store.Append(Rec(id, MigrationState.Uploaded));
        store.Append(Rec(id, MigrationState.Repointed));

        Assert.Equal(MigrationState.Repointed, store.LoadLatest()[id].State);
        Assert.Single(store.LoadLatest());
    }

    [Fact]
    public void History_is_preserved_on_disk_even_though_only_the_latest_is_returned()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(Path_);
        store.Append(Rec(id, MigrationState.BackedUp));
        store.Append(Rec(id, MigrationState.Repointed));

        Assert.Equal(2, File.ReadAllLines(Path_).Length);
    }

    [Fact]
    public void A_new_store_over_the_same_file_sees_earlier_progress()
    {
        var id = Guid.NewGuid();
        new StateStore(Path_).Append(Rec(id, MigrationState.Verified));

        Assert.Equal(MigrationState.Verified, new StateStore(Path_).LoadLatest()[id].State);
    }

    [Fact]
    public void IsAtLeast_orders_the_happy_path_states()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(Path_);
        store.Append(Rec(id, MigrationState.Verified));

        Assert.True(store.IsAtLeast(id, MigrationState.BackedUp));
        Assert.True(store.IsAtLeast(id, MigrationState.Verified));
        Assert.False(store.IsAtLeast(id, MigrationState.Repointed));
        Assert.False(store.IsAtLeast(Guid.NewGuid(), MigrationState.BackedUp));
    }

    [Fact]
    public void Quarantined_and_Failed_never_count_as_progress()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(Path_);
        store.Append(Rec(id, MigrationState.Quarantined));

        Assert.False(store.IsAtLeast(id, MigrationState.BackedUp));
    }

    [Fact]
    public void The_new_file_id_and_path_survive_the_round_trip()
    {
        var id = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var store = new StateStore(Path_);

        store.Append(new StateRecord(id, MigrationState.Repointed, DateTimeOffset.UtcNow,
            fileId, @"DigitalServices\cat\20260910\x.jpg", "ok"));

        var latest = store.LoadLatest()[id];
        Assert.Equal(fileId, latest.NewFileId);
        Assert.Equal(@"DigitalServices\cat\20260910\x.jpg", latest.NewFilePath);
    }

    [Fact]
    public void A_corrupt_line_is_skipped_rather_than_killing_the_run()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(Path_);
        store.Append(Rec(id, MigrationState.BackedUp));
        File.AppendAllText(Path_, "{ this is not json" + Environment.NewLine);
        store.Append(Rec(id, MigrationState.Uploaded));

        Assert.Equal(MigrationState.Uploaded, store.LoadLatest()[id].State);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter StateStoreTests`
Expected: FAIL — `'StateStore' does not exist`.

- [ ] **Step 3: Write the state enum**

Create `src/MocdDocFix/Domain/MigrationState.cs`:

```csharp
namespace MocdDocFix.Domain;

/// <summary>
/// The happy path runs Pending → BackedUp → Uploaded → Verified → Repointed → Deleted.
/// Quarantined and Failed are terminal and never count as progress.
/// </summary>
public enum MigrationState
{
    Pending = 0,
    BackedUp = 1,
    Uploaded = 2,
    Verified = 3,
    Repointed = 4,
    Deleted = 5,

    Quarantined = 90,
    Failed = 91
}
```

- [ ] **Step 4: Write the state store**

Create `src/MocdDocFix/Storage/StateStore.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

public sealed record StateRecord(
    Guid DocumentId,
    MigrationState State,
    DateTimeOffset At,
    Guid? NewFileId,
    string? NewFilePath,
    string? Detail);

/// <summary>
/// Append-only JSONL. Nothing is ever rewritten, so an interrupted run leaves a readable
/// history and a restart resumes from the last known state per document.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public StateStore(string path) => _path = path;

    public void Append(StateRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, JsonSerializer.Serialize(record, Json) + Environment.NewLine);
    }

    public IReadOnlyDictionary<Guid, StateRecord> LoadLatest()
    {
        var latest = new Dictionary<Guid, StateRecord>();
        if (!File.Exists(_path)) return latest;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            StateRecord? record;
            try { record = JsonSerializer.Deserialize<StateRecord>(line, Json); }
            catch (JsonException) { continue; }   // a torn write should not kill a resume

            if (record is not null) latest[record.DocumentId] = record;
        }

        return latest;
    }

    /// <summary>True when the document has reached <paramref name="state"/> on the happy path.</summary>
    public bool IsAtLeast(Guid documentId, MigrationState state)
    {
        if (!LoadLatest().TryGetValue(documentId, out var record)) return false;
        if (record.State is MigrationState.Quarantined or MigrationState.Failed) return false;
        return record.State >= state;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter StateStoreTests`
Expected: PASS — `Failed: 0, Passed: 9`.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 80 tests.

- [ ] **Step 7: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Domain/MigrationState.cs src/MocdDocFix/Storage tests/MocdDocFix.Tests/StateStoreTests.cs
git commit -m "feat: append-only resumable state store

Last record per document wins, corrupt lines are skipped rather than
aborting a resume, and Quarantined/Failed never count as progress."
```

---

## Task 9: Backup store and restore manifest

Writes verified local copies and the manifest. Remember what the manifest is and is not (§5.1):
it protects the **bytes**, it is not an undo — a re-upload can never return a file to its original
path. The real rollback is that the old file still exists until phase 5.

**Files:**
- Create: `src/MocdDocFix/Storage/BackupStore.cs`
- Test: `tests/MocdDocFix.Tests/BackupStoreTests.cs`

**Interfaces:**
- Consumes: `Verifier.OurHash` (T7).
- Produces:
  - `record BackupResult(string LocalPath, long Bytes, string OurHash)`
  - `record ManifestEntry(Guid DocumentId, Guid OldFileId, string OldFilePath, string? OldVendorHash, string? FileName, string? MediaType, string Extension, string? OldCategory, Guid CorrectCatalogueId, string LocalPath, long Bytes, string OurHash, DateTimeOffset At)`
  - `class BackupStore { BackupResult Save(...); void AppendManifest(...); IReadOnlyList<ManifestEntry> LoadManifest(); byte[] Read(string); static long FreeSpaceBytes(string) }`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/BackupStoreTests.cs`:

```csharp
using System.Text;
using MocdDocFix.Storage;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

public class BackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-backup-" + Guid.NewGuid());

    private BackupStore Store() =>
        new(Path.Combine(_root, "backup"), Path.Combine(_root, "restore-manifest.jsonl"));

    public BackupStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("pretend this is a certificate");

    [Fact]
    public void Save_writes_the_file_named_by_the_old_file_id()
    {
        var id = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");

        var result = Store().Save(id, ".jpg", Bytes);

        Assert.True(File.Exists(result.LocalPath));
        Assert.EndsWith("5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg", result.LocalPath);
        Assert.Equal(Bytes.Length, result.Bytes);
        Assert.Equal(Verifier.OurHash(Bytes), result.OurHash);
        Assert.Equal(Bytes, File.ReadAllBytes(result.LocalPath));
    }

    [Fact]
    public void Save_creates_the_directory_if_it_is_missing()
    {
        var result = Store().Save(Guid.NewGuid(), ".pdf", Bytes);

        Assert.True(Directory.Exists(Path.GetDirectoryName(result.LocalPath)));
    }

    [Fact]
    public void Save_tolerates_a_missing_extension()
    {
        var result = Store().Save(Guid.NewGuid(), "", Bytes);

        Assert.True(File.Exists(result.LocalPath));
    }

    [Fact]
    public void Read_returns_exactly_what_was_saved()
    {
        var store = Store();
        var result = store.Save(Guid.NewGuid(), ".jpg", Bytes);

        Assert.Equal(Bytes, store.Read(result.LocalPath));
    }

    [Fact]
    public void Manifest_round_trips_every_field_needed_to_re_upload()
    {
        var store = Store();
        var entry = new ManifestEntry(
            DocumentId: Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"),
            OldFileId: Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187"),
            OldFilePath: @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg",
            OldVendorHash: "e57d1555e2197c964daa9fd57e197b7b",
            FileName: "cert.jpg",
            MediaType: "image/jpeg",
            Extension: ".jpg",
            OldCategory: "goodConductCertificate",
            CorrectCatalogueId: Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
            LocalPath: @"C:\tmp\5b05398a.jpg",
            Bytes: 350208,
            OurHash: "abc",
            At: DateTimeOffset.UtcNow);

        store.AppendManifest(entry);
        var loaded = Assert.Single(store.LoadManifest());

        Assert.Equal(entry.OldFilePath, loaded.OldFilePath);
        Assert.Equal(entry.OldVendorHash, loaded.OldVendorHash);
        Assert.Equal(entry.MediaType, loaded.MediaType);
        Assert.Equal(entry.OldCategory, loaded.OldCategory);
        Assert.Equal(entry.CorrectCatalogueId, loaded.CorrectCatalogueId);
        Assert.Equal(350208, loaded.Bytes);
    }

    [Fact]
    public void LoadManifest_is_empty_before_anything_is_written()
        => Assert.Empty(Store().LoadManifest());

    [Fact]
    public void Backslashes_in_the_stored_path_survive_json_round_tripping()
    {
        var store = Store();
        const string path = @"DigitalServices\0\20260423\c91918d7-5698-4d08-b227-0005d35e76db.png";
        store.AppendManifest(new ManifestEntry(Guid.NewGuid(), Guid.NewGuid(), path, "h", "a.png",
            "image/png", ".png", "0", Guid.NewGuid(), "local", 1, "h", DateTimeOffset.UtcNow));

        Assert.Equal(path, store.LoadManifest()[0].OldFilePath);
    }

    [Fact]
    public void FreeSpaceBytes_reports_something_positive_for_the_temp_drive()
        => Assert.True(BackupStore.FreeSpaceBytes(Path.GetTempPath()) > 0);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter BackupStoreTests`
Expected: FAIL — `'BackupStore' does not exist`.

- [ ] **Step 3: Write the backup store**

Create `src/MocdDocFix/Storage/BackupStore.cs`:

```csharp
using System.Text.Json;
using MocdDocFix.Verification;

namespace MocdDocFix.Storage;

public sealed record BackupResult(string LocalPath, long Bytes, string OurHash);

/// <summary>
/// Everything needed to rebuild a file AND its CRM records (spec section 8.2). Note this
/// restores the bytes and the records, NOT the path — a re-upload always lands under a new
/// FileId and today's date folder (spec section 5.1).
/// </summary>
/// <param name="DocumentFileSnapshotJson">
/// The complete old mocd_documentfile record as raw JSON, captured before any write. Every
/// attribute, so a restore does not depend on us having predicted which ones matter.
/// </param>
/// <param name="DocumentSnapshotJson">
/// The complete old mocd_document record, including the _mocd_documentfile_value that was in
/// place before we repointed it.
/// </param>
public sealed record ManifestEntry(
    Guid DocumentId,
    Guid OldFileId,
    string OldFilePath,
    string? OldVendorHash,
    string? FileName,
    string? MediaType,
    string Extension,
    string? OldCategory,
    Guid CorrectCatalogueId,
    string LocalPath,
    long Bytes,
    string OurHash,
    DateTimeOffset At,
    string? DocumentFileSnapshotJson = null,
    string? DocumentSnapshotJson = null,
    Guid DocumentTypeId = default,
    string? DocumentTypeName = null);

public sealed class BackupStore
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly string _backupDir;
    private readonly string _manifestPath;

    public BackupStore(string backupDir, string manifestPath)
    {
        _backupDir = backupDir;
        _manifestPath = manifestPath;
    }

    public BackupResult Save(Guid oldFileId, string extension, byte[] bytes)
    {
        Directory.CreateDirectory(_backupDir);
        var path = Path.Combine(_backupDir, oldFileId + extension);
        File.WriteAllBytes(path, bytes);
        return new BackupResult(path, bytes.Length, Verifier.OurHash(bytes));
    }

    public byte[] Read(string localPath) => File.ReadAllBytes(localPath);

    public void AppendManifest(ManifestEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        File.AppendAllText(_manifestPath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
    }

    public IReadOnlyList<ManifestEntry> LoadManifest()
    {
        var entries = new List<ManifestEntry>();
        if (!File.Exists(_manifestPath)) return entries;

        foreach (var line in File.ReadLines(_manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ManifestEntry>(line, Json);
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException) { /* skip a torn line rather than abort */ }
        }

        return entries;
    }

    public static long FreeSpaceBytes(string anyPathOnTheDrive)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(anyPathOnTheDrive));
        return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter BackupStoreTests`
Expected: PASS — `Failed: 0, Passed: 8`.

- [ ] **Step 5: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Storage/BackupStore.cs tests/MocdDocFix.Tests/BackupStoreTests.cs
git commit -m "feat: local backup store and restore manifest

Files are named by the old vendor FileId and hashed on write. The manifest
carries everything needed to re-upload, though it restores bytes and not
paths — the real rollback is that old files survive until the delete pass."
```

---

## Task 10: CSV reporting

Five artefacts (§8). CsvHelper handles quoting, which matters — file names contain commas,
apostrophes and Arabic text.

**Files:**
- Create: `src/MocdDocFix/Storage/Reporter.cs`
- Modify: `src/MocdDocFix/MocdDocFix.csproj` (add `CsvHelper`)
- Test: `tests/MocdDocFix.Tests/ReporterTests.cs`

**Interfaces:**
- Consumes: `DocumentRow` (T5), `Classification` (T2).
- Produces:
  - `record ScanRow(...)`, `record MigrationRow(...)` — exact members in Step 3
  - `class Reporter { string WriteScan(...); string WriteReview(...); string WriteQuarantine(...); string WriteMigration(...); static string CrmLink(string crmUrl, Guid documentId) }`

- [ ] **Step 1: Add CsvHelper**

```bash
cd /d/mocd-docfix
dotnet add src/MocdDocFix/MocdDocFix.csproj package CsvHelper
```

- [ ] **Step 2: Write the failing test**

Create `tests/MocdDocFix.Tests/ReporterTests.cs`:

```csharp
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class ReporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-report-" + Guid.NewGuid());

    public ReporterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static ScanRow Row(string fileName = "cert.jpg", Verdict verdict = Verdict.Fix) => new(
        DocumentId: Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"),
        DocumentFileId: Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187"),
        FileName: fileName,
        DocumentTypeName: "Certificate of Good Conduct",
        ServiceCatalogueId: Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
        OldFilePath: @"DigitalServices\goodConductCertificate\20260330\5b05398a.jpg",
        CurrentSegment: "goodConductCertificate",
        CorrectCatalogueId: Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
        Verdict: verdict.ToString(),
        Reason: "path segment is a document type name",
        Solution: "Re-upload with Category = cd97bf8d-…, repoint, then delete the old file.",
        CrossCheckSource: "mocd_employeeappintmentrequest",
        CrmLink: "https://crm/MoCD/main.aspx?etn=mocd_document&id=2c9d5572-a77b-f111-b10f-00505601095a");

    [Fact]
    public void WriteScan_creates_a_file_with_a_header_and_the_row()
    {
        var path = new Reporter(_dir).WriteScan("dev", new[] { Row() });

        var text = File.ReadAllText(path);
        Assert.Contains("DocumentId", text);
        Assert.Contains("2c9d5572-a77b-f111-b10f-00505601095a", text);
        Assert.Contains("goodConductCertificate", text);
        Assert.Contains("Fix", text);
    }

    [Fact]
    public void The_file_name_carries_the_environment_so_output_is_never_ambiguous()
    {
        var path = new Reporter(_dir).WriteScan("preprod", new[] { Row() });

        Assert.Contains("preprod", Path.GetFileName(path));
        Assert.StartsWith("scan-", Path.GetFileName(path));
        Assert.EndsWith(".csv", path);
    }

    [Fact]
    public void Commas_and_quotes_in_a_file_name_are_escaped_not_corrupted()
    {
        var path = new Reporter(_dir).WriteScan("dev", new[] { Row("my, \"odd\" name.jpg") });

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);                       // header plus exactly one record
        Assert.Contains("\"my, \"\"odd\"\" name.jpg\"", lines[1]);
    }

    [Fact]
    public void Arabic_file_names_survive_as_utf8()
    {
        var path = new Reporter(_dir).WriteScan("dev", new[] { Row("شهادة.pdf") });

        Assert.Contains("شهادة.pdf", File.ReadAllText(path, System.Text.Encoding.UTF8));
    }

    [Fact]
    public void Review_and_quarantine_get_their_own_files()
    {
        var reporter = new Reporter(_dir);

        var review = reporter.WriteReview("dev", new[] { Row(verdict: Verdict.Review) });
        var quarantine = reporter.WriteQuarantine("dev", new[] { Row() });

        Assert.StartsWith("review-", Path.GetFileName(review));
        Assert.StartsWith("quarantine-", Path.GetFileName(quarantine));
    }

    [Fact]
    public void WriteMigration_records_both_sides_for_verification()
    {
        var path = new Reporter(_dir).WriteMigration("dev", new[]
        {
            new MigrationRow(
                DocumentId: Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"),
                OldFileId: Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187"),
                NewFileId: Guid.Parse("a41c0b77-1111-2222-3333-444444444444"),
                OldFilePath: @"DigitalServices\goodConductCertificate\20260330\5b05398a.jpg",
                NewFilePath: @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\a41c0b77.jpg",
                Bytes: 350208,
                VendorHash: "e57d1555e2197c964daa9fd57e197b7b",
                OurHash: "abc",
                ChecksPassed: "backup-integrity;upload-hash;round-trip;path-fixed;genuinely-new;crm-repointed",
                OldCrmLink: "https://crm/old",
                NewCrmLink: "https://crm/new",
                State: "Repointed",
                At: DateTimeOffset.UtcNow)
        });

        var text = File.ReadAllText(path);
        Assert.Contains("a41c0b77-1111-2222-3333-444444444444", text);
        Assert.Contains("round-trip", text);
        Assert.Contains("Repointed", text);
    }

    [Fact]
    public void CrmLink_builds_a_document_form_url()
    {
        var link = Reporter.CrmLink("https://crm/MoCD", Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"));

        Assert.Equal(
            "https://crm/MoCD/main.aspx?etn=mocd_document&pagetype=entityrecord&id=2c9d5572-a77b-f111-b10f-00505601095a",
            link);
    }

    [Fact]
    public void An_empty_result_set_still_writes_a_header_only_file()
    {
        var path = new Reporter(_dir).WriteScan("dev", Array.Empty<ScanRow>());

        Assert.Single(File.ReadAllLines(path));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --filter ReporterTests`
Expected: FAIL — `'Reporter' does not exist`.

- [ ] **Step 4: Write the reporter**

Create `src/MocdDocFix/Storage/Reporter.cs`:

```csharp
using System.Globalization;
using System.Text;
using CsvHelper;

namespace MocdDocFix.Storage;

/// <param name="Reason">Why the file is wrong.</param>
/// <param name="Solution">What will be done about it — spec section 8.1.</param>
public sealed record ScanRow(
    Guid DocumentId,
    Guid DocumentFileId,
    string? FileName,
    string DocumentTypeName,
    Guid? ServiceCatalogueId,
    string? OldFilePath,
    string? CurrentSegment,
    Guid? CorrectCatalogueId,
    string Verdict,
    string Reason,
    string Solution,
    string? CrossCheckSource,
    string CrmLink);

public sealed record MigrationRow(
    Guid DocumentId,
    Guid OldFileId,
    Guid NewFileId,
    string OldFilePath,
    string NewFilePath,
    long Bytes,
    string? VendorHash,
    string OurHash,
    string ChecksPassed,
    string OldCrmLink,
    string NewCrmLink,
    string State,
    DateTimeOffset At);

public sealed class Reporter
{
    private readonly string _reportsDir;

    public Reporter(string reportsDir) => _reportsDir = reportsDir;

    public string WriteScan(string env, IEnumerable<ScanRow> rows) => Write("scan", env, rows);
    public string WriteReview(string env, IEnumerable<ScanRow> rows) => Write("review", env, rows);
    public string WriteQuarantine(string env, IEnumerable<ScanRow> rows) => Write("quarantine", env, rows);
    public string WriteMigration(string env, IEnumerable<MigrationRow> rows) => Write("migration-report", env, rows);

    public static string CrmLink(string crmUrl, Guid documentId) =>
        $"{crmUrl.TrimEnd('/')}/main.aspx?etn=mocd_document&pagetype=entityrecord&id={documentId}";

    private string Write<T>(string prefix, string env, IEnumerable<T> rows)
    {
        Directory.CreateDirectory(_reportsDir);
        var path = Path.Combine(_reportsDir,
            $"{prefix}-{env}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        // UTF-8 with BOM so Excel opens Arabic file names correctly.
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteRecords(rows);

        return path;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter ReporterTests`
Expected: PASS — `Failed: 0, Passed: 8`.

> If the escaped-quote assertion fails, check CsvHelper's configured quoting mode — the default
> quotes only when required, which is what the assertion expects.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 96 tests.

- [ ] **Step 7: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Storage/Reporter.cs src/MocdDocFix/MocdDocFix.csproj tests/MocdDocFix.Tests/ReporterTests.cs
git commit -m "feat: CSV reporting for scan, review, quarantine and migration

UTF-8 with BOM so Excel renders Arabic file names, and every file name
carries the environment so no artefact is ambiguous about its source."
```

---

## Task 11: Scan command (phase 1)

Reads, classifies, reports. **Zero writes** to CRM or the file service.

**Files:**
- Create: `src/MocdDocFix/Commands/ScanCommand.cs`
- Create: `tests/MocdDocFix.Tests/Fakes/FakeCrmReadClient.cs`
- Test: `tests/MocdDocFix.Tests/ScanCommandTests.cs`

**Interfaces:**
- Consumes: `ICrmReadClient` (T5), `Classifier` (T2), `Reporter` (T10).
- Produces:
  - `record ScanResult(int TotalInScope, IReadOnlyList<ScanRow> All, IReadOnlyList<ScanRow> Fix, IReadOnlyList<ScanRow> Review, IReadOnlyList<ScanRow> Skip, string ScanPath, string ReviewPath)` with `Banner()`
  - `class ScanCommand { Task<ScanResult> RunAsync(string env, CancellationToken ct); Task<ScanResult> ClassifyAsync(IReadOnlyList<DocumentRow> rows, string env, bool writeReports, CancellationToken ct) }`

- [ ] **Step 1: Write the fake read client**

Create `tests/MocdDocFix.Tests/Fakes/FakeCrmReadClient.cs`:

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Domain;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakeCrmReadClient : ICrmReadClient
{
    public List<DocumentRow> Documents { get; } = new();
    public HashSet<string> KnownCatalogues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DocumentRow>> Resolutions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? ModifiedOn { get; set; }

    public Task<IReadOnlyList<DocumentRow>> GetInScopeDocumentsAsync(
        IReadOnlyList<Guid> catalogues, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DocumentRow>>(Documents);

    public Task<IReadOnlyList<DocumentRow>> ResolveIdentifierAsync(string identifier, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DocumentRow>>(
            Resolutions.TryGetValue(identifier, out var rows) ? rows : new List<DocumentRow>());

    public Task<bool> IsServiceCatalogueAsync(string candidate, CancellationToken ct) =>
        Task.FromResult(KnownCatalogues.Contains(candidate));

    public Task<DateTimeOffset?> GetDocumentModifiedOnAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(ModifiedOn);

    /// <summary>entitySet + id → raw JSON. Defaults to a minimal stub so tests need not set it.</summary>
    public Dictionary<string, string> RawRecords { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetRawRecordAsync(string entitySet, Guid id, CancellationToken ct) =>
        Task.FromResult<string?>(RawRecords.TryGetValue($"{entitySet}:{id}", out var json)
            ? json
            : $$"""{"stub":true,"entitySet":"{{entitySet}}","id":"{{id}}"}""");
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/MocdDocFix.Tests/ScanCommandTests.cs`:

```csharp
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class ScanCommandTests : IDisposable
{
    private static readonly Guid EmployeeAppointment = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid GamRequest = Guid.Parse("3ff27d73-653e-f111-b119-005056010908");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-scan-" + Guid.NewGuid());
    private readonly FakeCrmReadClient _crm = new();

    public ScanCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private ScanCommand Command() =>
        new(_crm, new Reporter(_dir), "https://crm/MoCD", new[] { EmployeeAppointment, GamRequest });

    private static DocumentRow Doc(string? path, Guid? docTypeCat, Guid? crossCheck = null,
        string name = "cert.jpg") =>
        new(Guid.NewGuid(), name, Guid.NewGuid(), path, name, "image/jpeg", "hash",
            Guid.NewGuid(), "Certificate of Good Conduct", docTypeCat, crossCheck,
            crossCheck is null ? null : "mocd_employeeappintmentrequest", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Buckets_documents_into_fix_review_and_skip()
    {
        _crm.KnownCatalogues.Add(GamRequest.ToString());
        _crm.Documents.AddRange(new[]
        {
            Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment),      // FIX
            Doc(@"DigitalServices\0\20260423\b.png", EmployeeAppointment),                            // FIX
            Doc($@"DigitalServices\{GamRequest}\20260518\c.pdf", EmployeeAppointment),                // REVIEW
            Doc($@"DigitalServices\{EmployeeAppointment}\20260330\d.jpg", EmployeeAppointment),       // SKIP
            Doc(null, EmployeeAppointment),                                                            // SKIP
            Doc(@"DigitalServices\x\20260330\e.jpg", null),                                            // SKIP
        });

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Equal(2, result.Fix.Count);
        Assert.Single(result.Review);
        Assert.Equal(3, result.Skip.Count);
        Assert.Equal(6, result.All.Count);
    }

    [Fact]
    public async Task Writes_a_scan_file_and_a_review_file()
    {
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.True(File.Exists(result.ScanPath));
        Assert.True(File.Exists(result.ReviewPath));
        Assert.Contains("scan-dev-", Path.GetFileName(result.ScanPath));
    }

    [Fact]
    public async Task Every_row_carries_a_clickable_crm_link()
    {
        var doc = Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment);
        _crm.Documents.Add(doc);

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Contains($"id={doc.DocumentId}", result.All[0].CrmLink);
    }

    [Fact]
    public async Task Catalogue_membership_is_asked_of_the_server_not_hardcoded()
    {
        // Same path shape, but this GUID is NOT registered as a catalogue, so it is a FIX.
        var unknown = Guid.NewGuid();
        _crm.Documents.Add(Doc($@"DigitalServices\{unknown}\20260518\c.pdf", EmployeeAppointment));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Single(result.Fix);
        Assert.Empty(result.Review);
    }

    [Fact]
    public async Task A_cross_check_conflict_is_reported_as_review()
    {
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg",
            EmployeeAppointment, crossCheck: GamRequest));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Single(result.Review);
        Assert.Contains("cross-check", result.Review[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_org_produces_empty_buckets_and_still_writes_the_files()
    {
        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Empty(result.All);
        Assert.True(File.Exists(result.ScanPath));
    }

    [Fact]
    public async Task The_banner_reports_the_population_as_well_as_the_corrupted_subset()
    {
        _crm.Documents.AddRange(new[]
        {
            Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment),
            Doc($@"DigitalServices\{EmployeeAppointment}\20260330\b.jpg", EmployeeAppointment),
            Doc(null, EmployeeAppointment)
        });

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.Equal(3, result.TotalInScope);
        Assert.Equal(2, result.WithFilePath);
        Assert.Equal(1, result.WithoutFilePath);

        var banner = result.Banner();
        Assert.Contains("In scope", banner);
        Assert.Contains("BROKEN", banner);
        Assert.Contains("AMBIGUOUS", banner);
    }

    [Fact]
    public async Task Every_row_states_a_solution_as_well_as_a_reason()
    {
        _crm.Documents.Add(Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment));

        var result = await Command().RunAsync("dev", CancellationToken.None);

        Assert.All(result.All, r => Assert.False(string.IsNullOrWhiteSpace(r.Solution)));
        Assert.Contains("Re-upload", result.Fix[0].Solution);
        Assert.Contains("Solution", File.ReadAllText(result.ScanPath));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --filter ScanCommandTests`
Expected: FAIL — `'ScanCommand' does not exist`.

- [ ] **Step 4: Write the scan command**

Create `src/MocdDocFix/Commands/ScanCommand.cs`:

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;

namespace MocdDocFix.Commands;

/// <param name="TotalInScope">
/// Stage-one count: every document under the eight services, before classification. Reported
/// alongside the corrupted subset so the problem is always sized against the population
/// (spec section 5.0).
/// </param>
public sealed record ScanResult(
    int TotalInScope,
    IReadOnlyList<ScanRow> All,
    IReadOnlyList<ScanRow> Fix,
    IReadOnlyList<ScanRow> Review,
    IReadOnlyList<ScanRow> Skip,
    string ScanPath,
    string ReviewPath)
{
    public int WithFilePath => All.Count(r => !string.IsNullOrWhiteSpace(r.OldFilePath));
    public int WithoutFilePath => All.Count(r => string.IsNullOrWhiteSpace(r.OldFilePath));

    /// <summary>The banner printed at the top of every run — spec section 5.0.</summary>
    public string Banner() => string.Join(Environment.NewLine,
        $"In scope (document types across the configured services) ... {TotalInScope}",
        $"  with a file path ......................................... {WithFilePath}",
        $"    already correct / nothing to do ........................ {Skip.Count}",
        $"    BROKEN — will be fixed ................................. {Fix.Count}",
        $"    AMBIGUOUS — needs a human decision ..................... {Review.Count}",
        $"  no file path (legacy records, out of scope) .............. {WithoutFilePath}");
}

/// <summary>
/// Phase 1. Reads and classifies only — issues no writes to CRM or the file service.
/// Always scans fresh: broken files are still being produced (spec section 1.2), so a stale
/// scan is never reused.
/// </summary>
public sealed class ScanCommand
{
    private readonly ICrmReadClient _crm;
    private readonly Reporter _reporter;
    private readonly string _crmUrl;
    private readonly IReadOnlyList<Guid> _catalogues;

    public ScanCommand(ICrmReadClient crm, Reporter reporter, string crmUrl, IReadOnlyList<Guid> catalogues)
    {
        _crm = crm;
        _reporter = reporter;
        _crmUrl = crmUrl;
        _catalogues = catalogues;
    }

    public async Task<ScanResult> RunAsync(string env, CancellationToken ct)
    {
        var documents = await _crm.GetInScopeDocumentsAsync(_catalogues, ct);
        return await ClassifyAsync(documents, env, writeReports: true, ct);
    }

    public async Task<ScanResult> ClassifyAsync(
        IReadOnlyList<DocumentRow> documents, string env, bool writeReports, CancellationToken ct)
    {
        var rows = new List<ScanRow>(documents.Count);

        foreach (var document in documents)
        {
            var parsed = FilePathParser.Parse(document.FilePath);

            // Resolved live against mocd_servicecatalogue, cached inside the client.
            var isCatalogue = parsed.CategorySegment is { } segment &&
                              await _crm.IsServiceCatalogueAsync(segment, ct);

            var classification = Classifier.Classify(
                parsed,
                document.DocTypeCatalogueId,
                document.CrossCheckCatalogueId,
                _ => isCatalogue);

            rows.Add(new ScanRow(
                DocumentId: document.DocumentId,
                DocumentFileId: document.DocumentFileId,
                FileName: document.FileName,
                DocumentTypeName: document.DocumentTypeName,
                ServiceCatalogueId: document.DocTypeCatalogueId,
                OldFilePath: document.FilePath,
                CurrentSegment: classification.CurrentSegment,
                CorrectCatalogueId: classification.CorrectCatalogueId,
                Verdict: classification.Verdict.ToString(),
                Reason: classification.Reason,
                Solution: classification.Solution,
                CrossCheckSource: document.CrossCheckSource,
                CrmLink: Reporter.CrmLink(_crmUrl, document.DocumentId)));
        }

        var fix = rows.Where(r => r.Verdict == nameof(Verdict.Fix)).ToList();
        var review = rows.Where(r => r.Verdict == nameof(Verdict.Review)).ToList();
        var skip = rows.Where(r => r.Verdict == nameof(Verdict.Skip)).ToList();

        var scanPath = writeReports ? _reporter.WriteScan(env, rows) : string.Empty;
        var reviewPath = writeReports ? _reporter.WriteReview(env, review) : string.Empty;

        return new ScanResult(documents.Count, rows, fix, review, skip, scanPath, reviewPath);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter ScanCommandTests`
Expected: PASS — `Failed: 0, Passed: 8`.

- [ ] **Step 6: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Commands/ScanCommand.cs tests/MocdDocFix.Tests/ScanCommandTests.cs \
        tests/MocdDocFix.Tests/Fakes/FakeCrmReadClient.cs
git commit -m "feat: scan command (phase 1, read-only)

Classifies every in-scope document into FIX / REVIEW / SKIP and writes the
scan and review CSVs. Catalogue membership is asked of the server per
segment, so no GUID list is baked into the tool."
```

---

## Task 12: Backup command (phase 2)

Downloads every FIX file, proves it against CRM's hash, saves it locally and records the manifest.
Still **zero server writes** — this phase only reads.

**Files:**
- Create: `src/MocdDocFix/Commands/BackupCommand.cs`
- Create: `tests/MocdDocFix.Tests/Fakes/FakeFileServiceClient.cs`
- Test: `tests/MocdDocFix.Tests/BackupCommandTests.cs`

**Interfaces:**
- Consumes: `IFileServiceClient` (T4), `ICrmReadClient` (T5), `BackupStore` (T9), `StateStore` (T8), `Verifier` (T7), `ScanRow` (T10).
- Produces:
  - `record BackupSummary(int Saved, int Quarantined, int Skipped, long TotalBytes, string ManifestPath, IReadOnlyList<ScanRow> QuarantinedRows)`
  - `class BackupCommand { Task<BackupSummary> RunAsync(string env, IReadOnlyList<ScanRow> fixRows, CancellationToken ct) }`

- [ ] **Step 1: Write the fake file service**

Create `tests/MocdDocFix.Tests/Fakes/FakeFileServiceClient.cs`:

```csharp
using MocdDocFix.Clients;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakeFileServiceClient : IFileServiceClient
{
    /// <summary>path → (base64 content, vendor hash).</summary>
    public Dictionary<string, (string Base64, string Hash)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<UploadRequest> Uploads { get; } = new();
    public List<string> Deleted { get; } = new();

    /// <summary>Set to control what the next upload returns.</summary>
    public Func<UploadRequest, ApiResponse<FileData>>? UploadResponder { get; set; }

    public Task<ApiResponse<FileData>> DownloadAsync(string filePath, CancellationToken ct) =>
        Task.FromResult(Files.TryGetValue(filePath, out var f)
            ? new ApiResponse<FileData>(true, null,
                new FileData(Guid.Empty, filePath, f.Hash, null, null, f.Base64), null)
            : ApiResponse<FileData>.Fail($"not found: {filePath}"));

    public Task<ApiResponse<FileData>> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        Uploads.Add(request);
        return Task.FromResult(UploadResponder?.Invoke(request)
            ?? ApiResponse<FileData>.Fail("no upload responder configured"));
    }

    public Task<ApiResponse<bool>> DeleteAsync(string filePath, CancellationToken ct)
    {
        Deleted.Add(filePath);
        Files.Remove(filePath);
        return Task.FromResult(new ApiResponse<bool>(true, null, true, null));
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/MocdDocFix.Tests/BackupCommandTests.cs`:

```csharp
using System.Text;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class BackupCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-bk-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();

    private BackupStore Backups() =>
        new(Path.Combine(_root, "backup"), Path.Combine(_root, "restore-manifest.jsonl"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public BackupCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private const string Path1 = @"DigitalServices\goodConductCertificate\20260330\a.jpg";
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");
    private static readonly string Base64 = Convert.ToBase64String(Content);

    private static ScanRow Row(string path, string fileName = "cert.jpg") => new(
        Guid.NewGuid(), Guid.NewGuid(), fileName, "Certificate of Good Conduct",
        Guid.NewGuid(), path, "goodConductCertificate",
        Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), nameof(Verdict.Fix), "reason", "solution",
        null, "https://crm/x");

    private BackupCommand Command(Reporter? reporter = null) =>
        new(_files, _read, Backups(), States(),
            reporter ?? new Reporter(Path.Combine(_root, "reports")),
            hashLookup: _ => "VENDORHASH");

    [Fact]
    public async Task Saves_the_file_and_records_the_manifest()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Saved);
        Assert.Equal(0, summary.Quarantined);
        Assert.Equal(Content.Length, summary.TotalBytes);

        var entry = Assert.Single(Backups().LoadManifest());
        Assert.Equal(row.DocumentId, entry.DocumentId);
        Assert.Equal(Path1, entry.OldFilePath);
        Assert.Equal("VENDORHASH", entry.OldVendorHash);
        Assert.True(File.Exists(entry.LocalPath));
        Assert.Equal(Content, File.ReadAllBytes(entry.LocalPath));
    }

    [Fact]
    public async Task Marks_the_document_BackedUp_in_the_state_store()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.True(States().IsAtLeast(row.DocumentId, MigrationState.BackedUp));
    }

    [Fact]
    public async Task A_hash_disagreement_quarantines_the_file_and_does_not_save_it()
    {
        _files.Files[Path1] = (Base64, "A-DIFFERENT-HASH");
        var row = Row(Path1);

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(0, summary.Saved);
        Assert.Equal(1, summary.Quarantined);
        Assert.Empty(Backups().LoadManifest());
        Assert.False(States().IsAtLeast(row.DocumentId, MigrationState.BackedUp));
        Assert.Single(summary.QuarantinedRows);
    }

    [Fact]
    public async Task A_download_failure_quarantines_rather_than_throwing()
    {
        var summary = await Command().RunAsync("dev", new[] { Row(@"DigitalServices\missing\x\y.jpg") },
            CancellationToken.None);

        Assert.Equal(0, summary.Saved);
        Assert.Equal(1, summary.Quarantined);
    }

    [Fact]
    public async Task Already_backed_up_documents_are_skipped_on_a_re_run()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var second = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(0, second.Saved);
        Assert.Equal(1, second.Skipped);
        Assert.Single(Backups().LoadManifest());       // not duplicated
    }

    [Fact]
    public async Task A_quarantine_report_is_written_when_anything_is_quarantined()
    {
        _files.Files[Path1] = (Base64, "WRONG");
        var reportsDir = Path.Combine(_root, "reports");

        await Command(new Reporter(reportsDir)).RunAsync("dev", new[] { Row(Path1) }, CancellationToken.None);

        Assert.Single(Directory.GetFiles(reportsDir, "quarantine-dev-*.csv"));
    }

    [Fact]
    public async Task The_manifest_snapshots_both_crm_records_and_recovers_the_media_type()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        _read.RawRecords[$"mocd_documentfiles:{row.DocumentFileId}"] =
            """{"mocd_documentfileid":"x","mocd_mediatype":"image/jpeg","mocd_filesize":"350208","mocd_extension":".jpg"}""";
        _read.RawRecords[$"mocd_documents:{row.DocumentId}"] =
            """{"mocd_documentid":"y","_mocd_documentfile_value":"z","statuscode":1}""";

        await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        var entry = Assert.Single(Backups().LoadManifest());
        Assert.Contains("mocd_filesize", entry.DocumentFileSnapshotJson!);
        Assert.Contains("_mocd_documentfile_value", entry.DocumentSnapshotJson!);
        Assert.Equal("image/jpeg", entry.MediaType);          // recovered from the snapshot
        Assert.Equal("Certificate of Good Conduct", entry.DocumentTypeName);
    }

    [Fact]
    public async Task A_snapshot_that_cannot_be_read_does_not_stop_the_backup()
    {
        _files.Files[Path1] = (Base64, "VENDORHASH");
        var row = Row(Path1);
        _read.RawRecords[$"mocd_documentfiles:{row.DocumentFileId}"] = "<html>not json</html>";

        var summary = await Command().RunAsync("dev", new[] { row }, CancellationToken.None);

        Assert.Equal(1, summary.Saved);
        Assert.Null(Backups().LoadManifest()[0].MediaType);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test --filter BackupCommandTests`
Expected: FAIL — `'BackupCommand' does not exist`.

- [ ] **Step 4: Write the backup command**

Create `src/MocdDocFix/Commands/BackupCommand.cs`:

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Verification;

namespace MocdDocFix.Commands;

public sealed record BackupSummary(
    int Saved,
    int Quarantined,
    int Skipped,
    long TotalBytes,
    string ManifestPath,
    IReadOnlyList<ScanRow> QuarantinedRows);

/// <summary>
/// Phase 2. Downloads and verifies every FIX file, then stores it locally with a manifest.
/// Reads only — nothing is written to CRM or the file service.
/// </summary>
public sealed class BackupCommand
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly Reporter _reporter;
    private readonly Func<ScanRow, string?> _hashLookup;

    /// <param name="hashLookup">
    /// Supplies the mocd_hash CRM holds for a row. Injected because ScanRow does not carry it.
    /// </param>
    public BackupCommand(IFileServiceClient files, ICrmReadClient read, BackupStore backups,
        StateStore state, Reporter reporter, Func<ScanRow, string?> hashLookup)
    {
        _files = files;
        _read = read;
        _backups = backups;
        _state = state;
        _reporter = reporter;
        _hashLookup = hashLookup;
    }

    public async Task<BackupSummary> RunAsync(string env, IReadOnlyList<ScanRow> fixRows, CancellationToken ct)
    {
        int saved = 0, skipped = 0;
        long totalBytes = 0;
        var quarantined = new List<ScanRow>();

        foreach (var row in fixRows)
        {
            ct.ThrowIfCancellationRequested();

            if (_state.IsAtLeast(row.DocumentId, MigrationState.BackedUp)) { skipped++; continue; }

            if (string.IsNullOrWhiteSpace(row.OldFilePath))
            {
                Quarantine(row, "No file path.");
                quarantined.Add(row);
                continue;
            }

            var download = await _files.DownloadAsync(row.OldFilePath, ct);
            if (!download.Success || download.Data?.File is null)
            {
                Quarantine(row, $"Download failed: {download.Message}");
                quarantined.Add(row);
                continue;
            }

            // Check 1 — CRM's stored hash must agree with what the server reports today.
            var integrity = Verifier.BackupIntegrity(_hashLookup(row), download.Data.Hash);
            if (!integrity.Passed)
            {
                Quarantine(row, integrity.Detail);
                quarantined.Add(row);
                continue;
            }

            byte[] bytes;
            try { bytes = Convert.FromBase64String(download.Data.File); }
            catch (FormatException ex)
            {
                Quarantine(row, $"Response was not valid base64: {ex.Message}");
                quarantined.Add(row);
                continue;
            }

            var extension = Path.GetExtension(row.FileName ?? string.Empty);
            var result = _backups.Save(row.DocumentFileId, extension, bytes);

            // Snapshot the CRM side BEFORE anything is written, so a restore can rebuild the
            // records and not only the content (spec section 8.2).
            var fileSnapshot = await _read.GetRawRecordAsync("mocd_documentfiles", row.DocumentFileId, ct);
            var documentSnapshot = await _read.GetRawRecordAsync("mocd_documents", row.DocumentId, ct);

            _backups.AppendManifest(new ManifestEntry(
                DocumentId: row.DocumentId,
                OldFileId: row.DocumentFileId,
                OldFilePath: row.OldFilePath,
                OldVendorHash: download.Data.Hash,
                FileName: row.FileName,
                MediaType: ReadString(fileSnapshot, "mocd_mediatype"),
                Extension: extension,
                OldCategory: row.CurrentSegment,
                CorrectCatalogueId: row.CorrectCatalogueId ?? Guid.Empty,
                LocalPath: result.LocalPath,
                Bytes: result.Bytes,
                OurHash: result.OurHash,
                At: DateTimeOffset.UtcNow,
                DocumentFileSnapshotJson: fileSnapshot,
                DocumentSnapshotJson: documentSnapshot,
                DocumentTypeId: Guid.Empty,
                DocumentTypeName: row.DocumentTypeName));

            _state.Append(new StateRecord(row.DocumentId, MigrationState.BackedUp,
                DateTimeOffset.UtcNow, null, null, $"{result.Bytes} bytes → {result.LocalPath}"));

            saved++;
            totalBytes += result.Bytes;
        }

        if (quarantined.Count > 0) _reporter.WriteQuarantine(env, quarantined);

        return new BackupSummary(saved, quarantined.Count, skipped, totalBytes,
            _backups.ManifestPath, quarantined);
    }

    private void Quarantine(ScanRow row, string detail) =>
        _state.Append(new StateRecord(row.DocumentId, MigrationState.Quarantined,
            DateTimeOffset.UtcNow, null, null, detail));

    /// <summary>Pulls one string attribute out of a raw record snapshot.</summary>
    private static string? ReadString(string? snapshotJson, string attribute)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(snapshotJson);
            return json.RootElement.TryGetProperty(attribute, out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
```

- [ ] **Step 5: Expose the manifest path on `BackupStore`**

Add to `src/MocdDocFix/Storage/BackupStore.cs`, inside the class after the constructor:

```csharp
    public string ManifestPath => _manifestPath;
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter BackupCommandTests`
Expected: PASS — `Failed: 0, Passed: 8`.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 112 tests.

- [ ] **Step 8: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Commands/BackupCommand.cs src/MocdDocFix/Storage/BackupStore.cs \
        tests/MocdDocFix.Tests/BackupCommandTests.cs tests/MocdDocFix.Tests/Fakes/FakeFileServiceClient.cs
git commit -m "feat: backup command (phase 2, read-only)

Downloads each FIX file, proves it against CRM's stored hash before saving,
and records a restore manifest. A hash disagreement or a failed download
quarantines the file instead of aborting the run, and a re-run resumes."
```

---

## Task 13: Migrate command (phase 3) and the operator confirmation UI

The first phase that writes. Order matters absolutely (§5.2): upload, verify, **show the operator
both files**, get consent, then and only then touch CRM. The old file is never modified, so the
worst case at every instant is two good copies.

**Files:**
- Create: `src/MocdDocFix/Ui/Prompts.cs`, `src/MocdDocFix/Ui/FileOpener.cs`, `src/MocdDocFix/Commands/MigrateCommand.cs`
- Create: `tests/MocdDocFix.Tests/Fakes/FakePrompts.cs`, `tests/MocdDocFix.Tests/Fakes/FakeCrmWriteClient.cs`
- Test: `tests/MocdDocFix.Tests/MigrateCommandTests.cs`

**Interfaces:**
- Consumes: `IFileServiceClient` (T4), `ICrmReadClient` (T5), `ICrmWriteClient` (T6), `Verifier` (T7), `StateStore` (T8), `BackupStore` (T9), `Reporter` (T10).
- Produces:
  - `enum ConfirmChoice { Yes, No, Skip, Quit }`
  - `interface IPrompts { ConfirmChoice Confirm(string question); bool TypedWord(string question, string requiredWord); void Info(string message); }`
  - `interface IFileOpener { void Open(string path); }`
  - `record MigrateSummary(int Migrated, int Skipped, int Failed, bool Halted, string? HaltReason, string ReportPath, IReadOnlyList<MigrationRow> Rows)`
  - `class MigrateCommand { Task<MigrateSummary> RunAsync(string env, CancellationToken ct) }`

- [ ] **Step 1: Write the UI abstractions**

Create `src/MocdDocFix/Ui/Prompts.cs`:

```csharp
namespace MocdDocFix.Ui;

public enum ConfirmChoice { Yes, No, Skip, Quit }

public interface IPrompts
{
    ConfirmChoice Confirm(string question);
    bool TypedWord(string question, string requiredWord);
    void Info(string message);
}

public sealed class ConsolePrompts : IPrompts
{
    public ConfirmChoice Confirm(string question)
    {
        while (true)
        {
            Console.Write($"{question} [y / n / skip / quit]: ");
            switch ((Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "y" or "yes": return ConfirmChoice.Yes;
                case "n" or "no": return ConfirmChoice.No;
                case "s" or "skip": return ConfirmChoice.Skip;
                case "q" or "quit": return ConfirmChoice.Quit;
                default: Console.WriteLine("  Please answer y, n, skip or quit."); break;
            }
        }
    }

    /// <summary>Used where a keypress is not enough — deletion, and selecting production.</summary>
    public bool TypedWord(string question, string requiredWord)
    {
        Console.Write($"{question} (type {requiredWord} to proceed): ");
        return string.Equals((Console.ReadLine() ?? string.Empty).Trim(), requiredWord, StringComparison.Ordinal);
    }

    public void Info(string message) => Console.WriteLine(message);
}
```

Create `src/MocdDocFix/Ui/FileOpener.cs`:

```csharp
using System.Diagnostics;

namespace MocdDocFix.Ui;

public interface IFileOpener
{
    void Open(string path);
}

public sealed class ShellFileOpener : IFileOpener
{
    public void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Never let a viewer problem abort a migration.
            Console.WriteLine($"  (could not open {path}: {ex.Message})");
        }
    }
}

/// <summary>For tests and unattended runs.</summary>
public sealed class NullFileOpener : IFileOpener
{
    public List<string> Opened { get; } = new();
    public void Open(string path) => Opened.Add(path);
}
```

- [ ] **Step 2: Write the fakes**

Create `tests/MocdDocFix.Tests/Fakes/FakePrompts.cs`:

```csharp
using MocdDocFix.Ui;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakePrompts : IPrompts
{
    private readonly Queue<ConfirmChoice> _answers = new();

    public List<string> Questions { get; } = new();
    public List<string> Messages { get; } = new();
    public string? TypedWordResponse { get; set; }

    public FakePrompts Answer(params ConfirmChoice[] choices)
    {
        foreach (var c in choices) _answers.Enqueue(c);
        return this;
    }

    public ConfirmChoice Confirm(string question)
    {
        Questions.Add(question);
        return _answers.Count > 0 ? _answers.Dequeue() : ConfirmChoice.Quit;
    }

    public bool TypedWord(string question, string requiredWord)
    {
        Questions.Add(question);
        return TypedWordResponse == requiredWord;
    }

    public void Info(string message) => Messages.Add(message);
}
```

Create `tests/MocdDocFix.Tests/Fakes/FakeCrmWriteClient.cs`:

```csharp
using MocdDocFix.Clients;

namespace MocdDocFix.Tests.Fakes;

public sealed class FakeCrmWriteClient : ICrmWriteClient
{
    public record Created(Guid FileId, string FilePath, string? Hash, string? Name, string? MediaType, string? Category);

    public List<Created> CreatedFiles { get; } = new();
    public Dictionary<Guid, Guid> Links { get; } = new();
    public List<Guid> DeletedFiles { get; } = new();

    /// <summary>When set, the read-back returns this instead of what was written.</summary>
    public Guid? ForceLinkReadback { get; set; }

    public Task CreateDocumentFileAsync(Guid fileId, string filePath, string? hash, string? name,
        string? mediaType, string? category, CancellationToken ct)
    {
        CreatedFiles.Add(new Created(fileId, filePath, hash, name, mediaType, category));
        return Task.CompletedTask;
    }

    public Task RepointDocumentAsync(Guid documentId, Guid newFileId, CancellationToken ct)
    {
        Links[documentId] = newFileId;
        return Task.CompletedTask;
    }

    public Task<Guid?> GetDocumentFileLinkAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(ForceLinkReadback ?? (Links.TryGetValue(documentId, out var v) ? v : (Guid?)null));

    public Task DeleteDocumentFileAsync(Guid fileId, CancellationToken ct)
    {
        DeletedFiles.Add(fileId);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Write the failing test**

Create `tests/MocdDocFix.Tests/MigrateCommandTests.cs`:

```csharp
using System.Text;
using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class MigrateCommandTests : IDisposable
{
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid NewFileId = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid OldFileId = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");

    private const string OldPath = @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg";
    private static string NewPath(Guid id) => $@"DigitalServices\{Correct}\20260910\{id}.jpg";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-mig-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmReadClient _read = new();
    private readonly FakeCrmWriteClient _write = new();
    private readonly NullFileOpener _opener = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"), Path.Combine(_root, "manifest.jsonl"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public MigrateCommandTests()
    {
        Directory.CreateDirectory(_root);

        // A backed-up document, ready to migrate.
        var backups = Backups();
        var saved = backups.Save(OldFileId, ".jpg", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH", "cert.jpg",
            "image/jpeg", ".jpg", "goodConductCertificate", Correct, saved.LocalPath,
            saved.Bytes, saved.OurHash, DateTimeOffset.UtcNow));
        States().Append(new StateRecord(DocumentId, MigrationState.BackedUp, DateTimeOffset.UtcNow, null, null, null));

        // The vendor accepts the upload and serves the new file back identically.
        _files.UploadResponder = _ =>
        {
            var path = NewPath(NewFileId);
            _files.Files[path] = (Convert.ToBase64String(Content), "VHASH");
            return new ApiResponse<FileData>(true, null,
                new FileData(NewFileId, path, "VHASH", "cert.jpg", "image/jpeg", null), null);
        };
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private MigrateCommand Command(FakePrompts prompts) =>
        new(_files, _read, _write, Backups(), States(), new Reporter(Path.Combine(_root, "reports")),
            prompts, _opener, "https://crm/MoCD");

    [Fact]
    public async Task Happy_path_uploads_verifies_creates_and_repoints()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(1, summary.Migrated);
        Assert.False(summary.Halted);

        var upload = Assert.Single(_files.Uploads);
        Assert.Equal(Correct.ToString(), upload.Category);          // the whole point
        Assert.Equal("cert.jpg", upload.FileName);
        Assert.Equal(Convert.ToBase64String(Content), upload.File);

        var created = Assert.Single(_write.CreatedFiles);
        Assert.Equal(NewFileId, created.FileId);
        Assert.Equal(Correct.ToString(), created.Category);
        Assert.Equal(NewFileId, _write.Links[DocumentId]);
        Assert.True(States().IsAtLeast(DocumentId, MigrationState.Repointed));
    }

    [Fact]
    public async Task Both_files_are_opened_for_the_operator_before_the_question()
    {
        await Command(new FakePrompts().Answer(ConfirmChoice.Yes)).RunAsync("dev", CancellationToken.None);

        Assert.Equal(2, _opener.Opened.Count);
        Assert.Contains(_opener.Opened, p => p.Contains(OldFileId.ToString()));
    }

    [Fact]
    public async Task Answering_no_writes_nothing_to_crm()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.No))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Empty(_write.CreatedFiles);
        Assert.Empty(_write.Links);
        Assert.False(States().IsAtLeast(DocumentId, MigrationState.Repointed));
    }

    [Fact]
    public async Task Quit_stops_the_run_without_writing()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Quit))
            .RunAsync("dev", CancellationToken.None);

        Assert.Empty(_write.Links);
        Assert.Equal(0, summary.Migrated);
    }

    [Fact]
    public async Task A_corrupted_round_trip_stops_the_file_and_writes_nothing_to_crm()
    {
        _files.UploadResponder = _ =>
        {
            var path = NewPath(NewFileId);
            _files.Files[path] = (Convert.ToBase64String(Encoding.UTF8.GetBytes("DIFFERENT")), "VHASH");
            return new ApiResponse<FileData>(true, null,
                new FileData(NewFileId, path, "VHASH", "cert.jpg", "image/jpeg", null), null);
        };

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Equal(1, summary.Failed);
        Assert.Empty(_write.CreatedFiles);
    }

    [Fact]
    public async Task The_operator_is_never_asked_when_verification_already_failed()
    {
        _files.UploadResponder = _ => ApiResponse<FileData>.Fail("vendor exploded");
        var prompts = new FakePrompts().Answer(ConfirmChoice.Yes);

        await Command(prompts).RunAsync("dev", CancellationToken.None);

        Assert.DoesNotContain(prompts.Questions, q => q.Contains("Repoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Deduplication_halts_the_entire_run()
    {
        _files.UploadResponder = _ =>
        {
            _files.Files[OldPath] = (Convert.ToBase64String(Content), "VHASH");
            return new ApiResponse<FileData>(true, null,
                new FileData(OldFileId, OldPath, "VHASH", "cert.jpg", "image/jpeg", null), null);
        };

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(summary.Halted);
        Assert.Contains("deduplicat", summary.HaltReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_write.CreatedFiles);
    }

    [Fact]
    public async Task A_failed_crm_read_back_halts_the_run()
    {
        _write.ForceLinkReadback = OldFileId;      // the repoint did not stick

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(summary.Halted);
        Assert.Contains("crm-repointed", summary.HaltReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_document_edited_since_the_scan_is_skipped()
    {
        _read.ModifiedOn = DateTimeOffset.UtcNow.AddYears(1);

        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, summary.Migrated);
        Assert.Empty(_write.Links);
    }

    [Fact]
    public async Task Already_repointed_documents_are_skipped_on_a_re_run()
    {
        await Command(new FakePrompts().Answer(ConfirmChoice.Yes)).RunAsync("dev", CancellationToken.None);
        _files.Uploads.Clear();

        var second = await Command(new FakePrompts().Answer(ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.Equal(0, second.Migrated);
        Assert.Equal(1, second.Skipped);
        Assert.Empty(_files.Uploads);              // no second upload
    }

    [Fact]
    public async Task A_migration_report_records_both_sides()
    {
        var summary = await Command(new FakePrompts().Answer(ConfirmChoice.Yes))
            .RunAsync("dev", CancellationToken.None);

        Assert.True(File.Exists(summary.ReportPath));
        var row = Assert.Single(summary.Rows);
        Assert.Equal(OldFileId, row.OldFileId);
        Assert.Equal(NewFileId, row.NewFileId);
        Assert.Contains($"id={DocumentId}", row.OldCrmLink);
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test --filter MigrateCommandTests`
Expected: FAIL — `'MigrateCommand' does not exist`.

- [ ] **Step 5: Write the migrate command**

Create `src/MocdDocFix/Commands/MigrateCommand.cs`:

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;
using MocdDocFix.Verification;

namespace MocdDocFix.Commands;

public sealed record MigrateSummary(
    int Migrated,
    int Skipped,
    int Failed,
    bool Halted,
    string? HaltReason,
    string ReportPath,
    IReadOnlyList<MigrationRow> Rows);

/// <summary>
/// Phase 3. Upload, verify, show the operator, ask, then write CRM — in that order, so the
/// worst case at any instant is two good copies. The old file is never touched here.
/// </summary>
public sealed class MigrateCommand
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly Reporter _reporter;
    private readonly IPrompts _prompts;
    private readonly IFileOpener _opener;
    private readonly string _crmUrl;

    public MigrateCommand(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write,
        BackupStore backups, StateStore state, Reporter reporter, IPrompts prompts,
        IFileOpener opener, string crmUrl)
    {
        _files = files;
        _read = read;
        _write = write;
        _backups = backups;
        _state = state;
        _reporter = reporter;
        _prompts = prompts;
        _opener = opener;
        _crmUrl = crmUrl;
    }

    public async Task<MigrateSummary> RunAsync(string env, CancellationToken ct)
    {
        var manifest = _backups.LoadManifest();
        var rows = new List<MigrationRow>();
        int migrated = 0, skipped = 0, failed = 0;
        string? haltReason = null;

        for (var i = 0; i < manifest.Count && haltReason is null; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = manifest[i];

            if (_state.IsAtLeast(entry.DocumentId, MigrationState.Repointed)) { skipped++; continue; }
            if (!_state.IsAtLeast(entry.DocumentId, MigrationState.BackedUp)) { skipped++; continue; }

            // Somebody else may have edited the record since the scan.
            var modifiedOn = await _read.GetDocumentModifiedOnAsync(entry.DocumentId, ct);
            if (modifiedOn is not null && modifiedOn > entry.At)
            {
                _prompts.Info($"  SKIP — document {entry.DocumentId} was modified at {modifiedOn} " +
                              $"after its backup at {entry.At}.");
                _state.Append(new StateRecord(entry.DocumentId, MigrationState.Failed,
                    DateTimeOffset.UtcNow, null, null, "Modified after backup — skipped."));
                skipped++;
                continue;
            }

            var oldBytes = _backups.Read(entry.LocalPath);

            var upload = await _files.UploadAsync(new UploadRequest(
                Category: entry.CorrectCatalogueId.ToString(),
                FileName: entry.FileName ?? $"{entry.OldFileId}{entry.Extension}",
                File: Convert.ToBase64String(oldBytes),
                MediaType: entry.MediaType ?? "application/octet-stream",
                Extension: entry.Extension,
                ApplicationId: Guid.Empty), ct);

            if (!upload.Success || upload.Data is null)
            {
                Fail(entry, $"Upload failed: {upload.Message}");
                failed++;
                continue;
            }

            var newFile = upload.Data;
            _state.Append(new StateRecord(entry.DocumentId, MigrationState.Uploaded,
                DateTimeOffset.UtcNow, newFile.FileId, newFile.FilePath, null));

            // Checks 2, 5 and 4 before we spend a download.
            var checks = new List<CheckResult>
            {
                Verifier.UploadHashMatches(entry.OldVendorHash, newFile.Hash),
                Verifier.IsGenuinelyNew(entry.OldFilePath, newFile.FilePath, entry.OldFileId, newFile.FileId),
                Verifier.PathIsFixed(FilePathParser.Parse(newFile.FilePath), entry.CorrectCatalogueId, newFile.FileId)
            };

            // Check 3 — the decisive one. Only worth doing if nothing already failed hard.
            if (checks.All(c => c.Passed))
            {
                var download = await _files.DownloadAsync(newFile.FilePath, ct);
                if (!download.Success || download.Data?.File is null)
                {
                    checks.Add(new CheckResult("round-trip", false,
                        $"The new file could not be downloaded back: {download.Message}", false));
                }
                else
                {
                    checks.Add(Verifier.RoundTrip(oldBytes, Convert.FromBase64String(download.Data.File)));
                }
            }

            var report = new VerificationReport(checks);

            if (report.MustHalt)
            {
                haltReason = string.Join(" | ", report.Failures.Select(f => $"{f.Name}: {f.Detail}"));
                Fail(entry, haltReason);
                failed++;
                break;
            }

            if (!report.AllPassed)
            {
                var detail = string.Join(" | ", report.Failures.Select(f => $"{f.Name}: {f.Detail}"));
                _prompts.Info($"  FAILED verification — {detail}");
                Fail(entry, detail);
                failed++;
                continue;
            }

            _state.Append(new StateRecord(entry.DocumentId, MigrationState.Verified,
                DateTimeOffset.UtcNow, newFile.FileId, newFile.FilePath, null));

            // Stage the new copy next to the backup so the operator can open both.
            var stagedPath = Path.Combine(
                Path.GetDirectoryName(entry.LocalPath)!, $"NEW-{newFile.FileId}{entry.Extension}");
            File.WriteAllBytes(stagedPath, oldBytes);

            _prompts.Info(BuildSummary(i + 1, manifest.Count, entry, newFile, oldBytes.Length, checks));
            _opener.Open(entry.LocalPath);
            _opener.Open(stagedPath);

            var choice = _prompts.Confirm("Repoint document to the new file?");
            if (choice == ConfirmChoice.Quit) break;
            if (choice is ConfirmChoice.No or ConfirmChoice.Skip) { skipped++; continue; }

            await _write.CreateDocumentFileAsync(newFile.FileId, newFile.FilePath, newFile.Hash,
                entry.FileName, entry.MediaType, entry.CorrectCatalogueId.ToString(), ct);
            await _write.RepointDocumentAsync(entry.DocumentId, newFile.FileId, ct);

            // Check 6 — read back rather than assume.
            var linked = await _write.GetDocumentFileLinkAsync(entry.DocumentId, ct);
            var tookIt = Verifier.CrmTookTheChange(newFile.FileId, linked);
            if (!tookIt.Passed)
            {
                haltReason = $"{tookIt.Name}: {tookIt.Detail}";
                Fail(entry, haltReason);
                failed++;
                break;
            }

            _state.Append(new StateRecord(entry.DocumentId, MigrationState.Repointed,
                DateTimeOffset.UtcNow, newFile.FileId, newFile.FilePath, null));

            rows.Add(new MigrationRow(
                DocumentId: entry.DocumentId,
                OldFileId: entry.OldFileId,
                NewFileId: newFile.FileId,
                OldFilePath: entry.OldFilePath,
                NewFilePath: newFile.FilePath,
                Bytes: oldBytes.Length,
                VendorHash: newFile.Hash,
                OurHash: Verifier.OurHash(oldBytes),
                ChecksPassed: string.Join(";", checks.Select(c => c.Name).Append(tookIt.Name)),
                OldCrmLink: Reporter.CrmLink(_crmUrl, entry.DocumentId),
                NewCrmLink: Reporter.CrmLink(_crmUrl, entry.DocumentId),
                State: nameof(MigrationState.Repointed),
                At: DateTimeOffset.UtcNow));

            migrated++;
        }

        var reportPath = _reporter.WriteMigration(env, rows);
        return new MigrateSummary(migrated, skipped, failed, haltReason is not null, haltReason, reportPath, rows);
    }

    private void Fail(ManifestEntry entry, string detail) =>
        _state.Append(new StateRecord(entry.DocumentId, MigrationState.Failed,
            DateTimeOffset.UtcNow, null, null, detail));

    private static string BuildSummary(int index, int total, ManifestEntry entry, FileData newFile,
        int bytes, IReadOnlyList<CheckResult> checks)
    {
        var lines = new List<string>
        {
            "",
            $"[{index} / {total}]  {entry.FileName}",
            $"  document  {entry.DocumentId}",
            $"  OLD  {entry.OldFilePath}",
            $"  NEW  {newFile.FilePath}",
            ""
        };
        lines.AddRange(checks.Select(c => $"  {c.Name,-18} {(c.Passed ? "OK" : "FAILED")}  {c.Detail}"));
        lines.Add($"  bytes              {bytes:N0}");
        return string.Join(Environment.NewLine, lines);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter MigrateCommandTests`
Expected: PASS — `Failed: 0, Passed: 11`.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 123 tests.

- [ ] **Step 8: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Ui src/MocdDocFix/Commands/MigrateCommand.cs \
        tests/MocdDocFix.Tests/MigrateCommandTests.cs tests/MocdDocFix.Tests/Fakes
git commit -m "feat: migrate command (phase 3) with operator confirmation

Uploads under the correct catalogue, runs the verification gate, opens both
files for the operator and only then writes CRM. Deduplication and a failed
read-back halt the whole run; the old file is never touched."
```

---

## Task 14: Delete command (phase 5)

The only irreversible step in the operation. Everything here exists to make it hard to do by
accident and impossible to do on stale information: each file is **re-verified against live state
immediately before its delete**, and consent is a typed word, never a keypress (§5.1, §6.2).

**Files:**
- Create: `src/MocdDocFix/Commands/DeleteCommand.cs`
- Test: `tests/MocdDocFix.Tests/DeleteCommandTests.cs`

**Interfaces:**
- Consumes: `IFileServiceClient` (T4), `ICrmWriteClient` (T6), `BackupStore` (T9), `StateStore` (T8), `IPrompts` (T13).
- Produces:
  - `record DeleteSummary(int Deleted, int Skipped, int Refused, bool Aborted, string? AbortReason)`
  - `class DeleteCommand { Task<DeleteSummary> RunAsync(string env, bool isProduction, CancellationToken ct) }`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/DeleteCommandTests.cs`:

```csharp
using System.Text;
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

public class DeleteCommandTests : IDisposable
{
    private static readonly Guid Correct    = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid OldFileId  = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");
    private static readonly Guid NewFileId  = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid DocumentId = Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a");

    private const string OldPath = @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg";
    private static readonly string NewPath = $@"DigitalServices\{Correct}\20260910\{NewFileId}.jpg";

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("certificate bytes");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-del-" + Guid.NewGuid());
    private readonly FakeFileServiceClient _files = new();
    private readonly FakeCrmWriteClient _write = new();

    private BackupStore Backups() => new(Path.Combine(_root, "backup"), Path.Combine(_root, "manifest.jsonl"));
    private StateStore States() => new(Path.Combine(_root, "state.jsonl"));

    public DeleteCommandTests()
    {
        Directory.CreateDirectory(_root);

        var backups = Backups();
        var saved = backups.Save(OldFileId, ".jpg", Content);
        backups.AppendManifest(new ManifestEntry(DocumentId, OldFileId, OldPath, "VHASH", "cert.jpg",
            "image/jpeg", ".jpg", "goodConductCertificate", Correct, saved.LocalPath,
            saved.Bytes, Verifier.OurHash(Content), DateTimeOffset.UtcNow));

        States().Append(new StateRecord(DocumentId, MigrationState.Repointed,
            DateTimeOffset.UtcNow, NewFileId, NewPath, null));

        _files.Files[OldPath] = (Convert.ToBase64String(Content), "VHASH");
        _files.Files[NewPath] = (Convert.ToBase64String(Content), "VHASH");
        _write.Links[DocumentId] = NewFileId;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private DeleteCommand Command(FakePrompts prompts) =>
        new(_files, _write, Backups(), States(), prompts);

    private static FakePrompts Confirmed() => new() { TypedWordResponse = "DELETE" };

    [Fact]
    public async Task Deletes_the_old_vendor_file_and_the_old_documentfile_row()
    {
        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Deleted);
        Assert.Contains(OldPath, _files.Deleted);
        Assert.Contains(OldFileId, _write.DeletedFiles);
        Assert.DoesNotContain(NewFileId, _write.DeletedFiles);
        Assert.True(States().IsAtLeast(DocumentId, MigrationState.Deleted));
    }

    [Fact]
    public async Task Without_the_typed_word_nothing_is_deleted()
    {
        var prompts = new FakePrompts { TypedWordResponse = "yes" };

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
        Assert.Empty(_write.DeletedFiles);
    }

    [Fact]
    public async Task Documents_not_yet_repointed_are_never_considered()
    {
        States().Append(new StateRecord(Guid.NewGuid(), MigrationState.BackedUp,
            DateTimeOffset.UtcNow, null, null, null));

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Deleted);          // only the repointed one
    }

    [Fact]
    public async Task A_new_file_that_no_longer_downloads_refuses_the_delete()
    {
        _files.Files.Remove(NewPath);

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task A_new_file_whose_content_drifted_refuses_the_delete()
    {
        _files.Files[NewPath] = (Convert.ToBase64String(Encoding.UTF8.GetBytes("DIFFERENT")), "VHASH");

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task A_document_no_longer_pointing_at_the_new_file_refuses_the_delete()
    {
        _write.Links[DocumentId] = OldFileId;      // someone repointed it back

        var summary = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Equal(1, summary.Refused);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Production_additionally_requires_typing_the_exact_count()
    {
        var prompts = new FakePrompts { TypedWordResponse = "DELETE" };

        // TypedWordResponse only matches one word, so the count prompt fails and aborts.
        var summary = await Command(prompts).RunAsync("prod", isProduction: true, CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Empty(_files.Deleted);
        Assert.Contains(prompts.Questions, q => q.Contains("1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Already_deleted_documents_are_skipped_on_a_re_run()
    {
        await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);
        _files.Deleted.Clear();

        var second = await Command(Confirmed()).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, second.Deleted);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Nothing_to_delete_is_reported_without_prompting()
    {
        File.Delete(Path.Combine(_root, "state.jsonl"));
        var prompts = Confirmed();

        var summary = await Command(prompts).RunAsync("dev", isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Deleted);
        Assert.Empty(prompts.Questions);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter DeleteCommandTests`
Expected: FAIL — `'DeleteCommand' does not exist`.

- [ ] **Step 3: Write the delete command**

Create `src/MocdDocFix/Commands/DeleteCommand.cs`:

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;
using MocdDocFix.Verification;

namespace MocdDocFix.Commands;

public sealed record DeleteSummary(int Deleted, int Skipped, int Refused, bool Aborted, string? AbortReason);

/// <summary>
/// Phase 5 — the only irreversible step. Every candidate is re-verified against live state
/// immediately before its delete, because a report written an hour ago is a stale fact.
/// Note the vendor's delete is a GET (spec section 3.4), so it is called exactly once per file
/// and never retried automatically.
/// </summary>
public sealed class DeleteCommand
{
    private readonly IFileServiceClient _files;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly IPrompts _prompts;

    public DeleteCommand(IFileServiceClient files, ICrmWriteClient write, BackupStore backups,
        StateStore state, IPrompts prompts)
    {
        _files = files;
        _write = write;
        _backups = backups;
        _state = state;
        _prompts = prompts;
    }

    public async Task<DeleteSummary> RunAsync(string env, bool isProduction, CancellationToken ct)
    {
        var manifest = _backups.LoadManifest().ToDictionary(m => m.DocumentId);
        var latest = _state.LoadLatest();

        var candidates = latest.Values
            .Where(r => r.State == MigrationState.Repointed && manifest.ContainsKey(r.DocumentId))
            .ToList();

        if (candidates.Count == 0)
        {
            _prompts.Info("Nothing is awaiting deletion.");
            return new DeleteSummary(0, 0, 0, false, null);
        }

        _prompts.Info("");
        _prompts.Info($"About to permanently delete {candidates.Count} old file(s) from {env}:");
        foreach (var c in candidates.Take(20))
            _prompts.Info($"  {manifest[c.DocumentId].OldFilePath}");
        if (candidates.Count > 20) _prompts.Info($"  … and {candidates.Count - 20} more");
        _prompts.Info("");
        _prompts.Info("This cannot be undone. Local backups keep the bytes, but a restored file");
        _prompts.Info("cannot return to its original path.");

        if (!_prompts.TypedWord($"Delete {candidates.Count} old file(s) from {env}?", "DELETE"))
            return new DeleteSummary(0, 0, 0, true, "Operator did not type DELETE.");

        if (isProduction &&
            !_prompts.TypedWord($"PRODUCTION. Confirm the number of files to delete ({candidates.Count})",
                candidates.Count.ToString()))
        {
            return new DeleteSummary(0, 0, 0, true, "Operator did not confirm the production count.");
        }

        int deleted = 0, refused = 0, skipped = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var entry = manifest[candidate.DocumentId];
            if (_state.IsAtLeast(candidate.DocumentId, MigrationState.Deleted)) { skipped++; continue; }

            var refusal = await WhyNotSafeAsync(candidate, entry, ct);
            if (refusal is not null)
            {
                _prompts.Info($"  REFUSED {entry.OldFilePath} — {refusal}");
                _state.Append(new StateRecord(candidate.DocumentId, MigrationState.Failed,
                    DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                    $"Delete refused: {refusal}"));
                refused++;
                continue;
            }

            var fileDelete = await _files.DeleteAsync(entry.OldFilePath, ct);
            if (!fileDelete.Success)
            {
                _prompts.Info($"  FAILED to delete {entry.OldFilePath} — {fileDelete.Message}");
                refused++;
                continue;
            }

            await _write.DeleteDocumentFileAsync(entry.OldFileId, ct);

            _state.Append(new StateRecord(candidate.DocumentId, MigrationState.Deleted,
                DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                $"Deleted {entry.OldFilePath} and documentfile {entry.OldFileId}."));
            deleted++;
        }

        return new DeleteSummary(deleted, skipped, refused, false, null);
    }

    /// <summary>Re-runs the safety checks against live state. Null means safe to delete.</summary>
    private async Task<string?> WhyNotSafeAsync(StateRecord candidate, ManifestEntry entry, CancellationToken ct)
    {
        if (candidate.NewFileId is null || string.IsNullOrWhiteSpace(candidate.NewFilePath))
            return "no new file recorded";

        var download = await _files.DownloadAsync(candidate.NewFilePath, ct);
        if (!download.Success || download.Data?.File is null)
            return $"the new file no longer downloads ({download.Message})";

        byte[] newBytes;
        try { newBytes = Convert.FromBase64String(download.Data.File); }
        catch (FormatException) { return "the new file did not come back as valid base64"; }

        if (!string.Equals(Verifier.OurHash(newBytes), entry.OurHash, StringComparison.OrdinalIgnoreCase))
            return "the new file's content no longer matches the backup";

        var linked = await _write.GetDocumentFileLinkAsync(entry.DocumentId, ct);
        if (linked != candidate.NewFileId)
            return $"the document points at '{linked?.ToString() ?? "null"}', not the new file";

        return null;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter DeleteCommandTests`
Expected: PASS — `Failed: 0, Passed: 9`.

- [ ] **Step 5: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Commands/DeleteCommand.cs tests/MocdDocFix.Tests/DeleteCommandTests.cs
git commit -m "feat: delete command (phase 5), the only irreversible step

Re-verifies each file against live state immediately before deleting it:
the new copy must download, match the backup's hash, and still be the
document's linked file. Consent is a typed word, and production also
requires typing the exact count."
```

---

## Task 15: Targeted mode

One or more identifiers, all five phases, the same code as the bulk path (§5.3). This is the
verification vehicle — proving it on two documents genuinely proves the bulk path.

**Files:**
- Create: `src/MocdDocFix/Commands/TargetedCommand.cs`
- Test: `tests/MocdDocFix.Tests/TargetedCommandTests.cs`

**Interfaces:**
- Consumes: `ScanCommand` (T11), `BackupCommand` (T12), `MigrateCommand` (T13), `DeleteCommand` (T14), `ICrmReadClient` (T5), `Reporter` (T10), `IPrompts` (T13).
- Produces:
  - `record TargetedSummary(int Resolved, int Fixed, int Reviewed, int Skipped, int NotFound, int Ambiguous, string ScanPath)`
  - `class TargetedCommand { Task<TargetedSummary> RunAsync(string env, IReadOnlyList<string> identifiers, bool forceReview, bool isProduction, CancellationToken ct) }`

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/TargetedCommandTests.cs`:

```csharp
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

public class TargetedCommandTests : IDisposable
{
    private static readonly Guid Correct = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid GamRequest = Guid.Parse("3ff27d73-653e-f111-b119-005056010908");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docfix-tgt-" + Guid.NewGuid());
    private readonly FakeCrmReadClient _read = new();

    public TargetedCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static DocumentRow Doc(string path, Guid? cat) =>
        new(Guid.NewGuid(), "cert.jpg", Guid.NewGuid(), path, "cert.jpg", "image/jpeg", "VHASH",
            Guid.NewGuid(), "Certificate of Good Conduct", cat, null, null, DateTimeOffset.UtcNow);

    private Reporter Reports() => new(Path.Combine(_root, "reports"));

    private TargetedCommand Command(FakePrompts prompts, Func<Task<int>>? pipeline = null) =>
        new(_read,
            new ScanCommand(_read, Reports(), "https://crm/MoCD", new[] { Correct }),
            Reports(),
            prompts,
            runPipelineAsync: rows => pipeline is null
                ? Task.FromResult(rows.Count)
                : pipeline());

    [Fact]
    public async Task An_unknown_identifier_is_reported_and_nothing_runs()
    {
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "nope.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.NotFound);
        Assert.Equal(0, summary.Resolved);
        Assert.Contains(prompts.Messages, m => m.Contains("nope.jpg"));
    }

    [Fact]
    public async Task A_broken_document_is_reported_as_broken_and_queued_for_the_pipeline()
    {
        var doc = Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct);
        _read.Resolutions["cert.jpg"] = new List<DocumentRow> { doc };
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "cert.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Resolved);
        Assert.Equal(1, summary.Fixed);
        Assert.Contains(prompts.Messages, m => m.Contains("BROKEN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(prompts.Messages, m => m.Contains("goodConductCertificate"));
    }

    [Fact]
    public async Task An_already_correct_document_is_reported_and_left_alone()
    {
        var doc = Doc($@"DigitalServices\{Correct}\20260330\a.jpg", Correct);
        _read.Resolutions["cert.jpg"] = new List<DocumentRow> { doc };
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "cert.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Fixed);
        Assert.Equal(1, summary.Skipped);
        Assert.Contains(prompts.Messages, m => m.Contains("already correct", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_ambiguous_document_needs_force_review()
    {
        _read.KnownCatalogues.Add(GamRequest.ToString());
        var doc = Doc($@"DigitalServices\{GamRequest}\20260518\a.pdf", Correct);
        _read.Resolutions["a.pdf"] = new List<DocumentRow> { doc };
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "a.pdf" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(0, summary.Fixed);
        Assert.Equal(1, summary.Reviewed);
        Assert.Contains(prompts.Messages, m => m.Contains("--force-review"));
    }

    [Fact]
    public async Task With_force_review_an_ambiguous_document_is_queued()
    {
        _read.KnownCatalogues.Add(GamRequest.ToString());
        var doc = Doc($@"DigitalServices\{GamRequest}\20260518\a.pdf", Correct);
        _read.Resolutions["a.pdf"] = new List<DocumentRow> { doc };

        var summary = await Command(new FakePrompts()).RunAsync("dev", new[] { "a.pdf" },
            forceReview: true, isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Fixed);
    }

    [Fact]
    public async Task Several_identifiers_are_handled_in_one_run()
    {
        _read.Resolutions["a.jpg"] = new List<DocumentRow>
            { Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct) };
        _read.Resolutions["b.jpg"] = new List<DocumentRow>
            { Doc(@"DigitalServices\0\20260330\b.jpg", Correct) };

        var summary = await Command(new FakePrompts()).RunAsync("dev", new[] { "a.jpg", "b.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(2, summary.Resolved);
        Assert.Equal(2, summary.Fixed);
    }

    [Fact]
    public async Task An_identifier_matching_several_documents_is_flagged_not_guessed()
    {
        _read.Resolutions["dup.jpg"] = new List<DocumentRow>
        {
            Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct),
            Doc(@"DigitalServices\goodConductCertificate\20260331\b.jpg", Correct)
        };
        var prompts = new FakePrompts();

        var summary = await Command(prompts).RunAsync("dev", new[] { "dup.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Equal(1, summary.Ambiguous);
        Assert.Equal(0, summary.Fixed);
        Assert.Contains(prompts.Messages, m => m.Contains("matches 2 documents"));
    }

    [Fact]
    public async Task A_report_is_written_in_targeted_mode_too_stating_reason_and_solution()
    {
        _read.Resolutions["a.jpg"] = new List<DocumentRow>
            { Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct) };

        var summary = await Command(new FakePrompts()).RunAsync("dev", new[] { "a.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.True(File.Exists(summary.ScanPath));
        var text = File.ReadAllText(summary.ScanPath);
        Assert.Contains("Reason", text);
        Assert.Contains("Solution", text);
        Assert.Contains("Re-upload", text);
    }

    [Fact]
    public async Task The_solution_is_shown_on_screen_for_a_broken_file()
    {
        _read.Resolutions["a.jpg"] = new List<DocumentRow>
            { Doc(@"DigitalServices\goodConductCertificate\20260330\a.jpg", Correct) };
        var prompts = new FakePrompts();

        await Command(prompts).RunAsync("dev", new[] { "a.jpg" },
            forceReview: false, isProduction: false, CancellationToken.None);

        Assert.Contains(prompts.Messages, m => m.Contains("SOLUTION"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter TargetedCommandTests`
Expected: FAIL — `'TargetedCommand' does not exist`.

- [ ] **Step 3: Write the targeted command**

Create `src/MocdDocFix/Commands/TargetedCommand.cs`:

```csharp
using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

public sealed record TargetedSummary(
    int Resolved, int Fixed, int Reviewed, int Skipped, int NotFound, int Ambiguous,
    string ScanPath);

/// <summary>
/// Targeted mode (spec section 5.3). Accepts document ids, documentfile ids and file names —
/// all three are usable because mocd_documentfileid == vendor FileId == the path's file stem.
/// Runs the same code as the bulk path.
/// </summary>
public sealed class TargetedCommand
{
    private readonly ICrmReadClient _read;
    private readonly ScanCommand _scan;
    private readonly Reporter _reporter;
    private readonly IPrompts _prompts;
    private readonly Func<IReadOnlyList<ScanRow>, Task<int>> _runPipelineAsync;

    /// <param name="runPipelineAsync">
    /// Backup → migrate → (optionally) delete for the chosen rows. Injected so this command's
    /// resolution and reporting behaviour is testable without the network.
    /// </param>
    public TargetedCommand(ICrmReadClient read, ScanCommand scan, Reporter reporter, IPrompts prompts,
        Func<IReadOnlyList<ScanRow>, Task<int>> runPipelineAsync)
    {
        _read = read;
        _scan = scan;
        _reporter = reporter;
        _prompts = prompts;
        _runPipelineAsync = runPipelineAsync;
    }

    public async Task<TargetedSummary> RunAsync(string env, IReadOnlyList<string> identifiers,
        bool forceReview, bool isProduction, CancellationToken ct)
    {
        int resolved = 0, notFound = 0, ambiguous = 0, reviewed = 0, skipped = 0;
        var queued = new List<ScanRow>();
        var classified = new List<ScanRow>();   // every resolved row, whatever its verdict

        foreach (var identifier in identifiers)
        {
            ct.ThrowIfCancellationRequested();

            var matches = await _read.ResolveIdentifierAsync(identifier, ct);

            if (matches.Count == 0)
            {
                _prompts.Info($"NOT FOUND  '{identifier}' matched no document.");
                notFound++;
                continue;
            }

            if (matches.Count > 1)
            {
                _prompts.Info($"AMBIGUOUS  '{identifier}' matches {matches.Count} documents:");
                foreach (var m in matches)
                    _prompts.Info($"    {m.DocumentId}  {m.FileName}  {m.FilePath}");
                _prompts.Info("           Re-run with the specific document id you want.");
                ambiguous++;
                continue;
            }

            resolved++;
            var document = matches[0];
            var result = await _scan.ClassifyAsync(new[] { document }, env, writeReports: false, ct);
            var row = result.All[0];
            classified.Add(row);

            _prompts.Info("");
            _prompts.Info($"{identifier}");
            _prompts.Info($"  document      {document.DocumentId}");
            _prompts.Info($"  document type {document.DocumentTypeName}");
            _prompts.Info($"  current path  {document.FilePath}");

            if (row.Verdict == nameof(Verdict.Skip))
            {
                _prompts.Info($"  VERDICT  OK — {row.Reason}");
                skipped++;
                continue;
            }

            if (row.Verdict == nameof(Verdict.Review) && !forceReview)
            {
                _prompts.Info($"  VERDICT  AMBIGUOUS — {row.Reason}");
                _prompts.Info("           Not touched. Re-run with --force-review to act on it anyway.");
                reviewed++;
                continue;
            }

            _prompts.Info($"  VERDICT  BROKEN — {row.Reason}");
            _prompts.Info($"  correct catalogue  {row.CorrectCatalogueId}");
            if (document.CrossCheckSource is not null)
                _prompts.Info($"  cross-check        {document.CrossCheckSource} " +
                              $"{(document.CrossCheckCatalogueId == row.CorrectCatalogueId ? "agrees" : "DISAGREES")}");
            _prompts.Info($"  SOLUTION {row.Solution}");

            queued.Add(row);
        }

        // The report comes first in every mode, not only bulk (spec section 8.1) — so there is
        // always a written record of what was found and what was intended, before any change.
        var scanPath = _reporter.WriteScan(env, classified);
        _prompts.Info("");
        _prompts.Info($"report → {scanPath}");

        var fixedCount = queued.Count == 0 ? 0 : await _runPipelineAsync(queued);
        return new TargetedSummary(resolved, fixedCount, reviewed, skipped, notFound, ambiguous, scanPath);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter TargetedCommandTests`
Expected: PASS — `Failed: 0, Passed: 9`.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 141 tests.

- [ ] **Step 6: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Commands/TargetedCommand.cs tests/MocdDocFix.Tests/TargetedCommandTests.cs
git commit -m "feat: targeted mode for one or more identifiers

Accepts document ids, documentfile ids and file names, says plainly whether
each is broken before doing anything, refuses to guess when a name matches
several documents, and requires --force-review for ambiguous files."
```

---

## Task 16: Command line, production gating and the dev rehearsal

Wires everything together and enforces §7.1: **production is never selectable by pressing Enter.**

**Files:**
- Create: `src/MocdDocFix/Cli/CommandLineOptions.cs`, `src/MocdDocFix/Cli/EnvironmentSelector.cs`
- Modify: `src/MocdDocFix/Program.cs` (replace the template contents entirely)
- Test: `tests/MocdDocFix.Tests/CommandLineOptionsTests.cs`, `tests/MocdDocFix.Tests/EnvironmentSelectorTests.cs`

**Interfaces:**
- Consumes: everything from T1–T15.
- Produces:
  - `record CommandLineOptions(string Command, string? Environment, IReadOnlyList<string> Identifiers, bool ForceReview, bool DryRun, bool ConfirmProduction, string? Error)` with `static Parse(string[] args)`
  - `static class EnvironmentSelector { static string? Select(IPrompts prompts, string? fromArgs, AppConfig config, bool confirmProductionFlag) }`

- [ ] **Step 1: Write the failing tests**

Create `tests/MocdDocFix.Tests/CommandLineOptionsTests.cs`:

```csharp
using MocdDocFix.Cli;
using Xunit;

namespace MocdDocFix.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void Parses_a_simple_scan()
    {
        var o = CommandLineOptions.Parse(new[] { "scan", "--env", "dev" });

        Assert.Equal("scan", o.Command);
        Assert.Equal("dev", o.Environment);
        Assert.Null(o.Error);
    }

    [Fact]
    public void Parses_a_comma_separated_identifier_list()
    {
        var o = CommandLineOptions.Parse(new[] { "targeted", "--env", "dev", "--docs", "a.jpg,b.jpg, c.jpg " });

        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, o.Identifiers);
    }

    [Fact]
    public void Parses_repeated_docs_flags()
    {
        var o = CommandLineOptions.Parse(new[] { "targeted", "--docs", "a.jpg", "--docs", "b.jpg" });

        Assert.Equal(new[] { "a.jpg", "b.jpg" }, o.Identifiers);
    }

    [Fact]
    public void Parses_the_boolean_flags()
    {
        var o = CommandLineOptions.Parse(new[] { "targeted", "--force-review", "--dry-run", "--confirm-production" });

        Assert.True(o.ForceReview);
        Assert.True(o.DryRun);
        Assert.True(o.ConfirmProduction);
    }

    [Fact]
    public void An_unknown_flag_is_an_error_not_a_silent_ignore()
    {
        var o = CommandLineOptions.Parse(new[] { "scan", "--enviroment", "dev" });

        Assert.NotNull(o.Error);
        Assert.Contains("--enviroment", o.Error);
    }

    [Fact]
    public void An_unknown_command_is_an_error()
    {
        Assert.Contains("frobnicate", CommandLineOptions.Parse(new[] { "frobnicate" }).Error!);
    }

    [Fact]
    public void No_arguments_asks_for_help_rather_than_guessing()
    {
        Assert.NotNull(CommandLineOptions.Parse(Array.Empty<string>()).Error);
    }

    [Fact]
    public void A_flag_with_a_missing_value_is_an_error()
    {
        Assert.Contains("--env", CommandLineOptions.Parse(new[] { "scan", "--env" }).Error!);
    }
}
```

Create `tests/MocdDocFix.Tests/EnvironmentSelectorTests.cs`:

```csharp
using MocdDocFix.Cli;
using MocdDocFix.Config;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

public class EnvironmentSelectorTests
{
    private static AppConfig Config(bool withProd = true)
    {
        var c = AppConfig.Default();
        c.Environments["dev"] = new EnvironmentConfig("http://d", "https://d", "D", "u", false);
        if (withProd) c.Environments["prod"] = new EnvironmentConfig("http://p", "https://p", "P", "u", true);
        return c;
    }

    [Fact]
    public void A_non_production_environment_from_args_is_accepted()
        => Assert.Equal("dev", EnvironmentSelector.Select(new FakePrompts(), "dev", Config(), false));

    [Fact]
    public void Production_from_args_still_requires_typing_the_word()
    {
        var refused = new FakePrompts { TypedWordResponse = "" };
        Assert.Null(EnvironmentSelector.Select(refused, "prod", Config(), confirmProductionFlag: true));

        var confirmed = new FakePrompts { TypedWordResponse = "prod" };
        Assert.Equal("prod", EnvironmentSelector.Select(confirmed, "prod", Config(), confirmProductionFlag: true));
    }

    [Fact]
    public void Production_without_the_confirm_flag_is_refused_outright()
    {
        var prompts = new FakePrompts { TypedWordResponse = "prod" };

        Assert.Null(EnvironmentSelector.Select(prompts, "prod", Config(), confirmProductionFlag: false));
        Assert.Contains(prompts.Messages, m => m.Contains("--confirm-production"));
    }

    [Fact]
    public void An_unconfigured_environment_is_rejected()
        => Assert.Null(EnvironmentSelector.Select(new FakePrompts(), "test", Config(), false));

    [Fact]
    public void The_menu_never_offers_production_as_a_default_keypress()
    {
        var prompts = new FakePrompts { TypedWordResponse = "" };

        EnvironmentSelector.Select(prompts, null, Config(), false);

        var menu = string.Join("\n", prompts.Messages);
        Assert.Contains("dev", menu);
        Assert.Contains("type the environment name in full", menu, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "CommandLineOptionsTests|EnvironmentSelectorTests"`
Expected: FAIL — `'CommandLineOptions' does not exist`.

- [ ] **Step 3: Write the option parser**

Create `src/MocdDocFix/Cli/CommandLineOptions.cs`:

```csharp
namespace MocdDocFix.Cli;

public sealed record CommandLineOptions(
    string Command,
    string? Environment,
    IReadOnlyList<string> Identifiers,
    bool ForceReview,
    bool DryRun,
    bool ConfirmProduction,
    string? Error)
{
    private static readonly string[] Commands =
        { "scan", "backup", "migrate", "delete", "targeted", "config" };

    public const string Usage = """
        docfix <command> [options]

        Commands
          scan       classify every in-scope document (read-only)
          backup     download and verify every FIX file (read-only)
          migrate    upload corrected copies and repoint CRM (writes)
          delete     delete the old files — IRREVERSIBLE (writes)
          targeted   run all phases on one or more identifiers
          config     set up an environment

        Options
          --env <name>            dev | test | preprod | prod
          --docs <a,b,c>          document id, documentfile id or file name (repeatable)
          --docs-file <path>      one identifier per line
          --force-review          act on ambiguous files too (targeted only)
          --dry-run               read and verify, write nothing
          --confirm-production    required before prod can be selected at all
        """;

    public static CommandLineOptions Parse(string[] args)
    {
        if (args.Length == 0) return Error_("No command given." + System.Environment.NewLine + Usage);

        var command = args[0].ToLowerInvariant();
        if (!Commands.Contains(command))
            return Error_($"Unknown command '{args[0]}'." + System.Environment.NewLine + Usage);

        string? env = null;
        var identifiers = new List<string>();
        bool forceReview = false, dryRun = false, confirmProduction = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--env":
                    if (++i >= args.Length) return Error_("--env needs a value.");
                    env = args[i];
                    break;

                case "--docs":
                    if (++i >= args.Length) return Error_("--docs needs a value.");
                    identifiers.AddRange(args[i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0));
                    break;

                case "--docs-file":
                    if (++i >= args.Length) return Error_("--docs-file needs a value.");
                    if (!File.Exists(args[i])) return Error_($"File not found: {args[i]}");
                    identifiers.AddRange(File.ReadAllLines(args[i])
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0 && !l.StartsWith('#')));
                    break;

                case "--force-review": forceReview = true; break;
                case "--dry-run": dryRun = true; break;
                case "--confirm-production": confirmProduction = true; break;

                default:
                    return Error_($"Unknown option '{args[i]}'." + System.Environment.NewLine + Usage);
            }
        }

        return new CommandLineOptions(command, env, identifiers, forceReview, dryRun, confirmProduction, null);
    }

    private static CommandLineOptions Error_(string message) =>
        new(string.Empty, null, Array.Empty<string>(), false, false, false, message);
}
```

- [ ] **Step 4: Write the environment selector**

Create `src/MocdDocFix/Cli/EnvironmentSelector.cs`:

```csharp
using MocdDocFix.Config;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>
/// Production can never be reached by pressing Enter on a menu (spec section 7.1): it needs
/// the --confirm-production flag AND the environment name typed in full.
/// </summary>
public static class EnvironmentSelector
{
    public static string? Select(IPrompts prompts, string? fromArgs, AppConfig config, bool confirmProductionFlag)
    {
        var name = fromArgs;

        if (name is null)
        {
            prompts.Info("");
            prompts.Info("Configured environments:");
            foreach (var (key, value) in config.Environments.OrderBy(e => e.Key))
                prompts.Info($"  {key}{(value.IsProduction ? "   *** PRODUCTION ***" : "")}");
            prompts.Info("");

            // A typed name, never a numbered menu — a number is too easy to mis-key.
            if (!prompts.TypedWord("Which environment? (type the environment name in full)", " "))
            {
                // TypedWord returns false for anything but the sentinel; capture the real answer below.
            }
            return null;
        }

        if (!config.Environments.TryGetValue(name, out var env))
        {
            prompts.Info($"Environment '{name}' is not configured. Run: docfix config --env {name}");
            return null;
        }

        if (!env.IsProduction) return name;

        if (!confirmProductionFlag)
        {
            prompts.Info($"'{name}' is a PRODUCTION environment.");
            prompts.Info("Re-run with --confirm-production if you really mean it.");
            return null;
        }

        prompts.Info("");
        prompts.Info($"*** {name.ToUpperInvariant()} IS PRODUCTION ***");
        prompts.Info("Writes here affect live citizen documents.");

        return prompts.TypedWord($"Confirm the environment name", name) ? name : null;
    }
}
```

> **Note for the implementer:** the interactive branch above is deliberately incomplete —
> `IPrompts` has no free-text read. Add one method to the interface and its two implementations:
> `string ReadLine(string question);` (`ConsolePrompts` writes the question and returns
> `Console.ReadLine()?.Trim() ?? ""`; `FakePrompts` returns a settable `ReadLineResponse`).
> Then replace the interactive branch with:
> ```csharp
> name = prompts.ReadLine("Which environment? (type the environment name in full)");
> if (string.IsNullOrWhiteSpace(name)) return null;
> return Select(prompts, name, config, confirmProductionFlag);
> ```
> and update `EnvironmentSelectorTests.The_menu_never_offers_production_as_a_default_keypress`
> to set `ReadLineResponse = ""`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "CommandLineOptionsTests|EnvironmentSelectorTests"`
Expected: PASS — `Failed: 0, Passed: 13`.

- [ ] **Step 6: Write the composition root**

Replace the entire contents of `src/MocdDocFix/Program.cs`:

```csharp
using MocdDocFix.Cli;
using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Config;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

var options = CommandLineOptions.Parse(args);
if (options.Error is not null)
{
    Console.Error.WriteLine(options.Error);
    return 2;
}

var prompts = new ConsolePrompts();
var configDir = ConfigStore.DefaultDirectory;
var secrets = new DpapiSecretStore(Path.Combine(configDir, "secrets.dat"));
var configStore = new ConfigStore(Path.Combine(configDir, "config.json"), secrets);
var appConfig = configStore.Load();

if (options.Command == "config")
{
    Console.WriteLine("Interactive environment setup is not yet implemented.");
    Console.WriteLine($"Edit {Path.Combine(configDir, "config.json")} and store secrets with the config command.");
    return 1;
}

var envName = EnvironmentSelector.Select(prompts, options.Environment, appConfig, options.ConfirmProduction);
if (envName is null) return 1;

var env = configStore.Resolve(envName);

Console.WriteLine($"Environment: {envName}{(env.IsProduction ? "   *** PRODUCTION ***" : "")}");
Console.WriteLine($"CRM:         {env.CrmUrl}");
Console.WriteLine($"File server: {env.FileServiceBaseUrl}");
Console.WriteLine($"Data root:   {appConfig.DataRoot}");
if (options.DryRun) Console.WriteLine("DRY RUN — nothing will be written.");
Console.WriteLine();

var dataRoot = Path.Combine(appConfig.DataRoot, envName);
var reporter = new Reporter(Path.Combine(dataRoot, "reports"));
var backups = new BackupStore(Path.Combine(dataRoot, "backup"),
                              Path.Combine(dataRoot, "state", "restore-manifest.jsonl"));
var state = new StateStore(Path.Combine(dataRoot, "state", $"state-{envName}.jsonl"));

using var crmHttp = CrmHttp.Create(env);
var read = new CrmReadClient(crmHttp, env);
var write = new CrmWriteClient(crmHttp);

using var fileHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var files = new FileServiceClient(fileHttp, env);

var scan = new ScanCommand(read, reporter, env.CrmUrl, appConfig.ServiceCatalogues);
var opener = new ShellFileOpener();

// mocd_hash for a scanned row, needed by the backup phase's first check.
var hashes = new Dictionary<Guid, string?>();
Func<ScanRow, string?> hashLookup = row => hashes.TryGetValue(row.DocumentFileId, out var h) ? h : null;

async Task<ScanResult> ScanAsync()
{
    var documents = await read.GetInScopeDocumentsAsync(appConfig.ServiceCatalogues, CancellationToken.None);
    foreach (var d in documents) hashes[d.DocumentFileId] = d.Hash;
    return await scan.ClassifyAsync(documents, envName, writeReports: true, CancellationToken.None);
}

switch (options.Command)
{
    case "scan":
    {
        var result = await ScanAsync();
        Console.WriteLine(result.Banner());
        Console.WriteLine();
        Console.WriteLine($"scan   → {result.ScanPath}   (reason and solution for every row)");
        Console.WriteLine($"review → {result.ReviewPath}");
        return 0;
    }

    case "backup":
    {
        // Stage one then stage two, reported before anything else happens (spec section 5.0).
        var result = await ScanAsync();
        Console.WriteLine(result.Banner());
        Console.WriteLine();
        Console.WriteLine($"scan   → {result.ScanPath}   (reason and solution for every row)");
        Console.WriteLine($"review → {result.ReviewPath}");
        Console.WriteLine();

        var estimate = result.Fix.Count * 350_000L;
        var free = BackupStore.FreeSpaceBytes(appConfig.DataRoot);
        Console.WriteLine($"{result.Fix.Count} files, roughly {estimate / 1_048_576:N0} MB. " +
                          $"Free space {free / 1_048_576:N0} MB.");
        if (free < estimate * 2)
        {
            Console.Error.WriteLine("Not enough free space with margin. Aborting.");
            return 1;
        }
        if (options.DryRun) return 0;

        var summary = await new BackupCommand(files, read, backups, state, reporter, hashLookup)
            .RunAsync(envName, result.Fix, CancellationToken.None);
        Console.WriteLine($"Saved {summary.Saved}, quarantined {summary.Quarantined}, " +
                          $"skipped {summary.Skipped}, {summary.TotalBytes / 1_048_576:N0} MB.");
        return summary.Quarantined > 0 ? 1 : 0;
    }

    case "migrate":
    {
        if (options.DryRun) { Console.WriteLine("Dry run: migrate writes, so nothing was done."); return 0; }

        var summary = await new MigrateCommand(files, read, write, backups, state, reporter,
            prompts, opener, env.CrmUrl).RunAsync(envName, CancellationToken.None);

        Console.WriteLine($"Migrated {summary.Migrated}, skipped {summary.Skipped}, failed {summary.Failed}.");
        Console.WriteLine($"report → {summary.ReportPath}");
        if (summary.Halted) Console.Error.WriteLine($"RUN HALTED: {summary.HaltReason}");
        return summary.Halted ? 1 : 0;
    }

    case "delete":
    {
        if (options.DryRun) { Console.WriteLine("Dry run: delete is irreversible, so nothing was done."); return 0; }

        var summary = await new DeleteCommand(files, write, backups, state, prompts)
            .RunAsync(envName, env.IsProduction, CancellationToken.None);

        Console.WriteLine($"Deleted {summary.Deleted}, refused {summary.Refused}, skipped {summary.Skipped}.");
        if (summary.Aborted) Console.WriteLine($"Aborted: {summary.AbortReason}");
        return summary.Refused > 0 ? 1 : 0;
    }

    case "targeted":
    {
        if (options.Identifiers.Count == 0)
        {
            Console.Error.WriteLine("targeted needs --docs or --docs-file.");
            return 2;
        }

        var targeted = new TargetedCommand(read, scan, reporter, prompts, async rows =>
        {
            foreach (var row in rows)
            {
                var document = (await read.ResolveIdentifierAsync(row.DocumentId.ToString(),
                    CancellationToken.None)).FirstOrDefault();
                if (document is not null) hashes[document.DocumentFileId] = document.Hash;
            }

            if (options.DryRun) { Console.WriteLine("Dry run — stopping before backup."); return 0; }

            await new BackupCommand(files, read, backups, state, reporter, hashLookup)
                .RunAsync(envName, rows, CancellationToken.None);

            var migrated = await new MigrateCommand(files, read, write, backups, state, reporter,
                prompts, opener, env.CrmUrl).RunAsync(envName, CancellationToken.None);

            if (migrated.Migrated > 0)
                await new DeleteCommand(files, write, backups, state, prompts)
                    .RunAsync(envName, env.IsProduction, CancellationToken.None);

            return migrated.Migrated;
        });

        var summary = await targeted.RunAsync(envName, options.Identifiers,
            options.ForceReview, env.IsProduction, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"Resolved {summary.Resolved}, fixed {summary.Fixed}, " +
                          $"ambiguous-verdict {summary.Reviewed}, already-ok {summary.Skipped}, " +
                          $"not found {summary.NotFound}, name clashes {summary.Ambiguous}.");
        return 0;
    }

    default:
        Console.Error.WriteLine(CommandLineOptions.Usage);
        return 2;
}
```

- [ ] **Step 7: Build and run the whole suite**

Run: `dotnet build && dotnet test`
Expected: `Build succeeded`, then PASS — 154 tests.

- [ ] **Step 8: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Cli src/MocdDocFix/Program.cs tests/MocdDocFix.Tests/CommandLineOptionsTests.cs \
        tests/MocdDocFix.Tests/EnvironmentSelectorTests.cs
git commit -m "feat: command line, composition root and production gating

Production needs both --confirm-production and the environment name typed
in full; it can never be reached by pressing Enter. Every run echoes the
environment before doing anything."
```

- [ ] **Step 9: Dev rehearsal — the first real run**

Do these in order, on **dev only**, and stop at the first surprise.

1. Configure dev. Edit `%APPDATA%\mocd-docfix\config.json` with the dev file service base URL and
   CRM URL, then store the secrets.
2. **Confirm the unencoded path assumption** (§3.3) — the one thing unit tests cannot prove:
   pick any `mocd_filepath` from dev and call the download endpoint by hand with the backslashes
   unescaped. If it returns content, the client is correct as written. If it 404s, try
   percent-encoded and record which form works before going further.
3. `docfix scan --env dev` — check the counts look plausible and open the review CSV.
4. `docfix targeted --env dev --docs <one document id> --dry-run` — verify it reports BROKEN with
   the right correct-catalogue and stops.
5. `docfix targeted --env dev --docs <that same id>` — run it for real on **one** document.
   Confirm: both files open and look identical, the new path carries the right catalogue GUID,
   the CRM document opens and its View button works, and only then answer `DELETE`.
6. Verify in CRM that the document shows the new file and the old path is gone from the server.
7. Repeat step 5 with **two** ids in one run to exercise the multi-identifier path.
8. Only then consider `backup` + `migrate` across the full dev set.

- [ ] **Step 10: Record what the rehearsal found**

Append a short "Rehearsal notes" section to
`docs/specs/2026-09-10-document-filepath-remediation-design.md` answering §11's open questions
that the rehearsal settles — the unencoded-path result, whether the file service is reachable,
whether the UNC share works, and what `ApplicationId` the vendor expects. Commit it.

---

## Self-review

**Spec coverage.** Every section maps to a task: §1 evidence → T2/T11 classification; §2 scope →
T3 config; §3 endpoints → T4; §4.1 invariants → T1/T5/T6; §4.2–4.3 classification → T2/T11;
§5 phases → T11–T15; §5.3 modes → T15/T16; §6.1–6.2 gate → T7; §6.3 operator UI → T13;
§6.4 annotations → no task needed (proven to have no impact); §7 config → T3; §7.1 prod gating →
T16; §8 artefacts → T8/T9/T10; §9 error handling → spread across T11–T14 (quarantine, halt,
resume, `modifiedon`, disk space, `--dry-run`); §10 testing → every task plus T16 step 9;
§11 open questions → T16 step 10.

**Known gaps, deliberate.** The `config` command's interactive setup is stubbed in T16 —
environments are configured by editing the JSON for now, which is acceptable because only dev is
in use. `IPrompts.ReadLine` is called for in T16 step 4's note and must be added there.

**Type consistency.** `ScanRow` and `MigrationRow` are defined once in T10 and consumed unchanged
by T11–T15. `IFileServiceClient`, `ICrmReadClient` and `ICrmWriteClient` are defined in T4/T5/T6
and faked identically in tests. `MigrationState` ordering is relied on by `IsAtLeast` (T8) and by
the resume checks in T12/T13/T14.

**Counts.** Test totals accumulate 9, 15, 7, 5, 10, 6, 19, 9, 8, 8, 8, 8, 11, 9, 9, 13 = **154**.
