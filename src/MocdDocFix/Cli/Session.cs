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
    private readonly DocumentTypeDecisions _typeDecisions;
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
    private readonly DocumentTypeCheck _typeCheck;
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

    /// <param name="ado">
    /// The DevOps backlog, for the scan's third opinion. Null leaves the scan exactly as it was.
    /// </param>
    public Session(AppConfig appConfig, ResolvedEnvironment env, string envName,
        IPrompts prompts, bool dryRun, IAdoClient? ado = null)
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

        // The decisions file sits beside config.json rather than under the environment: a
        // document type belongs to the same service in dev as it does in production, and being
        // asked the same awkward question again after switching environment would be absurd.
        _typeDecisions = new DocumentTypeDecisions(
            Path.Combine(ConfigStore.DefaultDirectory, "document-types.json"));

        // Held on the session, not built inside the scan, because the upload step asks it again
        // for each document it is about to move — with the same per-type cache, so asking twice
        // costs one query.
        _typeCheck = new DocumentTypeCheck(ado, _typeDecisions, prompts,
            appConfig.Ado.LocalCopy, appConfig.Ado.DropFolder);

        _scan = new ScanCommand(_read, _reporter, env.CrmUrl, appConfig.ServiceCatalogues,
            new GroupedReportWriter(runRoot),
            new GuidListWriter(runRoot),
            _typeCheck,
            _docReports, _backups);
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
    /// <summary>
    /// Where the backlog stands on one document type. Handed to the upload step so it can ask
    /// again for each document it is about to move, against the same per-type cache and the same
    /// saved decisions the scan used — so a second look costs nothing and cannot contradict the
    /// first by accident.
    /// </summary>
    private Task<TypeRuling> CheckTypeAsync(string? documentType, string? crmService, CancellationToken ct) =>
        _typeCheck.RuleOnAsync(documentType, crmService, ct);

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

            // Every document that is wrong, one at a time, the way a targeted run shows them —
            // so a full run can be read and argued with rather than only counted.
            WriteEachDocument(result);

            var details = result.Banner().Split(Environment.NewLine).ToList();

            if (result.DevOpsSummary() is { } devops)
            {
                details.Add("");
                details.Add(devops);
                foreach (var name in result.DevOpsCouldNotTell().Take(10))
                    details.Add($"  could not be told about: {name}");
            }

            details.Add("");
            details.Add($"grouped report → {result.GroupsPath}");
            details.Add($"GUIDs by group → {result.GuidsPath}");

            if (result.PerDocumentReports > 0)
            {
                details.Add("");
                details.Add($"{result.PerDocumentReports} document(s) have their own folder, " +
                            "each holding 01-check.txt:");

                foreach (var row in result.Fix.Concat(result.Review).Take(10))
                    details.Add($"  {Path.Combine(_docReports.FolderFor(row.DocumentId, row.FileName), "01-check.txt")}");

                if (result.PerDocumentReports > 10)
                    details.Add($"  … and {result.PerDocumentReports - 10} more");
            }

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
            }, _docReports).RunAsync(_envName, identifiers, forceReview: false, _env.IsProduction, ct);

            // The hashes come back with the summary rather than being fetched again. Re-resolving
            // each document was a CRM round trip per document for something already in hand, and
            // it ran after the report was written — so a hiccup there threw away a finished check
            // and left the operator with "an error occurred while sending the request".
            foreach (var document in summary.Documents ?? Array.Empty<DocumentRow>())
                _hashes[document.DocumentFileId] = document.Hash;

            // Each document gets its own folder, and everything about it lands there as the
            // later steps run.
            foreach (var row in _pending)
                DocumentRecord.WriteHeader(_backups, row, _docReports);

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

            var summary = await new BackupCommand(_files, _read, _backups, _state, _reporter, HashOf, (m, tone) => _prompts.Say(m, tone), _docReports)
                .RunAsync(_envName, _pending, ct);

            return StepOutcome.Of(
                $"{summary.Saved} saved, {summary.Quarantined} quarantined, {summary.Skipped} skipped.",
                $"{summary.TotalBytes / 1_048_576:N0} MB downloaded",
                $"one folder per document under {summary.BackupRoot}");
        },

        MigrateAsync: async () =>
        {
            var summary = await new MigrateCommand(_files, _read, _write, _backups, _state, _reporter,
                    _prompts, _opener, _env.CrmUrl, DeleteOneAsync, _repointedList, _docReports,
                    CheckTypeAsync)
                .RunAsync(_envName, ct);

            // Each document's own account first — that is where the detail is, and it is the
            // folder the operator wants open. The whole-run files are an index across them.
            var details = new List<string>();

            foreach (var row in summary.Rows.Take(10))
                details.Add($"{Path.Combine(_docReports.FolderFor(row.DocumentId), "03-upload-repoint.txt")}");
            if (summary.Rows.Count > 10) details.Add($"… and {summary.Rows.Count - 10} more");

            if (summary.Rows.Count > 0) details.Add("");

            // A bare count of skips reads as a shortfall whatever caused it. Saying why turns
            // "5 skipped" into "5 were done last time", which is not the same news at all.
            foreach (var skip in summary.Skips ?? Array.Empty<SkipTally>())
                details.Add($"skipped: {skip.Count} {skip.Why}");

            if ((summary.Skips?.Count ?? 0) > 0) details.Add("");

            details.Add($"index of new files → {summary.RepointedPath}");
            details.Add($"index of the run   → {summary.ReportPath}");

            // Only said when it is true. Printing it unconditionally contradicted the very next
            // line of a run whose old files were removed as each document was repointed.
            var left = summary.Migrated - summary.OldFilesRemoved;
            if (summary.OldFilesRemoved > 0 && left <= 0)
                details.Add($"each old file was removed as its document was repointed " +
                            $"({summary.OldFilesRemoved} in all)");
            else if (summary.OldFilesRemoved > 0)
                details.Add($"{summary.OldFilesRemoved} old file(s) removed here; {left} still in place");
            else if (summary.Migrated > 0)
                details.Add("the old files and their CRM records are still in place");

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

            // The per-document file is the report; the whole-run one is an index over them.
            var details = new List<string>();

            foreach (var verdict in summary.Verdicts.Take(10))
                details.Add(Path.Combine(
                    _docReports.FolderFor(verdict.DocumentId, verdict.FileName), "05-final-check.txt"));
            if (summary.Verdicts.Count > 10) details.Add($"… and {summary.Verdicts.Count - 10} more");

            details.Add("");
            details.Add($"index of all of them → {summary.ReportPath}");

            foreach (var bad in summary.Verdicts.Where(v => !v.Ok).Take(10))
            {
                details.Add("");
                details.Add($"  {bad.FileName}");
                foreach (var p in bad.Problems) details.Add($"      {p}");
            }

            return new StepOutcome(
                summary.WithProblems == 0
                    ? $"{summary.Checked} checked, all correct."
                    : $"{summary.Checked} checked, {summary.Ok} correct, {summary.WithProblems} WITH PROBLEMS.",
                details);
        },

        // The closing question, asked of the OLD file for every document the run touched: the
        // same check the menu's "Is this file still there?" runs, so a run ends by proving what
        // it claims rather than reporting its own belief.
        OldFileCheckAsync: async () =>
        {
            var summary = await new OldFileCheckCommand(
                    new LookupCommand(_files, _read, _backups, _state, _prompts),
                    _backups, _state, _prompts, _docReports)
                .RunAsync(ct);

            if (summary.Checked == 0)
                return StepOutcome.Of("No old files to ask about — no document got as far as " +
                                      "having a new one.");

            var gone = summary.Results.Count(r => r.State == MigrationState.Deleted && r.AsExpected);
            var waiting = summary.Results.Count(r => r.State == MigrationState.Repointed && r.AsExpected);

            var headline = $"{summary.Checked} old file(s) asked about — {gone} correctly gone " +
                           $"from the file server and CRM";
            if (waiting > 0) headline += $", {waiting} still there awaiting the delete step";
            headline += summary.NotAsExpected > 0
                ? $", {summary.NotAsExpected} NOT AS EXPECTED."
                : ".";

            var details = new List<string>();
            foreach (var wrong in summary.Results.Where(r => !r.AsExpected).Take(10))
            {
                details.Add($"  {wrong.FileName}");
                details.Add($"      {wrong.Verdict}");
            }

            if (summary.NotAsExpected == 0)
                details.Add("both systems agree with what the run recorded, for every document");

            details.Add("");
            details.Add("each document's answer is in its own folder, in 06-old-file.txt");

            return new StepOutcome(headline, details);
        },

        // Counted before the delete step is offered, so a run whose old files were removed as it
        // went does not walk through that step with nothing in it.
        AwaitingDeleteAsync: () =>
            new DeleteCommand(_files, _read, _write, _backups, _state, _prompts, _docReports)
                .AwaitingDeletionAsync(ct),

        LookAsync: async asked =>
        {
            var reports = await new LookupCommand(_files, _read, _backups, _state, _prompts)
                .RunAsync(asked, ct);

            var details = reports.Select(Summarise).ToList();

            return new StepOutcome($"{reports.Count} looked up. Nothing was changed.", details);
        });

    /// <summary>
    /// How many documents a full scan shows in full before it stops listing. A run over the
    /// whole population can turn up more than anyone will read on one screen; past this the
    /// grouped report and the per-document folders are the better way in.
    /// </summary>
    private const int MostWeWillList = 30;

    /// <summary>
    /// Each broken document, printed the way a targeted run prints it. A full run that shows
    /// only totals gives the operator nothing to disagree with — and being able to read the
    /// console and say "that one is wrong" is the whole point of step 1.
    /// </summary>
    private void WriteEachDocument(ScanResult result)
    {
        var interesting = result.Fix.Concat(result.Review).ToList();
        if (interesting.Count == 0) return;

        _prompts.Blank();
        _prompts.Section($"{interesting.Count} document(s) to look at", Tone.Normal);
        _prompts.Say("Each one below, with what CRM says, what DevOps says, and what will be " +
                     "done about it. Nothing has been changed.", Tone.Muted);

        foreach (var row in interesting.Take(MostWeWillList))
            CheckLines.Write(_prompts, row);

        if (interesting.Count > MostWeWillList)
        {
            _prompts.Blank();
            _prompts.Say($"… and {interesting.Count - MostWeWillList} more, not listed here. " +
                         "Every one of them has its own folder with 01-check.txt in it, and the " +
                         "grouped report below covers them all.", Tone.Muted);
        }
    }

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

            var backup = await new BackupCommand(_files, _read, _backups, _state, _reporter, HashOf, (m, tone) => _prompts.Say(m, tone), _docReports)
                .RunAsync(_envName, rows, ct);

            if (!gate.Ask("2", "Backup",
                    $"{backup.Saved} saved, {backup.Quarantined} quarantined, {backup.Skipped} skipped.",
                    new[] { $"one folder per document under {backup.BackupRoot}" },
                    "upload the corrected copies — the first step that WRITES"))
                return 0;

            var migrated = await new MigrateCommand(_files, _read, _write, _backups, _state, _reporter,
                    _prompts, _opener, _env.CrmUrl, DeleteOneAsync, _repointedList, _docReports,
                    CheckTypeAsync)
                .RunAsync(_envName, ct);

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

                var summary = await new BackupCommand(_files, _read, _backups, _state, _reporter, HashOf, (m, tone) => _prompts.Say(m, tone), _docReports)
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
                        _reporter, _prompts, _opener, _env.CrmUrl, DeleteOneAsync, _repointedList,
                        _docReports, CheckTypeAsync)
                    .RunAsync(_envName, ct);

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
