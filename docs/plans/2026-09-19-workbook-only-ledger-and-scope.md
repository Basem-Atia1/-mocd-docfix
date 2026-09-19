# Workbook-only ledger, a shorter sheet, and a choice of scope — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Excel workbook the only ledger, keep correct documents out of it, split it into three tabs and two scoped files, let the operator re-read it mid-run, and stop it taking eleven seconds to save.

**Architecture:** Every change lands behind an existing boundary. `LedgerWorkbook` gains tabs and an atomic save but still returns one flat `IReadOnlyList<LedgerRow>`, so no mode notices. `LedgerMerge` gains the rule that a correct document gets no row. The scope choice is a new `LedgerSet` that owns one or two `LedgerStore`s and routes a row to the file its service catalogue belongs to. Two new small command classes — `LegacyCleanup` and `VerdictGuard` — hold the one-time clearing and the hand-edit guard, so `Session` only wires them.

**Tech Stack:** C# / .NET 8 (`net8.0`), xUnit, ClosedXML 0.97.0, Dynamics 365 Web API over NTLM. Build with `dotnet build`, test with `dotnet test`. No Visual Studio needed.

**Spec:** `docs/specs/2026-09-18-workbook-only-ledger-and-scope-design.md`

## Global Constraints

- Target framework `net8.0`; `TreatWarningsAsErrors` is on — a warning fails the build.
- `Nullable` and `ImplicitUsings` are enabled.
- **The CsvHelper package reference is removed in Task 2.** No file may reference `CsvHelper` after that task.
- Column **headers and their order must not change**. They are read in Excel and every ledger on disk depends on them.
- `RowStates.Corrected` is the literal string `"corrected and pending the delete of old docs"`. Never abbreviate it.
- The tool is **read-only toward CRM except for the corrections it already makes**. Nothing in this plan adds a CRM write.
- Tests must not reach the network, the file server, or CRM. Use the fakes in `tests/MocdDocFix.Tests/Fakes/`.
- `dotnet test` must end green at every commit. Baseline before Task 1: **576 passed, 0 failed.**

## File Structure

| file | responsibility | task |
|---|---|---|
| `src/MocdDocFix/Storage/LedgerWorkbook.cs` | the xlsx itself: three tabs, fixed widths, atomic save | 1, 3 |
| `src/MocdDocFix/Storage/LedgerStore.cs` | one ledger file: backups, retry, write | 2 |
| `src/MocdDocFix/Storage/LedgerSet.cs` | **new** — one or two stores, routing a row to its file | 8 |
| `src/MocdDocFix/Domain/ColumnAttribute.cs` | **new** — replaces CsvHelper's `[Index]`/`[Name]` | 2 |
| `src/MocdDocFix/Domain/LedgerTabs.cs` | **new** — which tab a row belongs on; the one rule | 3 |
| `src/MocdDocFix/Domain/LedgerColumns.cs` | columns by reflection over `[Column]` | 2 |
| `src/MocdDocFix/Domain/LedgerOrder.cs` | sort and renumber, now per tab | 3 |
| `src/MocdDocFix/Domain/RowVerdict.cs` | the verdict vocabulary, minus `skip` | 4 |
| `src/MocdDocFix/Domain/Classifier.cs` | groups 8 and 9 | 4 |
| `src/MocdDocFix/Domain/DocumentGroups.cs` | the wording of groups 8 and 9 | 4 |
| `src/MocdDocFix/Commands/LedgerMerge.cs` | correct documents get no row | 5 |
| `src/MocdDocFix/Commands/LegacyCleanup.cs` | **new** — the one-time clearing of an old sheet | 5 |
| `src/MocdDocFix/Commands/VerdictGuard.cs` | **new** — a verdict hand-edited onto a finished row | 6 |
| `src/MocdDocFix/Cli/LedgerGate.cs` | **new** — "go ahead / read again / cancel", shared by four modes | 7 |
| `src/MocdDocFix/Cli/Session.cs` | wiring only | 2, 5, 6, 7, 8 |
| `src/MocdDocFix/Cli/Wizard.cs` | banner and the Change services entry | 8 |
| `src/MocdDocFix/Config/*` | the eight services, and the remembered scope | 8 |
| `src/MocdDocFix/Clients/CrmReadClient.cs` | list every service catalogue | 8 |

Tasks are ordered so each one leaves the build green and is useful on its own. Task 1 fixes a
live defect and depends on nothing.

---

## Task 1: Fixed column widths and an atomic save

Spec §10 and §11. `AdjustToContents()` is 97% of every ledger write, and the write is not atomic.

**Files:**
- Modify: `src/MocdDocFix/Storage/LedgerWorkbook.cs`
- Test: `tests/MocdDocFix.Tests/LedgerWorkbookTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `LedgerWorkbook.Write` still has the signature `void Write(IReadOnlyList<LedgerRow> rows)`. No caller changes.

- [ ] **Step 1: Write the failing test for the atomic save**

Add to `tests/MocdDocFix.Tests/LedgerWorkbookTests.cs`:

```csharp
[Fact]
public void A_stray_temporary_file_is_cleared_by_the_next_write()
{
    var path = Path.Combine(_dir, "repair-dev.xlsx");
    var stray = path + ".tmp";
    File.WriteAllText(stray, "half a workbook");

    new LedgerWorkbook(path).Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

    Assert.True(File.Exists(path));
    Assert.False(File.Exists(stray));
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~A_stray_temporary_file_is_cleared"
```

Expected: FAIL — `Assert.False() Failure` on the stray file, which is still there.

- [ ] **Step 3: Make the save go through a temporary file**

In `src/MocdDocFix/Storage/LedgerWorkbook.cs`, replace the final line of `Write`:

```csharp
        workbook.SaveAs(Path);
```

with:

```csharp
        // Saved beside the ledger and moved over it, rather than saved onto it. A process
        // killed mid-save — the console window closed, the machine shut down — otherwise leaves
        // a half-written workbook, and half a workbook opens as nothing at all. A move within
        // one folder is atomic on NTFS, so what is on disk is always one whole version or the
        // other.
        var temporary = Path + ".tmp";

        workbook.SaveAs(temporary);
        File.Move(temporary, Path, overwrite: true);
```

and add, immediately after the `Directory.CreateDirectory` line at the top of `Write`:

```csharp
        // A .tmp left by a killed process is rubbish: it is a partial file with no reader.
        // Clearing it here rather than on start-up keeps the rule beside the code that makes it.
        var leftOver = Path + ".tmp";
        if (File.Exists(leftOver)) File.Delete(leftOver);
```

- [ ] **Step 4: Run it and watch it pass**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~A_stray_temporary_file_is_cleared"
```

Expected: PASS.

- [ ] **Step 5: Write the failing test for fixed widths**

Add to `tests/MocdDocFix.Tests/LedgerWorkbookTests.cs`:

```csharp
/// <summary>
/// AdjustToContents measured every cell of every column and cost 97% of each write — about
/// eleven seconds on a thousand-row ledger, paid after every corrected document. The widths are
/// now fixed, so this pins that no column is left at the default and none is absurdly wide.
/// </summary>
[Fact]
public void Every_column_gets_a_width_and_none_is_wider_than_the_cap()
{
    var path = Path.Combine(_dir, "repair-dev.xlsx");

    new LedgerWorkbook(path).Write(new[] { Row(RowVerdicts.Fix, "Board Decision") });

    using var book = new ClosedXML.Excel.XLWorkbook(path);
    var sheet = book.Worksheet("ledger");

    for (var c = 1; c <= LedgerColumns.All.Count; c++)
    {
        Assert.True(sheet.Column(c).Width >= 6, $"column {c} is too narrow");
        Assert.True(sheet.Column(c).Width <= 60, $"column {c} is wider than the cap");
    }
}
```

- [ ] **Step 6: Run it and watch it fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~Every_column_gets_a_width"
```

Expected: FAIL — the narrow columns come back at the default width, which is under 6 for the
`row` and `group` columns once `AdjustToContents` has fitted them to a one-digit number.

- [ ] **Step 7: Replace the measured widths with fixed ones**

In `src/MocdDocFix/Storage/LedgerWorkbook.cs`, delete:

```csharp
        // Fitted to the contents, then capped — a file path is 120 characters and one such
        // column pushes every other off the screen.
        sheet.Columns().AdjustToContents();
        foreach (var column in sheet.ColumnsUsed())
            if (column.Width > WidestColumn)
                column.Width = WidestColumn;
```

and put in its place:

```csharp
        SetWidths(sheet);
```

Then add the field and the method to the class:

```csharp
    /// <summary>
    /// How wide each column is, by header.
    ///
    /// Fixed rather than measured. AdjustToContents walks every cell of every column to fit the
    /// widths, and on a 29-column sheet that was 97% of the cost of a write — 15.7 seconds at
    /// 2,000 rows against 0.57 without it — paid after every single corrected document. The
    /// sheet looks the same; nobody was ever going to notice a column two characters wider than
    /// its widest value.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> Widths =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = 6,
            ["group"] = 7,
            ["verdict"] = 12,
            ["final state"] = 38,
            ["way of upload"] = 14,
            ["doc name"] = 30,
            ["doc type name"] = 30,
            ["doc file name"] = 26,
            ["service catalogue name"] = 30,
            ["correct service catalogue name"] = 30,
            ["old category"] = 22,
        };

    /// <summary>Anything not named above, including every path, link and explanation.</summary>
    private const double DefaultColumn = 40;

    private static void SetWidths(IXLWorksheet sheet)
    {
        var columns = LedgerColumns.All;

        for (var c = 0; c < columns.Count; c++)
            sheet.Column(c + 1).Width =
                Widths.TryGetValue(columns[c].Header, out var width) ? width : DefaultColumn;
    }
```

Delete the now-unused constant:

```csharp
    /// <summary>Anything longer than this is still stored whole — only the column is capped.</summary>
    private const double WidestColumn = 60;
```

- [ ] **Step 8: Run the full suite**

```bash
cd /d/mocd-docfix && dotnet test
```

Expected: PASS, 578 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
cd /d/mocd-docfix
git add src/MocdDocFix/Storage/LedgerWorkbook.cs tests/MocdDocFix.Tests/LedgerWorkbookTests.cs
git commit -m "perf(ledger): fixed column widths, and a save that cannot leave half a file

AdjustToContents measured every cell of every column on every write, which is
after every corrected document: 15.7 seconds at 2,000 rows against 0.57 without
it. The widths are now fixed per column.

The save also goes through a .tmp and is moved into place, so a process killed
mid-write leaves either the whole old ledger or the whole new one."
```

---

## Task 2: The CSV goes

Spec §1. The workbook becomes the only ledger, and the CsvHelper dependency leaves with it.

**Files:**
- Create: `src/MocdDocFix/Domain/ColumnAttribute.cs`
- Modify: `src/MocdDocFix/Domain/LedgerRow.cs`, `src/MocdDocFix/Domain/LedgerColumns.cs`, `src/MocdDocFix/Storage/LedgerStore.cs`, `src/MocdDocFix/Cli/Session.cs`, `src/MocdDocFix/MocdDocFix.csproj`
- Test: `tests/MocdDocFix.Tests/LedgerStoreTests.cs`

**Interfaces:**
- Consumes: Task 1's `LedgerWorkbook`.
- Produces: `[Column(int index, string header)]` in `MocdDocFix.Domain`, with properties `Index` and `Header`. `LedgerStore` no longer has `CsvPath`, `WarnAboutCsv` or `LastCsvProblem`.

- [ ] **Step 1: Write the failing test**

Add to `tests/MocdDocFix.Tests/LedgerStoreTests.cs`:

```csharp
[Fact]
public void No_csv_is_written_and_an_old_one_is_removed()
{
    var path = Path.Combine(_dir, "repair-dev.xlsx");
    var csv = Path.Combine(_dir, "repair-dev.csv");
    File.WriteAllText(csv, "row,doc id\r\n1,whatever\r\n");

    new LedgerStore(path).Write(new[] { Row() });

    Assert.True(File.Exists(path));
    Assert.False(File.Exists(csv));
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~No_csv_is_written"
```

Expected: FAIL — the CSV exists, freshly rewritten by the store.

- [ ] **Step 3: Add the column attribute**

Create `src/MocdDocFix/Domain/ColumnAttribute.cs`:

```csharp
namespace MocdDocFix.Domain;

/// <summary>
/// A ledger column: where it sits and what the operator reads at the top of it.
///
/// This used to be CsvHelper's [Index] and [Name], kept long after the CSV they were written
/// for. The workbook is the only ledger now, so the ordering belongs to us rather than to a
/// serialisation library we no longer reference.
///
/// The header is the operator's name for the column, read in Excel. Renaming one silently
/// orphans that column in every ledger already on disk.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnAttribute : Attribute
{
    public ColumnAttribute(int index, string header)
    {
        Index = index;
        Header = header;
    }

    /// <summary>Position in the sheet, left to right, from zero.</summary>
    public int Index { get; }

    /// <summary>The text in row 1.</summary>
    public string Header { get; }
}
```

- [ ] **Step 4: Move LedgerRow onto it**

In `src/MocdDocFix/Domain/LedgerRow.cs`, delete the first line:

```csharp
using CsvHelper.Configuration.Attributes;
```

Then replace every `[Index(n), Name("header")]` with `[Column(n, "header")]`. All 29 of them, in
place, with no change to the numbers or the header strings:

```csharp
    [Column(0, "row")] public int Row { get; set; }
    [Column(1, "doc id")] public Guid DocId { get; set; }
    [Column(2, "doc name")] public string DocName { get; set; } = string.Empty;
    [Column(3, "doc type name")] public string DocTypeName { get; set; } = string.Empty;
    [Column(4, "doc file name")] public string DocFileName { get; set; } = string.Empty;
    [Column(5, "old file path")] public string OldFilePath { get; set; } = string.Empty;
    [Column(6, "new file path predicted")] public string NewFilePathPredicted { get; set; } = string.Empty;
    [Column(7, "new file path")] public string NewFilePath { get; set; } = string.Empty;
    [Column(8, "service catalogue name")] public string ServiceCatalogueName { get; set; } = string.Empty;
    [Column(9, "old category")] public string OldCategory { get; set; } = string.Empty;
    [Column(10, "correct service catalogue name")] public string CorrectServiceCatalogueName { get; set; } = string.Empty;
    [Column(11, "verdict")] public string Verdict { get; set; } = string.Empty;
    [Column(12, "final state")] public string FinalState { get; set; } = string.Empty;
    [Column(13, "group")] public int Group { get; set; }
    [Column(14, "way of upload")] public string WayOfUpload { get; set; } = string.Empty;
    [Column(15, "error")] public string Error { get; set; } = string.Empty;
    [Column(16, "backup path")] public string BackupPath { get; set; } = string.Empty;
    [Column(17, "reason of bug")] public string ReasonOfBug { get; set; } = string.Empty;
    [Column(18, "solution")] public string Solution { get; set; } = string.Empty;
    [Column(19, "notes")] public string Notes { get; set; } = string.Empty;
    [Column(20, "crm link of doc")] public string CrmLinkOfDoc { get; set; } = string.Empty;
    [Column(21, "crm link of doc file")] public string CrmLinkOfDocFile { get; set; } = string.Empty;
    [Column(22, "doc file id")] public Guid DocFileId { get; set; }
    [Column(23, "service catalogue id")] public string ServiceCatalogueId { get; set; } = string.Empty;
    [Column(24, "correct service catalogue id")] public string CorrectServiceCatalogueId { get; set; } = string.Empty;
    [Column(25, "old hash")] public string OldHash { get; set; } = string.Empty;
    [Column(26, "old file name")] public string OldFileName { get; set; } = string.Empty;
    [Column(27, "old file id")] public string OldFileId { get; set; } = string.Empty;
    [Column(28, "superseded paths")] public string SupersededPaths { get; set; } = string.Empty;
```

Keep every XML doc comment exactly where it is — only the attribute changes. Also update the
class comment, which names `IndexAttribute`:

```csharp
/// The order is pinned with <see cref="ColumnAttribute"/> rather than left to member order,
```

and the comment on `Verdict2()`, which explains itself in CsvHelper's terms:

```csharp
    /// <summary>
    /// The verdict cell, parsed. A method rather than a property because the column itself is
    /// called Verdict and the two would collide.
    /// </summary>
```

- [ ] **Step 5: Point LedgerColumns at the new attribute**

In `src/MocdDocFix/Domain/LedgerColumns.cs`, delete:

```csharp
using CsvHelper.Configuration.Attributes;
```

and replace `Build()`:

```csharp
    private static IReadOnlyList<LedgerColumn> Build() =>
        typeof(LedgerRow)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Column: p.GetCustomAttribute<ColumnAttribute>()))
            .Where(x => x.Column is not null)
            .OrderBy(x => x.Column!.Index)
            .Select(x => new LedgerColumn(
                x.Column!.Header,
                row => x.Property.GetValue(row)?.ToString() ?? string.Empty,
                (row, text) => x.Property.SetValue(row, Convert(x.Property.PropertyType, text))))
            .ToList();
```

Also correct the class comment, which says "the workbook and the CSV cannot drift":

```csharp
/// Derived by reflection rather than listed again here, so the header the operator reads and the
/// property the tool writes cannot drift apart. Adding a column to LedgerRow adds it to the
/// workbook with no second edit.
```

- [ ] **Step 6: Strip the CSV out of LedgerStore**

In `src/MocdDocFix/Storage/LedgerStore.cs`:

Delete these `using` lines:

```csharp
using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
```

Delete the `Utf8` field, the `CsvPath` property, the `WarnAboutCsv` property, the
`LastCsvProblem` property, and the whole `Config()` method at the bottom of the file.

Replace the class comment with:

```csharp
/// <summary>
/// The ledger on disk: one workbook per environment.
///
/// **The workbook is the ledger.** It is what the operator edits — the verdict and final state
/// columns are dropdowns, which only works if the file carrying them is the file read back —
/// and it is what every mode reads.
///
/// It is rewritten in full the moment a row finishes. Rewriting the whole file for one cell is
/// deliberate: the operator opens it between runs, so it must be complete at every instant, and
/// a crash then loses at most the row in flight.
/// </summary>
```

Change the constructor to take only the workbook, and delete the leftover CSV on the way past:

```csharp
    /// <param name="path">
    /// The .xlsx. A .csv is still accepted and read as its .xlsx sibling, so a caller holding an
    /// old path does not break.
    /// </param>
    public LedgerStore(string path)
    {
        Path = System.IO.Path.ChangeExtension(path, ".xlsx");

        _workbook = new LedgerWorkbook(Path);
    }
```

In `Write`, delete the entire `try { ... } catch (Exception problem) when (IsLocked(problem)) { ... }`
block that writes the CSV, and put this in its place:

```csharp
        // The plain-text copy this tool used to keep beside the ledger is gone. Nothing read it,
        // and a stale one left behind looks current while showing fewer corrections than really
        // happened — so it is removed rather than left to mislead.
        var abandonedCopy = System.IO.Path.ChangeExtension(Path, ".csv");
        try
        {
            if (File.Exists(abandonedCopy)) File.Delete(abandonedCopy);
        }
        catch (IOException) { }                  // it is tidying; never worth failing a run over
        catch (UnauthorizedAccessException) { }
```

- [ ] **Step 7: Unwire it from Session**

In `src/MocdDocFix/Cli/Session.cs`, delete the whole `_ledger.WarnAboutCsv = why => { ... };`
assignment from the constructor.

In the `RepairAsync` summary, replace:

```csharp
            var details = new List<string>
            {
                $"ledger → {_ledger.Path}   (edit this one)",
                $"copy   → {_ledger.CsvPath}   (plain text, regenerated)"
            };

            if (_ledger.LastCsvProblem is { } stale) details.Add($"NOTE: {stale}");
```

with:

```csharp
            var details = new List<string> { $"ledger → {_ledger.Path}" };
```

- [ ] **Step 8: Drop the package**

In `src/MocdDocFix/MocdDocFix.csproj`, delete the line:

```xml
    <PackageReference Include="CsvHelper" Version="33.1.0" />
```

- [ ] **Step 9: Build and find every remaining reference**

```bash
cd /d/mocd-docfix && dotnet build 2>&1 | grep -E "error|Warning" | head -30
```

Expected: errors naming any file still using CsvHelper. Fix each by the same pattern — the
likely ones are `src/MocdDocFix/Commands/RedoRun.cs` and `src/MocdDocFix/Cli/Wizard.cs`, which
only mention the CSV in comments and in menu wording. In `Wizard.cs`, the Repair run choice says
"writes one CSV — the ledger"; change it to "writes one workbook — the ledger". In
`DocumentGroups.cs` the class comment says "the grouped report and the CSV all read their
wording from here"; change "the CSV" to "the ledger".

Then confirm nothing is left:

```bash
cd /d/mocd-docfix && grep -rn "CsvHelper\|CsvPath\|LastCsvProblem\|WarnAboutCsv" src tests --include=*.cs
```

Expected: no output.

- [ ] **Step 10: Run the full suite**

```bash
cd /d/mocd-docfix && dotnet test
```

Expected: PASS. Tests asserting on `CsvPath` must be deleted, not weakened — the property is
gone because the file is gone.

- [ ] **Step 11: Commit**

```bash
cd /d/mocd-docfix
git add -A
git commit -m "refactor(ledger): the workbook is the only ledger

Nothing ever read the CSV copy. It cost a second write on every completed row, a
lock warning of its own, and a dependency on CsvHelper kept solely for the two
attributes that ordered the columns — which are now a nine-line attribute of our
own. An abandoned .csv beside the ledger is deleted, because a stale one looks
current while showing fewer corrections than really happened."
```

---

## Task 3: Three tabs, one per mode

Spec §5.

**Files:**
- Create: `src/MocdDocFix/Domain/LedgerTabs.cs`
- Modify: `src/MocdDocFix/Storage/LedgerWorkbook.cs`, `src/MocdDocFix/Domain/LedgerOrder.cs`, `src/MocdDocFix/Domain/LedgerRow.cs`
- Test: `tests/MocdDocFix.Tests/LedgerWorkbookTests.cs`, `tests/MocdDocFix.Tests/LedgerOrderTests.cs`

**Interfaces:**
- Consumes: Task 2's `LedgerColumns`.
- Produces: `LedgerTabs.Of(LedgerRow) → LedgerTab` and `enum LedgerTab { Ledger, Corrected, Finished }`, plus `LedgerTabs.Name(LedgerTab) → string`. `LedgerWorkbook.Read()` still returns one flat list of every row from every tab.

- [ ] **Step 1: Write the failing test**

Add to `tests/MocdDocFix.Tests/LedgerWorkbookTests.cs`:

```csharp
/// <summary>
/// One tab per mode: what is still to fix, what is waiting to be deleted, and what is over.
/// Reading gives them all back as one list, because nothing above the workbook knows about tabs.
/// </summary>
[Fact]
public void Rows_are_split_across_three_tabs_and_read_back_as_one_list()
{
    var path = Path.Combine(_dir, "repair-dev.xlsx");

    var toFix = Row(RowVerdicts.Fix, "Board Decision");
    var awaitingDelete = Row(RowVerdicts.Done, "Passport");
    awaitingDelete.FinalState = RowStates.Corrected;
    var over = Row(RowVerdicts.Done, "Licence");
    over.FinalState = RowStates.Deleted;

    var book = new LedgerWorkbook(path);
    book.Write(new[] { toFix, awaitingDelete, over });

    using (var raw = new ClosedXML.Excel.XLWorkbook(path))
    {
        Assert.Equal(2, raw.Worksheet("ledger").RangeUsed()!.RowCount());      // header + 1
        Assert.Equal(2, raw.Worksheet("corrected").RangeUsed()!.RowCount());
        Assert.Equal(2, raw.Worksheet("finished").RangeUsed()!.RowCount());
    }

    Assert.Equal(3, book.Read().Count);
}

[Fact]
public void A_done_row_with_no_final_state_is_finished()
{
    var path = Path.Combine(_dir, "repair-dev.xlsx");

    var settled = Row(RowVerdicts.Done, "Board Decision");
    settled.FinalState = string.Empty;

    new LedgerWorkbook(path).Write(new[] { settled });

    using var raw = new ClosedXML.Excel.XLWorkbook(path);
    Assert.Equal(2, raw.Worksheet("finished").RangeUsed()!.RowCount());
    Assert.Null(raw.Worksheet("ledger").RangeUsed());
}
```

- [ ] **Step 2: Run them and watch them fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~Rows_are_split_across_three_tabs|FullyQualifiedName~A_done_row_with_no_final_state"
```

Expected: FAIL — `System.ArgumentException: There isn't a worksheet named 'corrected'`.

- [ ] **Step 3: Write the tab rule**

Create `src/MocdDocFix/Domain/LedgerTabs.cs`:

```csharp
namespace MocdDocFix.Domain;

/// <summary>Which sheet of the workbook a row is shown on.</summary>
public enum LedgerTab
{
    /// <summary>Documents still to fix. What the repair run reads.</summary>
    Ledger,

    /// <summary>Corrected, old file still on the server. What the delete step reads.</summary>
    Corrected,

    /// <summary>Nothing left to do. What "Check it all" verifies.</summary>
    Finished
}

/// <summary>
/// The one rule saying where a row is shown.
///
/// One tab per mode, so each has a single place to look instead of a filter over one long
/// sheet. It is presentation only: an .xlsx is a single zip archive and a tab is a folder
/// inside it, so writing one tab rebuilds the whole file — three tabs cost exactly what one
/// costs, and no mode is restricted to its own tab. <see cref="LedgerWorkbook"/> reads all
/// three back as one list.
/// </summary>
public static class LedgerTabs
{
    public static LedgerTab Of(LedgerRow row) => row.State() switch
    {
        // The old file is still there and the delete has not run. This must never be called
        // finished: a tab named finished holding a file that still needs deleting is how an
        // orphan gets left on the server with nobody counting it.
        RowState.Corrected => LedgerTab.Corrected,

        RowState.Deleted => LedgerTab.Finished,

        // Settled by the pre-run check or by a sibling that shares the document file record:
        // done, with no final state, because there was never a delete of its own to queue.
        RowState.NotStarted when row.Verdict2() == RowVerdict.Done => LedgerTab.Finished,

        _ => LedgerTab.Ledger
    };

    public static string Name(LedgerTab tab) => tab switch
    {
        LedgerTab.Corrected => "corrected",
        LedgerTab.Finished => "finished",
        _ => "ledger"
    };

    /// <summary>In workbook order, left to right.</summary>
    public static readonly IReadOnlyList<LedgerTab> All =
        new[] { LedgerTab.Ledger, LedgerTab.Corrected, LedgerTab.Finished };
}
```

- [ ] **Step 4: Teach the workbook to write three tabs**

In `src/MocdDocFix/Storage/LedgerWorkbook.cs`, delete:

```csharp
    private const string SheetName = "ledger";
```

Replace the body of `Write` down to (but not including) the `SaveAs`/`File.Move` lines from
Task 1 with:

```csharp
    public void Write(IReadOnlyList<LedgerRow> rows)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var leftOver = Path + ".tmp";
        if (File.Exists(leftOver)) File.Delete(leftOver);

        using var workbook = new XLWorkbook();

        foreach (var tab in LedgerTabs.All)
        {
            // Numbered inside its own tab, so every sheet reads 1, 2, 3 down the page. Sorting
            // the whole ledger and then splitting it would leave each tab with the gaps its
            // neighbours took.
            var mine = LedgerOrder.Sorted(rows.Where(r => LedgerTabs.Of(r) == tab).ToList());
            Fill(workbook.Worksheets.Add(LedgerTabs.Name(tab)), mine);
        }

        var temporary = Path + ".tmp";

        workbook.SaveAs(temporary);
        File.Move(temporary, Path, overwrite: true);
    }

    /// <summary>One sheet: the header, the rows, the dropdowns and the widths.</summary>
    private static void Fill(IXLWorksheet sheet, IReadOnlyList<LedgerRow> rows)
    {
        var columns = LedgerColumns.All;

        for (var c = 0; c < columns.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = columns[c].Header;
            for (var r = 0; r < rows.Count; r++)
                sheet.Cell(r + 2, c + 1).Value = columns[c].Read(rows[r]);
        }

        var header = sheet.Row(1);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromArgb(0xEE, 0xEE, 0xEE);

        sheet.SheetView.FreezeRows(1);
        sheet.RangeUsed()?.SetAutoFilter();

        Validate(sheet, ColumnOf(columns, "verdict"), rows.Count, RowVerdicts.All);
        Validate(sheet, ColumnOf(columns, "final state"), rows.Count, RowStates.All);

        SetWidths(sheet);

        sheet.Cell(1, 1).CreateComment().AddText(
            "This is the ledger. Edit verdict and final state here, save, and close it before " +
            "running docfix — the tool rewrites this file after every document. A row moves " +
            "between the three tabs on its own as its final state changes.");
    }
```

- [ ] **Step 5: Teach it to read three tabs**

Replace `Read()` in the same file:

```csharp
    /// <summary>
    /// Every row from every tab, as one list, in workbook order. Columns are found by header,
    /// not by position, so a workbook whose columns have been dragged around still reads — and
    /// one missing a column reads the rest rather than failing.
    ///
    /// A ledger written by an earlier build has one sheet with some other name. Reading every
    /// worksheet rather than the three we expect means those still open.
    /// </summary>
    public IReadOnlyList<LedgerRow> Read()
    {
        if (!Exists) return Array.Empty<LedgerRow>();

        using var workbook = new XLWorkbook(Path);

        var rows = new List<LedgerRow>();
        foreach (var sheet in workbook.Worksheets) rows.AddRange(ReadSheet(sheet));

        return rows;
    }

    private static IReadOnlyList<LedgerRow> ReadSheet(IXLWorksheet sheet)
    {
        var used = sheet.RangeUsed();
        if (used is null || used.RowCount() < 2) return Array.Empty<LedgerRow>();

        var byHeader = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 1; c <= used.ColumnCount(); c++)
        {
            var header = sheet.Cell(1, c).GetString().Trim();
            if (header.Length > 0) byHeader[header] = c;
        }

        var rows = new List<LedgerRow>(used.RowCount() - 1);

        for (var r = 2; r <= used.RowCount(); r++)
        {
            var row = new LedgerRow();
            var anything = false;

            foreach (var column in LedgerColumns.All)
            {
                if (!byHeader.TryGetValue(column.Header, out var c)) continue;

                var text = sheet.Cell(r, c).GetString().Trim();
                column.Set(row, text);
                if (text.Length > 0) anything = true;
            }

            // A blank line left behind by Excel is not a document.
            if (anything && row.DocId != Guid.Empty) rows.Add(row);
        }

        return rows;
    }
```

- [ ] **Step 6: Run the tab tests**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~Rows_are_split_across_three_tabs|FullyQualifiedName~A_done_row_with_no_final_state"
```

Expected: PASS.

- [ ] **Step 7: Make LedgerOrder read the same predicate**

In `src/MocdDocFix/Domain/LedgerOrder.cs`, replace `Finished` so the sort and the partition can
never disagree:

```csharp
    /// <summary>
    /// Whether the work on this row is over, whatever its verdict happens to say.
    ///
    /// The same predicate that decides which tab a row is written to — deliberately, because two
    /// rules meaning "finished" would drift apart, and the one that drifted would put a
    /// corrected document back among the outstanding work.
    /// </summary>
    private static bool Finished(LedgerRow row) => LedgerTabs.Of(row) != LedgerTab.Ledger;
```

- [ ] **Step 8: Name the tab in a row's reference**

In `src/MocdDocFix/Domain/LedgerRow.cs`, replace `Ref()`:

```csharp
    public string Ref()
    {
        var tab = LedgerTabs.Of(this);
        var where = tab == LedgerTab.Ledger ? string.Empty : $" on the {LedgerTabs.Name(tab)} tab";

        return $"row {Row}{where} · doc {DocId.ToString()[..8]}" +
               (DocFileName.Length == 0 ? string.Empty : $" ({DocFileName})");
    }
```

- [ ] **Step 9: Run the full suite and fix the fallout**

```bash
cd /d/mocd-docfix && dotnet test
```

Expected: some `LedgerOrderTests` and message-text assertions fail, because rows that used to be
numbered across the whole ledger are now numbered per tab and `Ref()` names a tab. Update those
assertions to the new numbering — do not change the production rule to satisfy an old test.

- [ ] **Step 10: Commit**

```bash
cd /d/mocd-docfix
git add -A
git commit -m "feat(ledger): three tabs, one per mode

Still to fix, waiting to be deleted, and over. Each mode gets one place to look
instead of a filter over one long sheet. A pending delete is never called
finished — a tab named finished holding a file that still needs deleting is how
an orphan gets left on the server.

Presentation only: an .xlsx is one zip archive, so three tabs cost what one
costs, and Read still hands back every row as a single list."
```

---

## Task 4: `skip` leaves the vocabulary, and two new groups

Spec §3 and §4. Note the classifier has **three** group-7 arms, and two of them are not "already
correct" — they are problems that would silently vanish once correct rows stop being written.

**Files:**
- Modify: `src/MocdDocFix/Domain/RowVerdict.cs`, `src/MocdDocFix/Domain/LedgerOrder.cs`, `src/MocdDocFix/Domain/Classifier.cs`, `src/MocdDocFix/Domain/DocumentGroups.cs`, `src/MocdDocFix/Commands/LedgerBuilder.cs`, `src/MocdDocFix/Commands/AlreadyCorrect.cs`, `src/MocdDocFix/Commands/RepairOneRow.cs`
- Test: `tests/MocdDocFix.Tests/LedgerRowTests.cs`, `tests/MocdDocFix.Tests/ClassifierTests.cs`, `tests/MocdDocFix.Tests/DocumentGroupsTests.cs`, `tests/MocdDocFix.Tests/AlreadyCorrectTests.cs`, `tests/MocdDocFix.Tests/RepairOneRowTests.cs`

**Interfaces:**
- Consumes: Task 3's `LedgerTabs`.
- Produces: `RowVerdict` without `Skip`; `RowVerdicts.Parse("skip") == RowVerdict.Done`; groups 8 and 9 in `DocumentGroups.All`. **After this task the only `Verdict.Skip` the classifier can return is "the path is already the correct catalogue"** — which is what Task 5 keys on.

- [ ] **Step 1: Write the failing tests**

In `tests/MocdDocFix.Tests/LedgerRowTests.cs`, change the existing inline case:

```csharp
    [InlineData("skip", RowVerdict.Skip)]
```

to:

```csharp
    // Legacy. Sheets written before skip left the vocabulary have the word in hundreds of
    // cells; if it stopped parsing, every one would be reported as an unrecognised typo at the
    // end of every run.
    [InlineData("skip", RowVerdict.Done)]
```

Add to `tests/MocdDocFix.Tests/ClassifierTests.cs`:

```csharp
[Fact]
public void A_record_with_no_file_path_is_group_eight_and_wants_a_human()
{
    var r = Classifier.Classify(
        FilePathParser.Parse(string.Empty), Guid.NewGuid(), null, _ => true);

    Assert.Equal(Verdict.Review, r.Verdict);
    Assert.Equal(8, r.Group);
}

[Fact]
public void A_document_type_with_no_catalogue_is_group_nine_and_wants_a_human()
{
    var r = Classifier.Classify(
        FilePathParser.Parse(@"DigitalServices\boardDecision\20250509\a.pdf"),
        docTypeCatalogue: null, crossCheckCatalogue: null, _ => true);

    Assert.Equal(Verdict.Review, r.Verdict);
    Assert.Equal(9, r.Group);
}
```

- [ ] **Step 2: Run them and watch them fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~ClassifierTests|FullyQualifiedName~LedgerRowTests"
```

Expected: FAIL — `Assert.Equal() Failure: Expected: Review, Actual: Skip` and `Expected: Done,
Actual: Skip`.

- [ ] **Step 3: Take `skip` out of the vocabulary**

In `src/MocdDocFix/Domain/RowVerdict.cs`:

```csharp
public enum RowVerdict { Fix, Review, Ignore, Redo, Done, Unrecognised }
```

Delete the `Skip` constant and take it out of `All`:

```csharp
    /// <summary>Every value the column may hold, for the workbook's dropdown.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Fix, Review, Redo, Ignore, Done };
```

In `Parse`, replace the `Skip => RowVerdict.Skip,` line with:

```csharp
        // Legacy. The word was written by every build before correct documents stopped entering
        // the sheet, and it always meant "we looked, there was nothing to do" — which is what
        // done means now. It is readable so old ledgers open cleanly; nothing writes it again.
        "skip" => RowVerdict.Done,
```

and delete `RowVerdict.Skip => Skip,` from `Text`.

In `src/MocdDocFix/Domain/LedgerOrder.cs`, delete `RowVerdict.Skip => 1,` from `Rank`.

- [ ] **Step 4: Move the two group-7 arms that are not "already correct"**

In `src/MocdDocFix/Domain/Classifier.cs`, replace the first arm:

```csharp
        if (path.SegmentCount == 0)
            return new Classification(Verdict.Skip, 7,
                "No file path on the document file record.",
                "Nothing to do — this is a legacy record with no file on the vendor server.",
                null, null);
```

with:

```csharp
        // Group 8, not 7. A record naming no file is not a document that is fine; it is a
        // document with nothing behind it. Once correct documents stop being written to the
        // sheet, leaving this as Skip would delete it from view without anyone deciding to.
        if (path.SegmentCount == 0)
            return new Classification(Verdict.Review, 8,
                "The document file record has no file path at all.",
                "Nothing this tool can do — there is no path to diagnose and no file to move. " +
                "Someone has to decide what became of it.",
                null, null);
```

and the second:

```csharp
        if (docTypeCatalogue is null)
            return new Classification(Verdict.Skip, 7,
                "The document type has no service catalogue, so there is no correct value to write.",
                "Cannot be fixed by this tool. Set mocd_servicecatalogue on the document type " +
                "in CRM first, then re-scan.",
                null, path.CategorySegment);
```

with:

```csharp
        // Group 9, for the same reason. The file may be perfectly well filed or badly filed —
        // there is no way to tell, because the record that would say has no catalogue on it.
        // That is a thing to be fixed in CRM, not a thing to be hidden.
        if (docTypeCatalogue is null)
            return new Classification(Verdict.Review, 9,
                "The document type has no service catalogue, so there is no correct value to write.",
                "Cannot be fixed by this tool. Set mocd_servicecatalogue on the document type " +
                "in CRM first, then re-scan.",
                null, path.CategorySegment);
```

Leave the third arm — the one comparing the segment to the document type's catalogue — returning
`Verdict.Skip` and group 7. It is now the only one.

- [ ] **Step 5: Write the two groups**

In `src/MocdDocFix/Domain/DocumentGroups.cs`, append to the `All` array, after group 7:

```csharp
        new DocumentGroup(8,
            "the record names no file at all",
            "nothing — the mocd_documentfile record has an empty mocd_filepath.",
            "there is no file to move and nothing in CRM says where it went. The document " +
            "exists, its file record exists, and between them they name no file.",
            "the record's own mocd_filepath is blank.",
            "nothing. It is listed so a person can judge — there is no path to diagnose and " +
            "nothing to re-upload.",
            false),

        new DocumentGroup(9,
            "the document type has no service catalogue",
            "whatever it happens to hold — there is no correct value to compare it against.",
            "the correct catalogue is read off the document's own document type, and that " +
            "field is empty. The path may be right or wrong and there is no way to tell.",
            "nothing does. The record that would say has nothing on it.",
            "nothing. Set mocd_servicecatalogue on the document type in CRM and re-scan, and " +
            "these move into a group that can be judged.",
            false),
```

Update the `Get` doc comment on `DocumentGroup.Number`:

```csharp
/// <param name="Number">1-9. Groups 1-5 are fixed; 6 to 9 are not.</param>
```

- [ ] **Step 6: Make the two settlements write `done`**

In `src/MocdDocFix/Commands/AlreadyCorrect.cs`, in `Apply`:

```csharp
            if (how == SettleAs.AlwaysRight)
            {
                row.Verdict = RowVerdicts.Done;
                row.Notes = Note(row.Notes,
                    $"checked {Now()} — CRM already files this under the right catalogue and the " +
                    "path has not changed, so there is nothing to correct and nothing to delete");
                continue;
            }
```

In `src/MocdDocFix/Commands/RepairOneRow.cs`, in the `wasAlwaysRight` branch:

```csharp
            row.Verdict = RowVerdicts.Done;
```

In `src/MocdDocFix/Commands/LedgerBuilder.cs`, `VerdictFor` can no longer name the constant:

```csharp
    /// <summary>
    /// The scan's verdict as a cell. Verdict.Skip has no spelling in the sheet any more —
    /// LedgerMerge declines to write those rows at all — so it maps to nothing and the string
    /// is never read.
    /// </summary>
    private static string VerdictFor(Verdict verdict) => verdict switch
    {
        Verdict.Fix => RowVerdicts.Fix,
        Verdict.Review => RowVerdicts.Review,
        _ => string.Empty
    };
```

- [ ] **Step 7: Run the full suite and fix the fallout**

```bash
cd /d/mocd-docfix && dotnet test
```

Expected: several failures in `AlreadyCorrectTests`, `LedgerBuilderTests`, `LedgerMergeTests`,
`LedgerOrderTests`, `DeleteOldFilesTests`, `RedoRunTests`, `RepairRunTests` and
`RepairOneRowTests` — everywhere that names `RowVerdicts.Skip` or `RowVerdict.Skip`. Replace each
with `Done`, except in `DeleteOldFilesTests`, `RedoRunTests` and `RepairRunTests` where the
`[InlineData(RowVerdicts.Skip)]` case exists to prove a non-acting verdict is walked past — use
`RowVerdicts.Review` there instead, which is still a verdict those modes must ignore.

In `AlreadyCorrectTests.A_row_whose_path_never_changed_becomes_a_skip`, rename it to
`A_row_whose_path_never_changed_is_done` and assert `RowVerdict.Done`.

- [ ] **Step 8: Confirm nothing writes the word**

```bash
cd /d/mocd-docfix && grep -rn "RowVerdicts.Skip\|RowVerdict.Skip" src tests --include=*.cs
```

Expected: no output.

- [ ] **Step 9: Commit**

```bash
cd /d/mocd-docfix
git add -A
git commit -m "feat(ledger): skip leaves the vocabulary, and two problems stop hiding in group 7

Group 7 held three different things: a document that is filed correctly, a record
naming no file at all, and a document type with no service catalogue. Only the
first is 'nothing to do'. The other two become groups 8 and 9 with verdict
review, so that when correct documents stop entering the sheet they are not
swept out with them.

skip itself is gone from the dropdown but still parses, as done, so the hundreds
of cells already carrying it in existing ledgers do not all read as typos."
```

---

## Task 5: Correct documents stop entering the sheet

Spec §2, and §4's count. After Task 4 the only `Verdict.Skip` left means "already the correct
catalogue", so the rule is exactly one line in the merge.

**Files:**
- Create: `src/MocdDocFix/Commands/LegacyCleanup.cs`
- Modify: `src/MocdDocFix/Commands/LedgerMerge.cs`, `src/MocdDocFix/Cli/Session.cs`
- Test: `tests/MocdDocFix.Tests/LedgerMergeTests.cs`, new `tests/MocdDocFix.Tests/LegacyCleanupTests.cs`

**Interfaces:**
- Consumes: Task 4's classifier verdicts, Task 3's `LedgerTabs`.
- Produces: `Merged` gains `int NotAdded`; `LegacyCleanup.Apply(IReadOnlyList<LedgerRow> existing, IReadOnlyList<LedgerRow> scanned) → CleanupResult` where `CleanupResult` is `(IReadOnlyList<LedgerRow> Rows, int Removed, int Kept)`.

- [ ] **Step 1: Write the failing merge test**

Add to `tests/MocdDocFix.Tests/LedgerMergeTests.cs`:

```csharp
/// <summary>
/// A document nothing is wrong with is not written. The sheet is the work, not the census —
/// 96 of the dev ledger's 410 rows were this, and every one of them was noise.
/// </summary>
[Fact]
public void A_new_document_that_is_already_correct_is_not_added()
{
    var merged = LedgerMerge.Into(
        Array.Empty<LedgerRow>(),
        new[] { Row(One, verdict: string.Empty), Row(Two, verdict: RowVerdicts.Fix) });

    Assert.Single(merged.Rows);
    Assert.Equal(Two, merged.Rows[0].DocId);
    Assert.Equal(1, merged.NotAdded);
}

/// <summary>
/// The filter is on adding, never on matching. A row already in the sheet that has since been
/// corrected outside this tool still comes back from the scan, so it must not look vanished.
/// </summary>
[Fact]
public void A_row_that_has_become_correct_is_not_reported_as_gone()
{
    var existing = Row(One, verdict: RowVerdicts.Fix);

    var merged = LedgerMerge.Into(new[] { existing }, new[] { Row(One, verdict: string.Empty) });

    Assert.Empty(merged.Gone);
    Assert.Single(merged.Rows);
}
```

- [ ] **Step 2: Run them and watch them fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~A_new_document_that_is_already_correct|FullyQualifiedName~A_row_that_has_become_correct"
```

Expected: FAIL — `Assert.Single() Failure: The collection contained 2 items`, and `NotAdded` does
not compile.

- [ ] **Step 3: Add the rule to the merge**

In `src/MocdDocFix/Commands/LedgerMerge.cs`, add `int NotAdded` to the `Merged` record:

```csharp
/// <param name="NotAdded">
/// Documents the scan found nothing wrong with, which were therefore never written. Counted
/// rather than listed: they are the majority of any environment and there is nothing to say
/// about any one of them.
/// </param>
public sealed record Merged(
    IReadOnlyList<LedgerRow> Rows, int Added, int Refreshed, int Protected, int Vanished,
    IReadOnlyList<string> Notes, IReadOnlyList<VerdictDisagreement> Disagreements,
    IReadOnlyList<VerdictDisagreement> Excluded, IReadOnlyList<PathMoved> Moved,
    IReadOnlyList<VerdictDisagreement> StillWrong, IReadOnlyList<LedgerRow> Gone,
    int NotAdded = 0);
```

Inside `Into`, at the point where an unmatched scanned row is added to the list — after the
existing-row branch has `continue`d — put the filter:

```csharp
            // Nothing is wrong with it, so it gets no row. The scan still classified it and it
            // was still matched against the sheet above, which is what keeps a row that has
            // since been corrected from looking as though CRM had lost it.
            if (scanned.Verdict.Length == 0)
            {
                notAdded++;
                continue;
            }
```

Declare `var notAdded = 0;` beside the other counters and pass it into the returned `Merged`.

- [ ] **Step 4: Run them and watch them pass**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~A_new_document_that_is_already_correct|FullyQualifiedName~A_row_that_has_become_correct"
```

Expected: PASS.

- [ ] **Step 5: Write the failing cleanup tests**

Create `tests/MocdDocFix.Tests/LegacyCleanupTests.cs`:

```csharp
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The one-time tidy of a sheet written before correct documents stopped entering it. Getting
/// this wrong throws away a record of work that was really done, so every branch is pinned.
/// </summary>
public class LegacyCleanupTests
{
    private static readonly Guid Doc = Guid.Parse("11111111-0000-0000-0000-000000000001");

    private static LedgerRow Row(string verdict, string finalState = "", string notes = "") => new()
    {
        Row = 1,
        DocId = Doc,
        DocFileName = "a.pdf",
        Verdict = verdict,
        FinalState = finalState,
        Notes = notes
    };

    private static LedgerRow Correct() => new() { DocId = Doc, Verdict = string.Empty };

    [Fact]
    public void A_correct_row_with_no_final_state_is_removed()
    {
        var result = LegacyCleanup.Apply(new[] { Row("skip") }, new[] { Correct() });

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void A_row_awaiting_its_delete_is_kept_and_reads_done()
    {
        var row = Row("skip", RowStates.Corrected);

        var result = LegacyCleanup.Apply(new[] { row }, new[] { Correct() });

        Assert.Single(result.Rows);
        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(LedgerTab.Corrected, LedgerTabs.Of(row));
    }

    [Fact]
    public void A_row_whose_old_file_has_gone_is_kept_and_is_finished()
    {
        var row = Row("skip", RowStates.Deleted);

        LegacyCleanup.Apply(new[] { row }, new[] { Correct() });

        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(LedgerTab.Finished, LedgerTabs.Of(row));
    }

    [Fact]
    public void A_failed_row_is_kept_for_a_human()
    {
        var row = Row("skip", RowStates.Failed);

        LegacyCleanup.Apply(new[] { row }, new[] { Correct() });

        Assert.Equal(RowVerdict.Review, row.Verdict2());
    }

    [Fact]
    public void A_row_closed_by_hand_stays_closed()
    {
        var row = Row("skip", RowStates.Ignore);

        LegacyCleanup.Apply(new[] { row }, new[] { Correct() });

        Assert.Equal(RowVerdict.Ignore, row.Verdict2());
    }

    /// <summary>
    /// The test is the scan's own classification, not the cell. Somebody who typed fix on a row
    /// has said they want it, whatever CRM thinks of it.
    /// </summary>
    [Fact]
    public void A_row_the_scan_still_calls_broken_is_never_removed()
    {
        var result = LegacyCleanup.Apply(
            new[] { Row("skip") },
            new[] { new LedgerRow { DocId = Doc, Verdict = RowVerdicts.Fix } });

        Assert.Single(result.Rows);
    }

    [Fact]
    public void A_row_with_notes_on_it_is_never_removed()
    {
        var result = LegacyCleanup.Apply(
            new[] { Row("skip", notes: "checked 2026-09-16 — already right") },
            new[] { Correct() });

        Assert.Single(result.Rows);
    }
}
```

- [ ] **Step 6: Run them and watch them fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~LegacyCleanupTests"
```

Expected: FAIL — `LegacyCleanup` does not exist.

- [ ] **Step 7: Write the cleanup**

Create `src/MocdDocFix/Commands/LegacyCleanup.cs`:

```csharp
using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <param name="Removed">Rows taken out of the sheet because there was never anything to do.</param>
/// <param name="Kept">Rows that said skip but carried a record of work, and were rewritten.</param>
public sealed record CleanupResult(IReadOnlyList<LedgerRow> Rows, int Removed, int Kept);

/// <summary>
/// Puts a sheet written by an earlier build in order, once.
///
/// Correct documents no longer enter the ledger, but a sheet already on disk is full of them —
/// 96 of the dev ledger's 410 rows. They cannot be un-written by a scan that simply stops
/// producing them, so they are cleared out here.
///
/// **A row with a final state is never deleted.** The final state is the record that something
/// was really done to that document: a file uploaded, a record overwritten, an old file removed.
/// The verdict is the column a hand can change in a second; the final state is not. So the
/// verdict is rewritten to agree with the final state, and the row stays.
///
/// It runs before the legacy reading of "skip" as "done" means anything, which is why it looks
/// at the raw cell rather than at <see cref="LedgerRow.Verdict2"/>: read as done first, every
/// untouched row would land on the finished tab and never be cleared at all.
/// </summary>
public static class LegacyCleanup
{
    private const string LegacySkip = "skip";

    public static CleanupResult Apply(
        IReadOnlyList<LedgerRow> existing, IReadOnlyList<LedgerRow> scanned)
    {
        var stillBroken = scanned
            .Where(s => s.Verdict.Length > 0)
            .Select(s => s.DocId)
            .ToHashSet();

        var kept = new List<LedgerRow>(existing.Count);
        int removed = 0, rewritten = 0;

        foreach (var row in existing)
        {
            if (!row.Verdict.Trim().Equals(LegacySkip, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(row);
                continue;
            }

            // Anything the scan still has an opinion about, anything somebody wrote a note on,
            // and anything with a record of work stays. Only a row that is correct, untouched
            // and unremarked-upon goes.
            if (row.State() == RowState.NotStarted &&
                row.Notes.Length == 0 &&
                !stillBroken.Contains(row.DocId))
            {
                removed++;
                continue;
            }

            row.Verdict = VerdictFor(row.State());
            rewritten++;
            kept.Add(row);
        }

        return new CleanupResult(kept, removed, rewritten);
    }

    /// <summary>
    /// What the verdict should have said, given what really happened to the row. The final
    /// state is the authority here, not the word somebody typed over the top of it.
    /// </summary>
    private static string VerdictFor(RowState state) => state switch
    {
        RowState.Corrected or RowState.Deleted => RowVerdicts.Done,

        // It failed. That is not finished and it is not nothing — somebody has to look.
        RowState.Failed => RowVerdicts.Review,

        RowState.Ignore => RowVerdicts.Ignore,

        // Untouched, but the scan still wants it or a note was left on it. Review, because the
        // one thing that is certainly untrue now is that there is nothing to do.
        _ => RowVerdicts.Review
    };
}
```

- [ ] **Step 8: Run them and watch them pass**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~LegacyCleanupTests"
```

Expected: PASS, 7 passed.

- [ ] **Step 9: Wire it into the session**

In `src/MocdDocFix/Cli/Session.cs`, in `OpenLedgerAsync`, between `var scanned = await
_builder.BuildAsync(ct);` and the `if (existing.Count == 0)` branch:

```csharp
        // Once, on the first run after correct documents stopped entering the sheet. After that
        // there is nothing left saying skip and this does nothing at all.
        var tidied = LegacyCleanup.Apply(existing, scanned);
        if (tidied.Removed > 0 || tidied.Kept > 0)
        {
            existing = tidied.Rows;

            _prompts.Blank();
            if (tidied.Removed > 0)
                _prompts.Say($"{tidied.Removed} row(s) were correct and had never been worked " +
                             "on. They have been taken out of the sheet.", Tone.Good);
            if (tidied.Kept > 0)
                _prompts.Say($"{tidied.Kept} row(s) said skip but had work recorded against " +
                             "them. Their verdicts now say what actually happened.", Tone.Muted);
        }
```

`existing` is currently declared with `var` and typed `IReadOnlyList<LedgerRow>`; `tidied.Rows`
is the same type, so no declaration change is needed.

Then, after the merge, report the two counts. Replace:

```csharp
        _prompts.Say($"{merged.Rows.Count} row(s): {merged.Added} new since last time, " +
                     $"{merged.Refreshed} refreshed, {merged.Protected} left as they are because " +
                     "they have already been worked on or you closed them.");
```

with:

```csharp
        _prompts.Say($"{merged.Rows.Count} row(s): {merged.Added} new since last time, " +
                     $"{merged.Refreshed} refreshed, {merged.Protected} left as they are because " +
                     "they have already been worked on or you closed them.");

        if (merged.NotAdded > 0)
            _prompts.Say($"{merged.NotAdded} document(s) were already filed correctly and are " +
                         "not in the sheet.", Tone.Muted);

        SayHowManyHaveNoFile(merged.Rows);
```

And add the method beside `SayHowManyFiles`:

```csharp
    /// <summary>
    /// How many rows are documents with no file at all.
    ///
    /// In the eight services this is 29 rows, worth reading one by one. Across every catalogue
    /// in pre-prod it is roughly thirty-seven thousand, because two thirds of that environment
    /// is documents that name no file — and thirty-seven thousand rows do not read as a list,
    /// they read as wallpaper. So the number is said out loud and the rows are left to sit.
    /// </summary>
    private void SayHowManyHaveNoFile(IReadOnlyList<LedgerRow> rows)
    {
        var none = rows.Count(r => r.Group == 8);
        if (none == 0) return;

        _prompts.Say($"{none} document(s) have no file path at all. They are in the sheet as " +
                     "review. There is nothing this tool can do with them — no path to " +
                     "diagnose and no file to move.", Tone.Warn);
    }
```

- [ ] **Step 10: Run the full suite**

```bash
cd /d/mocd-docfix && dotnet test
```

Expected: PASS. Any `LedgerMergeTests` constructing `Merged` positionally needs the new
parameter; it has a default of 0, so most will not.

- [ ] **Step 11: Commit**

```bash
cd /d/mocd-docfix
git add -A
git commit -m "feat(ledger): a document nothing is wrong with gets no row

The sheet is the work, not the census. 96 of the dev ledger's 410 rows were
documents in perfect order, and every one of them was noise.

The filter is on adding, never on matching: a row already in the sheet that has
since been corrected elsewhere still comes back from the scan, so it cannot look
as though CRM had lost it. A sheet written by an earlier build is tidied once,
and a row carrying a final state is never deleted — that is the record that work
was really done, whatever a hand later typed over the verdict."
```

---

## Task 6: The verdict hand-edited onto a finished row

Spec §8. Delete old files reads the final state, not the verdict, so typing `ignore` on a
finished row does nothing at all.

**Files:**
- Create: `src/MocdDocFix/Commands/VerdictGuard.cs`
- Modify: `src/MocdDocFix/Cli/Session.cs`
- Test: new `tests/MocdDocFix.Tests/VerdictGuardTests.cs`

**Interfaces:**
- Consumes: Task 3's `LedgerTabs`.
- Produces: `VerdictGuard.Find(IReadOnlyList<LedgerRow>) → IReadOnlyList<StrandedRow>` where `StrandedRow` is `(LedgerRow Row, bool DeleteStillPending, bool WouldReRun)`, and `VerdictGuard.Apply(IReadOnlyList<StrandedRow>, GuardChoice)` with `enum GuardChoice { BackToDone, StopTheDelete, LeaveAsTyped }`.

- [ ] **Step 1: Write the failing tests**

Create `tests/MocdDocFix.Tests/VerdictGuardTests.cs`:

```csharp
using MocdDocFix.Commands;
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Delete old files reads the final state column, never the verdict. So typing ignore on a
/// finished row stops nothing, and the old file still goes. This is the guard on that.
/// </summary>
public class VerdictGuardTests
{
    private static LedgerRow Row(string verdict, string finalState) => new()
    {
        Row = 12,
        DocId = Guid.Parse("22222222-0000-0000-0000-000000000001"),
        DocFileName = "a.pdf",
        OldFilePath = @"DigitalServices\boardDecision\20250509\a.pdf",
        Verdict = verdict,
        FinalState = finalState
    };

    [Fact]
    public void Ignore_typed_over_a_pending_delete_is_caught()
    {
        var found = VerdictGuard.Find(new[] { Row(RowVerdicts.Ignore, RowStates.Corrected) });

        var stranded = Assert.Single(found);
        Assert.True(stranded.DeleteStillPending);
        Assert.False(stranded.WouldReRun);
    }

    [Fact]
    public void Fix_typed_over_a_corrected_row_is_caught_as_a_re_run()
    {
        var found = VerdictGuard.Find(new[] { Row(RowVerdicts.Fix, RowStates.Corrected) });

        Assert.True(Assert.Single(found).WouldReRun);
    }

    /// <summary>Redo is the mode for exactly this. Asking about it would be asking twice.</summary>
    [Fact]
    public void Redo_is_never_questioned()
    {
        Assert.Empty(VerdictGuard.Find(new[] { Row(RowVerdicts.Redo, RowStates.Corrected) }));
    }

    [Fact]
    public void A_row_that_is_already_done_is_not_questioned()
    {
        Assert.Empty(VerdictGuard.Find(new[] { Row(RowVerdicts.Done, RowStates.Corrected) }));
    }

    [Fact]
    public void An_untouched_row_is_not_questioned()
    {
        Assert.Empty(VerdictGuard.Find(new[] { Row(RowVerdicts.Ignore, string.Empty) }));
    }

    /// <summary>The file has gone. There is no delete left to stop, only a verdict to correct.</summary>
    [Fact]
    public void A_deleted_row_is_caught_but_has_no_delete_to_stop()
    {
        var found = VerdictGuard.Find(new[] { Row(RowVerdicts.Ignore, RowStates.Deleted) });

        Assert.False(Assert.Single(found).DeleteStillPending);
    }

    [Fact]
    public void Putting_it_back_leaves_the_final_state_alone()
    {
        var row = Row(RowVerdicts.Ignore, RowStates.Corrected);

        VerdictGuard.Apply(VerdictGuard.Find(new[] { row }), GuardChoice.BackToDone);

        Assert.Equal(RowVerdict.Done, row.Verdict2());
        Assert.Equal(RowState.Corrected, row.State());
    }

    [Fact]
    public void Stopping_the_delete_writes_ignore_into_both_columns()
    {
        var row = Row(RowVerdicts.Ignore, RowStates.Corrected);

        VerdictGuard.Apply(VerdictGuard.Find(new[] { row }), GuardChoice.StopTheDelete);

        Assert.Equal(RowVerdict.Ignore, row.Verdict2());
        Assert.Equal(RowState.Ignore, row.State());
        Assert.Contains("will not be deleted", row.Notes);
    }

    [Fact]
    public void Leaving_it_as_typed_changes_nothing_but_says_so()
    {
        var row = Row(RowVerdicts.Ignore, RowStates.Corrected);

        VerdictGuard.Apply(VerdictGuard.Find(new[] { row }), GuardChoice.LeaveAsTyped);

        Assert.Equal(RowVerdict.Ignore, row.Verdict2());
        Assert.Equal(RowState.Corrected, row.State());
        Assert.Contains("will still be deleted", row.Notes);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~VerdictGuardTests"
```

Expected: FAIL — `VerdictGuard` does not exist.

- [ ] **Step 3: Write the guard**

Create `src/MocdDocFix/Commands/VerdictGuard.cs`:

```csharp
using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <param name="DeleteStillPending">
/// The old file is on the server and the delete step has not run. Only then is there anything
/// left to stop.
/// </param>
/// <param name="WouldReRun">
/// The verdict says fix on a row that has already been corrected. Running it again uploads a
/// second copy and orphans the first, which is a different and worse mistake.
/// </param>
public sealed record StrandedRow(LedgerRow Row, bool DeleteStillPending, bool WouldReRun);

public enum GuardChoice
{
    /// <summary>It is done. The final state is the record, and the verdict should agree.</summary>
    BackToDone,

    /// <summary>Close the row properly: ignore in both columns, so the delete never comes.</summary>
    StopTheDelete,

    /// <summary>Exactly as typed. The old file will still be deleted.</summary>
    LeaveAsTyped
}

/// <summary>
/// Catches a verdict typed by hand onto a row that has already been worked on.
///
/// The reason this has to exist: **Delete old files reads the final state column, never the
/// verdict.** Setting a finished row's verdict to ignore therefore stops nothing — the old file
/// is deleted just the same — and everybody expects the opposite. Two columns, one of which
/// looks like a switch and is not.
///
/// The answer is not to make the verdict control the delete as well. Then two columns would
/// control it and neither would be the record of what happened. The answer is to notice, say
/// what will really occur, and offer to write the column that does control it.
///
/// It reads the rows and nothing else — no CRM, no file server, no ledger.
/// </summary>
public static class VerdictGuard
{
    public static IReadOnlyList<StrandedRow> Find(IReadOnlyList<LedgerRow> rows)
    {
        var found = new List<StrandedRow>();

        foreach (var row in rows)
        {
            var state = row.State();
            if (state is not (RowState.Corrected or RowState.Deleted)) continue;

            var verdict = row.Verdict2();

            // Done agrees with the final state, and redo is the mode built for precisely this
            // row. Neither is somebody having misunderstood a column.
            if (verdict is RowVerdict.Done or RowVerdict.Redo) continue;

            found.Add(new StrandedRow(row,
                DeleteStillPending: state == RowState.Corrected,
                WouldReRun: verdict == RowVerdict.Fix));
        }

        return found;
    }

    public static void Apply(IReadOnlyList<StrandedRow> stranded, GuardChoice choice)
    {
        foreach (var (row, pending, _) in stranded)
        {
            switch (choice)
            {
                case GuardChoice.BackToDone:
                    row.Verdict = RowVerdicts.Done;
                    row.Notes = Note(row.Notes,
                        $"{Now()} — the verdict was put back to done; the final state says the " +
                        "work was carried out and that is the record");
                    break;

                case GuardChoice.StopTheDelete when pending:
                    row.Verdict = RowVerdicts.Ignore;
                    row.FinalState = RowStates.Ignore;
                    row.Notes = Note(row.Notes,
                        $"{Now()} — closed by hand: the old file at {row.OldFilePath} will not " +
                        "be deleted");
                    break;

                // Nothing to stop: the file has already gone. Only the verdict is out of step,
                // and the operator has asked to leave it that way.
                case GuardChoice.StopTheDelete:
                case GuardChoice.LeaveAsTyped:
                    row.Notes = Note(row.Notes, pending
                        ? $"{Now()} — left as typed; the old file at {row.OldFilePath} will " +
                          "still be deleted when you run Delete old files"
                        : $"{Now()} — left as typed; its old file was already deleted");
                    break;
            }
        }
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");

    private static string Note(string existing, string addition) =>
        existing.Length == 0 ? addition : $"{existing}; {addition}";
}
```

- [ ] **Step 4: Run them and watch them pass**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~VerdictGuardTests"
```

Expected: PASS, 10 passed.

- [ ] **Step 5: Ask the question in the session**

In `src/MocdDocFix/Cli/Session.cs`, add:

```csharp
    /// <summary>
    /// Puts a hand-edited verdict on a finished row to the operator, once for the lot.
    ///
    /// The thing worth saying out loud is the thing nobody expects: the delete step reads the
    /// final state, so the verdict they just typed will not stop it. Everything else follows
    /// from that.
    /// </summary>
    /// <returns>True when anything was changed, so the caller knows to write the ledger.</returns>
    private bool AskAboutStranded(IReadOnlyList<LedgerRow> rows)
    {
        var stranded = VerdictGuard.Find(rows);
        if (stranded.Count == 0) return false;

        var pending = stranded.Count(s => s.DeleteStillPending);
        var reRuns = stranded.Count(s => s.WouldReRun);

        _prompts.Section($"{stranded.Count} finished row(s) have a verdict typed over them",
            Tone.Warn);
        _prompts.Say("These rows record work that was carried out, and their verdict now says " +
                     "something else.");
        _prompts.Blank();

        foreach (var one in stranded.Take(10))
            _prompts.Bullet($"{one.Row.Ref()}: {one.Row.FinalState} — verdict says " +
                            $"{one.Row.Verdict}", Tone.Muted);

        if (stranded.Count > 10)
            _prompts.Bullet($"… and {stranded.Count - 10} more", Tone.Muted);

        if (pending > 0)
        {
            _prompts.Blank();
            _prompts.Warn($"Delete old files reads the final state column, not the verdict. " +
                          $"{pending} of these still say \"{RowStates.Corrected}\", so their " +
                          "old files WILL be deleted whatever the verdict says.", Tone.Danger);
        }

        if (reRuns > 0)
        {
            _prompts.Blank();
            _prompts.Warn($"{reRuns} of them say fix on a document that has already been " +
                          "corrected. Working those again uploads a second copy and leaves the " +
                          "first with nothing pointing at it.", Tone.Danger);
        }

        var choices = new List<Choice>
        {
            new("Put them back to done", "the final state is the record",
                "The work was carried out. The final state column says so, and it is the one " +
                "written by the run rather than by hand. This makes the verdict agree with it."),
            new("Keep ignore, and stop the delete", $"{pending} row(s) have a delete to stop",
                "Writes ignore into the final state as well, so Delete old files walks past " +
                "them and the old files stay on the server.",
                Enabled: pending > 0,
                DisabledNote: "none of these still have an old file waiting to be deleted."),
            new("Leave them exactly as typed", "and let the old files go",
                "Nothing is changed. The old files of any row still awaiting deletion will be " +
                "deleted the next time you run Delete old files.")
        };

        var answer = new Asker(_prompts).Ask("What do you want done with them?", choices,
            defaultIndex: 0);

        if (answer.Kind != AnswerKind.Chosen) return false;

        var choice = answer.Index switch
        {
            1 => GuardChoice.StopTheDelete,
            2 => GuardChoice.LeaveAsTyped,
            _ => GuardChoice.BackToDone
        };

        if (!Confirm(stranded.Count)) return false;

        VerdictGuard.Apply(stranded, choice);
        return true;
    }
```

Call it in `OpenLedgerAsync`, immediately after `AskAboutStillWrong(merged);`:

```csharp
        AskAboutStranded(merged.Rows);
```

- [ ] **Step 6: Run the full suite**

```bash
cd /d/mocd-docfix && dotnet test
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
cd /d/mocd-docfix
git add -A
git commit -m "feat(ledger): notice a verdict typed over a row that was already finished

Delete old files reads the final state column, never the verdict — so typing
ignore on a finished row stops nothing and the old file goes anyway. Two columns,
one of which looks like a switch and is not.

Rather than make the verdict control the delete as well, which would leave two
columns controlling it and neither being the record, the tool says what will
really happen and offers to write the column that does control it."
```

---

## Task 7: Use the file as it is, and read it again

Spec §6 and §7.

**Files:**
- Create: `src/MocdDocFix/Cli/LedgerGate.cs`
- Modify: `src/MocdDocFix/Cli/Session.cs`
- Test: new `tests/MocdDocFix.Tests/LedgerGateTests.cs`

**Interfaces:**
- Consumes: Task 6's `VerdictGuard`, Task 5's merge.
- Produces:
  ```csharp
  public enum GateAnswer { GoAhead, Cancel }

  public static (GateAnswer Answer, IReadOnlyList<LedgerRow> Rows) LedgerGate.Ask(
      IPrompts prompts,
      string question,
      IReadOnlyList<LedgerRow> rows,
      Func<IReadOnlyList<LedgerRow>> reread);
  ```
  Also `Session.RereadLedger() → IReadOnlyList<LedgerRow>` and
  `Session.NarrowToOneDocument(IReadOnlyList<LedgerRow>) → (IReadOnlyList<LedgerRow> Working, IReadOnlyList<LedgerRow> Whole)`.

**Testing note:** `ScriptedPrompts` is line-driven — it feeds `IPrompts.ReadLine`, and `Asker`
falls to its typed-number path because `IPrompts.Interactive` defaults to false. **Menu numbers
are 1-based**, so `new ScriptedPrompts("1")` picks index 0. It throws when the script runs out,
which is what makes a question that re-asks for ever fail loudly instead of hanging.

- [ ] **Step 1: Write the failing test**

Create `tests/MocdDocFix.Tests/LedgerGateTests.cs`:

```csharp
using MocdDocFix.Cli;
using MocdDocFix.Domain;
using MocdDocFix.Tests.Fakes;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Every mode reads the sheet the moment it is chosen, so edits made before that are picked up
/// already. The gap is the pause after a mode says what it is about to do — which is exactly
/// when somebody thinks "wait, not that row" and opens Excel.
/// </summary>
public class LedgerGateTests
{
    private static LedgerRow Row(string verdict) => new()
    {
        DocId = Guid.Parse("33333333-0000-0000-0000-000000000001"),
        DocFileName = "a.pdf",
        Verdict = verdict
    };

    // Menu numbers are 1-based: "1" is Go ahead, "2" is Read again, "3" is Cancel.

    [Fact]
    public void Going_ahead_keeps_the_rows_it_was_given()
    {
        var before = new[] { Row(RowVerdicts.Fix) };

        var (answer, after) = LedgerGate.Ask(new ScriptedPrompts("1"), "Delete them?", before,
            () => new[] { Row(RowVerdicts.Ignore) });

        Assert.Equal(GateAnswer.GoAhead, answer);
        Assert.Same(before, after);
    }

    [Fact]
    public void Reading_again_replaces_the_rows_then_asks_once_more()
    {
        var (answer, after) = LedgerGate.Ask(
            new ScriptedPrompts("2", "1"), "Delete them?",
            new[] { Row(RowVerdicts.Fix) },
            () => new[] { Row(RowVerdicts.Ignore) });

        Assert.Equal(GateAnswer.GoAhead, answer);
        Assert.Equal(RowVerdict.Ignore, after[0].Verdict2());
    }

    [Fact]
    public void Reading_again_says_what_changed()
    {
        var prompts = new ScriptedPrompts("2", "1");

        LedgerGate.Ask(prompts, "Delete them?", new[] { Row(RowVerdicts.Fix) },
            () => new[] { Row(RowVerdicts.Ignore) });

        Assert.True(prompts.Said("fix → ignore"));
    }

    [Fact]
    public void Cancelling_says_so()
    {
        var (answer, _) = LedgerGate.Ask(new ScriptedPrompts("3"), "Delete them?",
            new[] { Row(RowVerdicts.Fix) }, Array.Empty<LedgerRow>);

        Assert.Equal(GateAnswer.Cancel, answer);
    }
}
```

`Reading_again_says_what_changed` depends on both rows having the same `DocId`, which `Row`
above guarantees — the reload is compared by document, never by row number, because row numbers
are positional and change on every write.

- [ ] **Step 2: Run it and watch it fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~LedgerGateTests"
```

Expected: FAIL — `LedgerGate` does not exist.

- [ ] **Step 3: Write the gate**

Create `src/MocdDocFix/Cli/LedgerGate.cs`:

```csharp
using MocdDocFix.Domain;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

public enum GateAnswer { GoAhead, Cancel }

/// <summary>
/// The pause before a mode acts, with a way to pick up edits made during it.
///
/// Every mode already reads the sheet from disk the moment it is chosen, so anything edited
/// before that is seen. The gap is afterwards: a mode reads the ledger, prints what it is about
/// to do, and waits — and that is the moment somebody thinks "hold on, not that row", opens
/// Excel, changes it and answers yes, at which point the mode acts on what it read a minute ago.
///
/// One gate for all four modes, so "read the sheet again" cannot come to mean one thing in the
/// delete step and something else in the repair run.
/// </summary>
public static class LedgerGate
{
    /// <param name="reread">
    /// Fetches the ledger from disk again. A delegate rather than a store, because the caller
    /// is the one that knows how to reconcile what comes back — the journal replay and the
    /// verdict guard both belong to the session, not here.
    /// </param>
    /// <returns>What to do, and the rows to do it with — which may not be the rows passed in.</returns>
    public static (GateAnswer Answer, IReadOnlyList<LedgerRow> Rows) Ask(
        IPrompts prompts,
        string question,
        IReadOnlyList<LedgerRow> rows,
        Func<IReadOnlyList<LedgerRow>> reread)
    {
        var asker = new Asker(prompts);

        while (true)
        {
            var answer = asker.Ask(question, new[]
            {
                new Choice("Go ahead", $"{rows.Count} row(s) as the sheet now reads"),
                new Choice("Read the sheet again", "pick up edits you just made",
                    "Reads the workbook from disk again, says what changed, and asks this " +
                    "question once more. Nothing is written by a reload."),
                new Choice("Cancel", "do nothing")
            }, defaultIndex: 0);

            if (answer.Kind != AnswerKind.Chosen || answer.Index == 2)
                return (GateAnswer.Cancel, rows);

            if (answer.Index == 0) return (GateAnswer.GoAhead, rows);

            var fresh = reread();
            if (fresh.Count == 0)
            {
                prompts.Say("Nothing came back from the sheet. Carrying on with what was " +
                            "already read.", Tone.Warn);
                continue;
            }

            Report(prompts, rows, fresh);
            rows = fresh;
        }
    }

    /// <summary>
    /// What moved, by document rather than by row number — the numbers are positional and change
    /// on every write, so comparing them would report every row as changed.
    /// </summary>
    private static void Report(
        IPrompts prompts, IReadOnlyList<LedgerRow> before, IReadOnlyList<LedgerRow> after)
    {
        var was = before.ToDictionary(r => r.DocId, r => r.Verdict);
        var changes = new List<string>();

        foreach (var row in after)
        {
            if (!was.TryGetValue(row.DocId, out var old)) continue;
            if (string.Equals(old, row.Verdict, StringComparison.OrdinalIgnoreCase)) continue;

            changes.Add($"{(old.Length == 0 ? "(blank)" : old)} → " +
                        $"{(row.Verdict.Length == 0 ? "(blank)" : row.Verdict)}");
        }

        prompts.Blank();

        if (changes.Count == 0)
        {
            prompts.Say($"{after.Count} row(s) read back. No verdict changed.", Tone.Muted);
            return;
        }

        var grouped = changes
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}");

        prompts.Say($"{changes.Count} row(s) changed: {string.Join(", ", grouped)}", Tone.Good);
    }
}
```

- [ ] **Step 4: Run it and watch it pass**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~LedgerGateTests"
```

Expected: PASS.

- [ ] **Step 5: Add the gate to the three read-only-ish modes**

In `src/MocdDocFix/Cli/Session.cs`, add the shared re-read:

```csharp
    /// <summary>
    /// The ledger from disk again, put through everything a freshly-opened one goes through:
    /// the journal replay, and the guard on a verdict typed over a finished row. A reload that
    /// skipped those would quietly behave differently from opening the mode again.
    /// </summary>
    private IReadOnlyList<LedgerRow> RereadLedger()
    {
        var rows = Reconciled(_ledger.Read());
        if (rows.Count > 0 && AskAboutStranded(rows)) _ledger.Write(rows);

        return rows;
    }
```

Then in `DeleteAsync`, after the rows are opened and before the work begins:

```csharp
            var (gate, rows) = LedgerGate.Ask(_prompts,
                "Delete the old files of every corrected row?", opened, RereadLedger);

            if (gate == GateAnswer.Cancel) return StepOutcome.Of("Nothing was deleted.");
```

renaming the existing `var rows = await OpenLedgerAsync(mayRebuild: false, ct);` to `var opened =
…` and checking `opened.Count` instead. Do the same in `RedoAsync` with the question `"Put those
records back the way they were?"` and the cancel message `"Nothing was reverted."`, and in
`CheckAsync` with `"Check every row against both systems?"` and `"Nothing was checked."`.

- [ ] **Step 6: Add the third choice to the repair run's work menu**

In `src/MocdDocFix/Cli/Session.cs`, change `NarrowToOneDocument` to loop and to return both sets:

```csharp
    /// <param name="rows">Everything in the ledger. Replaced when the operator reloads.</param>
    /// <returns>The rows to work on, and the whole ledger they came from.</returns>
    private (IReadOnlyList<LedgerRow> Working, IReadOnlyList<LedgerRow> Whole)
        NarrowToOneDocument(IReadOnlyList<LedgerRow> rows)
    {
        while (true)
        {
            var fixable = rows.Count(r => r.Verdict2() == RowVerdict.Fix);

            var how = new Asker(_prompts).Ask("What do you want to work on?", new[]
            {
                new Choice("From the file", $"every row marked fix — {fixable} of {rows.Count}",
                    "Works down the ledger in order, acting on every row whose verdict says " +
                    "fix and walking past the rest."),
                new Choice("One document", "type its GUID",
                    "The same six steps, for the single row whose doc id you give. Useful for " +
                    "re-trying one document without opening the whole run."),
                new Choice("Read the sheet again", "pick up edits you just made",
                    "Reads the workbook from disk again and comes back to this question with " +
                    "the counts refreshed. Nothing is written by a reload.")
            }, defaultIndex: 0);

            if (how.Kind != AnswerKind.Chosen) return (Array.Empty<LedgerRow>(), rows);

            if (how.Index == 2)
            {
                var fresh = RereadLedger();
                if (fresh.Count > 0) rows = fresh;
                continue;
            }

            if (how.Index == 0) return (rows, rows);

            _prompts.Blank();
            var typed = _prompts.ReadLine("  Document GUID").Trim();

            if (!Guid.TryParse(typed, out var wanted))
            {
                _prompts.Say($"'{typed}' is not a GUID.", Tone.Warn);
                return (Array.Empty<LedgerRow>(), rows);
            }

            var found = rows.Where(r => r.DocId == wanted).ToList();

            if (found.Count == 0)
            {
                // Not in the ledger means the ledger is older than the document, or the document
                // is outside the services this run is scoped to. Guessing is worse than saying so.
                _prompts.Say($"No row in the ledger has doc id {wanted}. If the document is " +
                             "new, run a scan first; if it belongs to a service this run is not " +
                             "scoped to, it will never appear.", Tone.Warn);
                return (Array.Empty<LedgerRow>(), rows);
            }

            _prompts.Say($"Row {found[0].Row} — {found[0].DocName}", Tone.Muted);
            return (found, rows);
        }
    }
```

In `RepairAsync`, take both halves:

```csharp
            var (working, all) = NarrowToOneDocument(opened);
            var rows = working.Where(r => !_gone.Contains(r.DocId)).ToList();
```

where `opened` is what `OpenLedgerAsync` returned. Every later use of `all` in `RepairAsync`
already refers to the whole ledger, so it now correctly refers to the reloaded one.

- [ ] **Step 7: Ask whether to rescan at all**

In `src/MocdDocFix/Cli/Session.cs`, add:

```csharp
    /// <summary>
    /// Whether this entry into the repair run should ask CRM.
    ///
    /// The rescan reads every document in scope — 4,510 in dev and far more across every
    /// catalogue — plus a catalogue-name lookup per row. When the sheet is already in front of
    /// you and you only want to carry on working through it, all of that is spent for nothing.
    ///
    /// Skipping it is safe. Before a single byte is uploaded the run still asks CRM, row by row,
    /// whether that document is now filed correctly, so a stale sheet cannot cause a second copy.
    /// </summary>
    private bool AskWhetherToRescan()
    {
        if (!_ledger.Exists) return true;

        var when = File.GetLastWriteTime(_ledger.Path).ToString("yyyy-MM-dd HH:mm");
        var rows = _ledger.Read().Count;

        var answer = new Asker(_prompts).Ask("Where should this run get its rows?", new[]
        {
            new Choice("Use the ledger as it is", $"{rows} row(s), last written {when}",
                "Goes straight to the work, with the sheet exactly as it is on disk. Nothing " +
                "is asked of CRM until the run reaches a document."),
            new Choice("Update it from CRM first", "reads every document in scope",
                "Reads every document under the services this run is scoped to, classifies " +
                "them, and merges the answers into the sheet without discarding anything you " +
                "have typed. This is the slow one.")
        }, defaultIndex: 0);

        return answer.Kind == AnswerKind.Chosen && answer.Index == 1;
    }
```

and in `RepairAsync`, replace `var all = await OpenLedgerAsync(mayRebuild: true, ct);` with:

```csharp
            var opened = await OpenLedgerAsync(mayRebuild: AskWhetherToRescan(), ct);
            if (opened.Count == 0) return StepOutcome.Of("Nothing in the ledger.");
```

- [ ] **Step 8: Run the full suite**

```bash
cd /d/mocd-docfix && dotnet test
```

Expected: PASS. `WizardTests` may need an extra queued answer now that the repair run asks one
more question before it starts.

- [ ] **Step 9: Commit**

```bash
cd /d/mocd-docfix
git add -A
git commit -m "feat(cli): read the sheet again, in every mode that acts on it

Each mode already reads the ledger when it is chosen. The gap is the pause after
it says what it is about to do — which is exactly when somebody thinks 'wait, not
that row' and opens Excel. One gate for all four modes, so the reload cannot mean
one thing in the delete step and another in the repair run.

The repair run also asks whether to rescan at all. Reading 4,510 documents to
carry on with a sheet already in front of you is time spent for nothing, and it
is safe to skip: the pre-run check still asks CRM about every row before a byte
is uploaded."
```

---

## Task 8: Choosing the scope, and two ledger files

Spec §9. The largest task, and the only one that changes where the ledger lives.

**Files:**
- Create: `src/MocdDocFix/Storage/LedgerSet.cs`
- Modify: `src/MocdDocFix/Config/AppConfig.cs`, `src/MocdDocFix/Config/EnvironmentConfig.cs`, `src/MocdDocFix/Config/ConfigStore.cs`, `src/MocdDocFix/Clients/CrmReadClient.cs`, `src/MocdDocFix/Cli/Session.cs`, `src/MocdDocFix/Cli/Wizard.cs`
- Test: `tests/MocdDocFix.Tests/ConfigStoreTests.cs`, `tests/MocdDocFix.Tests/CrmReadClientTests.cs`, new `tests/MocdDocFix.Tests/LedgerSetTests.cs`

**Interfaces:**
- Consumes: Tasks 1–7 in full.
- Produces:
  ```csharp
  public enum LedgerScope { Ours, All }

  public LedgerSet(string path, LedgerScope scope, IReadOnlyList<Guid> ourCatalogues);
  public LedgerScope Scope { get; }
  public string Path { get; }                     // the file the operator works in
  public string? OtherPath { get; }               // the other services' file, when there is one
  public bool Exists { get; }
  public bool CanWrite();
  public Func<string, bool>? AskToRetry { set; }  // set on both stores
  public string PreviousDirectory { get; }
  public LedgerStore PrimaryStore { get; }        // for the modes that take a store
  public IReadOnlyList<LedgerRow> Read();
  public void Write(IReadOnlyList<LedgerRow> rows);

  Task<IReadOnlyList<(Guid Id, string Name)>> ICrmReadClient.GetServiceCataloguesAsync(
      CancellationToken ct);

  public Task<LedgerScope> Session.AskAboutScopeAsync(LedgerScope remembered, CancellationToken ct);
  ```
  and `EnvironmentConfig` gains `string Scope = "ours"` as its last positional member.

- [ ] **Step 1: Write the failing config test**

Add to `tests/MocdDocFix.Tests/ConfigStoreTests.cs`:

```csharp
/// <summary>
/// Membership Managment was taken out of scope on 2026-09-13 and put back on 2026-09-18 at the
/// operator's request. Seven became eight.
/// </summary>
[Fact]
public void The_default_scope_is_eight_services_including_membership_management()
{
    var services = AppConfig.Default().ServiceCatalogues;

    Assert.Equal(8, services.Count);
    Assert.Contains(Guid.Parse("6bcb221c-6c2b-f111-b119-005056010908"), services);
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~The_default_scope_is_eight_services"
```

Expected: FAIL — `Assert.Equal() Failure: Expected: 8, Actual: 7`.

- [ ] **Step 3: Put Membership Managment back and give the environment a scope**

In `src/MocdDocFix/Config/AppConfig.cs`, replace the comment and add the entry:

```csharp
    /// <summary>
    /// The eight in-scope services. Configuration, not code — this list only seeds config.json.
    /// </summary>
    public List<Guid> ServiceCatalogues { get; set; } = new();

    public static AppConfig Default() => new()
    {
        ServiceCatalogues = new List<Guid>
        {
            Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"), // Employee Appointment Request
            Guid.Parse("3ff27d73-653e-f111-b119-005056010908"), // General Assembly Meeting Request
            Guid.Parse("d2744b68-aa50-f111-b119-005056010908"), // GAM - Nomination List Request
            Guid.Parse("24db2387-c15d-f111-b119-005056010908"), // GAM - Attendance
            Guid.Parse("35105602-2b5f-f111-b119-005056010908"), // GAM - Update (Reschedule)
            Guid.Parse("d8155dcc-635e-f111-b119-005056010908"), // GAM - Minutes of Meeting
            Guid.Parse("930f636a-077a-f111-b119-005056010908"), // By-Laws Amendment Requests

            // Taken out of scope on 2026-09-13 and put back on 2026-09-18, both at the
            // operator's instruction. Seven became eight.
            Guid.Parse("6bcb221c-6c2b-f111-b119-005056010908"), // Membership Managment
        }
    };
```

In `src/MocdDocFix/Config/EnvironmentConfig.cs`, add the remembered scope:

```csharp
namespace MocdDocFix.Config;

/// <summary>Non-secret settings for one environment. Secrets live in ISecretStore.</summary>
/// <param name="Scope">
/// Which services this environment was last worked on: "ours" or "all". Remembered per
/// environment rather than globally, because dev may be surveying every catalogue while
/// pre-prod — fifty thousand documents — stays on the eight. Anything unrecognised means "ours".
/// </param>
public sealed record EnvironmentConfig(
    string FileServiceBaseUrl,
    string CrmUrl,
    string CrmDomain,
    string CrmUser,
    bool IsProduction,
    string Scope = "ours");
```

- [ ] **Step 4: Write the failing LedgerSet test**

Create `tests/MocdDocFix.Tests/LedgerSetTests.cs`:

```csharp
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Two files whose contents do not overlap: the services we work on, and every other. A
/// document therefore lives in exactly one of them and they can never disagree about it.
/// </summary>
[Collection(LedgerCollection.Name)]
public class LedgerSetTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ledgerset-" + Guid.NewGuid().ToString("N"));

    private static readonly Guid Ours = Guid.Parse("3ff27d73-653e-f111-b119-005056010908");
    private static readonly Guid Theirs = Guid.Parse("9ee8941a-8870-f111-b119-005056010908");

    public LedgerSetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static LedgerRow Row(Guid catalogue) => new()
    {
        DocId = Guid.NewGuid(),
        DocFileName = "a.pdf",
        Verdict = RowVerdicts.Fix,
        ServiceCatalogueId = catalogue.ToString()
    };

    private LedgerSet Set(LedgerScope scope) =>
        new(Path.Combine(_dir, "repair-dev.xlsx"), scope, new[] { Ours });

    [Fact]
    public void Working_on_our_services_never_opens_the_other_file()
    {
        Set(LedgerScope.Ours).Write(new[] { Row(Ours) });

        Assert.True(File.Exists(Path.Combine(_dir, "repair-dev.xlsx")));
        Assert.False(File.Exists(Path.Combine(_dir, "repair-dev-other-services.xlsx")));
    }

    [Fact]
    public void Across_everything_each_row_goes_to_the_file_its_service_belongs_to()
    {
        Set(LedgerScope.All).Write(new[] { Row(Ours), Row(Theirs) });

        Assert.Single(new LedgerStore(Path.Combine(_dir, "repair-dev.xlsx")).Read());
        Assert.Single(new LedgerStore(
            Path.Combine(_dir, "repair-dev-other-services.xlsx")).Read());
    }

    [Fact]
    public void Across_everything_reading_gives_both_files_back_as_one_list()
    {
        var set = Set(LedgerScope.All);
        set.Write(new[] { Row(Ours), Row(Theirs) });

        Assert.Equal(2, set.Read().Count);
    }

    /// <summary>
    /// A row whose service is not one of ours, written while scoped to ours. It has nowhere
    /// else to go, so it stays — losing it would lose a pending delete nobody could then find.
    /// </summary>
    [Fact]
    public void A_stray_row_is_kept_rather_than_dropped_when_the_other_file_is_shut()
    {
        var set = Set(LedgerScope.Ours);
        set.Write(new[] { Row(Ours), Row(Theirs) });

        Assert.Equal(2, set.Read().Count);
    }
}
```

- [ ] **Step 5: Run it and watch it fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~LedgerSetTests"
```

Expected: FAIL — `LedgerSet` does not exist.

- [ ] **Step 6: Write the set**

Create `src/MocdDocFix/Storage/LedgerSet.cs`:

```csharp
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>Which services a sitting is working on.</summary>
public enum LedgerScope
{
    /// <summary>The eight this tool was built for.</summary>
    Ours,

    /// <summary>Every service catalogue in CRM.</summary>
    All
}

/// <summary>
/// One or two ledger files, according to the scope, presented as one.
///
/// The two files hold sets that do not overlap — the services we work on, and every other — so a
/// document lives in exactly one of them and they can never disagree about it. That is why the
/// second file is "the others" rather than "all".
///
/// Two files rather than two tabs of one, and the reason is speed and nothing else. An .xlsx is
/// a single zip archive, so the cost of a write follows the file: at 50,000 rows a save is 11
/// seconds, and the ledger is saved after every corrected document. Keeping the other services
/// in their own file means a sitting scoped to the eight never opens it and never pays for it.
/// </summary>
public sealed class LedgerSet
{
    private readonly LedgerStore _ours;
    private readonly LedgerStore? _others;
    private readonly HashSet<string> _ourCatalogues;

    /// <param name="path">The .xlsx of the services we work on. The other is derived from it.</param>
    /// <param name="ourCatalogues">
    /// The services that belong in the first file. Everything else belongs in the second.
    /// </param>
    public LedgerSet(string path, LedgerScope scope, IReadOnlyList<Guid> ourCatalogues)
    {
        Scope = scope;

        _ours = new LedgerStore(path);

        _others = scope == LedgerScope.All
            ? new LedgerStore(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(path)!,
                System.IO.Path.GetFileNameWithoutExtension(path) + "-other-services.xlsx"))
            : null;

        _ourCatalogues = ourCatalogues
            .Select(c => c.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public LedgerScope Scope { get; }

    /// <summary>The file the operator is told about. The one they work in.</summary>
    public string Path => _ours.Path;

    /// <summary>The other file, when there is one.</summary>
    public string? OtherPath => _others?.Path;

    public bool Exists => _ours.Exists || (_others?.Exists ?? false);

    public bool CanWrite() => _ours.CanWrite() && (_others?.CanWrite() ?? true);

    /// <summary>Set on both stores, so either file being open in Excel asks the same question.</summary>
    public Func<string, bool>? AskToRetry
    {
        set
        {
            _ours.AskToRetry = value;
            if (_others is not null) _others.AskToRetry = value;
        }
    }

    public string PreviousDirectory => _ours.PreviousDirectory;

    public IReadOnlyList<LedgerRow> Read()
    {
        var rows = new List<LedgerRow>(_ours.Read());
        if (_others is not null) rows.AddRange(_others.Read());

        return rows;
    }

    public void Write(IReadOnlyList<LedgerRow> rows)
    {
        if (_others is null)
        {
            // Scoped to our services, so there is one file and everything goes in it — including
            // any row whose service is not one of ours. Such a row can only have got there by
            // having been worked on when the scope was wider, and dropping it would lose a
            // pending delete that nobody could then find.
            _ours.Write(rows);
            return;
        }

        var mine = new List<LedgerRow>();
        var theirs = new List<LedgerRow>();

        foreach (var row in rows)
            (IsOurs(row) ? mine : theirs).Add(row);

        _ours.Write(mine);
        _others.Write(theirs);
    }

    /// <summary>
    /// A row with no service catalogue on it at all counts as ours. It is almost always a row
    /// whose document CRM no longer returns, and the file we work in is where it can be seen.
    /// </summary>
    private bool IsOurs(LedgerRow row) =>
        row.ServiceCatalogueId.Length == 0 || _ourCatalogues.Contains(row.ServiceCatalogueId);
}
```

- [ ] **Step 6b: Say when a row changes file**

A document type can be moved to another service in CRM, which moves its row from one file to the
other. That happens on its own — `Write` routes on the catalogue every time — but silently it
reads as a row vanishing from one place and appearing in another. Add to `LedgerSet`:

```csharp
    /// <summary>Which file each document was last read from, so a move can be noticed.</summary>
    private readonly Dictionary<Guid, bool> _wasOurs = new();

    /// <summary>
    /// How many rows changed file on the last write, because their document type was moved to
    /// a different service in CRM. Silently this reads as a row vanishing from one file and
    /// appearing in another, which is worth a sentence.
    /// </summary>
    public int LastMoved { get; private set; }
```

In `Read`, record where each row came from:

```csharp
    public IReadOnlyList<LedgerRow> Read()
    {
        var rows = new List<LedgerRow>();

        foreach (var row in _ours.Read()) { _wasOurs[row.DocId] = true; rows.Add(row); }

        if (_others is not null)
            foreach (var row in _others.Read()) { _wasOurs[row.DocId] = false; rows.Add(row); }

        return rows;
    }
```

and in the two-file branch of `Write`, count the changes as the rows are sorted:

```csharp
        var mine = new List<LedgerRow>();
        var theirs = new List<LedgerRow>();
        var moved = 0;

        foreach (var row in rows)
        {
            var ours = IsOurs(row);
            if (_wasOurs.TryGetValue(row.DocId, out var before) && before != ours) moved++;

            (ours ? mine : theirs).Add(row);
            _wasOurs[row.DocId] = ours;
        }

        LastMoved = moved;

        _ours.Write(mine);
        _others.Write(theirs);
```

In `src/MocdDocFix/Cli/Session.cs`, after the ledger is written in `OpenLedgerAsync`:

```csharp
        if (_ledger.LastMoved > 0)
            _prompts.Say($"{_ledger.LastMoved} row(s) changed file — their document type was " +
                         "moved to a different service in CRM.", Tone.Muted);
```

Add to `tests/MocdDocFix.Tests/LedgerSetTests.cs`:

```csharp
[Fact]
public void A_row_whose_service_changed_is_counted_as_moved()
{
    var set = Set(LedgerScope.All);

    var row = Row(Ours);
    set.Write(new[] { row });
    set.Read();

    row.ServiceCatalogueId = Theirs.ToString();
    set.Write(new[] { row });

    Assert.Equal(1, set.LastMoved);
}
```

- [ ] **Step 7: Run it and watch it pass**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~LedgerSetTests"
```

Expected: PASS, 4 passed.

- [ ] **Step 8: Write the failing CRM test**

Add to `tests/MocdDocFix.Tests/CrmReadClientTests.cs`, using that file's own `Build()` helper,
which returns `(CrmReadClient client, FakeHttpMessageHandler handler)`:

```csharp
[Fact]
public async Task Every_service_catalogue_comes_back_with_its_name()
{
    var (client, handler) = Build();

    handler.Enqueue(HttpStatusCode.OK, """
        {"value":[
          {"mocd_servicecatalogueid":"3ff27d73-653e-f111-b119-005056010908","mocd_name":"GAM Request"},
          {"mocd_servicecatalogueid":"9ee8941a-8870-f111-b119-005056010908","mocd_name":"Violation Report"}
        ]}
        """);

    var catalogues = await client.GetServiceCataloguesAsync(CancellationToken.None);

    Assert.Equal(2, catalogues.Count);
    Assert.Equal("GAM Request", catalogues[0].Name);
    Assert.Contains("mocd_servicecatalogues", handler.Requests[0].RequestUri!.ToString());
}
```

- [ ] **Step 9: Run it and watch it fail**

```bash
cd /d/mocd-docfix && dotnet test --filter "FullyQualifiedName~Every_service_catalogue_comes_back"
```

Expected: FAIL — `GetServiceCataloguesAsync` does not exist.

- [ ] **Step 10: Add the query**

In `src/MocdDocFix/Clients/CrmReadClient.cs`, add to the interface, with a default so the test
doubles keep compiling — the same pattern `FindDocumentFilesByFileIdAsync` uses:

```csharp
    /// <summary>
    /// Every mocd_servicecatalogue, id and display name. Asked live when the operator chooses to
    /// work across everything, rather than read from a stored list — the point of that choice is
    /// to find out what is actually there.
    /// </summary>
    Task<IReadOnlyList<(Guid Id, string Name)>> GetServiceCataloguesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(Guid, string)>>(Array.Empty<(Guid, string)>());
```

and implement it on `CrmReadClient`, paging the same way `GetDocumentTypeIdsAsync` does:

```csharp
    public async Task<IReadOnlyList<(Guid Id, string Name)>> GetServiceCataloguesAsync(
        CancellationToken ct)
    {
        var found = new List<(Guid, string)>();
        string? next = "mocd_servicecatalogues?$select=mocd_servicecatalogueid,mocd_name";

        while (next is not null)
        {
            using var response = await _http.GetAsync(next, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) break;

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("value", out var value))
                foreach (var item in value.EnumerateArray())
                {
                    if (!item.TryGetProperty("mocd_servicecatalogueid", out var id)) continue;
                    if (!Guid.TryParse(id.GetString(), out var parsed)) continue;

                    var name = item.TryGetProperty("mocd_name", out var n)
                        ? n.GetString() ?? string.Empty
                        : string.Empty;

                    found.Add((parsed, name));
                }

            next = root.TryGetProperty("@odata.nextLink", out var link)
                ? link.GetString()
                : null;
        }

        return found;
    }
```

- [ ] **Step 11: Ask the scope question**

In `src/MocdDocFix/Cli/Session.cs`, add:

```csharp
    /// <summary>
    /// Which services this whole sitting is about. Asked once, after the environment and before
    /// the menu, because every mode needs the answer — the delete step and Check it all have to
    /// know which file to open, not just the repair run.
    /// </summary>
    public async Task<LedgerScope> AskAboutScopeAsync(LedgerScope remembered, CancellationToken ct)
    {
        var names = _appConfig.ServiceCatalogues.Count;

        var answer = new Asker(_prompts).Ask("Which services are you working on?", new[]
        {
            new Choice("The services we work on", $"{names} services",
                "Employee Appointment Request · General Assembly Meeting Request · GAM " +
                "Nomination List · GAM Attendance · GAM Update · GAM Minutes of Meeting · " +
                "By-Laws Amendment Requests · Membership Managment"),
            new Choice("Every service catalogue in CRM", "asks CRM what there is first",
                "Reads the full list of service catalogues from CRM, tells you what it found, " +
                "and asks again before it reads a single document. The other services are kept " +
                "in their own file so this one stays quick to save.")
        }, defaultIndex: remembered == LedgerScope.All ? 1 : 0);

        if (answer.Kind != AnswerKind.Chosen || answer.Index == 0) return LedgerScope.Ours;

        return await ConfirmTheWholeLotAsync(ct);
    }

    /// <summary>
    /// Shows what "everything" actually means before anything is read. In pre-prod it is fifty
    /// thousand documents and hours of CRM reads, which is not a thing to discover afterwards.
    /// </summary>
    private async Task<LedgerScope> ConfirmTheWholeLotAsync(CancellationToken ct)
    {
        _prompts.Blank();
        _prompts.Say("Asking CRM what service catalogues there are. This writes nothing.",
            Tone.Muted);

        var catalogues = await _read.GetServiceCataloguesAsync(ct);

        if (catalogues.Count == 0)
        {
            _prompts.Say("CRM returned no service catalogues, so there is nothing to widen to. " +
                         "Staying on the services we work on.", Tone.Warn);
            return LedgerScope.Ours;
        }

        var ours = _appConfig.ServiceCatalogues.ToHashSet();
        var others = catalogues.Count(c => !ours.Contains(c.Id));

        _prompts.Section("Every service catalogue");
        _prompts.Say($"{catalogues.Count} service catalogues, {others} of them outside the " +
                     $"{ours.Count} this tool was built for.");
        _prompts.Blank();
        _prompts.Bullet("Every document under all of them is read and classified. In pre-prod " +
                        "that is over fifty thousand documents.", Tone.Warn);
        _prompts.Bullet("Documents whose document type carries no service catalogue cannot be " +
                        "reached this way and will not appear, however wide the scope.",
            Tone.Muted);
        _prompts.Bullet("The other services go in their own file, so the one you usually work " +
                        "in stays small and quick to save.", Tone.Muted);
        _prompts.Blank();

        return _prompts.YesNo("  Work across all of them?", defaultYes: false)
            ? LedgerScope.All
            : LedgerScope.Ours;
    }
```

- [ ] **Step 12: Put the set behind the session**

In `src/MocdDocFix/Cli/Session.cs`, change the `_ledger` field's type from `LedgerStore` to
`LedgerSet` and build it in the constructor from a scope passed in:

```csharp
        _ledger = new LedgerSet(Path.Combine(reports, $"repair-{envName}.xlsx"),
            scope, appConfig.ServiceCatalogues);
```

Add `LedgerScope scope` as the last constructor parameter with a default of `LedgerScope.Ours`,
so the direct-command path in `RunDirectAsync` and every existing caller keeps working.

`LedgerSet` already exposes `Read`, `Write`, `CanWrite`, `Exists`, `Path`, `PreviousDirectory`
and `AskToRetry` with the same shapes `LedgerStore` did, so no other line in `Session` changes.
`LookupCommand` and the four command classes take a `LedgerStore`; give them
`_ledger.PrimaryStore` by adding to `LedgerSet`:

```csharp
    /// <summary>
    /// The file the modes write single rows back to. They are handed one store because a row
    /// they finish is a row already in a file, and moving it between files is the set's job at
    /// the next full write, not theirs mid-run.
    /// </summary>
    public LedgerStore PrimaryStore => _ours;
```

- [ ] **Step 13: Show the scope, and let it be changed**

In `src/MocdDocFix/Cli/Wizard.cs`, add a scope line to `Banner` and a menu entry. The wizard is
handed the text and the action:

```csharp
    public Wizard(IPrompts prompts, string envName, bool isProduction,
        string crmUrl, string fileServerUrl, LedgerActions actions,
        string scopeLabel, Func<Task>? changeScope = null)
```

In `Banner`, after the Environment field:

```csharp
        _prompts.Field("Services", scopeLabel);
```

and in the menu, between "Is this file still there?" and "Change environment":

```csharp
                new Choice("Change services", $"currently {_scopeLabel}",
                    "Switches between the services this tool was built for and every service " +
                    "catalogue in CRM. Each has its own ledger file, so nothing is lost either " +
                    "way — the rows are still there when you come back.",
                    Enabled: _changeScope is not null,
                    DisabledNote: "this build was not given a way to change the scope."),
```

shifting the two indices below it by one, and adding its case to the switch:

```csharp
                case 5: if (_changeScope is not null) await _changeScope(); break;
                case 6: return WizardExit.ChangeEnvironment;
```

with the default arm's index updated from 6 to 7 in the `mode.Kind == AnswerKind.Chosen ?
mode.Index : 6` expression, which becomes `: 7`.

- [ ] **Step 14: Remember the answer**

`Session` is constructed at **`src/MocdDocFix/Program.cs:100`**:

```csharp
    using var session = new Session(appConfig, env, envName, prompts, options.DryRun);
```

Immediately before that line, read the remembered scope; immediately after it, ask and save the
answer back. Only the guided path asks — `RunDirectAsync` below it is scripted and must not
stop for a question:

```csharp
var remembered = config.Environments.TryGetValue(envName, out var env) &&
                 env.Scope.Equals("all", StringComparison.OrdinalIgnoreCase)
    ? LedgerScope.All
    : LedgerScope.Ours;

var scope = await session.AskAboutScopeAsync(remembered, ct);

if (scope != remembered && config.Environments.TryGetValue(envName, out var toSave))
{
    config.Environments[envName] = toSave with
    {
        Scope = scope == LedgerScope.All ? "all" : "ours"
    };
    store.Save(config);
}
```

The session that asks the question is built before the scope is known, so build it with the
remembered scope and rebuild it with the answer when they differ — `Session` is `IDisposable`
and cheap to construct:

```csharp
    if (scope != remembered)
    {
        session.Dispose();
        session = new Session(appConfig, env, envName, prompts, options.DryRun, scope);
    }
```

which means `session` is a plain local with an explicit `try/finally` dispose rather than
`using var`. Guard the question so a scripted run never sees it:

```csharp
    if (options.Command == "guided")
    {
        // …ask, save, rebuild as above…
    }
```

- [ ] **Step 15: Collapse the out-of-scope report**

In `src/MocdDocFix/Cli/Session.cs`, replace the body of `ReportGone` so out-of-scope rows are
counted rather than listed one by one:

```csharp
    private void ReportGone(Merged merged)
    {
        if (merged.Gone.Count == 0) return;

        var configured = _appConfig.ServiceCatalogues
            .Select(c => c.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outOfScope = merged.Gone
            .Where(r => r.ServiceCatalogueId.Length > 0 &&
                        !configured.Contains(r.ServiceCatalogueId))
            .ToList();

        var absent = merged.Gone.Except(outOfScope).ToList();

        _prompts.Blank();

        // Counted, not listed. Narrowing the scope puts tens of thousands of rows in here at
        // once, and a bullet each says nothing a single number does not.
        if (outOfScope.Count > 0)
            _prompts.Say($"{outOfScope.Count} row(s) belong to services you did not scan this " +
                         "run — nothing will act on them.", Tone.Muted);

        // These are different: still in scope, and CRM did not return them. That is a document
        // that has been deleted, and it is worth naming.
        foreach (var row in absent.Take(10))
            _prompts.Bullet($"{row.Ref()}: CRM did not return this document. Kept in the " +
                            "ledger, but nothing will act on it.", Tone.Warn);

        if (absent.Count > 10)
            _prompts.Bullet($"… and {absent.Count - 10} more", Tone.Muted);
    }
```

- [ ] **Step 16: Run the full suite**

```bash
cd /d/mocd-docfix && dotnet test
```

Expected: PASS. `WizardTests` needs its menu indices moved by one, and `ConfigStoreTests` may
need the new `Scope` argument where `EnvironmentConfig` is constructed positionally — it has a
default, so only positional calls that already pass every argument are affected.

- [ ] **Step 17: Update the README**

In `README.md`, correct every place that says seven services to eight, add the scope question and
the two files to the walkthrough of a run, replace any mention of the `.csv` beside the ledger,
and describe the three tabs. Then check nothing stale is left:

```bash
cd /d/mocd-docfix && grep -n "seven service\|\.csv\|CSV" README.md
```

Expected: no output.

- [ ] **Step 18: Commit**

```bash
cd /d/mocd-docfix
git add -A
git commit -m "feat(scope): choose the eight services or every catalogue, in two files

Asked once, after the environment and before the menu, because every mode needs
the answer. Membership Managment comes back into scope, making seven eight.

The two files hold sets that do not overlap — the services we work on, and every
other — so a document lives in exactly one of them and they can never disagree.
Two files rather than two tabs, because an .xlsx is one zip archive and the cost
of a write follows the file: 11 seconds at 50,000 rows, paid after every
corrected document. A sitting scoped to the eight never opens the other file.

Rows that fall outside the scope are now counted rather than listed. Narrowing
puts tens of thousands in that bucket at once, and a bullet each says nothing a
single number does not."
```

---

## Verification

After Task 8, from a clean checkout:

```bash
cd /d/mocd-docfix
dotnet build
dotnet test
grep -rn "CsvHelper\|RowVerdicts.Skip\|AdjustToContents" src tests --include=*.cs
```

Expected: build succeeds with no warnings, every test passes, and the grep returns nothing.

Then a manual pass against dev, with the VPN up:

1. `bin\docfix\docfix.exe`, pick dev — the scope question appears before the menu, defaulting to the eight services.
2. Repair run → it asks whether to use the ledger as it is or update from CRM.
3. Update from CRM → it reports rows removed as correct-and-untouched, and the count of documents with no file path.
4. Open the ledger: three tabs, columns readable, no `.csv` beside it.
5. Set a finished row's verdict to `ignore` in Excel, save, close, and re-enter the repair run — the guard asks about it and names what Delete old files will really do.
6. In the work menu, choose "Read the sheet again" after editing a verdict — the count changes.
