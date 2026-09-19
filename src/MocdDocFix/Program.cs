using MocdDocFix.Cli;
using MocdDocFix.Config;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

// Arabic file names and the dashes in the reports both need this; without it the console
// substitutes '?' for anything outside the OEM code page.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* redirected */ }

var options = CommandLineOptions.Parse(args);
if (options.Error is not null)
{
    Console.Error.WriteLine(options.Error);
    return 2;
}

var prompts = new ConsolePrompts();
var configDir = ConfigStore.DefaultDirectory;
var secretsPath = Path.Combine(configDir, "secrets.dat");
var configPath = Path.Combine(configDir, "config.json");
var secrets = new DpapiSecretStore(secretsPath);
var configStore = new ConfigStore(configPath, secrets);

// Said once, up front. The usual cause is the app folder or the config folder having been
// copied to another machine or another Windows account, and the alternative is a password
// prompt that appears to have forgotten a password that was set only yesterday.
if (secrets.Problem is { } secretsProblem)
{
    prompts.Blank();
    prompts.Warn(secretsProblem, Tone.Warn);
    prompts.Blank();
}

if (options.Command == "config")
{
    if (!File.Exists(configPath))
    {
        configStore.Save(configStore.Load());
        Console.WriteLine($"Wrote a starter config to {configPath}");
    }

    Console.WriteLine($"Config file:  {configPath}");
    Console.WriteLine($"Secrets file: {secretsPath} (DPAPI, current user only)");
    Console.WriteLine();
    Console.WriteLine("You no longer need this command: run docfix with no arguments and the");
    Console.WriteLine("wizard offers to set up any environment that is not configured yet.");

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

var picker = new EnvironmentPicker(prompts, configStore.Load, (name, added) =>
{
    var config = configStore.Load();
    config.Environments[name] = added.Config;
    configStore.Save(config);
    secrets.Set($"{name}:apiKey", added.ApiKey);
    secrets.Set($"{name}:crmPassword", added.CrmPassword);
});

// The environment question is a loop, not a one-shot: a name that is not configured, or that
// fails to resolve, comes back here rather than ending the program (spec 2026-09-13 section 6.1).
var fromArgs = options.Environment;

while (true)
{
    var envName = picker.Choose(fromArgs, options.ConfirmProduction);
    if (envName is null) return 0;

    fromArgs = null;   // having asked once, never force the same name again

    ResolvedEnvironment env;
    try
    {
        env = configStore.Resolve(envName);
    }
    catch (InvalidOperationException ex)
    {
        prompts.Info("");
        prompts.Info(ex.Message);
        prompts.Info("Nothing has been changed. Choose another environment, or set this one up.");
        continue;
    }

    var appConfig = configStore.Load();

    // What this environment was last worked on. Remembered per environment rather than globally,
    // because dev may be surveying every catalogue while pre-prod — fifty thousand documents —
    // stays on the eight.
    var remembered = appConfig.Environments.TryGetValue(envName, out var stored) &&
                     stored.Scope.Equals("all", StringComparison.OrdinalIgnoreCase)
        ? LedgerScope.All
        : LedgerScope.Ours;

    var scope = remembered;

    using var probe = new Session(appConfig, env, envName, prompts, options.DryRun, remembered);

    if (options.Command != "guided")
    {
        if (options.DryRun) Console.WriteLine("DRY RUN — nothing will be written.");
        Console.WriteLine($"Environment: {envName}{(env.IsProduction ? "   *** PRODUCTION ***" : "")}");
        Console.WriteLine($"CRM:         {env.CrmUrl}");
        Console.WriteLine($"File server: {env.FileServiceBaseUrl}");
        Console.WriteLine();

        // Scripted, so it must not stop for a question. It takes whatever was remembered.
        return await probe.RunDirectAsync(options, CancellationToken.None);
    }

    // Asked once, before the menu, because every mode needs the answer — the delete step and
    // Check it all have to know which ledger file to open, not just the repair run.
    scope = await probe.AskAboutScopeAsync(remembered, CancellationToken.None);

    if (scope != remembered)
    {
        var toSave = configStore.Load();
        if (toSave.Environments.TryGetValue(envName, out var was))
        {
            toSave.Environments[envName] = was with
            {
                Scope = scope == LedgerScope.All ? "all" : "ours"
            };
            configStore.Save(toSave);
        }
    }

    // The scope decides which files the session holds, so a different answer wants a new one.
    // Built beside the first rather than instead of it, so exactly one of them is disposed.
    using var rebuilt = scope == remembered
        ? null
        : new Session(appConfig, env, envName, prompts, options.DryRun, scope);

    var session = rebuilt ?? probe;

    var scopeLabel = scope == LedgerScope.All
        ? "every service catalogue in CRM"
        : $"the {appConfig.ServiceCatalogues.Count} we work on";

    var wizard = new Wizard(prompts, envName, env.IsProduction, env.CrmUrl, env.FileServiceBaseUrl,
        session.Actions(CancellationToken.None), scopeLabel, () => Task.CompletedTask);

    try
    {
        var exit = await wizard.RunAsync(CancellationToken.None);

        if (exit == WizardExit.Finished) return 0;

        // Straight back round without re-asking the environment: the scope question comes first
        // in the loop, which is the whole point of having chosen it.
        if (exit == WizardExit.ChangeScope) fromArgs = envName;
    }
    catch (OperationCanceledException stopped)
    {
        // Asked for. "Stop the run" is an answer, not a failure, and printing it as one — under
        // "That did not work", with an exception name and a chain of causes — reads as though
        // something had broken.
        prompts.Blank();
        prompts.Section("Stopped", Tone.Normal);
        prompts.Say(stopped.Message);
        prompts.Blank();
        prompts.Say("Nothing further was run. Everything already done is recorded on disk, so " +
                    "the run can be picked up where it stopped.", Tone.Muted);
    }
    catch (Exception ex)
    {
        // A failure mid-run returns to the environment question rather than dumping a stack
        // trace and exiting. Whatever was already done stays on disk in the state file.
        //
        // The whole chain is printed, not just the top message: every HttpClient failure
        // surfaces as "An error occurred while sending the request", which names neither the
        // system nor the reason and leaves the operator with nothing to act on.
        prompts.Blank();
        prompts.Section("That did not work", Tone.Danger);

        for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
            prompts.Say($"{cause.GetType().Name}: {cause.Message}",
                ReferenceEquals(cause, ex) ? Tone.Danger : Tone.Muted);

        prompts.Blank();
        prompts.Say("Nothing further was run. Anything already completed is recorded on disk.");
    }
}
