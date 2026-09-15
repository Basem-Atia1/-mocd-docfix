using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Config;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>
/// One environment's worth of wiring: the clients, the stores and the four modes, built once
/// and shared by the wizard and the direct commands so both run exactly the same code.
///
/// Everything here works from one file — the ledger. Each mode re-reads it from disk before it
/// starts, so an edit made in Excel since the last run takes effect; that is the whole point of
/// the design.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly AppConfig _appConfig;
    private readonly ResolvedEnvironment _env;
    private readonly string _envName;
    private readonly IPrompts _prompts;
    private readonly bool _dryRun;

    private readonly HttpClient _crmHttp;
    private readonly HttpClient _fileHttp;
    private readonly CrmReadClient _read;
    private readonly CrmWriteClient _write;
    private readonly FileServiceClient _files;
    private readonly BackupStore _backups;
    private readonly ShellFileOpener _opener = new();

    private readonly LedgerStore _ledger;
    private readonly ChangeJournal _journal;
    private readonly ErrorLog _errors;
    private readonly LedgerBuilder _builder;

    public Session(AppConfig appConfig, ResolvedEnvironment env, string envName,
        IPrompts prompts, bool dryRun)
    {
        _appConfig = appConfig;
        _env = env;
        _envName = envName;
        _prompts = prompts;
        _dryRun = dryRun;

        // The backup folder holds the bytes and the crm.json snapshot — the only route back
        // once a correction has overwritten a record. Everything else is one file each.
        _backups = new BackupStore(Path.Combine(appConfig.DataRoot, "backup", envName));

        // One folder per environment, so dev and production can never be read for each other.
        var reports = Path.Combine(appConfig.DataRoot, "reports", envName);

        _ledger = new LedgerStore(Path.Combine(reports, $"repair-{envName}.xlsx"));
        _journal = new ChangeJournal(Path.Combine(reports, $"changes-{envName}.jsonl"));
        _errors = new ErrorLog(Path.Combine(reports, $"errors-{envName}.txt"));

        _crmHttp = CrmHttp.Create(env);
        _read = new CrmReadClient(_crmHttp, env);
        _write = new CrmWriteClient(_crmHttp);

        _fileHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _files = new FileServiceClient(_fileHttp, env);

        _builder = new LedgerBuilder(_read, appConfig.ServiceCatalogues, env.CrmUrl);
    }

    public void Dispose()
    {
        _crmHttp.Dispose();
        _fileHttp.Dispose();
    }

    // ---- opening the ledger ----

    /// <summary>
    /// The rows every mode works from. Re-read from disk each time, so a hand edit since the
    /// last run takes effect.
    /// </summary>
    /// <param name="mayRebuild">
    /// True only for the repair run — it is the one mode allowed to go and ask CRM. The others
    /// work from what is already there, because a mode that silently rebuilt the ledger would
    /// discard the verdicts the operator had typed into it.
    /// </param>
    private async Task<IReadOnlyList<LedgerRow>> OpenLedgerAsync(bool mayRebuild, CancellationToken ct)
    {
        // Excel holds an exclusive lock on an open workbook, and the ledger is rewritten after
        // every completed row. Finding that out now is far kinder than finding out after the
        // first document has already been uploaded and cannot be recorded.
        //
        // It waits rather than giving up: having the ledger open is the normal thing to be doing
        // a moment before a run, and making the operator start the whole mode again to fix a
        // ten-second problem is a punishment, not a safeguard.
        while (!_ledger.CanWrite())
        {
            _prompts.Blank();
            _prompts.Warn("The ledger is open in Excel, so this run could not record what it did.",
                Tone.Warn);
            _prompts.Field("file", _ledger.Path, Tone.Muted);
            _prompts.Blank();

            if (!_prompts.YesNo("  Close it in Excel, then answer yes to carry on. Try again?",
                    defaultYes: true))
            {
                _prompts.Say("Stopped. Nothing has been changed.", Tone.Muted);
                return Array.Empty<LedgerRow>();
            }
        }

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

            if (_prompts.YesNo("  Carry on with it?", defaultYes: true)) return existing;

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
        var fixable = rows.Count(r => r.Verdict2() == RowVerdict.Fix);

        var how = new Asker(_prompts).Ask("What do you want to work on?", new[]
        {
            new Choice("From the file", $"every row marked fix — {fixable} of {rows.Count}",
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

        return answer.Kind == AnswerKind.Chosen ? (WatchMode)answer.Index : WatchMode.Quiet;
    }

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

    // ---- the four modes ----

    public LedgerActions Actions(CancellationToken outer) => new(
        RepairAsync: async ct =>
        {
            var all = await OpenLedgerAsync(mayRebuild: true, ct);
            if (all.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            var rows = NarrowToOneDocument(all);
            if (rows.Count == 0) return StepOutcome.Of("Nothing to work on.");

            if (_dryRun)
                return StepOutcome.Of($"Dry run — {rows.Count} row(s) would be worked on.",
                    $"ledger → {_ledger.Path}");

            WarnAboutInPlace();

            var progress = new RunProgress(_prompts, AskHowCloselyToWatch());

            var summary = await new RepairRun(_ledger,
                    new RepairOneRow(_files, _read, _write, _backups, _journal, _prompts,
                        _opener, progress, _envName),
                    progress, _prompts, _errors)
                .RunAsync(rows, all, ct);

            var details = new List<string>
            {
                $"ledger → {_ledger.Path}   (edit this one)",
                $"copy   → {_ledger.CsvPath}   (plain text, regenerated)"
            };

            if (_ledger.LastCsvProblem is { } stale) details.Add($"NOTE: {stale}");

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

            if (_dryRun) return StepOutcome.Of("Dry run: delete is irreversible, so nothing was done.");

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

            if (_dryRun) return StepOutcome.Of("Dry run: redo writes to CRM, so nothing was done.");

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
            var reports = await new LookupCommand(_files, _read, _backups, _prompts, _ledger)
                .RunAsync(asked, outer);

            return new StepOutcome($"{reports.Count} looked up. Nothing was changed.",
                reports.Select(Summarise).ToList());
        });

    /// <summary>One line per look-up, for the summary under the step.</summary>
    private static string Summarise(LookupReport report)
    {
        // A system that never answered is not a system that said no.
        if (report.CrmProblem is { } crm) return $"{report.Asked} — CRM could not be asked: {crm}";
        if (report.ServerProblem is { } server)
            return $"{report.Asked} — the file server could not be asked: {server}";

        var onDisk = report.OnTheServer switch
        {
            true => "on the file server",
            false => "NOT on the file server",
            _ => "no path to check"
        };

        var inCrm = report.Record is not null || report.PointingAtThePath.Count > 0
            ? "still referred to in CRM"
            : "not referred to in CRM";

        return $"{report.Asked} — {onDisk}, {inCrm}";
    }

    // ---- the direct commands ----

    public async Task<int> RunDirectAsync(CommandLineOptions options, CancellationToken ct)
    {
        var actions = Actions(ct);

        var run = options.Command switch
        {
            "repair" => actions.RepairAsync,
            "delete" => actions.DeleteAsync,
            "redo" => actions.RedoAsync,
            "check" => actions.CheckAsync,
            _ => null
        };

        if (run is null)
        {
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return 2;
        }

        var outcome = await run(ct);

        Console.WriteLine(outcome.Headline);
        foreach (var line in outcome.Details) Console.WriteLine($"  {line}");

        return 0;
    }

    /// <summary>Only so the wizard can be told where things are. Nothing else reads it.</summary>
    public string LedgerPath => _ledger.Path;

    private AppConfig Config => _appConfig;
}
