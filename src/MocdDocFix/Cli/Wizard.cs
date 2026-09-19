using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>What a mode reports back, so the wizard can show it before returning to the menu.</summary>
public sealed record StepOutcome(string Headline, IReadOnlyList<string> Details)
{
    public static StepOutcome Of(string headline, params string[] details) => new(headline, details);
}

/// <param name="RepairAsync">
/// Build or open the ledger, then work through it. The only mode that uploads.
/// </param>
/// <param name="DeleteAsync">Remove the old files of rows the repair run finished.</param>
/// <param name="RedoAsync">Put reverted rows' records back the way they were.</param>
/// <param name="CheckAsync">Confirm the old files are as the ledger says. Reads only.</param>
public sealed record LedgerActions(
    Func<CancellationToken, Task<StepOutcome>> RepairAsync,
    Func<CancellationToken, Task<StepOutcome>> DeleteAsync,
    Func<CancellationToken, Task<StepOutcome>> RedoAsync,
    Func<CancellationToken, Task<StepOutcome>> CheckAsync,
    Func<IReadOnlyList<string>, Task<StepOutcome>>? LookAsync = null);

/// <param name="ChangeScope">
/// Switch between the services we work on and every catalogue. It returns rather than looping,
/// because the scope decides which ledger files the session holds — so the session is rebuilt.
/// </param>
public enum WizardExit { Finished, ChangeEnvironment, ChangeScope }

/// <summary>
/// The front end: seven entries, one ledger behind all of them.
///
/// Everything that used to be four separate ways in — targeted, full, just report, check it
/// all — is now entry 1, because the ledger is what tells those apart. The verdict column
/// chooses which documents are worked on and the final state column records what happened, so
/// there is nothing left for a mode to mean.
/// </summary>
public sealed class Wizard
{
    private readonly IPrompts _prompts;
    private readonly Asker _asker;
    private readonly string _envName;
    private readonly bool _isProduction;
    private readonly string _crmUrl;
    private readonly string _fileServerUrl;
    private readonly LedgerActions _actions;

    private readonly string _scopeLabel;
    private readonly Func<Task>? _changeScope;

    /// <param name="scopeLabel">Which services this sitting is about, for the banner.</param>
    /// <param name="changeScope">
    /// Switches between the services we work on and every catalogue in CRM. Left null in a build
    /// with no way to do it, and the menu entry is then shown but disabled.
    /// </param>
    public Wizard(IPrompts prompts, string envName, bool isProduction,
        string crmUrl, string fileServerUrl, LedgerActions actions,
        string scopeLabel = "the services we work on", Func<Task>? changeScope = null)
    {
        _prompts = prompts;
        _asker = new Asker(prompts);
        _envName = envName;
        _isProduction = isProduction;
        _crmUrl = crmUrl;
        _fileServerUrl = fileServerUrl;
        _actions = actions;
        _scopeLabel = scopeLabel;
        _changeScope = changeScope;
    }

    public async Task<WizardExit> RunAsync(CancellationToken ct)
    {
        Banner();

        while (!ct.IsCancellationRequested)
        {
            var mode = _asker.Ask("What do you want to do?", new[]
            {
                new Choice("Repair run", "build the ledger, then work through it",
                    "Reads every document in the seven services and writes one workbook — the " +
                    "ledger. You look at it, edit the verdict column where you disagree, and " +
                    "it works through the rows marked fix: back up, upload under the correct " +
                    "catalogue, check the copy four ways, show you both, and update the record " +
                    "the document already points at. Nothing is created and nothing is " +
                    "repointed. It does not delete anything."),

                new Choice("Delete old files", "of rows the repair run finished",
                    "Reads the ledger and takes only rows whose final state says \"corrected " +
                    "and pending the delete of old docs\". For each one the OLD file is removed " +
                    "from the file server. No CRM record is deleted — the correction updated " +
                    "the record rather than replacing it, so there is no orphan. IRREVERSIBLE."),

                new Choice("Redo", "put records back the way they were",
                    "Reads the ledger and acts on rows where you typed redo in the verdict " +
                    "column, as long as their old files have not been deleted. It writes the " +
                    "old path, category, hash and name back into the same record, from the " +
                    "snapshot saved in the backup folder. The corrected copy stays on the " +
                    "server with nothing pointing at it, and its path is recorded."),

                new Choice("Check it all", "confirm the old files really are gone",
                    "Walks the ledger and asks both systems the plain question: for every row " +
                    "that says its old files were deleted, is the file actually off the server " +
                    "and is nothing in CRM still naming it — and for every row still awaiting " +
                    "the delete step, is its old file still there. Reads only."),

                new Choice("Is this file still there?", "check one path or documentfile id",
                    "Give it an old file path, or the id of a mocd_documentfile, and it asks " +
                    "the file server whether the file is on disk and CRM whether any record " +
                    "still refers to it. It changes nothing, in either system.",
                    Enabled: _actions.LookAsync is not null,
                    DisabledNote: "this build was not given a look-up action."),

                new Choice("Change services", $"currently {_scopeLabel}",
                    "Switches between the services this tool was built for and every service " +
                    "catalogue in CRM. Each keeps its own ledger file, so nothing is lost " +
                    "either way — the rows are still there when you come back.",
                    Enabled: _changeScope is not null,
                    DisabledNote: "this build was not given a way to change the scope."),

                new Choice("Change environment", $"currently {_envName}"),

                new Choice("Quit", "stop here")
            }, defaultIndex: 0, allowBack: false, confirm: true);

            switch (mode.Kind == AnswerKind.Chosen ? mode.Index : 7)
            {
                case 0: Report("Repair run", await _actions.RepairAsync(ct)); break;
                case 1: Report("Delete old files", await _actions.DeleteAsync(ct)); break;
                case 2: Report("Redo", await _actions.RedoAsync(ct)); break;
                case 3: Report("Check it all", await _actions.CheckAsync(ct)); break;
                case 4: await LookUpAsync(); break;
                case 5:
                    if (_changeScope is not null) await _changeScope();
                    return WizardExit.ChangeScope;
                case 6: return WizardExit.ChangeEnvironment;
                default:
                    _prompts.Blank();
                    _prompts.Say("Nothing further was done. Bye.");
                    return WizardExit.Finished;
            }
        }

        return WizardExit.Finished;
    }

    private void Banner()
    {
        _prompts.Title("MoCD — document file path remediation");
        _prompts.Blank();
        _prompts.Field("Environment", _envName + (_isProduction ? "   *** PRODUCTION ***" : ""),
            _isProduction ? Tone.Danger : Tone.Normal);
        _prompts.Field("Services", _scopeLabel);
        _prompts.Field("CRM", _crmUrl, Tone.Muted);
        _prompts.Field("File server", _fileServerUrl, Tone.Muted);
        _prompts.Blank();
        _prompts.Say("Type ? at any question for a fuller explanation, b to go back, q to quit.",
            Tone.Muted);
    }

    private async Task LookUpAsync()
    {
        if (_actions.LookAsync is not { } look) return;

        _prompts.Section("Is this file still there?");
        _prompts.Say("Give an old file path, or the id of a mocd_documentfile record. Several " +
                     "at once, separated by commas.");
        _prompts.Blank();

        var asked = _prompts.ReadLine("  Path or id")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.Equals("q", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (asked.Count == 0)
        {
            _prompts.Blank();
            _prompts.Say("Nothing to look up.", Tone.Muted);
            return;
        }

        Report("Look-up", await look(asked));
    }

    private void Report(string name, StepOutcome outcome)
    {
        _prompts.Section(name, Tone.Good);
        _prompts.Say(outcome.Headline);

        if (outcome.Details.Count == 0) return;

        _prompts.Blank();
        foreach (var line in outcome.Details) _prompts.Info("    " + line, Tone.Muted);
    }
}
