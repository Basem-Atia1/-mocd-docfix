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

    private readonly LedgerStore _ledger;
    private readonly ChangeJournal _journal;
    private readonly ErrorLog _errors;
    private readonly LedgerBuilder _builder;

    /// <summary>
    /// Documents CRM stopped returning at the last scan. Held so the repair run can pass over
    /// them: the message says nothing will act on them, and until now the loop knew nothing
    /// about it and would have tried to correct one that still said fix.
    /// </summary>
    private HashSet<Guid> _gone = new();

    public Session(AppConfig appConfig, ResolvedEnvironment env, string envName,
        IPrompts prompts, bool dryRun)
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

        _ledger = new LedgerStore(Path.Combine(reports, $"repair-{envName}.xlsx"));
        _journal = new ChangeJournal(Path.Combine(reports, $"changes-{envName}.jsonl"));
        _errors = new ErrorLog(Path.Combine(reports, $"errors-{envName}.txt"));

        _crmHttp = CrmHttp.Create(env);
        _read = new CrmReadClient(_crmHttp, env);
        _write = new CrmWriteClient(_crmHttp);

        _fileHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _files = new FileServiceClient(_fileHttp, env);

        _builder = new LedgerBuilder(_read, appConfig.ServiceCatalogues, env.CrmUrl);

        // A locked ledger mid-run is recoverable and must never end a run: by the time a row is
        // written, its file has been uploaded and its CRM record changed, so failing to record
        // that is the one outcome worse than waiting.
        _ledger.AskToRetry = WaitForExcel;

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
        // Excel holds an exclusive lock on an open workbook, and the ledger is rewritten after
        // every completed row. Finding that out now is far kinder than finding out after the
        // first document has already been uploaded and cannot be recorded.
        //
        // It waits rather than giving up: having the ledger open is the normal thing to be doing
        // a moment before a run, and making the operator start the whole mode again to fix a
        // ten-second problem is a punishment, not a safeguard.
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
                return Array.Empty<LedgerRow>();
            }
        }

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

        var scanned = await _builder.BuildAsync(ct);

        // There is one ledger per environment for its whole life. A fresh scan updates it; it
        // never replaces it, because replacing it would throw away every verdict typed into it
        // and every final state the runs have recorded.
        if (existing.Count == 0)
        {
            _ledger.Write(scanned);
            _prompts.Say($"{scanned.Count} document(s) written to {_ledger.Path}", Tone.Good);
            return scanned;
        }
        var merged = LedgerMerge.Into(existing, scanned);
        ReportMoved(merged);
        AskAboutStillWrong(merged);
        AskAboutVerdicts(merged);
        var typeThemMyself = AskAboutExcluded(merged);
        _ledger.Write(merged.Rows);

        _gone = merged.Gone.Select(r => r.DocId).ToHashSet();

        _prompts.Section("The ledger is up to date with CRM");
        _prompts.Field("file", _ledger.Path, Tone.Muted);
        _prompts.Say($"{merged.Rows.Count} row(s): {merged.Added} new since last time, " +
                     $"{merged.Refreshed} refreshed, {merged.Protected} left as they are because " +
                     "they have already been worked on or you closed them.");

        SayHowManyFiles(merged.Rows);
        ReportGone(merged);

        return typeThemMyself ? ReadBackHandEdits(merged.Rows) : merged.Rows;
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

        var configured = _appConfig.ServiceCatalogues
            .Select(c => c.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _prompts.Blank();

        foreach (var row in merged.Gone.Take(10))
        {
            var outOfScope = row.ServiceCatalogueId.Length > 0 &&
                             !configured.Contains(row.ServiceCatalogueId);

            _prompts.Bullet(outOfScope
                ? $"{row.Ref()}: its service is not one this tool is set up for any more — out " +
                  "of scope, not missing. Kept, and nothing will act on it."
                : $"{row.Ref()}: CRM did not return this document. Kept in the ledger, but " +
                  "nothing will act on it.", Tone.Warn);
        }

        if (merged.Gone.Count > 10)
            _prompts.Bullet($"… and {merged.Gone.Count - 10} more", Tone.Muted);
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

        foreach (var d in merged.Excluded.Take(10))
            _prompts.Bullet($"{d.Row.Ref()}: ignored — the scan makes " +
                            $"it '{d.ScanSays}'{Because(d.ScanReason)}", Tone.Muted);

        if (merged.Excluded.Count > 10)
            _prompts.Bullet($"… and {merged.Excluded.Count - 10} more", Tone.Muted);

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
                     "journal had it, so the ledger has been put right:");
        _prompts.Blank();

        foreach (var note in recovered.Notes.Take(20)) _prompts.Bullet(note, Tone.Muted);
        if (recovered.Notes.Count > 20)
            _prompts.Bullet($"… and {recovered.Notes.Count - 20} more", Tone.Muted);

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
            _prompts.Section($"{scan.MissingFile.Count} row(s) are filed correctly in CRM, but " +
                             "their file is not on the server", Tone.Warn);

            foreach (var m in scan.MissingFile.Take(10))
                _prompts.Bullet($"{m.Row.Ref()}: nothing at {m.NowAt}", Tone.Muted);

            if (scan.MissingFile.Count > 10)
                _prompts.Bullet($"… and {scan.MissingFile.Count - 10} more", Tone.Muted);

            _prompts.Blank();
            _prompts.Say("They keep their verdict, so the run will put the file back from the " +
                         "old copy. If that has gone too, the row will fail and say so.",
                Tone.Muted);
            _prompts.Blank();
        }

        if (scan.Settled.Count == 0) return;

        _prompts.Section($"{scan.Settled.Count} row(s) marked fix are already correct in CRM",
            Tone.Warn);

        foreach (var s in scan.Settled.Take(10))
            _prompts.Bullet($"{s.Row.Ref()}: {Why(s)}", Tone.Muted);

        if (scan.Settled.Count > 10)
            _prompts.Bullet($"… and {scan.Settled.Count - 10} more", Tone.Muted);

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

    /// <summary>Why one row needs no work, said the way it should appear in the list.</summary>
    private static string Why(AlreadyCorrectRow settled) => settled.As switch
    {
        SettleAs.AlwaysRight => "the path never changed — nothing to correct, nothing to delete",
        SettleAs.BySibling =>
            $"its file was corrected by {settled.Sibling!.Ref()}, which shares the same record",
        SettleAs.PendingDelete => "already corrected — its old file is still on the server",
        _ => "already corrected — its old file has gone"
    };

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

        _prompts.Section($"{merged.Moved.Count} row(s) point at a file that has moved since the " +
                         "ledger last looked", Tone.Warn);

        foreach (var m in merged.Moved.Take(10))
        {
            _prompts.Bullet(m.CorrectedBy is null
                ? $"{m.Row.Ref()}: its file is now at {m.NowAt}, and " +
                  "nothing in this ledger put it there"
                : $"{m.Row.Ref()}: the same document file record was " +
                  $"corrected by {m.CorrectedBy!.Ref()}, so this one is already correct too",
                Tone.Muted);
        }

        if (merged.Moved.Count > 10)
            _prompts.Bullet($"… and {merged.Moved.Count - 10} more", Tone.Muted);

        _prompts.Blank();
        _prompts.Say("Their old path is kept as it was, so the old file can still be found and " +
                     "deleted. Nothing will be uploaded for them a second time.", Tone.Muted);
        _prompts.Blank();
    }

    /// <summary>
    /// What the scan makes of a row, in words.
    ///
    /// A blank verdict is the scan saying there is nothing wrong with the document. It is what
    /// a correctly-filed row gets, and such rows are no longer written to the sheet at all —
    /// but an existing row can still turn correct, when somebody fixes it in CRM. "CRM says ''"
    /// is not a sentence, so this says the thing it means instead.
    /// </summary>
    private static string ScanSaid(string scanSays) =>
        scanSays.Length == 0
            ? "the scan no longer finds anything wrong with it"
            : $"CRM says '{scanSays}'";

    /// <summary>The scan's own words for a verdict, trimmed to fit one line of the report.</summary>
    private static string Because(string reason)
    {
        if (reason.Length == 0) return string.Empty;
        return reason.Length <= 90 ? $" — {reason}" : $" — {reason[..87]}…";
    }

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

        foreach (var d in merged.StillWrong.Take(10))
            _prompts.Bullet($"{d.Row.Ref()}: says '{d.Row.Verdict}'" +
                            (d.Row.FinalState.Length > 0 ? $" / '{d.Row.FinalState}'" : "") +
                            $"{Because(d.ScanReason)}", Tone.Muted);

        if (merged.StillWrong.Count > 10)
            _prompts.Bullet($"… and {merged.StillWrong.Count - 10} more", Tone.Muted);

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

        foreach (var d in rows.Take(10))
            _prompts.Bullet($"{d.Row.Ref()}: the ledger says " +
                            $"'{d.Row.Verdict}', {ScanSaid(d.ScanSays)}{Because(d.ScanReason)}",
                Tone.Muted);

        if (rows.Count > 10) _prompts.Bullet($"… and {rows.Count - 10} more", Tone.Muted);

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
    private IReadOnlyList<LedgerRow> NarrowToOneDocument(IReadOnlyList<LedgerRow> rows)
    {
        var fixable = rows.Count(r => r.Verdict2() == RowVerdict.Fix);

        var how = new Asker(_prompts).Ask("What do you want to work on?", new[]
        {
            new Choice("From the file", $"every row marked fix — {fixable} of {rows.Count}",
                "Works down the ledger in order, acting on every row whose verdict says fix and " +
                "walking past the rest."),
            new Choice("One document", "type its GUID",
                "The same six steps, for the single row whose doc id you give. Useful for " +
                "re-trying one document without opening the whole run.")
        }, defaultIndex: 0);

        if (how.Kind != AnswerKind.Chosen) return Array.Empty<LedgerRow>();
        if (how.Index == 0) return rows;

        _prompts.Blank();
        var typed = _prompts.ReadLine("  Document GUID").Trim();

        if (!Guid.TryParse(typed, out var wanted))
        {
            _prompts.Say($"'{typed}' is not a GUID.", Tone.Warn);
            return Array.Empty<LedgerRow>();
        }

        var found = rows.Where(r => r.DocId == wanted).ToList();

        if (found.Count == 0)
        {
            // Not in the ledger means the ledger is older than the document, or the document is
            // outside the seven services. Either way, guessing is worse than saying so.
            _prompts.Say($"No row in the ledger has doc id {wanted}. If the document is new, " +
                         "start a fresh ledger; if it belongs to a service this tool is not " +
                         "scoped to, it will never appear.", Tone.Warn);
            return Array.Empty<LedgerRow>();
        }

        _prompts.Say($"Row {found[0].Row} — {found[0].DocName}", Tone.Muted);
        return found;
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
            var all = await OpenLedgerAsync(mayRebuild: true, ct);
            if (all.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            // Documents CRM stopped returning. The scan says nothing will act on them, and
            // this is what makes that true — the loop reads the verdict and nothing else.
            var rows = NarrowToOneDocument(all).Where(r => !_gone.Contains(r.DocId)).ToList();
            if (rows.Count == 0) return StepOutcome.Of("Nothing to work on.");

            if (_dryRun)
                return StepOutcome.Of($"Dry run — {rows.Count} row(s) would be worked on.",
                    $"ledger → {_ledger.Path}");

            await SettleAlreadyCorrectAsync(rows, all, ct);

            WarnAboutInPlace();

            var progress = new RunProgress(_prompts, AskHowCloselyToWatch());

            var summary = await new RepairRun(_ledger,
                    new RepairOneRow(_files, _read, _write, _backups, _journal, _prompts,
                        _opener, progress, _envName),
                    progress, _prompts, _errors)
                .RunAsync(rows, all, ct);

            var details = new List<string> { $"ledger → {_ledger.Path}" };

            foreach (var skip in summary.Skips) details.Add($"skipped: {skip.Count} — {skip.Why}");

            if (summary.Declined > 0)
                details.Add($"{summary.Declined} left alone because you said the copies did not match");

            if (summary.Failed > 0) details.Add($"errors → {_errors.Path}");

            foreach (var odd in summary.Unrecognised.Take(10))
                details.Add($"VERDICT NOT UNDERSTOOD — {odd}");

            if (summary.Stopped) details.Add("THE RUN WAS STOPPED at your request.");

            return new StepOutcome(
                $"{summary.Corrected} corrected, {summary.Declined} left alone, " +
                $"{summary.Failed} failed.", details);
        },

        DeleteAsync: async ct =>
        {
            var rows = await OpenLedgerAsync(mayRebuild: false, ct);
            if (rows.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            if (_dryRun) return StepOutcome.Of("Dry run: delete is irreversible, so nothing was done.");

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
                summary.Reasons);
        },

        RedoAsync: async ct =>
        {
            var rows = await OpenLedgerAsync(mayRebuild: false, ct);
            if (rows.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            if (_dryRun) return StepOutcome.Of("Dry run: redo writes to CRM, so nothing was done.");

            var summary = await new RedoRun(_files, _write, _backups, _journal, _ledger, _prompts)
                .RunAsync(rows, ct);

            return new StepOutcome(
                $"{summary.Reverted} record(s) put back, {summary.Refused} refused.",
                summary.Reasons);
        },

        CheckAsync: async ct =>
        {
            var rows = await OpenLedgerAsync(mayRebuild: false, ct);
            if (rows.Count == 0) return StepOutcome.Of("Nothing in the ledger.");

            var summary = await new CheckItAll(_files, _read, _env.CrmUrl).RunAsync(rows, ct);

            return new StepOutcome(
                summary.NotAsExpected == 0
                    ? $"{summary.Checked} checked, all as the ledger says."
                    : $"{summary.Checked} checked, {summary.AsExpected} correct, " +
                      $"{summary.NotAsExpected} NOT AS EXPECTED.",
                summary.Problems);
        },

        LookAsync: async asked =>
        {
            var reports = await new LookupCommand(_files, _read, _backups, _prompts, _ledger)
                .RunAsync(asked, outer);

            return new StepOutcome($"{reports.Count} looked up. Nothing was changed.",
                reports.Select(Summarise).ToList());
        });

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
