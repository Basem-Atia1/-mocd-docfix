using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Config;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>
/// One environment's worth of wiring: the clients, the stores and the phases, built once and
/// shared by the wizard and the direct commands so both run exactly the same code.
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
    private readonly Reporter _reporter;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly ScanCommand _scan;
    private readonly ShellFileOpener _opener = new();
    private readonly RepointedListWriter _repointedList;
    private readonly DocumentReportStore _docReports;
    private readonly string _reportsRoot;

    /// <summary>mocd_hash per scanned row, needed by the backup phase's first check.</summary>
    private readonly Dictionary<Guid, string?> _hashes = new();

    /// <summary>
    /// What the next backup will act on: the fixable rows from the last scan, or from the last
    /// targeted check. Holding it here is what lets the wizard put a stop between checking and
    /// backing up instead of running a whole targeted pipeline off one answer.
    /// </summary>
    private IReadOnlyList<ScanRow> _pending = Array.Empty<ScanRow>();

    public Session(AppConfig appConfig, ResolvedEnvironment env, string envName,
        IPrompts prompts, bool dryRun)
    {
        _appConfig = appConfig;
        _env = env;
        _envName = envName;
        _prompts = prompts;
        _dryRun = dryRun;

        var dataRoot = Path.Combine(appConfig.DataRoot, envName);

        // Two roots, kept apart on purpose. The backup root holds the files and everything
        // needed to restore them; the reports root holds only the account of what happened.
        // Both are laid out the same way: <root>\<env>\<file name>__<document id>\
        var backupRoot = Path.Combine(appConfig.DataRoot, "backup", envName);
        var reportsRoot = Path.Combine(appConfig.DataRoot, "reports", envName);

        // Whole-run files go in their own folder, so the reports root holds nothing but one
        // folder per document.
        var runRoot = Path.Combine(reportsRoot, "_whole-run");

        _reporter = new Reporter(runRoot);
        _repointedList = new RepointedListWriter(runRoot);
        _docReports = new DocumentReportStore(reportsRoot);
        _reportsRoot = runRoot;
        _backups = new BackupStore(backupRoot);
        _state = new StateStore(Path.Combine(dataRoot, "state", $"state-{envName}.jsonl"));

        _crmHttp = CrmHttp.Create(env);
        _read = new CrmReadClient(_crmHttp, env);
        _write = new CrmWriteClient(_crmHttp);

        _fileHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _files = new FileServiceClient(_fileHttp, env);

        _scan = new ScanCommand(_read, _reporter, env.CrmUrl, appConfig.ServiceCatalogues,
            new GroupedReportWriter(runRoot),
            new GuidListWriter(runRoot));
    }

    public void Dispose()
    {
        _crmHttp.Dispose();
        _fileHttp.Dispose();
    }

    private string? HashOf(ScanRow row) => _hashes.TryGetValue(row.DocumentFileId, out var h) ? h : null;

    /// <summary>
    /// Deletes one document's old file, using the delete step's own safety checks. Passed to
    /// the migrate step so the operator can remove a file right after repointing it, without a
    /// second implementation of what "safe to delete" means.
    /// </summary>
    private Task<string?> DeleteOneAsync(Guid documentId, CancellationToken ct) =>
        new DeleteCommand(_files, _read, _write, _backups, _state, _prompts, _docReports).DeleteOneAsync(documentId, ct);

    public async Task<ScanResult> ScanAsync(CancellationToken ct)
    {
        var documents = await _read.GetInScopeDocumentsAsync(_appConfig.ServiceCatalogues, ct);
        foreach (var d in documents) _hashes[d.DocumentFileId] = d.Hash;
        return await _scan.ClassifyAsync(documents, _envName, writeReports: true, ct);
    }

    // ---- the wizard's view of each phase ----

    public WizardActions WizardActions(CancellationToken ct) => new(
        ScanAsync: async () =>
        {
            var result = await ScanAsync(ct);
            _pending = result.Fix;

            var details = result.Banner().Split(Environment.NewLine).ToList();
            details.Add("");
            details.Add($"grouped report → {result.GroupsPath}");
            details.Add($"GUIDs by group → {result.GuidsPath}");
            details.Add($"spreadsheet    → {result.ScanPath}");
            details.Add($"needs a human  → {result.ReviewPath}");

            return new ScanOutcome(
                $"{result.Fix.Count} to fix, {result.Review.Count} need a human, " +
                $"{result.Skip.Count} nothing to do, out of {result.TotalInScope}.",
                details, Pickable(result.Fix));
        },

        ClassifyAsync: async identifiers =>
        {
            // The pipeline callback only records what would be acted on. Nothing is downloaded,
            // uploaded or deleted here — the wizard asks before each of those separately.
            var summary = await new TargetedCommand(_read, _scan, _reporter, _prompts, rows =>
            {
                _pending = rows;
                return Task.FromResult(0);
            }).RunAsync(_envName, identifiers, forceReview: false, _env.IsProduction, ct);

            foreach (var row in _pending)
            {
                var document = (await _read.ResolveIdentifierAsync(row.DocumentId.ToString(), ct))
                    .FirstOrDefault();
                if (document is not null) _hashes[document.DocumentFileId] = document.Hash;

                // Each document gets its own folder and its own readable record from step 1,
                // and everything about it lands there as the later steps run.
                DocumentRecord.WriteHeader(_backups, row, _docReports);
            }

            var details = new List<string>();
            foreach (var row in _pending.Take(10))
                details.Add($"{row.FileName}  →  {_docReports.FolderFor(row.DocumentId, row.FileName)}");
            if (_pending.Count > 10) details.Add($"… and {_pending.Count - 10} more");

            return new ScanOutcome(
                $"{_pending.Count} to fix, {summary.Reviewed} need a human, " +
                $"{summary.Skipped} already correct, {summary.NotFound} not found.",
                details, Pickable(_pending));
        },

        BackupAsync: async () =>
        {
            if (_pending.Count == 0)
                return StepOutcome.Of("Nothing to back up — no files are queued.");

            var estimate = _pending.Count * 350_000L;
            var free = BackupStore.FreeSpaceBytes(_appConfig.DataRoot);
            if (free < estimate * 2)
                return StepOutcome.Of("Not enough free disk space with margin — nothing was downloaded.",
                    $"needs roughly {estimate * 2 / 1_048_576:N0} MB, {free / 1_048_576:N0} MB free");

            var summary = await new BackupCommand(_files, _read, _backups, _state, _reporter, HashOf, m => _prompts.Info(m), _docReports)
                .RunAsync(_envName, _pending, ct);

            return StepOutcome.Of(
                $"{summary.Saved} saved, {summary.Quarantined} quarantined, {summary.Skipped} skipped.",
                $"{summary.TotalBytes / 1_048_576:N0} MB downloaded",
                $"one folder per document under {summary.BackupRoot}");
        },

        MigrateAsync: async () =>
        {
            var summary = await new MigrateCommand(_files, _read, _write, _backups, _state, _reporter,
                _prompts, _opener, _env.CrmUrl, DeleteOneAsync, _repointedList, _docReports).RunAsync(_envName, ct);

            var details = new List<string>
            {
                $"new files → {summary.RepointedPath}   (id and CRM link of each new documentfile)",
                $"report    → {summary.ReportPath}",
                "the old files and their CRM records are still in place"
            };
            if (summary.Halted) details.Add($"RUN HALTED: {summary.HaltReason}");

            return new StepOutcome(
                $"{summary.Migrated} migrated, {summary.Skipped} skipped, {summary.Failed} failed.",
                details);
        },

        DeleteAsync: async () =>
        {
            var summary = await new DeleteCommand(_files, _read, _write, _backups, _state, _prompts, _docReports)
                .RunAsync(_envName, _env.IsProduction, ct);

            var details = new List<string>();
            if (summary.Aborted) details.Add($"Aborted: {summary.AbortReason}");

            return new StepOutcome(
                $"{summary.Deleted} deleted, {summary.Refused} refused, {summary.Skipped} skipped.",
                details);
        },

        VerifyAsync: async () =>
        {
            var summary = await new VerifyCommand(_files, _read, _write, _backups, _state,
                _env.CrmUrl, _reportsRoot, _docReports)
                .RunAsync(_envName, ct);

            var details = new List<string> { $"report → {summary.ReportPath}" };
            foreach (var bad in summary.Verdicts.Where(v => !v.Ok).Take(10))
            {
                details.Add($"  {bad.FileName}");
                foreach (var p in bad.Problems) details.Add($"      {p}");
            }

            return new StepOutcome(
                summary.WithProblems == 0
                    ? $"{summary.Checked} checked, all correct."
                    : $"{summary.Checked} checked, {summary.Ok} correct, {summary.WithProblems} WITH PROBLEMS.",
                details);
        });

    private static IReadOnlyList<PickableFile> Pickable(IEnumerable<ScanRow> rows) =>
        rows.Select(r => new PickableFile(r.Group, r.DocumentId.ToString(), r.FileName,
                r.ServiceCatalogueName ?? "(no service)", r.DocumentTypeName))
            .ToList();

    /// <summary>
    /// The command-line targeted run. It asks between every phase, exactly as the wizard does —
    /// one answer must never set off backup, upload and delete in sequence.
    /// </summary>
    private async Task<TargetedSummary> Targeted(
        IReadOnlyList<string> identifiers, bool forceReview, CancellationToken ct) =>
        await new TargetedCommand(_read, _scan, _reporter, _prompts, async rows =>
        {
            foreach (var row in rows)
            {
                var document = (await _read.ResolveIdentifierAsync(row.DocumentId.ToString(), ct))
                    .FirstOrDefault();
                if (document is not null) _hashes[document.DocumentFileId] = document.Hash;
            }

            if (_dryRun) { _prompts.Info("Dry run — stopping before backup."); return 0; }

            var gate = new StepGate(_prompts);

            if (!gate.Ask("1", "Check", $"{rows.Count} file(s) will be fixed.",
                    Array.Empty<string>(), "download and back them up"))
                return 0;

            if (_prompts.Confirm($"Contact the file server at {_env.FileServiceBaseUrl} now?")
                != ConfirmChoice.Yes)
                return 0;

            var backup = await new BackupCommand(_files, _read, _backups, _state, _reporter, HashOf, m => _prompts.Info(m), _docReports)
                .RunAsync(_envName, rows, ct);

            if (!gate.Ask("2", "Backup",
                    $"{backup.Saved} saved, {backup.Quarantined} quarantined, {backup.Skipped} skipped.",
                    new[] { $"one folder per document under {backup.BackupRoot}" },
                    "upload the corrected copies — the first step that WRITES"))
                return 0;

            var migrated = await new MigrateCommand(_files, _read, _write, _backups, _state, _reporter,
                _prompts, _opener, _env.CrmUrl, DeleteOneAsync, _repointedList, _docReports).RunAsync(_envName, ct);

            if (migrated.Migrated == 0) return 0;

            if (!gate.Ask("3 and 4", "Upload, verify and repoint",
                    $"{migrated.Migrated} migrated, {migrated.Skipped} skipped, {migrated.Failed} failed.",
                    new[] { $"report → {migrated.ReportPath}" },
                    "delete the old files — IRREVERSIBLE"))
                return migrated.Migrated;

            await new DeleteCommand(_files, _read, _write, _backups, _state, _prompts, _docReports)
                .RunAsync(_envName, _env.IsProduction, ct);

            return migrated.Migrated;
        }).RunAsync(_envName, identifiers, forceReview, _env.IsProduction, ct);

    // ---- the direct commands ----

    public async Task<int> RunDirectAsync(CommandLineOptions options, CancellationToken ct)
    {
        switch (options.Command)
        {
            case "scan":
            {
                var result = await ScanAsync(ct);
                Console.WriteLine(result.Banner());
                Console.WriteLine();
                Console.WriteLine($"grouped → {result.GroupsPath}   (what is wrong, and why)");
                Console.WriteLine($"guids   → {result.GuidsPath}      (document GUIDs of each group)");
                Console.WriteLine($"scan    → {result.ScanPath}     (reason and solution for every row)");
                Console.WriteLine($"review  → {result.ReviewPath}");
                return 0;
            }

            case "backup":
            {
                var result = await ScanAsync(ct);
                Console.WriteLine(result.Banner());
                Console.WriteLine();
                Console.WriteLine($"grouped → {result.GroupsPath}");
                Console.WriteLine($"guids   → {result.GuidsPath}");
                Console.WriteLine($"scan    → {result.ScanPath}");
                Console.WriteLine();

                var estimate = result.Fix.Count * 350_000L;
                var free = BackupStore.FreeSpaceBytes(_appConfig.DataRoot);
                Console.WriteLine($"{result.Fix.Count} files, roughly {estimate / 1_048_576:N0} MB. " +
                                  $"Free space {free / 1_048_576:N0} MB.");
                if (free < estimate * 2)
                {
                    Console.Error.WriteLine("Not enough free space with margin. Aborting.");
                    return 1;
                }
                if (_dryRun) return 0;

                var summary = await new BackupCommand(_files, _read, _backups, _state, _reporter, HashOf, m => _prompts.Info(m), _docReports)
                    .RunAsync(_envName, result.Fix, ct);
                Console.WriteLine($"Saved {summary.Saved}, quarantined {summary.Quarantined}, " +
                                  $"skipped {summary.Skipped}, {summary.TotalBytes / 1_048_576:N0} MB.");
                Console.WriteLine($"one folder per document under {summary.BackupRoot}");
                return summary.Quarantined > 0 ? 1 : 0;
            }

            case "migrate":
            {
                if (_dryRun) { Console.WriteLine("Dry run: migrate writes, so nothing was done."); return 0; }

                var summary = await new MigrateCommand(_files, _read, _write, _backups, _state,
                    _reporter, _prompts, _opener, _env.CrmUrl, DeleteOneAsync, _repointedList, _docReports).RunAsync(_envName, ct);

                Console.WriteLine($"Migrated {summary.Migrated}, skipped {summary.Skipped}, failed {summary.Failed}.");
                Console.WriteLine($"report → {summary.ReportPath}");
                if (summary.Halted) Console.Error.WriteLine($"RUN HALTED: {summary.HaltReason}");
                return summary.Halted ? 1 : 0;
            }

            case "delete":
            {
                if (_dryRun) { Console.WriteLine("Dry run: delete is irreversible, so nothing was done."); return 0; }

                var summary = await new DeleteCommand(_files, _read, _write, _backups, _state, _prompts, _docReports)
                    .RunAsync(_envName, _env.IsProduction, ct);

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

                var summary = await Targeted(options.Identifiers, options.ForceReview, ct);

                Console.WriteLine();
                Console.WriteLine($"Resolved {summary.Resolved}, fixed {summary.Fixed}, " +
                                  $"needs-a-human {summary.Reviewed}, already-ok {summary.Skipped}, " +
                                  $"not found {summary.NotFound}, name clashes {summary.Ambiguous}.");
                return 0;
            }

            default:
                Console.Error.WriteLine(CommandLineOptions.Usage);
                return 2;
        }
    }
}
