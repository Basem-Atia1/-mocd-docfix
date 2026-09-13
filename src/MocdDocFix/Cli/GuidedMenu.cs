using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>What the menu can run. Injected so the menu is testable without any network.</summary>
public sealed record GuidedActions(
    Func<Task> ScanAsync,
    Func<Task> BackupAsync,
    Func<Task> MigrateAsync,
    Func<Task> DeleteAsync,
    Func<IReadOnlyList<string>, bool, Task> TargetedAsync);

/// <summary>
/// The guided front end. Walks the operator through the phases in order, says plainly what each
/// one does and whether it writes, and asks before anything that touches the file server or CRM.
/// </summary>
public sealed class GuidedMenu
{
    private readonly IPrompts _prompts;
    private readonly string _envName;
    private readonly bool _isProduction;
    private readonly string _crmUrl;
    private readonly string _fileServerUrl;
    private readonly GuidedActions _actions;

    public GuidedMenu(IPrompts prompts, string envName, bool isProduction,
        string crmUrl, string fileServerUrl, GuidedActions actions)
    {
        _prompts = prompts;
        _envName = envName;
        _isProduction = isProduction;
        _crmUrl = crmUrl;
        _fileServerUrl = fileServerUrl;
        _actions = actions;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ShowMenu();

            switch (_prompts.ReadLine("Choose").Trim())
            {
                case "1":
                    await _actions.ScanAsync();
                    break;

                case "2":
                    if (ConfirmFileServer("Backup downloads every broken file from the file server."))
                        await _actions.BackupAsync();
                    break;

                case "3":
                    if (ConfirmMigrate())
                        await _actions.MigrateAsync();
                    break;

                case "4":
                    if (ConfirmDelete())
                        await _actions.DeleteAsync();
                    break;

                case "5":
                    await RunTargetedAsync();
                    break;

                case "0" or "q" or "quit" or "exit":
                    _prompts.Info("");
                    _prompts.Info("Nothing else was done. Bye.");
                    return;

                default:
                    _prompts.Info("  That is not a choice. Type a number from the list.");
                    break;
            }
        }
    }

    private void ShowMenu()
    {
        _prompts.Info("");
        _prompts.Info("======================================================================");
        _prompts.Info("  MoCD — document file path remediation");
        _prompts.Info("======================================================================");
        _prompts.Info($"  Environment  {_envName}{(_isProduction ? "    *** PRODUCTION ***" : "")}");
        _prompts.Info($"  CRM          {_crmUrl}");
        _prompts.Info($"  File server  {_fileServerUrl}");
        _prompts.Info("");
        _prompts.Info("  Do these in order. Each one tells you what it found before the next.");
        _prompts.Info("");
        _prompts.Info("   1  Scan           find the broken files        reads only, writes nothing");
        _prompts.Info("   2  Backup         save a full copy of each     reads only, contacts the file server");
        _prompts.Info("   3  Migrate        upload the corrected copies  WRITES to CRM, reversible");
        _prompts.Info("   4  Delete old     remove the old files         WRITES, IRREVERSIBLE");
        _prompts.Info("");
        _prompts.Info("   5  Fix specific documents — you type the GUIDs or file names");
        _prompts.Info("");
        _prompts.Info("   0  Quit");
        _prompts.Info("");
    }

    private bool ConfirmFileServer(string what)
    {
        _prompts.Info("");
        _prompts.Info(what);
        _prompts.Info($"It will contact the file server at {_fileServerUrl}.");
        return _prompts.Confirm("Contact the file server now?") == ConfirmChoice.Yes;
    }

    private bool ConfirmMigrate()
    {
        _prompts.Info("");
        _prompts.Info("Migrate uploads a corrected copy of every backed-up file and repoints CRM at it.");
        _prompts.Info("It contacts the file server, and it WRITES to CRM.");
        _prompts.Info("You will still be shown both files and asked before each document is repointed.");
        _prompts.Info("The old files are not touched — this stays reversible until you run Delete.");
        return _prompts.Confirm("Start migrating?") == ConfirmChoice.Yes;
    }

    private bool ConfirmDelete()
    {
        _prompts.Info("");
        _prompts.Info("Delete removes the OLD files from the file server and their CRM records.");
        _prompts.Info("This cannot be undone. Your local backups keep the bytes, but a restored");
        _prompts.Info("file gets a new id and today's date folder — it cannot go back to its old path.");
        _prompts.Info("Only files that were migrated and verified are eligible, and each one is");
        _prompts.Info("re-checked against CRM immediately before it is deleted.");
        return _prompts.Confirm("Go to the delete step?") == ConfirmChoice.Yes;
    }

    private async Task RunTargetedAsync()
    {
        _prompts.Info("");
        _prompts.Info("Type one or more of: document GUID, document-file GUID, or file name.");
        _prompts.Info("Separate several with commas. Example:");
        _prompts.Info("   2c9d5572-a77b-f111-b10f-00505601095a, cert.jpg");

        var typed = _prompts.ReadLine("Documents");
        var identifiers = typed
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (identifiers.Count == 0)
        {
            _prompts.Info("  Nothing typed — going back to the menu.");
            return;
        }

        _prompts.Info("");
        _prompts.Info($"{identifiers.Count} identifier(s). Some files are AMBIGUOUS: the folder they");
        _prompts.Info("are in is a real service, just a different one, so they may already be right.");
        var force = _prompts.Confirm("Act on ambiguous files too?") == ConfirmChoice.Yes;

        if (!ConfirmFileServer("This will download and re-upload the files you named."))
            return;

        await _actions.TargetedAsync(identifiers, force);
    }
}
