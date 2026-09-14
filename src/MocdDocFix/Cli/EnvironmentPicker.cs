using MocdDocFix.Config;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>Everything needed to add an environment: the non-secret settings and its two secrets.</summary>
public sealed record NewEnvironment(EnvironmentConfig Config, string ApiKey, string CrmPassword);

/// <summary>
/// Asks which environment to work in, and — unlike the old selector — never ends the program
/// because of the answer. An environment that has not been set up becomes an offer to set it up,
/// not an exit (spec 2026-09-13 section 6.1).
///
/// Production keeps the original gate: it needs --confirm-production on the command line AND the
/// name typed in full. A number alone can never reach it.
/// </summary>
public sealed class EnvironmentPicker
{
    private static readonly string[] WellKnown = { "dev", "test", "preprod", "prod" };

    private readonly IPrompts _prompts;
    private readonly Asker _asker;
    private readonly Func<AppConfig> _load;
    private readonly Action<string, NewEnvironment> _save;

    public EnvironmentPicker(IPrompts prompts, Func<AppConfig> load, Action<string, NewEnvironment> save)
    {
        _prompts = prompts;
        _asker = new Asker(prompts);
        _load = load;
        _save = save;
    }

    /// <returns>The chosen environment name, or null if the operator chose to quit.</returns>
    public string? Choose(string? fromArgs, bool confirmProductionFlag)
    {
        if (fromArgs is not null)
        {
            var config = _load();

            if (config.Environments.TryGetValue(fromArgs, out var named))
            {
                if (!named.IsProduction) return fromArgs;

                if (!confirmProductionFlag)
                {
                    RefuseProduction(fromArgs);
                }
                else if (TypeTheNameInFull(fromArgs))
                {
                    return fromArgs;
                }
            }
            else
            {
                _prompts.Blank();
                _prompts.Say($"'{fromArgs}' has no settings saved on this machine yet.", Tone.Warn);
            }
        }

        while (true)
        {
            var config = _load();
            var names = Names(config);
            var answer = _asker.Ask("Which environment?", Choices(config, names, confirmProductionFlag),
                allowBack: false);

            if (answer.Kind != AnswerKind.Chosen) return null;

            var name = names[answer.Index];

            if (!config.Environments.TryGetValue(name, out var chosen))
            {
                var setup = SetUp(name);
                if (setup.Name is { } configured) return configured;
                if (setup.Quit) return null;
                continue;
            }

            if (!chosen.IsProduction) return name;

            // Reaching a production row at all means the flag was supplied — the Asker disables
            // it otherwise — so all that remains is typing the name.
            if (TypeTheNameInFull(name)) return name;
        }
    }

    private IReadOnlyList<string> Names(AppConfig config) =>
        WellKnown
            .Concat(config.Environments.Keys
                .Where(k => !WellKnown.Contains(k, StringComparer.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private static IReadOnlyList<Choice> Choices(
        AppConfig config, IReadOnlyList<string> names, bool confirmProductionFlag)
    {
        var choices = new List<Choice>(names.Count);

        foreach (var name in names)
        {
            if (!config.Environments.TryGetValue(name, out var env))
            {
                choices.Add(new Choice(name, "not set up yet",
                    $"Nothing is saved for '{name}' on this machine. Choosing it offers to set " +
                    "it up: the file service URL, the CRM URL, and the credentials for both."));
                continue;
            }

            var production = env.IsProduction;
            var description = production
                ? $"*** PRODUCTION ***   {env.CrmUrl}"
                : $"ready      {env.CrmUrl}";

            choices.Add(new Choice(name, description,
                LongHelp: $"CRM {env.CrmUrl}, file server {env.FileServiceBaseUrl}, " +
                          $"signing in as {env.CrmDomain}\\{env.CrmUser}.",
                Enabled: !production || confirmProductionFlag,
                DisabledNote: production
                    ? "Production needs --confirm-production on the command line, and then the " +
                      "environment name typed in full. It cannot be reached from a number alone."
                    : null));
        }

        return choices;
    }

    private void RefuseProduction(string name)
    {
        _prompts.Section($"'{name}' is a PRODUCTION environment", Tone.Danger);
        _prompts.Say("It needs --confirm-production on the command line. Nothing has been changed.");
    }

    private bool TypeTheNameInFull(string name)
    {
        _prompts.Section($"*** {name.ToUpperInvariant()} IS PRODUCTION ***", Tone.Danger);
        _prompts.Warn("Writes here affect live citizen documents.", Tone.Danger);
        _prompts.Blank();

        if (_prompts.TypedWord("  Confirm the environment name", name)) return true;

        _prompts.Say("That did not match. Production was not selected.", Tone.Warn);
        return false;
    }

    /// <returns>
    /// The name once saved; otherwise Quit says whether to leave altogether or
    /// go back to the list.
    /// </returns>
    private (string? Name, bool Quit) SetUp(string name)
    {
        _prompts.Section($"'{name}' has not been set up on this machine");
        _prompts.Say("Nothing is broken — it simply has no URLs or credentials saved yet.",
            Tone.Muted);

        var what = _asker.Ask($"What would you like to do about '{name}'?", new[]
        {
            new Choice("Set it up now", "I will ask for the URLs and credentials, then save them",
                "Non-secret settings go to config.json. The CRM password and the file service " +
                "API key go to the Windows credential store (DPAPI), readable only by you on " +
                "this machine."),
            new Choice("Pick another", "go back to the list of environments"),
            new Choice("Quit", "stop here, change nothing")
        }, defaultIndex: 0, allowBack: false);

        if (what.Kind != AnswerKind.Chosen || what.Index == 2) return (null, Quit: true);
        if (what.Index == 1) return (null, Quit: false);

        _prompts.Section($"Setting up '{name}'");
        _prompts.Say("Everything is saved locally; nothing is sent anywhere.", Tone.Muted);
        _prompts.Blank();

        var fileUrl = Required("File service base URL   e.g. http://mocdstgdpfs01.mocd.gov.ae:83");
        var crmUrl = Required("CRM base URL            e.g. https://devdigitalplatform.mocd.gov.ae/MoCD");
        var domain = Required("CRM domain              e.g. msa");
        var user = Required("CRM user name");
        var password = Required("CRM password            (stored encrypted, never shown again)");
        var apiKey = Required("File service API key    (the Apikey header value)");

        var isProduction = name.Equals("prod", StringComparison.OrdinalIgnoreCase);

        _prompts.Section("About to save");
        _prompts.Field("environment", name + (isProduction ? "   *** PRODUCTION ***" : ""),
            isProduction ? Tone.Danger : Tone.Normal);
        _prompts.Field("CRM", crmUrl, Tone.Muted);
        _prompts.Field("file server", fileUrl, Tone.Muted);
        _prompts.Field("signing in as", $"{domain}\\{user}", Tone.Muted);
        _prompts.Field("password", "stored encrypted, never shown again", Tone.Muted);
        _prompts.Field("API key", "stored encrypted, never shown again", Tone.Muted);
        _prompts.Blank();

        if (_prompts.Confirm($"  Save this as '{name}'?") != ConfirmChoice.Yes)
        {
            _prompts.Say("Not saved.", Tone.Muted);
            return (null, Quit: false);
        }

        _save(name, new NewEnvironment(
            new EnvironmentConfig(fileUrl, crmUrl, domain, user, isProduction), apiKey, password));

        _prompts.Say($"Saved. '{name}' is ready to use.", Tone.Good);
        return (name, Quit: false);
    }

    private string Required(string question)
    {
        while (true)
        {
            var value = _prompts.ReadLine(question).Trim();
            if (value.Length > 0) return value;
            _prompts.Say("That cannot be blank.", Tone.Warn);
        }
    }
}
