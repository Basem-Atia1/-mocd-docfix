using MocdDocFix.Config;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>
/// Production can never be reached by pressing Enter on a menu (spec section 7.1): it needs
/// the --confirm-production flag AND the environment name typed in full.
/// </summary>
public static class EnvironmentSelector
{
    public static string? Select(IPrompts prompts, string? fromArgs, AppConfig config, bool confirmProductionFlag)
    {
        if (fromArgs is null)
        {
            prompts.Info("");
            prompts.Info("Configured environments:");
            foreach (var (key, value) in config.Environments.OrderBy(e => e.Key))
                prompts.Info($"  {key}{(value.IsProduction ? "   *** PRODUCTION ***" : "")}");
            prompts.Info("");

            // A typed name, never a numbered menu — a number is too easy to mis-key.
            var typed = prompts.ReadLine("Which environment? (type the environment name in full)");
            if (string.IsNullOrWhiteSpace(typed)) return null;

            return Select(prompts, typed, config, confirmProductionFlag);
        }

        var name = fromArgs;

        if (!config.Environments.TryGetValue(name, out var env))
        {
            prompts.Info($"Environment '{name}' is not configured. Run: docfix config --env {name}");
            return null;
        }

        if (!env.IsProduction) return name;

        if (!confirmProductionFlag)
        {
            prompts.Info($"'{name}' is a PRODUCTION environment.");
            prompts.Info("Re-run with --confirm-production if you really mean it.");
            return null;
        }

        prompts.Info("");
        prompts.Info($"*** {name.ToUpperInvariant()} IS PRODUCTION ***");
        prompts.Info("Writes here affect live citizen documents.");

        return prompts.TypedWord("Confirm the environment name", name) ? name : null;
    }
}
