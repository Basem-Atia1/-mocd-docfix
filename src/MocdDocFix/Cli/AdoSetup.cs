using MocdDocFix.Clients;
using MocdDocFix.Config;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>
/// Connects to the DevOps backlog for the scan's third opinion, asking for the sign-in once and
/// keeping it in the same encrypted store as the CRM password.
///
/// Everything here is optional by design. DevOps is a cross-check, not a dependency: declining,
/// getting it wrong, or being off the VPN leaves the scan doing exactly what it did before, with
/// every row reporting "not checked" rather than implying an answer nobody gave.
/// </summary>
public static class AdoSetup
{
    public const string PasswordKey = "ado:password";

    public static IAdoClient? Connect(
        AppConfig config, ISecretStore secrets, IPrompts prompts, Action<AppConfig> save)
    {
        var ado = config.Ado;

        if (!ado.Enabled || string.IsNullOrWhiteSpace(ado.CollectionUrl)) return null;

        var password = secrets.Get(PasswordKey);

        if (string.IsNullOrWhiteSpace(ado.User) || string.IsNullOrWhiteSpace(password))
        {
            if (!AskToSetUp(prompts, ado)) return null;

            var user = prompts.ReadLine("  DevOps user name        e.g. MOCD\\basem.atia or basem.atia");
            if (user.Length == 0 || user.Equals("q", StringComparison.OrdinalIgnoreCase)) return null;

            password = prompts.ReadLine("  DevOps password         (stored encrypted, never shown again)");
            if (password.Length == 0) return null;

            // A user typed as DOMAIN\name is split here so NTLM gets the two parts it wants.
            var slash = user.IndexOf('\\');
            if (slash > 0)
            {
                ado.Domain = user[..slash];
                ado.User = user[(slash + 1)..];
            }
            else
            {
                ado.User = user;
            }

            secrets.Set(PasswordKey, password);
            save(config);

            prompts.Say($"Saved. The backlog will be read as {ado.User} from now on.", Tone.Good);
        }

        return new AdoClient(ado.CollectionUrl, ado.Project, ado.User, password!, ado.Domain);
    }

    private static bool AskToSetUp(IPrompts prompts, AdoConfig ado)
    {
        prompts.Section("DevOps cross-check");
        prompts.Say("The scan can also ask the DevOps backlog which service each document type " +
                    "belongs to, and stop on anything that disagrees with CRM. It reads work " +
                    "item titles and writes nothing.");
        prompts.Blank();
        prompts.Field("Backlog", $"{ado.CollectionUrl} — {ado.Project}", Tone.Muted);
        prompts.Say("It needs the VPN, and a sign-in the first time only.", Tone.Muted);
        prompts.Blank();

        return prompts.YesNo("  Set up the DevOps cross-check now?", defaultYes: true);
    }
}
