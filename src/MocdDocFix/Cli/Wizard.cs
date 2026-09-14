using MocdDocFix.Domain;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>What a phase reports back, so the wizard can show it before asking to continue.</summary>
public sealed record StepOutcome(string Headline, IReadOnlyList<string> Details)
{
    public static StepOutcome Of(string headline, params string[] details) => new(headline, details);
}

/// <summary>One row the operator can point at without typing a GUID.</summary>
public sealed record PickableFile(
    int Group,
    string Identifier,
    string? FileName,
    string ServiceName,
    string DocumentTypeName);

public sealed record ScanOutcome(
    string Headline,
    IReadOnlyList<string> Details,
    IReadOnlyList<PickableFile> Fixable);

/// <param name="ScanAsync">Classify everything in scope. Reads only.</param>
/// <param name="ClassifyAsync">
/// Classify just these identifiers. Reads only — it resolves and reports, and queues nothing.
/// Kept separate from <paramref name="BackupAsync"/> so a targeted run gets the same stop
/// between every step that a full run does.
/// </param>
/// <param name="BackupAsync">Back up whatever the last scan or check found fixable.</param>
/// <param name="LookAsync">
/// Ask both systems about particular files — by path or by documentfile id — and say what each
/// one holds. Reads only.
/// </param>
public sealed record WizardActions(
    Func<Task<ScanOutcome>> ScanAsync,
    Func<IReadOnlyList<string>, Task<ScanOutcome>> ClassifyAsync,
    Func<Task<StepOutcome>> BackupAsync,
    Func<Task<StepOutcome>> MigrateAsync,
    Func<Task<StepOutcome>> DeleteAsync,
    Func<Task<StepOutcome>> VerifyAsync,
    Func<IReadOnlyList<string>, Task<StepOutcome>>? LookAsync = null,

    /// <summary>
    /// How many old files still need removing. Asked before the delete step, so a run that
    /// removed them as it went is not walked through that step — and asked to confirm it — twice.
    /// </summary>
    Func<Task<int>>? AwaitingDeleteAsync = null,

    /// <summary>
    /// Asks both systems about the OLD file of every document the run touched — the same check
    /// as <see cref="LookAsync"/>, run unasked at the end of a run so it closes on what is true
    /// rather than on what the steps believed.
    /// </summary>
    Func<Task<StepOutcome>>? OldFileCheckAsync = null);

public enum WizardExit { Finished, ChangeEnvironment }

/// <summary>
/// The guided front end: one question at a time, a gate between every step, and nothing that
/// touches the file server or CRM without being announced first (spec 2026-09-13 section 6).
/// </summary>
public sealed class Wizard
{
    private readonly IPrompts _prompts;
    private readonly Asker _asker;
    private readonly StepGate _gate;
    private readonly string _envName;
    private readonly bool _isProduction;
    private readonly string _crmUrl;
    private readonly string _fileServerUrl;
    private readonly WizardActions _actions;

    private ScanOutcome? _lastScan;

    public Wizard(IPrompts prompts, string envName, bool isProduction,
        string crmUrl, string fileServerUrl, WizardActions actions)
    {
        _prompts = prompts;
        _asker = new Asker(prompts);
        _gate = new StepGate(prompts);
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
            var mode = _asker.Ask("How do you want to work?", new[]
            {
                new Choice("Targeted", "pick specific files and run them one at a time",
                    "The safest way to start. You choose the files — from the last scan, or by " +
                    "typing GUIDs or file names — and each one is backed up, re-uploaded, " +
                    "verified and shown to you before anything in CRM changes."),

                new Choice("Full", "work through every broken file",
                    "Exactly the same steps, checks and questions as a targeted run, over the " +
                    "whole population instead of the files you name. It starts by finding every " +
                    "broken document, shows you each one, and writes the grouped report and the " +
                    "GUID list that a targeted run has no use for."),

                new Choice("Just report", "scan and write the reports, change nothing",
                    "Reads CRM, classifies every document, writes each broken one's check report " +
                    "into its own folder along with the grouped report and the GUID list, and " +
                    "stops. Nothing is downloaded, uploaded or written."),

                new Choice("Check it all", "ask the file server and CRM what is actually true",
                    "For every document already migrated: is the old file still on the server, " +
                    "is its CRM record still there, is the new file where it should be, and does " +
                    "the document point at it. Reads only — it reports, it never repairs."),

                new Choice("Finish off old files", "delete the old files of work already done",
                    "Goes straight to the delete step for documents that have already been " +
                    "migrated — no scanning, no uploading. It asks CRM first which documents are " +
                    "genuinely finished, so one whose migration succeeded but was recorded as " +
                    "failed is picked up here instead of being left behind for good."),

                new Choice("Is this file still there?", "check one path or documentfile id",
                    "Give it an old file path, or the id of a mocd_documentfile, and it asks " +
                    "the file server whether the file is still on disk and CRM whether any " +
                    "record still refers to it — and says what this tool's own backup and state " +
                    "notes have for it. It changes nothing, in either system.",
                    Enabled: _actions.LookAsync is not null,
                    DisabledNote: "this build was not given a look-up action."),

                new Choice("Change environment", $"currently {_envName}"),

                new Choice("Quit", "stop here")
            }, defaultIndex: 0, allowBack: false, confirm: true);

            switch (mode.Kind == AnswerKind.Chosen ? mode.Index : 7)
            {
                case 0: await TargetedAsync(ct); break;
                case 1: await FullAsync(ct); break;
                case 2: await ReportOnlyAsync(); break;
                case 3: await VerifyOnlyAsync(); break;
                case 4: await DeleteOnlyAsync(); break;
                case 5: await LookUpAsync(); break;
                case 6: return WizardExit.ChangeEnvironment;
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

    // ---- modes ----

    private async Task ReportOnlyAsync()
    {
        var scan = await ScanAsync();
        Report("1", "Scan", scan.Headline, scan.Details);

        _prompts.Blank();
        _prompts.Say("Nothing was changed. The reports are on disk whenever you want them.",
            Tone.Good);
    }

    private async Task FullAsync(CancellationToken ct)
    {
        await PipelineAsync(await ScanAsync(), "1", "Scan", ct);
    }

    private async Task VerifyOnlyAsync()
    {
        _prompts.Section("Check it all");
        _prompts.Say("Asking the file server and CRM about every document already migrated.");
        _prompts.Say("This reads only — nothing is changed, whatever it finds.", Tone.Muted);

        var verify = await _actions.VerifyAsync();
        Report("", "Final check", verify.Headline, verify.Details);
    }

    /// <summary>
    /// The delete step on its own. A document can be finished in CRM and still have its old file
    /// on the server — a run that halted after repointing, or a fix made by hand. Without a way
    /// in here, the only route to the delete step was a full cycle, and a document the scan now
    /// calls correct never appears in one. So it could never be cleaned up.
    /// </summary>
    private async Task DeleteOnlyAsync()
    {
        _prompts.Section("Finish off old files");
        _prompts.Say("This goes straight to the delete step. Nothing is scanned or uploaded.");
        _prompts.Blank();
        _prompts.Bullet("First it asks CRM about every backed-up document, and corrects its own " +
                        "notes where they disagree — that read changes nothing.", Tone.Muted);
        _prompts.Bullet("Then it lists what is eligible and asks before deleting anything.",
            Tone.Muted);

        // No step number here: this is one action on its own, not the fifth of five, and
        // "Step 5 of 5" in a run with a single step invents four that never happened.
        if (!ConfirmDelete(standalone: true)) return;

        var delete = await _actions.DeleteAsync();
        Report("", "Delete old files", delete.Headline, delete.Details);

        _prompts.Blank();
        _prompts.Say("Confirming with the file server and CRM. Reads only.", Tone.Muted);
        var verify = await _actions.VerifyAsync();
        Report("", "Final check", verify.Headline, verify.Details);
    }

    /// <summary>
    /// One question, for particular files: is it still there? Everything else in the tool works
    /// on a population and wants to change something; this reads, about one file at a time.
    /// </summary>
    private async Task LookUpAsync()
    {
        if (_actions.LookAsync is not { } look) return;

        _prompts.Section("Is this file still there?");
        _prompts.Say("Give an old file path, or the id of a mocd_documentfile record. Several " +
                     "at once, separated by commas. For example:");
        _prompts.Blank();
        _prompts.Info(@"      DigitalServices\20260405\35687738-986f-413d-8846-6dc1a720a1ec.png",
            Tone.Muted);
        _prompts.Info("      2a1c51a3-e330-f111-b119-005056010908", Tone.Muted);
        _prompts.Blank();
        _prompts.Say("It asks the file server and CRM, and changes nothing in either.", Tone.Muted);
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

        var outcome = await look(asked);
        Report("", "Look-up", outcome.Headline, outcome.Details);
    }

    private async Task TargetedAsync(CancellationToken ct)
    {
        var identifiers = await ChooseFilesAsync();
        if (identifiers.Count == 0) return;

        _prompts.Section($"{identifiers.Count} file(s) chosen");
        foreach (var id in identifiers.Take(20)) _prompts.Info($"    {id}", Tone.Muted);
        if (identifiers.Count > 20)
            _prompts.Info($"    … and {identifiers.Count - 20} more", Tone.Muted);

        _prompts.Blank();
        _prompts.Say("Checking them against CRM first. This reads only.", Tone.Muted);

        await PipelineAsync(await _actions.ClassifyAsync(identifiers), "1", "Check", ct);
    }

    /// <summary>
    /// The five steps, with a stop between every one. Targeted and full runs share this exactly,
    /// so neither can ever execute two phases on one answer (spec 2026-09-13 section 6.4).
    /// </summary>
    private async Task PipelineAsync(ScanOutcome scan, string firstStep, string firstName, CancellationToken ct)
    {
        var count = scan.Fixable.Count;

        if (count == 0)
        {
            _prompts.Blank();
            _prompts.Say("Nothing here needs fixing. Stopping — no file server call, no writes.",
                Tone.Good);
            return;
        }

        if (!Gate(firstStep, firstName, scan.Headline, scan.Details, $"back up {Files(count)}"))
            return;

        if (!ConfirmFileServer(
                $"Backing up downloads {Files(count)} from the file server and saves a full " +
                "copy, with the CRM records, on this machine. It writes nothing anywhere else."))
            return;

        var backup = await _actions.BackupAsync();
        if (!Gate("2", "Backup", backup.Headline, backup.Details,
                "upload the corrected copies — the first step that WRITES"))
            return;

        if (!ConfirmMigrate()) return;

        var migrate = await _actions.MigrateAsync();

        // Step 3 and 4 offers to remove each old file the moment its document is repointed, and
        // an operator who takes that offer every time has already finished step 5. Asking them
        // to confirm a delete, twice, for work that is done — and then reporting "nothing is
        // awaiting deletion" as though something had gone wrong — is three questions and a
        // false alarm about a run that went perfectly.
        var awaiting = _actions.AwaitingDeleteAsync is { } count2 ? await count2() : -1;

        if (awaiting == 0)
        {
            Report("3 and 4", "Upload, verify and repoint", migrate.Headline, migrate.Details);

            _prompts.Blank();
            _prompts.Say("Every old file was removed as its document was repointed, so there is " +
                         "nothing left for the delete step.", Tone.Good);
        }
        else
        {
            if (!Gate("3 and 4", "Upload, verify and repoint", migrate.Headline, migrate.Details,
                    awaiting > 0
                        ? $"delete {Files(awaiting)} — IRREVERSIBLE"
                        : "delete the old files — IRREVERSIBLE"))
                return;

            if (!ConfirmDelete()) return;

            var delete = await _actions.DeleteAsync();
            Report("5", "Delete old files", delete.Headline, delete.Details);
        }

        // Always finish by asking both systems what is actually true, rather than trusting the
        // five steps that just ran.
        _prompts.Blank();
        _prompts.Say("Checking the file server and CRM to confirm everything landed. Reads only.", Tone.Muted);
        var verify = await _actions.VerifyAsync();
        Report("", "Final check", verify.Headline, verify.Details);

        // And then the old file itself, one document at a time. The final check compares the
        // run against its own records; this asks the two systems the plain question an operator
        // would ask by hand afterwards — is the old file off the server, and is its record out
        // of CRM — which is the only answer that settles it.
        if (_actions.OldFileCheckAsync is { } askAboutOldFiles)
        {
            _prompts.Blank();
            _prompts.Say("Last, the old files. Asking the file server and CRM about each one by " +
                         "name — the same check as \"Is this file still there?\" in the menu. " +
                         "Reads only.", Tone.Muted);

            var oldFiles = await askAboutOldFiles();
            Report("", "The old files", oldFiles.Headline, oldFiles.Details);
        }

        _ = ct;
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"all {count} files";

    // ---- choosing files ----

    private async Task<IReadOnlyList<string>> ChooseFilesAsync()
    {
        var how = _asker.Ask("How do you want to pick the files?", new[]
        {
            new Choice("From the last scan", Describe(_lastScan),
                "Lists the broken files grouped by the kind of corruption, so you can point at " +
                "them by number instead of copying GUIDs."),
            new Choice("Type them", "document GUID, document-file GUID, or file name",
                "Separate several with commas. A file name is matched against mocd_name on the " +
                "document file record.")
        }, defaultIndex: 0);

        if (how.Kind != AnswerKind.Chosen) return Array.Empty<string>();

        return how.Index == 0 ? await FromScanAsync() : Typed();
    }

    private string Describe(ScanOutcome? scan) =>
        scan is null ? "runs a scan first, then lists them" : $"{scan.Fixable.Count} files, grouped";

    private IReadOnlyList<string> Typed()
    {
        _prompts.Section("Type the documents to work on");
        _prompts.Say("One or more, separated by commas. A document GUID, a document-file GUID, " +
                     "or a file name. For example:");
        _prompts.Info("      2c9d5572-a77b-f111-b10f-00505601095a, cert.jpg", Tone.Muted);
        _prompts.Blank();

        return _prompts.ReadLine("Documents")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> FromScanAsync()
    {
        var scan = _lastScan ?? await ScanAsync();

        if (scan.Fixable.Count == 0)
        {
            _prompts.Info("");
            _prompts.Info("The scan found nothing that needs fixing.");
            return Array.Empty<string>();
        }

        while (true)
        {
            var groups = scan.Fixable
                .GroupBy(f => f.Group)
                .OrderBy(g => g.Key)
                .ToList();

            var choices = groups
                .Select(g => new Choice($"Group {g.Key}", $"[{g.Count(),4}]  {Label(g.Key)}",
                    Explain(g.Key)))
                .ToList();

            var pick = _asker.Ask("Which kind of problem do you want to work on?", choices);
            if (pick.Kind != AnswerKind.Chosen) return Array.Empty<string>();

            var chosen = groups[pick.Index].ToList();
            var files = PickWithin(chosen, groups[pick.Index].Key);
            if (files.Count > 0) return files;

            // Nothing selected — fall back to the group question rather than dropping out.
        }
    }

    private IReadOnlyList<string> PickWithin(IReadOnlyList<PickableFile> files, int group)
    {
        _prompts.Section($"Group {group} — {Label(group)} — {files.Count} file(s)");
        _prompts.Blank();

        // Columns add up to the wrap width, so nothing folds onto a second line and the numbers
        // stay in one place however long a service name happens to be.
        var header = $"   {"#",4}  {"File",-30}  {"Service",-16}  {"Document type",-16}";
        _prompts.Info(header.TrimEnd(), Tone.Muted);
        _prompts.Info("   " + new string('─', header.Length - 3), Tone.Muted);

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            _prompts.Info($"   {i + 1,4}  {Trim(f.FileName ?? "(no name)", 30)}  " +
                          $"{Trim(f.ServiceName, 16)}  {Trim(f.DocumentTypeName, 16)}");
        }

        _prompts.Blank();
        _prompts.Say("Type numbers (1,3,5), a range (1-10), all, or b to go back.", Tone.Muted);

        while (true)
        {
            var typed = _prompts.ReadLine("  Which files").Trim();

            if (typed.Equals("b", StringComparison.OrdinalIgnoreCase) ||
                typed.Equals("back", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();

            if (typed.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                typed.Equals("quit", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();

            var result = Selection.Parse(typed, files.Count);
            if (result.Error is null)
                return result.Indexes.Select(i => files[i].Identifier).ToList();

            _prompts.Info($"  {result.Error}");
        }
    }

    private static string Label(int group) => DocumentGroups.Get(group).ShortLabel;

    private static string Explain(int group)
    {
        var g = DocumentGroups.Get(group);
        return $"What is in the path: {g.WhatIsInThePath} " +
               $"Why it is wrong: {g.WhyItIsWrong} " +
               $"What the tool does: {g.WhatTheToolDoes}";
    }

    private static string Trim(string value, int width) =>
        value.Length <= width ? value.PadRight(width) : value[..(width - 1)] + "…";

    // ---- running and gating ----

    /// <summary>
    /// Runs the scan but does not report it — whoever asked for it decides how to present it, so
    /// the step-1 heading is printed exactly once.
    /// </summary>
    private async Task<ScanOutcome> ScanAsync()
    {
        _prompts.Blank();
        _prompts.Say("Reading CRM. This writes nothing.", Tone.Muted);

        _lastScan = await _actions.ScanAsync();
        return _lastScan;
    }

    private void Report(string step, string name, string headline, IReadOnlyList<string> details) =>
        _gate.Report(step, name, headline, details);

    /// <returns>True to carry on to the next step.</returns>
    private bool Gate(string step, string name, string headline, IReadOnlyList<string> details, string next) =>
        _gate.Ask(step, name, headline, details, next);

    // ---- the confirmations that were already there, kept on top of the gates ----

    private bool ConfirmFileServer(string what)
    {
        _prompts.Section("Step 2 of 5 — Backup", Tone.Normal);
        _prompts.Say(what);
        _prompts.Blank();
        _prompts.Field("File server", _fileServerUrl, Tone.Muted);
        _prompts.Blank();
        return _prompts.YesNo("  Contact the file server now?", defaultYes: false);
    }

    private bool ConfirmMigrate()
    {
        _prompts.Section("Steps 3 and 4 of 5 — Upload and repoint", Tone.Warn);
        _prompts.Say("This uploads a corrected copy of every backed-up file and repoints CRM at it.");
        _prompts.Blank();
        _prompts.Warn("It contacts the file server, and it WRITES to CRM.");
        _prompts.Blank();
        _prompts.Bullet("You will still be shown both files and asked before each document is " +
                        "repointed.", Tone.Muted);
        _prompts.Bullet("The old files are not touched — this stays reversible until you run " +
                        "Delete.", Tone.Muted);
        _prompts.Blank();
        return _prompts.YesNo("  Start uploading?", defaultYes: false, Tone.Warn);
    }

    private bool ConfirmDelete(bool standalone = false)
    {
        _prompts.Section(standalone ? "Delete old files" : "Step 5 of 5 — Delete old files", Tone.Danger);
        _prompts.Say("Delete removes the OLD files from the file server and their CRM records.");
        _prompts.Blank();
        _prompts.Warn("This cannot be undone.", Tone.Danger);
        _prompts.Blank();
        _prompts.Bullet("Your local backups keep the bytes, but a restored file gets a new id and " +
                        "today's date folder — it cannot go back to its old path.", Tone.Muted);
        _prompts.Bullet("Only files that were migrated and verified are eligible, and each one is " +
                        "re-checked against CRM immediately before it is deleted.", Tone.Muted);
        _prompts.Blank();
        return _prompts.YesNo("  Go to the delete step?", defaultYes: false, Tone.Danger);
    }
}
