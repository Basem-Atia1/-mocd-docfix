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
public sealed record WizardActions(
    Func<Task<ScanOutcome>> ScanAsync,
    Func<IReadOnlyList<string>, Task<ScanOutcome>> ClassifyAsync,
    Func<Task<StepOutcome>> BackupAsync,
    Func<Task<StepOutcome>> MigrateAsync,
    Func<Task<StepOutcome>> DeleteAsync);

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
                    "The same five steps, but over the whole population. Every step still stops " +
                    "and asks before the next one starts, and you still see each file before " +
                    "its document is repointed."),

                new Choice("Just report", "scan and write the files, change nothing",
                    "Reads CRM, classifies every document, writes the CSV and the grouped " +
                    "report, and stops. Nothing is downloaded, uploaded or written."),

                new Choice("Change environment", $"currently {_envName}"),

                new Choice("Quit", "stop here")
            }, defaultIndex: 0, allowBack: false);

            switch (mode.Kind == AnswerKind.Chosen ? mode.Index : 4)
            {
                case 0: await TargetedAsync(ct); break;
                case 1: await FullAsync(ct); break;
                case 2: await ReportOnlyAsync(); break;
                case 3: return WizardExit.ChangeEnvironment;
                default:
                    _prompts.Info("");
                    _prompts.Info("Nothing further was done. Bye.");
                    return WizardExit.Finished;
            }
        }

        return WizardExit.Finished;
    }

    private void Banner()
    {
        _prompts.Info("");
        _prompts.Info("======================================================================");
        _prompts.Info("  MoCD — document file path remediation");
        _prompts.Info("======================================================================");
        _prompts.Info($"  Environment  {_envName}{(_isProduction ? "    *** PRODUCTION ***" : "")}");
        _prompts.Info($"  CRM          {_crmUrl}");
        _prompts.Info($"  File server  {_fileServerUrl}");
        _prompts.Info("");
        _prompts.Info("  Type ? at any question for a fuller explanation, b to go back, q to quit.");
    }

    // ---- modes ----

    private async Task ReportOnlyAsync()
    {
        var scan = await ScanAsync();
        Report("1", "Scan", scan.Headline, scan.Details);

        _prompts.Info("");
        _prompts.Info("Nothing was changed. The reports are on disk whenever you want them.");
    }

    private async Task FullAsync(CancellationToken ct)
    {
        await PipelineAsync(await ScanAsync(), "1", "Scan", ct);
    }

    private async Task TargetedAsync(CancellationToken ct)
    {
        var identifiers = await ChooseFilesAsync();
        if (identifiers.Count == 0) return;

        _prompts.Info("");
        _prompts.Info($"{identifiers.Count} file(s) chosen:");
        foreach (var id in identifiers.Take(20)) _prompts.Info($"    {id}");
        if (identifiers.Count > 20) _prompts.Info($"    … and {identifiers.Count - 20} more");

        _prompts.Info("");
        _prompts.Info("Checking them against CRM first. This reads only.");

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
            _prompts.Info("");
            _prompts.Info("Nothing here needs fixing. Stopping — no file server call, no writes.");
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
        if (!Gate("3 and 4", "Upload, verify and repoint", migrate.Headline, migrate.Details,
                "delete the old files — IRREVERSIBLE"))
            return;

        if (!ConfirmDelete()) return;

        var delete = await _actions.DeleteAsync();
        Report("5", "Delete old files", delete.Headline, delete.Details);

        _prompts.Info("");
        _prompts.Info("All five steps are done.");

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
        _prompts.Info("");
        _prompts.Info("Type one or more, separated by commas. For example:");
        _prompts.Info("   2c9d5572-a77b-f111-b10f-00505601095a, cert.jpg");

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
        _prompts.Info("");
        _prompts.Info($"  Group {group} — {Label(group)} — {files.Count} file(s)");
        _prompts.Info("");

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            _prompts.Info($"   {i + 1,4}  {Trim(f.FileName ?? "(no name)", 34)}  " +
                          $"{Trim(f.ServiceName, 30)}  {Trim(f.DocumentTypeName, 40)}");
        }

        _prompts.Info("");
        _prompts.Info("  Type numbers (1,3,5), a range (1-10), all, or b to go back.");

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
        _prompts.Info("");
        _prompts.Info("Reading CRM. This writes nothing.");

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
        _prompts.Info("");
        _prompts.Info(what);
        _prompts.Info($"It will contact the file server at {_fileServerUrl}.");
        return _prompts.Confirm("Contact the file server now?") == ConfirmChoice.Yes;
    }

    private bool ConfirmMigrate()
    {
        _prompts.Info("");
        _prompts.Info("This uploads a corrected copy of every backed-up file and repoints CRM at it.");
        _prompts.Info("It contacts the file server, and it WRITES to CRM.");
        _prompts.Info("You will still be shown both files and asked before each document is repointed.");
        _prompts.Info("The old files are not touched — this stays reversible until you run Delete.");
        return _prompts.Confirm("Start uploading?") == ConfirmChoice.Yes;
    }

    private bool ConfirmDelete()
    {
        _prompts.Info("");
        _prompts.Info("Delete removes the OLD files from the file server and their CRM records.");
        _prompts.Info("This cannot be undone. Your local backups keep the bytes, but a restored");
        _prompts.Info("file gets a new id and today's date folder — it cannot go back to its old path.");
        _prompts.Info("Only files that were migrated and verified are eligible, and each one is");
        _prompts.Info("re-checked against CRM immediately before it is deleted.");
        return _prompts.Confirm("Go to the delete step?") == ConfirmChoice.Yes;
    }
}
