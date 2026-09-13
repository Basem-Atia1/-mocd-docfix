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
    var configFile = Path.Combine(configDir, "config.json");
    if (!File.Exists(configFile))
    {
        configStore.Save(appConfig);
        Console.WriteLine($"Wrote a starter config to {configFile}");
    }

    Console.WriteLine($"Config file:  {configFile}");
    Console.WriteLine($"Secrets file: {Path.Combine(configDir, "secrets.dat")} (DPAPI, current user only)");
    Console.WriteLine();
    Console.WriteLine("Add an environment by editing the config file, then store its secrets with:");
    Console.WriteLine("  docfix config --env dev");

    if (options.Environment is { } envToSet)
    {
        Console.WriteLine();
        var apiKey = prompts.ReadLine($"API key for '{envToSet}' (blank to keep existing)");
        var password = prompts.ReadLine($"CRM password for '{envToSet}' (blank to keep existing)");
        if (apiKey.Length > 0) secrets.Set($"{envToSet}:apiKey", apiKey);
        if (password.Length > 0) secrets.Set($"{envToSet}:crmPassword", password);
        Console.WriteLine($"Stored secrets for '{envToSet}'.");
    }

    return 0;
}

var envName = EnvironmentSelector.Select(prompts, options.Environment, appConfig, options.ConfirmProduction);
if (envName is null) return 1;

ResolvedEnvironment env;
try { env = configStore.Resolve(envName); }
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

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

// Each phase as a callable step, so the guided menu and the direct commands run the same code.
async Task RunScanAsync()
{
    var result = await ScanAsync();
    Console.WriteLine(result.Banner());
    Console.WriteLine();
    Console.WriteLine($"scan   → {result.ScanPath}   (reason and solution for every row)");
    Console.WriteLine($"review → {result.ReviewPath}");
}

async Task RunBackupAsync()
{
    var result = await ScanAsync();
    Console.WriteLine(result.Banner());
    Console.WriteLine();

    var estimate = result.Fix.Count * 350_000L;
    var free = BackupStore.FreeSpaceBytes(appConfig.DataRoot);
    Console.WriteLine($"{result.Fix.Count} files, roughly {estimate / 1_048_576:N0} MB. " +
                      $"Free space {free / 1_048_576:N0} MB.");
    if (free < estimate * 2)
    {
        Console.Error.WriteLine("Not enough free space with margin. Aborting.");
        return;
    }

    var summary = await new BackupCommand(files, read, backups, state, reporter, hashLookup)
        .RunAsync(envName, result.Fix, CancellationToken.None);
    Console.WriteLine($"Saved {summary.Saved}, quarantined {summary.Quarantined}, " +
                      $"skipped {summary.Skipped}, {summary.TotalBytes / 1_048_576:N0} MB.");
    Console.WriteLine($"manifest → {summary.ManifestPath}");
}

async Task RunMigrateAsync()
{
    var summary = await new MigrateCommand(files, read, write, backups, state, reporter,
        prompts, opener, env.CrmUrl).RunAsync(envName, CancellationToken.None);

    Console.WriteLine($"Migrated {summary.Migrated}, skipped {summary.Skipped}, failed {summary.Failed}.");
    Console.WriteLine($"report → {summary.ReportPath}");
    if (summary.Halted) Console.Error.WriteLine($"RUN HALTED: {summary.HaltReason}");
}

async Task RunDeleteAsync()
{
    var summary = await new DeleteCommand(files, write, backups, state, prompts)
        .RunAsync(envName, env.IsProduction, CancellationToken.None);

    Console.WriteLine($"Deleted {summary.Deleted}, refused {summary.Refused}, skipped {summary.Skipped}.");
    if (summary.Aborted) Console.WriteLine($"Aborted: {summary.AbortReason}");
}

async Task RunTargetedAsync(IReadOnlyList<string> identifiers, bool forceReview)
{
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

    var summary = await targeted.RunAsync(envName, identifiers, forceReview,
        env.IsProduction, CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine($"Resolved {summary.Resolved}, fixed {summary.Fixed}, " +
                      $"ambiguous-verdict {summary.Reviewed}, already-ok {summary.Skipped}, " +
                      $"not found {summary.NotFound}, name clashes {summary.Ambiguous}.");
}

switch (options.Command)
{
    case "guided":
    {
        await new GuidedMenu(prompts, envName, env.IsProduction, env.CrmUrl, env.FileServiceBaseUrl,
            new GuidedActions(RunScanAsync, RunBackupAsync, RunMigrateAsync, RunDeleteAsync, RunTargetedAsync))
            .RunAsync(CancellationToken.None);
        return 0;
    }

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
        Console.WriteLine($"manifest → {summary.ManifestPath}");
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
