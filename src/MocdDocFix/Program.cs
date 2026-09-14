using MocdDocFix.Cli;
using MocdDocFix.Config;
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

    // Asked for once, then read from the encrypted store. Declining, or having no VPN, leaves
    // the scan doing exactly what it did before: the backlog is a third opinion, not a
    // dependency, and nothing downstream fails without it.
    var ado = AdoSetup.Connect(appConfig, secrets, prompts, configStore.Save);

    using var session = new Session(appConfig, env, envName, prompts, options.DryRun, ado);

    if (options.Command != "guided")
    {
        if (options.DryRun) Console.WriteLine("DRY RUN — nothing will be written.");
        Console.WriteLine($"Environment: {envName}{(env.IsProduction ? "   *** PRODUCTION ***" : "")}");
        Console.WriteLine($"CRM:         {env.CrmUrl}");
        Console.WriteLine($"File server: {env.FileServiceBaseUrl}");
        Console.WriteLine();

        return await session.RunDirectAsync(options, CancellationToken.None);
    }

    var wizard = new Wizard(prompts, envName, env.IsProduction, env.CrmUrl, env.FileServiceBaseUrl,
        session.WizardActions(CancellationToken.None));

    try
    {
        if (await wizard.RunAsync(CancellationToken.None) == WizardExit.Finished) return 0;
    }
    catch (Exception ex)
    {
        // A failure mid-run returns to the environment question rather than dumping a stack
        // trace and exiting. Whatever was already done stays on disk in the state file.
        prompts.Info("");
        prompts.Info($"That did not work: {ex.Message}");
        prompts.Info("Nothing further was run. Anything already completed is recorded on disk.");
    }
}
