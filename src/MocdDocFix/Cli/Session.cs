using MocdDocFix.Clients;
using MocdDocFix.Commands;
using MocdDocFix.Config;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Cli;

/// <summary>
/// One environment's worth of wiring: the clients, the stores and the four modes, built once
/// and shared by the wizard and the direct commands so both run exactly the same code.
///
/// Everything here works from one file — the ledger. Each mode re-reads it from disk before it
/// starts, so an edit made in Excel since the last run takes effect; that is the whole point of
/// the design.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly AppConfig _appConfig;
    private readonly ResolvedEnvironment _env;
    private readonly string _envName;
    private readonly IPrompts _prompts;
    private readonly bool _dryRun;

    private readonly HttpClient _crmHttp;
    private readonly HttpClient _fileHttp;
    private readonly CrmReadClient _read;
    private readonly CrmWriteClient _write;
    private readonly FileServiceClient _files;
    private readonly BackupStore _backups;
    private readonly ShellFileOpener _opener = new();

    private readonly LedgerSet _ledger;
    private readonly ChangeJournal _journal;
    private readonly ErrorLog _errors;
    private readonly LedgerBuilder _builder;

    /// <summary>
    /// Documents CRM stopped returning at the last scan. Held so the repair run can pass over
    /// them: the message says nothing will act on them, and until now the loop knew nothing
    /// about it and would have tried to correct one that still said fix.
    /// </summary>
    private HashSet<Guid> _gone = new();

    /// <summary>
    /// Every service catalogue in CRM, once asked for. Only the all-services scope needs it, and
    /// it does not change during a sitting.
    /// </summary>
    private IReadOnlyList<Guid>? _everyCatalogue;

    /// <param name="scope">
    /// Which services this whole sitting is about. It decides which ledger file or files are
    /// opened, so it is fixed when the session is built rather than asked per mode — the delete
    /// step and Check it all need the answer just as much as the repair run does.
    /// </param>
    public Session(AppConfig appConfig, ResolvedEnvironment env, string envName,
        IPrompts prompts, bool dryRun, LedgerScope scope = LedgerScope.Ours)
    {
        _appConfig = appConfig;
        _env = env;
        _envName = envName;
        _prompts = prompts;
        _dryRun = dryRun;

        // The backup folder holds the bytes and the crm.json snapshot — the only route back
        // once a correction has overwritten a record. Everything else is one file each.
        _backups = new BackupStore(Path.Combine(appConfig.DataRoot, "backup", envName));

        // One folder per environment, so dev and production can never be read for each other.
        var reports = Path.Combine(appConfig.DataRoot, "reports", envName);

        _ledger = new LedgerSet(Path.Combine(reports, $"repair-{envName}.xlsx"),
            scope, appConfig.ServiceCatalogues);
        _journal = new ChangeJournal(Path.Combine(reports, $"changes-{envName}.jsonl"));
        _errors = new ErrorLog(Path.Combine(reports, $"errors-{envName}.txt"));

        _crmHttp = CrmHttp.Create(env);
        _read = new CrmReadClient(_crmHttp, env);
        _write = new CrmWriteClient(_crmHttp);

        // Downloads and deletes are retried for the same reason CRM's are. The upload is a POST
        // and is not: sending it twice would put two copies of the file on the server.
        _fileHttp = new HttpClient(new RetryTransient(new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _files = new FileServiceClient(_fileHttp, env);

        // Built here only for the services we work on. Across every catalogue the list comes from
        // CRM, which cannot be asked from a constructor — see BuilderAsync.
        _builder = new LedgerBuilder(_read, appConfig.ServiceCatalogues, env.CrmUrl);

        // A locked ledger mid-run is recoverable and must never end a run: by the time a row is
        // written, its file has been uploaded and its CRM record changed, so failing to record
        // that is the one outcome worse than waiting.
        _ledger.AskToRetry = WaitForExcel;

    }

    /// <summary>
    /// Holds until the ledger can be written, asking the operator to close Excel.
    ///
    /// **This must run before anything touches the file.** Excel opens a workbook with a lock
    /// that denies everything, so reading it throws "the process cannot access the file" just as
    /// surely as writing it does — and an exception is a far worse answer than a question
    /// somebody can act on in ten seconds. Any new step that reads the ledger goes after this,
    /// not before it.
    ///
    /// It waits rather than giving up: having the ledger open is the normal thing to be doing a
    /// moment before a run, and making the operator start the whole mode again to fix a
    /// ten-second problem is a punishment, not a safeguard.
    /// </summary>
    /// <returns>False when the operator would rather stop than close it.</returns>
    private bool WaitUntilWritable()
    {
        while (!_ledger.CanWrite())
        {
            _prompts.Blank();
            _prompts.Warn("The ledger is open in Excel, so this run could not record what it did.",
                Tone.Warn);
            _prompts.Field("file", _ledger.Path, Tone.Muted);
            _prompts.Blank();

            if (!_prompts.YesNo("  Close it in Excel, then answer yes to carry on. Try again?",
                    defaultYes: true))
            {
                _prompts.Say("Stopped. Nothing has been changed.", Tone.Muted);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Asks the operator to close the ledger, and says what is at stake. Returns true to try
    /// the write again.
    /// </summary>
    private bool WaitForExcel(string problem)
    {
        _prompts.Blank();
        _prompts.Warn("The ledger could not be written — it is open in Excel.", Tone.Warn);
        _prompts.Field("file", _ledger.Path, Tone.Muted);
        _prompts.Blank();
        _prompts.Say("The work itself is not lost: whatever this run has already done to CRM " +
                     "and to the file server stands. What is waiting is the record of it.",
                     Tone.Muted);
        _prompts.Blank();

        if (_prompts.YesNo("  Close it in Excel, then answer yes to write and carry on. Retry?",
                defaultYes: true))
            return true;

        _prompts.Blank();
        _prompts.Warn("Stopping without writing the ledger. The change journal still has every " +
                      "change this run made — see changes-" + _envName + ".jsonl.", Tone.Danger);
        _prompts.Info($"      {problem}", Tone.Muted);
        return false;
    }

    public void Dispose()
    {
        _crmHttp.Dispose();
        _fileHttp.Dispose();
    }

    // ---- opening the ledger ----

    /// <summary>
    /// The rows every mode works from. Re-read from disk each time, so a hand edit since the
    /// last run takes effect.
    /// </summary>
    /// <param name="mayRebuild">
    /// True only for the repair run — it is the one mode allowed to go and ask CRM. The others
    /// work from what is already there, because a mode that silently rebuilt the ledger would
    /// discard the verdicts the operator had typed into it.
    /// </param>
    private async Task<IReadOnlyList<LedgerRow>> OpenLedgerAsync(bool mayRebuild, CancellationToken ct)
    {
        if (!WaitUntilWritable()) return Array.Empty<LedgerRow>();

        var existing = _ledger.Exists ? Reconciled(_ledger.Read()) : Array.Empty<LedgerRow>();

        if (!mayRebuild)
        {
            if (existing.Count > 0) return existing;

            _prompts.Say("There is no ledger yet. Run a repair run first — every other mode " +
                         "works from it.", Tone.Warn);
            return Array.Empty<LedgerRow>();
        }

        _prompts.Blank();
        _prompts.Say("Reading CRM. This writes nothing.", Tone.Muted);

        var scanned = await (await BuilderAsync(ct)).BuildAsync(ct);

        // Once, on the first run after correct documents stopped entering the sheet. A scan that
        // no longer produces them cannot un-write the ones already on disk, so they are cleared
        // out here — and after that first run there is nothing left saying skip and this does
        // nothing at all.
        var tidied = LegacyCleanup.Apply(existing, scanned);
        if (tidied.Removed > 0 || tidied.Kept > 0 || tidied.NoFile > 0)
        {
            existing = tidied.Rows;

            _prompts.Blank();

            if (tidied.Removed > 0)
                _prompts.Say($"{tidied.Removed} row(s) were correct and had never been worked " +
                             "on. They have been taken out of the sheet.", Tone.Good);

            if (tidied.NoFile > 0)
                _prompts.Say($"{tidied.NoFile} row(s) had no file path at all. They have been " +
                             "taken out of the sheet.", Tone.Good);

            if (tidied.Kept > 0)
                _prompts.Say($"{tidied.Kept} row(s) said skip but had work recorded against " +
                             "them. Their verdicts now say what actually happened.", Tone.Muted);
        }

        // There is one ledger per environment for its whole life. A fresh scan updates it; it
        // never replaces it, because replacing it would throw away every verdict typed into it
        // and every final state the runs have recorded.
        //
        // Even a brand-new ledger goes through the merge, so that the rule keeping correct
        // documents out of the sheet is applied in exactly one place rather than two.
        var merged = LedgerMerge.Into(existing, scanned);

        if (existing.Count == 0)
        {
            _ledger.Write(merged.Rows);

            _prompts.Say($"{merged.Rows.Count} document(s) written to {_ledger.Path}", Tone.Good);
            SayWhatWasLeftOut(merged);
            SayHowManyHaveNoFile(merged);

            return merged.Rows;
        }

        ReportMoved(merged);
        AskAboutStillWrong(merged);
        AskAboutStranded(merged.Rows);
        AskAboutVerdicts(merged);
        var typeThemMyself = AskAboutExcluded(merged);
        _ledger.Write(merged.Rows);

        _gone = merged.Gone.Select(r => r.DocId).ToHashSet();

        _prompts.Section("The ledger is up to date with CRM");
        _prompts.Field("file", _ledger.Path, Tone.Muted);
        if (_ledger.OtherPath is { } other) _prompts.Field("other services", other, Tone.Muted);

        _prompts.Say($"{merged.Rows.Count} row(s): {merged.Added} new since last time, " +
                     $"{merged.Refreshed} refreshed, {merged.Protected} left as they are because " +
                     "they have already been worked on or you closed them.");

        SayWhatWasLeftOut(merged);
        SayHowManyHaveNoFile(merged);

        // A document type moved to another service in CRM moves its row between the two files.
        // Silently that reads as a row vanishing from one and appearing in the other.
        if (_ledger.LastMoved > 0)
            _prompts.Say($"{_ledger.LastMoved} row(s) changed file — their document type was " +
                         "moved to a different service in CRM.", Tone.Muted);

        SayHowManyFiles(merged.Rows);
        ReportGone(merged);

        return typeThemMyself ? ReadBackHandEdits(merged.Rows) : merged.Rows;
    }

    /// <summary>
    /// Which services this whole sitting is about.
    ///
    /// Asked once, after the environment and before the menu, because every mode needs the
    /// answer — the delete step and Check it all have to know which file to open, not just the
    /// repair run.
    /// </summary>
    /// <param name="remembered">What this environment was last worked on, as its default.</param>
    public async Task<LedgerScope> AskAboutScopeAsync(LedgerScope remembered, CancellationToken ct)
    {
        var ours = _appConfig.ServiceCatalogues;

        // Listed, not tucked behind '?'. Being asked to choose between "8 services" and
        // "everything" without being shown which eight is not a choice anybody can make.
        _prompts.Section($"The {ours.Count} services this tool works on");

        foreach (var id in ours)
            _prompts.Bullet(AppConfig.NameOf(id) ?? $"{id}   (added by hand; not one of ours)",
                Tone.Muted);

        _prompts.Blank();

        var answer = new Asker(_prompts).Ask("Which services are you working on?", new[]
        {
            new Choice("The services we work on", $"the {ours.Count} listed above",
                "Reads every document filed under those services and no others. This is the " +
                "list in config.json; a service can be added to it by hand."),

            new Choice("Every service catalogue in CRM", "asks CRM what there is first",
                "Reads the full list of service catalogues from CRM, tells you what it found, " +
                "and asks again before it reads a single document. The other services are kept " +
                "in a file of their own, so the one you usually work in stays quick to save.")
        }, defaultIndex: remembered == LedgerScope.All ? 1 : 0);

        if (answer.Kind != AnswerKind.Chosen || answer.Index == 0) return LedgerScope.Ours;

        return await ConfirmTheWholeLotAsync(ct);
    }

    /// <summary>
    /// Shows what "everything" actually means before anything is read. In pre-prod it is over
    /// fifty thousand documents and hours of CRM reads, which is not a thing to find out
    /// afterwards.
    ///
    /// The list comes from the environment chosen at the start of the sitting and from nowhere
    /// else — <see cref="_read"/> is built against that environment's URL and credentials — so
    /// the catalogues shown are the ones in the environment about to be worked on.
    /// </summary>
    private async Task<LedgerScope> ConfirmTheWholeLotAsync(CancellationToken ct)
    {
        _prompts.Blank();
        _prompts.Say($"Asking {_envName} what service catalogues it has. This writes nothing.",
            Tone.Muted);

        IReadOnlyList<(Guid Id, string Name)> catalogues;
        try
        {
            catalogues = await _read.GetServiceCataloguesAsync(ct);
        }
        catch (Exception problem)
        {
            _prompts.Blank();
            _prompts.Warn($"CRM could not be asked: {problem.Message}", Tone.Warn);
            _prompts.Say("Staying on the services we work on.", Tone.Muted);
            return LedgerScope.Ours;
        }

        if (catalogues.Count == 0)
        {
            _prompts.Say("CRM returned no service catalogues, so there is nothing to widen to. " +
                         "Staying on the services we work on.", Tone.Warn);
            return LedgerScope.Ours;
        }

        _everyCatalogue = catalogues.Select(c => c.Id).ToList();

        var ours = _appConfig.ServiceCatalogues.ToHashSet();
        var others = catalogues.Count(c => !ours.Contains(c.Id));

        _prompts.Section($"Every service catalogue in {_envName}");
        _prompts.Say($"{_envName} has {catalogues.Count} service catalogues, {others} of them " +
                     $"outside the {ours.Count} this tool was built for.");
        _prompts.Blank();

        if (_prompts.YesNo("  Work across all of them?", defaultYes: false)) return LedgerScope.All;

        _prompts.Say("Staying on the services we work on.", Tone.Muted);
        return LedgerScope.Ours;
    }

    /// <summary>
    /// The scan, scoped to whichever services this sitting is about.
    ///
    /// Across every catalogue the list is read from CRM rather than from config — the whole
    /// point of that choice is to work on what is actually there — which is why this cannot be
    /// settled in the constructor.
    /// </summary>
    private async Task<LedgerBuilder> BuilderAsync(CancellationToken ct)
    {
        if (_ledger.Scope == LedgerScope.Ours) return _builder;

        _everyCatalogue ??= (await _read.GetServiceCataloguesAsync(ct)).Select(c => c.Id).ToList();

        return new LedgerBuilder(_read, _everyCatalogue, _env.CrmUrl);
    }

    /// <summary>
    /// The services this sitting counts as in scope — the eight, or everything CRM has.
    /// Used to tell a row that has left the scope from a document that has been deleted.
    /// </summary>
    private IReadOnlyList<Guid> InScope =>
        _ledger.Scope == LedgerScope.All && _everyCatalogue is { } all
            ? all
            : _appConfig.ServiceCatalogues;

    /// <summary>
    /// How many documents the scan found nothing wrong with, and so did not write.
    ///
    /// Said rather than shown. It is the majority of any environment — 96 of the dev ledger's
    /// 410 rows were this before the change — and there is nothing to say about any one of them.
    /// But a sheet that is suddenly a quarter shorter wants explaining.
    /// </summary>
    private void SayWhatWasLeftOut(Merged merged)
    {
        if (merged.NotAdded == 0) return;

        _prompts.Say($"{merged.NotAdded} document(s) are already filed correctly and are not in " +
                     "the sheet.", Tone.Muted);
    }

    /// <summary>
    /// How many documents name no file at all.
    ///
    /// Said, not shown. Across every catalogue in pre-prod it is roughly thirty-seven thousand,
    /// because two thirds of that environment is documents naming no file — and thirty-seven
    /// thousand rows do not read as a list, they read as wallpaper. There is nothing this tool
    /// can do with one either, so they no longer enter the sheet at all.
    /// </summary>
    private void SayHowManyHaveNoFile(Merged merged)
    {
        if (merged.NoFile == 0) return;

        _prompts.Say($"{merged.NoFile} document(s) have no file path at all and are not in the " +
                     "sheet. There is nothing this tool can do with them — no path to diagnose " +
                     "and no file to move.", Tone.Muted);
    }

    /// <summary>
    /// How many distinct files those rows are, when it is not the same as how many rows.
    ///
    /// One mocd_documentfile can be the file of several mocd_document records, so correcting one
    /// row can settle another. Without this the sheet reads as more work than there is, and a
    /// row going green on its own looks like a bug.
    /// </summary>
    private void SayHowManyFiles(IReadOnlyList<LedgerRow> rows)
    {
        var files = rows.Where(r => r.DocFileId != Guid.Empty)
            .Select(r => r.DocFileId)
            .Distinct()
            .Count();

        var shared = rows.Count(r => r.DocFileId != Guid.Empty) - files;
        if (shared <= 0) return;

        _prompts.Say($"{rows.Count} rows · {files} distinct files — {shared} row(s) share a file " +
                     "with another row, so correcting one settles the other.", Tone.Muted);
    }

    /// <summary>
    /// Rows CRM no longer returns, and which kind of gone they are.
    ///
    /// The two causes want different reactions. A document type moved to another service, or a
    /// change to the services this tool is configured for, takes whole blocks of rows out of
    /// scope and is not a problem at all. A document that is still in scope and simply absent
    /// has been deleted, and that is worth knowing.
    /// </summary>
    private void ReportGone(Merged merged)
    {
        if (merged.Gone.Count == 0) return;

        var inScope = InScope.Select(c => c.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outOfScope = merged.Gone
            .Where(r => r.ServiceCatalogueId.Length > 0 && !inScope.Contains(r.ServiceCatalogueId))
            .ToList();

        var absent = merged.Gone.Except(outOfScope).ToList();

        _prompts.Blank();

        // Counted, not listed. Narrowing from every catalogue back to the eight puts tens of
        // thousands of rows in here at once, and a bullet each says nothing a single number does
        // not — it only buries the rows below that are worth reading.
        if (outOfScope.Count > 0)
            _prompts.Say($"{outOfScope.Count} row(s) belong to services you did not scan this " +
                         "run — nothing will act on them.", Tone.Muted);

        // These are a different thing: still in scope, and CRM did not return them. That is a
        // document that has been deleted — so it is said louder, but still as a number. The
        // rows are in the sheet, which is where anybody would go to read them.
        if (absent.Count > 0)
            _prompts.Say($"{absent.Count} row(s) are in scope and CRM did not return them — " +
                         "deleted documents. Kept in the sheet; nothing will act on them.",
                Tone.Warn);
    }

    /// <summary>
    /// Holds the run while the operator types verdicts into the sheet, then reads the sheet back.
    ///
    /// Without this the rows just written would be worked on from memory and the edits would only
    /// be noticed on the next scan — which is a poor answer to somebody who has just asked to
    /// decide those rows by hand.
    /// </summary>
    private IReadOnlyList<LedgerRow> ReadBackHandEdits(IReadOnlyList<LedgerRow> fallback)
    {
        _prompts.Blank();
        _prompts.Say("Open the ledger, set the verdict on those rows to fix, skip or whatever " +
                     "you mean, then save and close it.", Tone.Warn);
        _prompts.Field("file", _ledger.Path, Tone.Muted);

        if (!_prompts.YesNo("  Saved and closed? Answer yes and I will read it back.",
                defaultYes: true))
        {
            _prompts.Say("Carrying on with the ledger as it stands.", Tone.Muted);
            return fallback;
        }

        // Excel may still hold the file for a moment after it closes; the store asks and retries.
        var reread = _ledger.Read();
        if (reread.Count == 0) return fallback;

        _prompts.Say($"{reread.Count} row(s) read back from the sheet.", Tone.Good);
        return Reconciled(reread);
    }

    /// <summary>
    /// Offers a way back for rows marked ignore by hand that no run has touched.
    ///
    /// Excluding a row is meant to be deliberate, and nothing in the tool overrides it. But one
    /// fill-down in Excel can set four hundred cells to ignore in a second, and until now the
    /// only way back was to edit every one of them by hand — the tool refuses to touch an
    /// excluded verdict precisely so that a real exclusion sticks.
    ///
    /// So it asks. Leave them, give them the verdict the scan would have written, or send them
    /// to review and type the answers in the sheet.
    /// </summary>
    /// <returns>True when the operator wants to type the verdicts themselves.</returns>
    private bool AskAboutExcluded(Merged merged)
    {
        if (merged.Excluded.Count == 0) return false;

        _prompts.Section($"{merged.Excluded.Count} row(s) are marked ignore and have never been " +
                         "worked on", Tone.Warn);

        // What the scan makes of them, as a count per verdict rather than a row each. Which
        // rows they are is a question for the sheet; how many and of what kind is what decides
        // the answer to the question below.
        _prompts.Say(Tally(merged.Excluded), Tone.Muted);
        _prompts.Blank();

        var answer = new Asker(_prompts).Ask("What should happen to them?", new[]
        {
            new Choice("Leave them ignored", "they are excluded on purpose",
                "Nothing changes. No run will look at those rows, and the next scan will ask " +
                "this again. Use this when you meant to exclude them."),

            new Choice("Give them the verdict the scan makes", "undo the exclusion",
                "Each of those rows takes the verdict a fresh look at CRM would write — fix, " +
                "skip or review, whichever it is. Use this when a fill-down in Excel set the " +
                "column to ignore by accident. Their final state and notes are untouched, and " +
                "you can still edit any of them afterwards."),

            new Choice("Let me type them myself", "send them to review and pause",
                "Each of those rows is set to review, which no run acts on, and the tool waits " +
                "while you open the sheet and write the verdict you want on each one — fix, " +
                "skip or anything else in the list. It reads the sheet back when you are done.")
        }, defaultIndex: 0);

        if (answer.Kind != AnswerKind.Chosen || answer.Index == 0)
        {
            _prompts.Say("Left excluded.", Tone.Muted);
            _prompts.Blank();
            return false;
        }

        if (!Confirm(merged.Excluded.Count))
        {
            _prompts.Say("Left excluded.", Tone.Muted);
            _prompts.Blank();
            return false;
        }

        if (answer.Index == 1)
        {
            LedgerMerge.TakeScanVerdicts(merged.Excluded);
            _prompts.Say($"{merged.Excluded.Count} row(s) given the scan's verdict.", Tone.Muted);
            _prompts.Blank();
            return false;
        }

        LedgerMerge.MarkForReview(merged.Excluded);
        _prompts.Say($"{merged.Excluded.Count} row(s) set to review for you to decide.",
            Tone.Muted);
        return true;
    }

    /// <summary>
    /// Brings the ledger back in step with the change journal before anything reads it.
    ///
    /// The journal is written before the ledger, so it always knows at least as much. Where a
    /// run was cut short — a crash, a stop, a ledger locked in Excel — the work is done and the
    /// ledger does not say so, and the next run would redo it: uploading the file a second time
    /// and orphaning the copy it made before. This closes that gap, every time, without asking
    /// CRM anything.
    /// </summary>
    private IReadOnlyList<LedgerRow> Reconciled(IReadOnlyList<LedgerRow> rows)
    {
        var recovered = LedgerRecovery.Apply(rows, _journal.Read());

        if (recovered.MissingFromJournal > 0)
        {
            _prompts.Section("The change journal is missing history the ledger has", Tone.Warn);
            _prompts.Say($"{recovered.MissingFromJournal} row(s) record work the journal has " +
                         "never heard of. The journal is only ever appended to and is written " +
                         "before the ledger, so it cannot fall behind on its own — it has been " +
                         "deleted or replaced.");
            _prompts.Blank();
            _prompts.Field("journal", _journal.Path, Tone.Muted);
            _prompts.Bullet("Nothing is lost for those rows: Redo reads the crm.json snapshot in " +
                            "each document's backup folder, not the journal.", Tone.Muted);
            _prompts.Bullet("But the journal can no longer rebuild the ledger if the workbook is " +
                            "damaged. Leave it alone from here and it will fill in again.",
                Tone.Muted);
            _prompts.Blank();
        }

        if (recovered.Rows == 0) return rows;

        _prompts.Section($"{recovered.Rows} row(s) were out of step with the change journal",
            Tone.Warn);
        _prompts.Say("A run did the work but was cut short before it could record it. The " +
                     "journal had it, so the ledger has been put right. Each row says what " +
                     "changed, in its notes.");

        _ledger.Write(rows);
        _prompts.Blank();

        return rows;
    }

    /// <summary>
    /// Asks CRM which of the rows marked fix are already right, and offers to settle them in one
    /// question before any work starts.
    ///
    /// Asked here rather than row by row inside the loop: Quiet and Unattended have nobody to
    /// interrupt, and a question per document in Watch would bury the answer among four hundred
    /// others. One list, one answer, the same in all three modes — and nothing is written to the
    /// ledger until the answer is given.
    /// </summary>
    private async Task SettleAlreadyCorrectAsync(
        IReadOnlyList<LedgerRow> working, IReadOnlyList<LedgerRow> whole, CancellationToken ct)
    {
        var fixes = working.Count(r => r.Verdict2() == RowVerdict.Fix &&
                                       r.State() is not (RowState.Corrected or RowState.Deleted));
        if (fixes == 0) return;

        _prompts.Blank();
        _prompts.Say($"Asking CRM about the {fixes} row(s) marked fix. This writes nothing.",
            Tone.Muted);

        var scan = await new AlreadyCorrect(_read, _files).FindAsync(working, ct);

        if (scan.MissingFile.Count > 0)
        {
            _prompts.Blank();
            _prompts.Say($"{scan.MissingFile.Count} row(s) are filed correctly in CRM but their " +
                         "file is not on the server. They keep their verdict, so the run will " +
                         "put the file back from the old copy.", Tone.Warn);
        }

        if (scan.Settled.Count == 0) return;

        _prompts.Blank();
        _prompts.Say($"{scan.Settled.Count} row(s) marked fix are already correct in CRM. " +
                     Outstanding(scan.Settled), Tone.Warn);
        _prompts.Blank();

        if (!_prompts.YesNo($"  Settle those {scan.Settled.Count} row(s)? Nothing is uploaded " +
                            "and nothing in CRM is changed.", defaultYes: true))
        {
            _prompts.Say("Left as they are. The run will look at each one again.", Tone.Muted);
            _prompts.Blank();
            return;
        }

        AlreadyCorrect.Apply(scan.Settled);
        _ledger.Write(whole);

        _prompts.Say($"{scan.Settled.Count} row(s) settled without uploading anything.", Tone.Good);
        _prompts.Blank();
    }

    /// <summary>
    /// How many of the settled rows still owe a delete, in one line.
    ///
    /// That is the only part of "why" worth saying up front: it is the difference between a row
    /// that is finished and a row the delete step still has work on. Which rows, and the reason
    /// for each, goes into their notes as they are settled.
    /// </summary>
    private static string Outstanding(IReadOnlyList<AlreadyCorrectRow> settled)
    {
        var pending = settled.Count(s => s.As == SettleAs.PendingDelete);

        return pending == 0
            ? "Nothing is outstanding on any of them."
            : $"{pending} of them still have their old file on the server, so the delete step " +
              "will have work to do.";
    }

    /// <summary>
    /// Says which rows describe a file that has moved since the ledger last looked, and what
    /// moved it.
    ///
    /// One ledger row is one mocd_document, but a correction writes to the mocd_documentfile
    /// record — and several documents can share one of those. Correcting one row therefore
    /// moves the file under every row that shares its record, rows nothing in the ledger says
    /// were touched. Without this the operator meets them as a bare verdict disagreement: the
    /// ledger says fix, CRM says skip, no reason given, and the correct answer looks wrong.
    ///
    /// Nothing is asked here. The rows keep their own old path, and the verdict question that
    /// follows is where they are decided.
    /// </summary>
    private void ReportMoved(Merged merged)
    {
        if (merged.Moved.Count == 0) return;

        // Two causes, and the split between them is the whole content of this report: one is a
        // sibling row in this very sheet, the other is somebody outside the tool. Each row says
        // which in its notes, so the screen only needs the counts.
        var bySibling = merged.Moved.Count(m => m.CorrectedBy is not null);
        var byOthers = merged.Moved.Count - bySibling;

        _prompts.Blank();
        _prompts.Say($"{merged.Moved.Count} row(s) point at a file that has moved since the " +
                     $"ledger last looked — {bySibling} moved by another row sharing the same " +
                     $"record, {byOthers} by something outside this tool. Their old path is " +
                     "kept, and nothing will be uploaded for them twice.", Tone.Warn);
    }

    /// <summary>
    /// What a fresh look at CRM makes of a set of rows, counted per verdict.
    ///
    /// This replaced a bullet per row. Ten of four hundred told the operator nothing they could
    /// act on — the rows are in the sheet, which is where anybody goes to read them — while the
    /// question underneath, which is the thing that actually needs answering, was pushed off the
    /// screen. The shape of the set is what decides that answer, and it fits on one line.
    /// </summary>
    private static string Tally(IReadOnlyList<VerdictDisagreement> rows) =>
        "The scan makes them: " + string.Join(", ", rows
            .GroupBy(d => d.ScanSays.Length == 0 ? "already correct" : d.ScanSays)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}")) + ".";

    /// <summary>
    /// Rows the ledger calls finished that CRM still files under the wrong catalogue.
    ///
    /// Nothing else in the tool would ever mention them. A done row is refreshed for its name
    /// alone, the repair run skips it, and Check it all only looks at old files — so a done that
    /// is not true is the one mistake invisible in every mode. The scan has already read the
    /// answer for these rows, so saying it costs nothing.
    /// </summary>
    private void AskAboutStillWrong(Merged merged)
    {
        if (merged.StillWrong.Count == 0) return;

        _prompts.Section($"{merged.StillWrong.Count} row(s) are marked finished, but CRM still " +
                         "files them under the wrong catalogue", Tone.Warn);

        _prompts.Blank();

        var answer = new Asker(_prompts).Ask("What should happen to them?", new[]
        {
            new Choice("Keep them as they are", "I know about these",
                "Nothing changes. The rows stay finished and no run will look at them. The next " +
                "scan will say this again."),

            new Choice("Put them back to fix", "let the run correct them",
                "Each row takes the verdict fix and loses its final state, so the repair run " +
                "works on it like any other. Its old path and backup are untouched, so Redo can " +
                "still reach what was done before."),

            new Choice("Put them to review", "I will look at each one",
                "Each row takes the verdict review and loses its final state. No run acts on a " +
                "review row — it sorts near the top of the sheet and waits for you.")
        }, defaultIndex: 0);

        if (answer.Kind != AnswerKind.Chosen || answer.Index == 0)
        {
            _prompts.Say("Left as they are.", Tone.Muted);
            _prompts.Blank();
            return;
        }

        if (!Confirm(merged.StillWrong.Count))
        {
            _prompts.Say("Left as they are.", Tone.Muted);
            _prompts.Blank();
            return;
        }

        var verdict = answer.Index == 1 ? RowVerdicts.Fix : RowVerdicts.Review;

        foreach (var (row, _) in merged.StillWrong)
        {
            row.Verdict = verdict;
            row.FinalState = string.Empty;
        }

        _prompts.Say($"{merged.StillWrong.Count} row(s) set to {verdict}.", Tone.Muted);
        _prompts.Blank();
    }

    /// <summary>
    /// Puts a hand-edited verdict on a finished row to the operator, once for the lot.
    ///
    /// The thing worth saying out loud is the thing nobody expects: the delete step reads the
    /// final state column, so the verdict they have just typed will not stop it. Everything else
    /// here follows from that one sentence.
    /// </summary>
    /// <returns>True when something was changed, so the caller knows to write the ledger.</returns>
    private bool AskAboutStranded(IReadOnlyList<LedgerRow> rows)
    {
        var stranded = VerdictGuard.Find(rows);
        if (stranded.Count == 0) return false;

        var pending = stranded.Count(s => s.DeleteStillPending);
        var reRuns = stranded.Count(s => s.WouldReRun);

        _prompts.Blank();
        _prompts.Warn($"{stranded.Count} finished row(s) have a verdict typed over them — work " +
                      "was carried out, and the verdict now says something else.", Tone.Warn);

        // The two that change what happens next. Everything else about these rows is in the
        // sheet; these two sentences are not, and neither is obvious.
        if (pending > 0)
            _prompts.Warn($"{pending} still say \"{RowStates.Corrected}\", and Delete old files " +
                          "reads that column, not the verdict — their old files will go anyway " +
                          "(unless the verdict says redo, which holds them back).", Tone.Danger);

        if (reRuns > 0)
            _prompts.Warn($"{reRuns} say fix on a document already corrected — working those " +
                          "again uploads a second copy.", Tone.Danger);

        _prompts.Blank();

        var answer = new Asker(_prompts).Ask("What do you want done with them?", new[]
        {
            new Choice("Put them back to done", "the final state is the record",
                "The work was carried out. The final state column says so, and it is written by " +
                "the run rather than by hand. This makes the verdict agree with it."),

            new Choice("Keep ignore, and stop the delete", $"{pending} row(s) have a delete to stop",
                "Writes ignore into the final state as well, so Delete old files walks past " +
                "them and the old files stay on the server. This is the only answer that " +
                "actually stops the delete.",
                Enabled: pending > 0,
                DisabledNote: "none of these still have an old file waiting to be deleted."),

            new Choice("Leave them exactly as typed", "and let the old files go",
                "Nothing is changed. The old file of any row still awaiting deletion will be " +
                "deleted the next time you run Delete old files.")
        }, defaultIndex: 0);

        if (answer.Kind != AnswerKind.Chosen) return false;

        var choice = answer.Index switch
        {
            1 => GuardChoice.StopTheDelete,
            2 => GuardChoice.LeaveAsTyped,
            _ => GuardChoice.BackToDone
        };

        if (!Confirm(stranded.Count)) return false;

        VerdictGuard.Apply(stranded, choice);
        _prompts.Blank();

        return true;
    }

    /// <summary>
    /// Where the ledger's verdicts and a fresh scan disagree, asks which to believe.
    ///
    /// Both can be right. A verdict typed by hand is a decision — reading the reason and moving
    /// a row from review to fix is the whole point of the column — and silently overruling it
    /// makes the column unusable. But a scan can also be newer: somebody may have corrected a
    /// document type in CRM since, and the ledger would go on offering work that no longer
    /// exists.
    ///
    /// Asked as two questions, because one list held two opposite situations and gave them one
    /// answer: rows where the operator is overruling the tool, and rows where the tool has
    /// learned something since. Nobody can answer both at once.
    /// </summary>
    private void AskAboutVerdicts(Merged merged)
    {
        var overruling = merged.Disagreements
            .Where(d => string.Equals(d.ScanSays, RowVerdicts.Fix, StringComparison.Ordinal))
            .ToList();

        var newer = merged.Disagreements.Except(overruling).ToList();

        AskAboutOneGroup(overruling,
            "row(s) where you have told the tool not to fix something it wants fixed",
            "Keep my answers",
            "Those rows keep what you typed. Use this when you have read the reason and decided " +
            "— that is what the column is for. Everything else on the row is still brought up " +
            "to date from CRM: the paths, the catalogues, the reason and the solution.",
            "Fix them after all",
            "Each of those rows takes 'fix', the verdict a fresh look at CRM writes for them, " +
            "and the next run will correct them. Any verdict you typed on those rows is lost.");

        AskAboutOneGroup(newer,
            "row(s) the tool no longer thinks need what the ledger says",
            "Keep my answers",
            "Those rows keep what you typed. Use this when you mean to work on them anyway.",
            "Take what CRM says",
            "The verdict column of those rows is replaced by what a fresh look at CRM makes of " +
            "them — usually because somebody has corrected a document type since the ledger was " +
            "built. Any verdict you typed on those rows is lost.");
    }

    /// <summary>One half of the verdict question. Nothing is written unless it is confirmed.</summary>
    private void AskAboutOneGroup(IReadOnlyList<VerdictDisagreement> rows, string heading,
        string keepLabel, string keepHelp, string takeLabel, string takeHelp)
    {
        if (rows.Count == 0) return;

        _prompts.Section($"{rows.Count} {heading}", Tone.Warn);
        _prompts.Say(Tally(rows), Tone.Muted);
        _prompts.Blank();

        var answer = new Asker(_prompts).Ask("Which should the ledger keep?", new[]
        {
            new Choice(keepLabel, "leave my own answers alone", keepHelp),
            new Choice(takeLabel, "overwrite those verdicts with the scan's", takeHelp)
        }, defaultIndex: 0);

        if (answer.Kind == AnswerKind.Chosen && answer.Index == 1 && Confirm(rows.Count))
        {
            LedgerMerge.TakeScanVerdicts(rows);
            _prompts.Say($"{rows.Count} verdict(s) taken from CRM.", Tone.Muted);
        }
        else
        {
            _prompts.Say("Left as the file had them.", Tone.Muted);
        }

        _prompts.Blank();
    }

    /// <summary>
    /// The last gate before a verdict column is rewritten in bulk.
    ///
    /// The list above it shows ten rows and a count. Answering for four hundred on the strength
    /// of ten is easy to do by accident, and there is no undo but typing them all back.
    /// </summary>
    private bool Confirm(int count)
    {
        if (count <= 1) return true;

        _prompts.Blank();
        _prompts.Warn($"This changes the verdict on {count} row(s). It cannot be undone from " +
                      "inside the tool.", Tone.Danger);

        return _prompts.YesNo($"  Change all {count}?", defaultYes: false, Tone.Danger);
    }

    /// <summary>
    /// Work through the whole ledger, or just one document the operator names.
    ///
    /// One document takes exactly the same six steps as any other row — it is the same loop over
    /// a list of one — so there is no second code path that could behave differently from the
    /// one the operator has watched four hundred times.
    /// </summary>
    /// <param name="rows">Everything in the ledger. Replaced when the operator reloads.</param>
    /// <returns>The rows to work on, and the whole ledger they came from.</returns>
    private (IReadOnlyList<LedgerRow> Working, IReadOnlyList<LedgerRow> Whole)
        NarrowToOneDocument(IReadOnlyList<LedgerRow> rows)
    {
        while (true)
        {
            var fixable = rows.Count(r => r.Verdict2() == RowVerdict.Fix);

            var how = new Asker(_prompts).Ask("What do you want to work on?", new[]
            {
                new Choice("From the file", $"every row marked fix — {fixable} of {rows.Count}",
                    "Works down the ledger in order, acting on every row whose verdict says fix " +
                    "and walking past the rest."),

                new Choice("One document", "type its GUID",
                    "The same six steps, for the single row whose doc id you give. Useful for " +
                    "re-trying one document without opening the whole run."),

                new Choice("Read the sheet again", "pick up edits you just made",
                    "Reads the workbook from disk again and comes back to this question with " +
                    "the counts refreshed. A reload only reads — it never writes the sheet back.")
            }, defaultIndex: 0);

            if (how.Kind != AnswerKind.Chosen) return (Array.Empty<LedgerRow>(), rows);

            if (how.Index == 2)
            {
                var fresh = RereadLedger();
                if (fresh.Count > 0) rows = fresh;
                continue;
            }

            if (how.Index == 0) return (rows, rows);

            _prompts.Blank();
            var typed = _prompts.ReadLine("  Document GUID").Trim();

            if (!Guid.TryParse(typed, out var wanted))
            {
                _prompts.Say($"'{typed}' is not a GUID.", Tone.Warn);
                return (Array.Empty<LedgerRow>(), rows);
            }

            var found = rows.Where(r => r.DocId == wanted).ToList();

            if (found.Count == 0)
            {
                // Not in the ledger means the ledger is older than the document, or the document
                // is outside the services in scope. Guessing is worse than saying so.
                _prompts.Say($"No row in the ledger has doc id {wanted}. If the document is " +
                             "new, update the ledger from CRM first; if it belongs to a service " +
                             "this run is not scoped to, it will never appear.", Tone.Warn);
                return (Array.Empty<LedgerRow>(), rows);
            }

            _prompts.Say($"Row {found[0].Row} — {found[0].DocName}", Tone.Muted);
            return (found, rows);
        }
    }

    /// <summary>
    /// The ledger from disk again, put through everything a freshly-opened one goes through: the
    /// journal replay, and the guard on a verdict typed over a finished row. A reload that
    /// skipped those would quietly behave differently from leaving the mode and coming back.
    /// </summary>
    private IReadOnlyList<LedgerRow> RereadLedger()
    {
        // The operator has just been editing in Excel, so this is the likeliest moment of all
        // for the file still to be open.
        if (!WaitUntilWritable()) return Array.Empty<LedgerRow>();

        var rows = Reconciled(_ledger.Read());
        if (rows.Count > 0 && AskAboutStranded(rows)) _ledger.Write(rows);

        return rows;
    }

    /// <summary>
    /// Whether this entry into the repair run should ask CRM at all.
    ///
    /// The rescan reads every document in scope — 4,510 in dev, and far more across every
    /// catalogue — plus a catalogue-name lookup per row. When the sheet is already in front of
    /// you and you only want to carry on working through it, all of that is spent for nothing.
    ///
    /// Skipping it is safe. Before a single byte is uploaded the run still asks CRM, row by row,
    /// whether that document is now filed correctly, so a stale sheet cannot cause a second copy
    /// of a file — only a row settled at the start of the run instead of by the scan.
    /// </summary>
    private bool AskWhetherToRescan()
    {
        if (!_ledger.Exists) return true;

        var when = File.GetLastWriteTime(_ledger.Path).ToString("yyyy-MM-dd HH:mm");

        // Only to put a number on the choice. The caller has already waited for the file to be
        // closed, so this should not fail — but a question that cannot be asked must not take
        // the run down with it.
        int rows;
        try
        {
            rows = _ledger.Read().Count;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            _prompts.Say($"The ledger could not be read just now ({problem.Message}), so this " +
                         "run will update it from CRM.", Tone.Warn);
            return true;
        }

        // Widening to every catalogue for the first time leaves the other services with no sheet
        // at all. Offering the rows we do have would present one file as the whole scope that
        // was asked for, and the run would work through eight services without ever saying the
        // other forty-six were not in it.
        var built = _ledger.EveryFileBuilt;

        if (!built)
        {
            _prompts.Blank();
            _prompts.Warn("The other services have never been scanned, so the ledger does not " +
                          "yet cover the scope you chose. This run has to read CRM.", Tone.Warn);
            _prompts.Say("Nothing already in the sheet is lost by that: a scan refreshes the " +
                         "facts and adds what is new, and your verdicts, final states and notes " +
                         "are kept exactly as they are.", Tone.Muted);
        }

        var answer = new Asker(_prompts).Ask("Where should this run get its rows?", new[]
        {
            new Choice("Use the ledger as it is", $"{rows} row(s), last written {when}",
                "Goes straight to the work, with the sheet exactly as it is on disk. Nothing is " +
                "asked of CRM until the run reaches a document — and every row is still checked " +
                "against CRM before anything is uploaded.",
                Enabled: built,
                DisabledNote: "the other services have no sheet yet, so this would cover only " +
                              "part of the scope you chose."),

            new Choice("Update it from CRM first", "reads every document in scope",
                "Reads every document under the services this run is scoped to, classifies " +
                "them, and merges the answers into the sheet without discarding anything you " +
                "have typed. This is the slow one.")
        }, defaultIndex: built ? 0 : 1);

        return answer.Kind != AnswerKind.Chosen || answer.Index == 1;
    }

    /// <summary>
    /// Asked once per entry into the repair run, and never again while the loop runs. One answer
    /// governs every document in the ledger.
    /// </summary>
    private WatchMode AskHowCloselyToWatch()
    {
        var answer = new Asker(_prompts).Ask("How closely do you want to watch?", new[]
        {
            new Choice("Watch", "every step, and a pause after each document",
                "The full step-by-step account of each document, then a bare enter before the " +
                "next one begins. For the first few, or for production."),
            new Choice("Quiet", "one line per document",
                "One line each, straight through. The question about the two copies is the only " +
                "thing that interrupts it. This is the one to use over hundreds of rows."),
            new Choice("Unattended", "nothing is asked at all",
                "One line each, and you are never shown the two copies. The four automated " +
                "checks decide on their own — including a round-trip download compared byte for " +
                "byte against your backup. It still stops and asks on an error. Use it once you " +
                "have read the ledger and agree with it.")
        }, defaultIndex: 1);

        return answer.Kind == AnswerKind.Chosen ? (WatchMode)answer.Index : WatchMode.Quiet;
    }

    /// <summary>
    /// Rows carrying a copy that was uploaded and then left behind.
    ///
    /// The upload happens before the two files are shown, so answering "quit" or "they do not
    /// match" leaves a complete file on the server with nothing pointing at it — and the row
    /// still says fix, so the next run uploads another one. Nobody would notice that from a
    /// note buried in a cell, and the files pile up one per attempt.
    /// </summary>
    private void ReportAbandonedUploads(IReadOnlyList<LedgerRow> rows)
    {
        var abandoned = rows.Where(r => r.SupersededPaths.Length > 0).ToList();
        if (abandoned.Count == 0) return;

        var copies = abandoned.Sum(r => r.SupersededPaths.Split(';',
            StringSplitOptions.RemoveEmptyEntries).Length);

        // One line. The paths are in the "superseded paths" column of the sheet, which is where
        // somebody would go to act on them anyway, and the run reuses them by itself — so a
        // heading, a list of ten and three paragraphs of explanation was telling the operator
        // at length about something already handled.
        _prompts.Say($"{copies} copy/copies from an earlier run are on the server unpointed-at, " +
                     $"across {abandoned.Count} row(s). They will be reused, not uploaded again.",
            Tone.Muted);
    }

    /// <summary>
    /// Said once, on entering the repair run — not per document, which would train the operator
    /// to skip past it.
    /// </summary>
    private void WarnAboutInPlace()
    {
        _prompts.Section("Before this starts", Tone.Warn);
        _prompts.Say("Corrections are written into the existing mocd_documentfile record. A " +
                     "portal-created record's id equals its original file id, and after a " +
                     "correction it no longer will.");
        _prompts.Blank();
        _prompts.Bullet("Nothing in CRM reads a file path off the record id — DownloadDocument " +
                        "takes a FilePath, and its callers read mocd_filepath off the record — " +
                        "so this breaks the convention, not any code path.", Tone.Muted);
        _prompts.Bullet("There is no new record and nothing is repointed.", Tone.Muted);
        _prompts.Bullet("The ledger and the backup folder are the only route back. Do not delete " +
                        "them.", Tone.Muted);
        _prompts.Blank();
    }

    // ---- the four modes ----

    public LedgerActions Actions(CancellationToken outer) => new(
        RepairAsync: async ct =>
        {
            // Before the question, not after: the question reads the ledger to say how many
            // rows it holds, and reading a workbook Excel has open throws rather than waiting.
            if (!WaitUntilWritable()) return StepOutcome.Of("Stopped. Nothing has been changed.");

            var opened = await OpenLedgerAsync(mayRebuild: AskWhetherToRescan(), ct);
            if (opened.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            // The whole ledger comes back too, because a reload inside the question replaces it.
            var (working, all) = NarrowToOneDocument(opened);

            // Documents CRM stopped returning. The scan says nothing will act on them, and
            // this is what makes that true — the loop reads the verdict and nothing else.
            var rows = working.Where(r => !_gone.Contains(r.DocId)).ToList();
            if (rows.Count == 0) return StepOutcome.Of("Nothing to work on.");

            if (_dryRun)
                return StepOutcome.Of($"Dry run — {rows.Count} row(s) would be worked on.",
                    $"ledger → {_ledger.Path}");

            await SettleAlreadyCorrectAsync(rows, all, ct);

            ReportAbandonedUploads(rows);

            WarnAboutInPlace();

            var progress = new RunProgress(_prompts, AskHowCloselyToWatch());

            var summary = await new RepairRun(_ledger,
                    new RepairOneRow(_files, _read, _write, _backups, _journal, _prompts,
                        _opener, progress, _envName),
                    progress, _prompts, _errors)
                .RunAsync(rows, all, ct);

            var details = new List<string> { $"ledger → {_ledger.Path}" };

            foreach (var skip in summary.Skips) details.Add($"skipped: {skip.Count} — {skip.Why}");

            if (summary.SettledBySibling > 0)
                details.Add($"{summary.SettledBySibling} row(s) settled because another row " +
                            "corrected the file they share — nothing uploaded for them");

            if (summary.Declined > 0)
                details.Add($"{summary.Declined} left alone because you said the copies did not match");

            if (summary.Failed > 0) details.Add($"errors → {_errors.Path}");

            if (summary.Unrecognised.Count > 0)
            {
                // A typo in a verdict cell is worth knowing about, but which cells is a question
                // for the sheet — sort the verdict column and they are all together.
                _errors.AppendLines("verdict cells nobody recognises", summary.Unrecognised);
                details.Add($"{summary.Unrecognised.Count} verdict cell(s) NOT UNDERSTOOD — " +
                            $"listed in {_errors.Path}");
            }

            if (summary.Stopped) details.Add("THE RUN WAS STOPPED at your request.");

            return new StepOutcome(
                $"{summary.Corrected} corrected, {summary.Declined} left alone, " +
                $"{summary.Failed} failed.", details);
        },

        DeleteAsync: async ct =>
        {
            var opened = await OpenLedgerAsync(mayRebuild: false, ct);
            if (opened.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            if (_dryRun) return StepOutcome.Of("Dry run: delete is irreversible, so nothing was done.");

            // The irreversible one, so the reload matters most here: this is the moment somebody
            // remembers a row they meant to close before the files went.
            var (gate, rows) = LedgerGate.Ask(_prompts,
                "Delete the old files of every corrected row?", opened, RereadLedger);

            if (gate == GateAnswer.Cancel) return StepOutcome.Of("Nothing was deleted.");

            var summary = await new DeleteOldFiles(_files, _read, _journal, _ledger, _prompts, _errors)
                .RunAsync(rows, _env.IsProduction, ct);

            return new StepOutcome(
                summary.Aborted
                    ? "Nothing was deleted."
                    : $"{summary.Deleted} old file(s) deleted, {summary.Refused} refused." +
                      (summary.AlreadyGone > 0
                          ? $" {summary.AlreadyGone} of them were already gone."
                          : string.Empty) +
                      (summary.Checked > 0
                          ? $" {summary.Checked} checked afterwards, {summary.FoundAgain} still " +
                            "on the server."
                          : string.Empty),
                Filed("why the delete was refused", summary.Reasons));
        },

        RedoAsync: async ct =>
        {
            var opened = await OpenLedgerAsync(mayRebuild: false, ct);
            if (opened.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            if (_dryRun) return StepOutcome.Of("Dry run: redo writes to CRM, so nothing was done.");

            var (gate, rows) = LedgerGate.Ask(_prompts,
                "Put those records back the way they were?", opened, RereadLedger);

            if (gate == GateAnswer.Cancel) return StepOutcome.Of("Nothing was reverted.");

            var summary = await new RedoRun(_files, _write, _backups, _journal, _ledger, _prompts)
                .RunAsync(rows, ct);

            return new StepOutcome(
                $"{summary.Reverted} record(s) put back, {summary.Refused} refused.",
                Filed("why the redo was refused", summary.Reasons));
        },

        CheckAsync: async ct =>
        {
            var opened = await OpenLedgerAsync(mayRebuild: false, ct);
            if (opened.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            var (gate, rows) = LedgerGate.Ask(_prompts,
                "Check every row against both systems?", opened, RereadLedger);

            if (gate == GateAnswer.Cancel) return StepOutcome.Of("Nothing was checked.");

            // Said before it starts, because this asks the file server and CRM about every row
            // it takes and prints nothing until it has finished. On a ledger of thousands that
            // is a long silence, and knowing whether it is forty rows or four thousand is the
            // difference between waiting and giving up.
            var willCheck = rows.Count(r =>
                r.State() is RowState.Deleted or RowState.Corrected && r.OldFilePath.Length > 0);

            if (willCheck == 0)
                return StepOutcome.Of("No row has an old file to check — nothing has been " +
                                      "corrected or deleted yet.");

            _prompts.Blank();
            _prompts.Say($"Checking {willCheck} row(s) against the file server and CRM. This " +
                         "writes nothing, and says nothing until it is done.", Tone.Muted);

            var summary = await new CheckItAll(_files, _read, _env.CrmUrl).RunAsync(rows, ct);

            return new StepOutcome(
                summary.NotAsExpected == 0
                    ? $"{summary.Checked} checked, all as the ledger says."
                    : $"{summary.Checked} checked, {summary.AsExpected} correct, " +
                      $"{summary.NotAsExpected} NOT AS EXPECTED.",
                Filed("what the check found", summary.Problems));
        },

        LookAsync: async asked =>
        {
            var reports = await new LookupCommand(_files, _read, _backups, _prompts, _ledger)
                .RunAsync(asked, outer);

            return new StepOutcome($"{reports.Count} looked up. Nothing was changed.",
                reports.Select(Summarise).ToList());
        });

    /// <summary>
    /// Puts a step's reasons in the error log and hands back one line naming the count.
    ///
    /// They used to be printed under the step, a line each, which was also the only copy of
    /// them: reading forty refusals meant reading them there and then, and they pushed the rest
    /// of the summary off the screen. The log keeps them for as long as anybody wants.
    /// </summary>
    private IReadOnlyList<string> Filed(string heading, IReadOnlyList<string> reasons)
    {
        if (reasons.Count == 0) return Array.Empty<string>();

        _errors.AppendLines(heading, reasons);

        return new[] { $"{reasons.Count} reason(s) → {_errors.Path}" };
    }

    /// <summary>One line per look-up, for the summary under the step.</summary>
    private static string Summarise(LookupReport report)
    {
        // A system that never answered is not a system that said no.
        if (report.CrmProblem is { } crm) return $"{report.Asked} — CRM could not be asked: {crm}";
        if (report.ServerProblem is { } server)
            return $"{report.Asked} — the file server could not be asked: {server}";

        var onDisk = report.OnTheServer switch
        {
            true => "on the file server",
            false => "NOT on the file server",
            _ => "no path to check"
        };

        var inCrm = report.Record is not null || report.PointingAtThePath.Count > 0
            ? "still referred to in CRM"
            : "not referred to in CRM";

        return $"{report.Asked} — {onDisk}, {inCrm}";
    }

    // ---- the direct commands ----

    public async Task<int> RunDirectAsync(CommandLineOptions options, CancellationToken ct)
    {
        var actions = Actions(ct);

        var run = options.Command switch
        {
            "repair" => actions.RepairAsync,
            "delete" => actions.DeleteAsync,
            "redo" => actions.RedoAsync,
            "check" => actions.CheckAsync,
            _ => null
        };

        if (run is null)
        {
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return 2;
        }

        var outcome = await run(ct);

        Console.WriteLine(outcome.Headline);
        foreach (var line in outcome.Details) Console.WriteLine($"  {line}");

        return 0;
    }

    /// <summary>Only so the wizard can be told where things are. Nothing else reads it.</summary>
    public string LedgerPath => _ledger.Path;

    private AppConfig Config => _appConfig;
}
