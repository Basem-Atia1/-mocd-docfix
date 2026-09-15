namespace MocdDocFix.Cli;

public sealed record CommandLineOptions(
    string Command,
    string? Environment,
    bool DryRun,
    bool ConfirmProduction,
    string? Error)
{
    private static readonly string[] Commands =
        { "repair", "delete", "redo", "check", "config", "guided" };

    public const string Usage = """
        docfix                     start the guided menu (recommended)
        docfix <command> [options] run one step directly

        Commands
          repair     build the ledger, then work through it (writes)
          delete     delete the old files of corrected rows — IRREVERSIBLE
          redo       put corrected records back the way they were (writes)
          check      confirm the old files are as the ledger says (read-only)
          config     set up an environment

        Options
          --env <name>            dev | test | preprod | prod
          --dry-run               read and verify, write nothing
          --confirm-production    required before prod can be selected at all
        """;

    public static CommandLineOptions Parse(string[] args)
    {
        // No arguments is not a mistake — it is the guided menu, which is how most people
        // should use this tool.
        if (args.Length == 0)
            return new CommandLineOptions("guided", null, false, false, null);

        var command = args[0].ToLowerInvariant();
        if (!Commands.Contains(command))
            return Error_($"Unknown command '{args[0]}'." + System.Environment.NewLine + Usage);

        string? env = null;

        bool dryRun = false, confirmProduction = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--env":
                    if (++i >= args.Length) return Error_("--env needs a value.");
                    env = args[i];
                    break;



                case "--dry-run": dryRun = true; break;
                case "--confirm-production": confirmProduction = true; break;

                default:
                    return Error_($"Unknown option '{args[i]}'." + System.Environment.NewLine + Usage);
            }
        }

        return new CommandLineOptions(command, env, dryRun, confirmProduction, null);
    }

    private static CommandLineOptions Error_(string message) =>
        new(string.Empty, null, false, false, message);
}
