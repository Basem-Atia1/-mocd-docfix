namespace MocdDocFix.Cli;

public sealed record CommandLineOptions(
    string Command,
    string? Environment,
    IReadOnlyList<string> Identifiers,
    bool ForceReview,
    bool DryRun,
    bool ConfirmProduction,
    string? Error)
{
    private static readonly string[] Commands =
        { "scan", "backup", "migrate", "delete", "targeted", "config", "guided" };

    public const string Usage = """
        docfix                     start the guided menu (recommended)
        docfix <command> [options] run one step directly

        Commands
          scan       classify every in-scope document (read-only)
          backup     download and verify every FIX file (read-only)
          migrate    upload corrected copies and repoint CRM (writes)
          delete     delete the old files — IRREVERSIBLE (writes)
          targeted   run all phases on one or more identifiers
          config     set up an environment

        Options
          --env <name>            dev | test | preprod | prod
          --docs <a,b,c>          document id, documentfile id or file name (repeatable)
          --docs-file <path>      one identifier per line
          --force-review          act on ambiguous files too (targeted only)
          --dry-run               read and verify, write nothing
          --confirm-production    required before prod can be selected at all
        """;

    public static CommandLineOptions Parse(string[] args)
    {
        // No arguments is not a mistake — it is the guided menu, which is how most people
        // should use this tool.
        if (args.Length == 0)
            return new CommandLineOptions("guided", null, Array.Empty<string>(), false, false, false, null);

        var command = args[0].ToLowerInvariant();
        if (!Commands.Contains(command))
            return Error_($"Unknown command '{args[0]}'." + System.Environment.NewLine + Usage);

        string? env = null;
        var identifiers = new List<string>();
        bool forceReview = false, dryRun = false, confirmProduction = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--env":
                    if (++i >= args.Length) return Error_("--env needs a value.");
                    env = args[i];
                    break;

                case "--docs":
                    if (++i >= args.Length) return Error_("--docs needs a value.");
                    identifiers.AddRange(args[i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0));
                    break;

                case "--docs-file":
                    if (++i >= args.Length) return Error_("--docs-file needs a value.");
                    if (!File.Exists(args[i])) return Error_($"File not found: {args[i]}");
                    identifiers.AddRange(File.ReadAllLines(args[i])
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0 && !l.StartsWith('#')));
                    break;

                case "--force-review": forceReview = true; break;
                case "--dry-run": dryRun = true; break;
                case "--confirm-production": confirmProduction = true; break;

                default:
                    return Error_($"Unknown option '{args[i]}'." + System.Environment.NewLine + Usage);
            }
        }

        return new CommandLineOptions(command, env, identifiers, forceReview, dryRun, confirmProduction, null);
    }

    private static CommandLineOptions Error_(string message) =>
        new(string.Empty, null, Array.Empty<string>(), false, false, false, message);
}
