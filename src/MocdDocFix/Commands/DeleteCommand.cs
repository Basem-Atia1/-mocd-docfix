using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;
using MocdDocFix.Verification;

namespace MocdDocFix.Commands;

public sealed record DeleteSummary(int Deleted, int Skipped, int Refused, bool Aborted, string? AbortReason);

/// <summary>
/// Phase 5 — the only irreversible step. Every candidate is re-verified against live state
/// immediately before its delete, because a report written an hour ago is a stale fact.
/// Note the vendor's delete is a GET (spec section 3.4), so it is called exactly once per file
/// and never retried automatically.
/// </summary>
public sealed class DeleteCommand
{
    private readonly IFileServiceClient _files;
    private readonly ICrmReadClient _read;
    private readonly ICrmWriteClient _write;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly IPrompts _prompts;
    private readonly DocumentReportStore? _reports;

    public DeleteCommand(IFileServiceClient files, ICrmReadClient read, ICrmWriteClient write,
        BackupStore backups, StateStore state, IPrompts prompts, DocumentReportStore? reports = null)
    {
        _files = files;
        _read = read;
        _write = write;
        _backups = backups;
        _state = state;
        _prompts = prompts;
        _reports = reports;
    }

    public async Task<DeleteSummary> RunAsync(string env, bool isProduction, CancellationToken ct)
    {
        var manifest = _backups.LoadManifest().ToDictionary(m => m.DocumentId);

        // Ask CRM what is actually true before deciding what is eligible. Without this the step
        // reads only its own notes, and a document whose migration succeeded in CRM but was
        // recorded as Failed can never become eligible — its old file and old record stay behind
        // for good, with the app reporting "nothing awaiting deletion" and no way to say why.
        await ReconcileWithCrmAsync(manifest.Values, ct);

        var latest = _state.LoadLatest();

        var candidates = latest.Values
            .Where(r => r.State == MigrationState.Repointed && manifest.ContainsKey(r.DocumentId))
            .ToList();

        if (candidates.Count == 0)
        {
            ExplainWhyNothingIsEligible(latest, manifest);
            return new DeleteSummary(0, 0, 0, false, null);
        }

        _prompts.Section($"About to permanently delete {candidates.Count} old file(s) from {env}",
            Tone.Danger);
        _prompts.Blank();

        foreach (var c in candidates.Take(20))
            _prompts.Info($"    {manifest[c.DocumentId].OldFilePath}", Tone.Muted);
        if (candidates.Count > 20)
            _prompts.Info($"    … and {candidates.Count - 20} more", Tone.Muted);

        _prompts.Blank();
        _prompts.Warn("This cannot be undone. Local backups keep the bytes, but a restored file " +
                      "cannot return to its original path.", Tone.Danger);
        _prompts.Blank();

        // A yes/no question, not a word to be typed back. The typed word was meant as friction
        // and turned into a trap: "delete" in the wrong case read as a refusal and abandoned the
        // whole run, which teaches the operator to distrust the answer rather than to weigh it.
        // The weight belongs in what is shown above the question, and it is all still shown.
        if (!_prompts.YesNo($"  Delete {candidates.Count} old file(s) from {env} now?",
                defaultYes: false, Tone.Danger))
        {
            return new DeleteSummary(0, 0, 0, true, "Answered no at the delete confirmation.");
        }

        // Only now, once the answer is yes, is it worth reading a document-by-document account —
        // and this is the last moment at which reading one can still change anything.
        WriteBriefing(candidates, manifest);

        var how = AskHowToWorkThrough(candidates.Count);
        if (how == HowToDelete.Stop)
        {
            _prompts.Blank();
            _prompts.Say("Nothing was deleted. Everything is exactly as it was.", Tone.Good);
            return new DeleteSummary(0, 0, 0, true, "Stopped after reading what would be deleted.");
        }

        // Production keeps a second question that cannot be answered by reflex: the count has to
        // be read off the screen and typed. It is digits, so there is no case to get wrong.
        if (isProduction &&
            !_prompts.TypedWord($"  PRODUCTION. Confirm the number of files to delete ({candidates.Count})",
                candidates.Count.ToString()))
        {
            return new DeleteSummary(0, 0, 0, true, "Operator did not confirm the production count.");
        }

        int deleted = 0, refused = 0, skipped = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = candidates[i];

            var entry = manifest[candidate.DocumentId];
            if (_state.IsAtLeast(candidate.DocumentId, MigrationState.Deleted)) { skipped++; continue; }

            if (how == HowToDelete.OneAtATime)
            {
                WriteOneDocument(i + 1, candidates.Count, entry, candidate);
                _prompts.Blank();

                if (!_prompts.YesNo("  Delete this one?", defaultYes: false, Tone.Danger))
                {
                    _prompts.Say("Left alone. Its old file and old record both stay.", Tone.Muted);
                    skipped++;
                    continue;
                }
            }

            // Before the question, not after it: the shared-path answer can itself delete the old
            // CRM record, so the proof that it IS the old record has to come first.
            var identity = await WhoseRecordIsThisAsync(entry, candidate, ct);
            if (identity is not null)
            {
                _prompts.Info($"  REFUSED {entry.OldFilePath} — {identity}", Tone.Danger);
                _state.Append(new StateRecord(candidate.DocumentId, MigrationState.Failed,
                    DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                    $"Delete refused: {identity}"));
                refused++;
                continue;
            }

            // Asked second, so a shared file becomes a decision rather than a bare refusal.
            switch (await AskAboutSharedPathAsync(entry, ct))
            {
                case SharedPathChoice.LeaveEverything:
                    _prompts.Info("    Left alone. Nothing was deleted for this document.", Tone.Warn);
                    refused++;
                    continue;

                case SharedPathChoice.CrmRecordOnly:
                    await _write.DeleteDocumentFileAsync(entry.OldFileId, ct);
                    _prompts.Info($"    Removed the old CRM record {entry.OldFileId}. The file stays.", Tone.Good);
                    _state.Append(new StateRecord(candidate.DocumentId, MigrationState.Deleted,
                        DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                        $"Deleted documentfile {entry.OldFileId} only — the file is shared, so it was kept."));
                    deleted++;
                    continue;
            }

            var refusal = await WhyNotSafeAsync(candidate, entry, ct);
            if (refusal is not null)
            {
                _prompts.Info($"  REFUSED {entry.OldFilePath} — {refusal}", Tone.Danger);
                _state.Append(new StateRecord(candidate.DocumentId, MigrationState.Failed,
                    DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                    $"Delete refused: {refusal}"));
                refused++;
                continue;
            }

            var outcome = await RemoveOldAsync(entry, ct);
            WriteRemovalLog(outcome.Log);
            WriteDeleteReport(entry, outcome);

            if (!outcome.Removed)
            {
                _state.Append(new StateRecord(candidate.DocumentId, MigrationState.Failed,
                    DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                    $"Delete failed: {outcome.Reason}"));
                refused++;
                continue;
            }

            _state.Append(new StateRecord(candidate.DocumentId, MigrationState.Deleted,
                DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                $"Deleted {entry.OldFilePath} and documentfile {entry.OldFileId}."));
            deleted++;
        }

        return new DeleteSummary(deleted, skipped, refused, false, null);
    }

    private enum HowToDelete { OneAtATime, Everything, Stop }

    /// <summary>
    /// The blow-by-blow account of one removal, coloured by what each line is: the last line is
    /// the outcome and the rest are the steps that led to it.
    /// </summary>
    private void WriteRemovalLog(IReadOnlyList<string> log)
    {
        foreach (var line in log)
        {
            var tone = line.Contains("RESULT", StringComparison.Ordinal)
                ? (line.Contains("done", StringComparison.Ordinal) ? Tone.Good : Tone.Danger)
                : line.Contains("STILL ON THE SERVER", StringComparison.Ordinal)
                    ? Tone.Danger
                    : Tone.Muted;

            _prompts.Info(line, tone);
        }
    }

    /// <summary>
    /// The document-by-document account of what the yes just agreed to.
    ///
    /// A list of twenty paths is not something anyone can check. What has to be checkable is the
    /// pairing: for each document, the file and record about to be destroyed, next to the file
    /// and record it will be left using. If those two are ever the same thing, the delete would
    /// take the only copy — the checks catch that, but the operator should be able to see it
    /// before anything is asked of the file server, not read about it in a refusal afterwards.
    /// </summary>
    private void WriteBriefing(IReadOnlyList<StateRecord> candidates,
        IReadOnlyDictionary<Guid, ManifestEntry> manifest)
    {
        _prompts.Section($"What will be deleted — {candidates.Count} document(s)", Tone.Danger);
        _prompts.Say("GOES is the old file and its old CRM record. STAYS is what the document " +
                     "points at now — it is not touched. The two should never be the same.");

        for (var i = 0; i < candidates.Count; i++)
            WriteOneDocument(i + 1, candidates.Count, manifest[candidates[i].DocumentId], candidates[i]);

        _prompts.Blank();
        _prompts.Say("Each one is also re-checked against CRM and the file server immediately " +
                     "before it goes, and refused there if anything has changed since this list " +
                     "was written.", Tone.Muted);
    }

    private void WriteOneDocument(int index, int total, ManifestEntry entry, StateRecord candidate)
    {
        _prompts.Blank();
        _prompts.Info($"  {index} of {total}   {entry.FileName ?? "(no file name)"}", Tone.Strong);

        if (!string.IsNullOrWhiteSpace(entry.DocumentTypeName))
            _prompts.Info($"      {entry.DocumentTypeName}", Tone.Muted);

        _prompts.Info($"      document       {entry.DocumentId}", Tone.Muted);
        _prompts.Blank();
        _prompts.Info($"      GOES   file    {entry.OldFilePath}", Tone.Danger);
        _prompts.Info($"      GOES   record  {entry.OldFileId}", Tone.Danger);
        _prompts.Info($"      STAYS  file    {candidate.NewFilePath ?? "(none recorded)"}", Tone.Good);
        _prompts.Info($"      STAYS  record  {candidate.NewFileId?.ToString() ?? "(none recorded)"}",
            Tone.Good);
    }

    /// <summary>
    /// One at a time by default. A run of one or two is the normal case here, and for those the
    /// extra question costs a keypress; for a run of hundreds the operator can say so once.
    /// </summary>
    private HowToDelete AskHowToWorkThrough(int count)
    {
        var answer = new Asker(_prompts).Ask($"How do you want to work through the {count}?", new[]
        {
            new Choice("One at a time", "show each one again and ask before it goes",
                "The safest way. Each document is shown on its own — what goes and what stays — " +
                "and answering no leaves that one entirely alone and moves to the next."),

            new Choice("All of them", $"delete all {count} without asking again",
                "Every one on the list above is deleted, each still re-checked against CRM and " +
                "the file server first and refused if anything has changed. You are not asked " +
                "again, except where a file turns out to be shared with another record."),

            new Choice("Stop", "nothing is deleted",
                "Leaves everything exactly as it is. The old files stay on the server, the old " +
                "records stay in CRM, and this step can be run again at any time.")
        }, defaultIndex: 0, allowBack: false);

        return answer.Kind == AnswerKind.Chosen
            ? (HowToDelete)answer.Index
            : HowToDelete.Stop;
    }

    /// <summary>
    /// Brings the state file into line with CRM, and says out loud what it changed. CRM is read
    /// only — nothing is written to the file server and nothing is deleted here.
    /// </summary>
    private async Task ReconcileWithCrmAsync(IEnumerable<ManifestEntry> manifest, CancellationToken ct)
    {
        var corrected = await Reconciler.SweepAsync(_read, _write, _state, manifest, ct);
        if (corrected.Count == 0) return;

        _prompts.Section("Checked against CRM first");
        _prompts.Say($"{corrected.Count} document(s) disagreed with what this tool had written " +
                     "down. CRM is the authority, so its answer wins.");

        foreach (var fix in corrected)
        {
            _prompts.Blank();
            _prompts.Info($"    {fix.FileName ?? fix.DocumentId.ToString()}", Tone.Strong);
            _prompts.Info($"      recorded as   {fix.Was}", Tone.Muted);
            _prompts.Info($"      CRM says      the document points at {fix.Now.RecordId}", Tone.Muted);
            _prompts.Info($"      its path      {fix.Now.FilePath}", Tone.Muted);
            _prompts.Info("      CORRECTED     the migration did finish. Now eligible for deletion.",
                Tone.Good);
        }

        _prompts.Blank();
    }

    /// <summary>
    /// Only a repointed document can have its old file deleted, so "nothing to delete" is usually
    /// a consequence of something that happened earlier. Saying only "nothing is awaiting
    /// deletion" leaves the operator to work that out — which is exactly what it cost once.
    /// </summary>
    private void ExplainWhyNothingIsEligible(
        IReadOnlyDictionary<Guid, StateRecord> latest, IReadOnlyDictionary<Guid, ManifestEntry> manifest)
    {
        _prompts.Section("Nothing is awaiting deletion");

        if (latest.Count == 0)
        {
            _prompts.Say("Nothing has been migrated yet — there is nothing that could be deleted.",
                Tone.Muted);
            return;
        }

        _prompts.Say("Only a document that has been repointed can have its old file removed. " +
                     "Here is where each one actually got to:");
        _prompts.Blank();

        foreach (var group in latest.Values.GroupBy(r => r.State).OrderBy(g => g.Key))
        {
            var tone = group.Key switch
            {
                MigrationState.Failed or MigrationState.Quarantined => Tone.Danger,
                MigrationState.Repointed or MigrationState.Deleted => Tone.Good,
                _ => Tone.Normal
            };

            _prompts.Info($"    {group.Count(),4}  {Describe(group.Key)}", tone);

            // The reason matters most where something went wrong, so name those individually.
            if (group.Key is not (MigrationState.Failed or MigrationState.Quarantined)) continue;

            foreach (var record in group.Take(10))
            {
                var name = manifest.TryGetValue(record.DocumentId, out var entry)
                    ? entry.FileName ?? record.DocumentId.ToString()
                    : record.DocumentId.ToString();

                _prompts.Info($"          {name}", Tone.Muted);
                if (!string.IsNullOrWhiteSpace(record.Detail))
                    foreach (var line in Screen.Wrap(record.Detail!, Screen.Width - 12))
                        _prompts.Info($"            {line}", Tone.Muted);
            }
        }

        _prompts.Blank();
        _prompts.Say("Put right whatever stopped them, run the upload step again, and the old " +
                     "files become eligible. Nothing has been lost in the meantime.");
    }

    private static string Describe(MigrationState state) => state switch
    {
        MigrationState.Pending => "not started",
        MigrationState.BackedUp => "backed up, but not yet uploaded",
        MigrationState.Uploaded => "uploaded, but not yet verified",
        MigrationState.Verified => "verified, but the document was not repointed",
        MigrationState.Repointed => "repointed — these ARE eligible",
        MigrationState.Deleted => "already done; the old file is gone",
        MigrationState.Quarantined => "QUARANTINED during backup, so never migrated",
        MigrationState.Failed => "FAILED, so not eligible",
        _ => state.ToString()
    };

    /// <param name="Removed">True when the file is gone from the server AND the CRM row is gone.</param>
    /// <param name="Log">What happened, line by line, for the operator to read.</param>
    private sealed record RemovalOutcome(bool Removed, string? Reason, IReadOnlyList<string> Log);

    private enum SharedPathChoice { NotShared, LeaveEverything, CrmRecordOnly }

    /// <summary>
    /// Looks for other mocd_documentfile records pointing at the same file, and if there are any,
    /// explains the problem and offers the two things that can sensibly be done about it.
    ///
    /// Deleting a shared file is never one of them: the other records would be left pointing at
    /// something that no longer exists, and nothing in this run would notice.
    /// </summary>
    private async Task<SharedPathChoice> AskAboutSharedPathAsync(ManifestEntry entry, CancellationToken ct)
    {
        var sharers = (await _read.FindDocumentFilesByPathAsync(entry.OldFilePath, ct))
            .Where(id => id != entry.OldFileId)
            .ToList();

        if (sharers.Count == 0) return SharedPathChoice.NotShared;

        _prompts.Section($"PROBLEM — {entry.FileName} is a shared file", Tone.Warn);
        _prompts.Info($"    {entry.OldFilePath}", Tone.Muted);
        _prompts.Blank();
        _prompts.Say($"is also referenced by {sharers.Count} other mocd_documentfile record(s):");
        foreach (var id in sharers.Take(10)) _prompts.Info($"      {id}", Tone.Muted);
        if (sharers.Count > 10) _prompts.Info($"      … and {sharers.Count - 10} more", Tone.Muted);
        _prompts.Blank();
        _prompts.Warn("Deleting the file would leave those records pointing at nothing, and their " +
                      "documents would stop opening. So the file will NOT be deleted.");
        _prompts.Blank();
        _prompts.Say("Two things can be done instead:");
        _prompts.Bullet("Leave everything. The old file and its CRM record both stay. Costs " +
                        "nothing; the old row remains as clutter.", Tone.Muted);
        _prompts.Bullet("Delete only this document's old CRM record, and keep the file. This " +
                        "document is already repointed, so it loses nothing, and the other " +
                        "records keep working because the file is still there.", Tone.Muted);

        var answer = new Asker(_prompts).Ask("What should happen to this one?", new[]
        {
            new Choice("Leave everything", "the file and the old record both stay",
                "Nothing is removed. Re-run the delete step later if the other records get " +
                "migrated too, at which point the file stops being shared."),
            new Choice("Delete the CRM record only", "keep the file, remove this old row",
                "The file stays on the server for the other records. This document already " +
                "points at its new file, so removing its old row changes nothing it depends on.")
        }, defaultIndex: 0, allowBack: false);

        return answer.Kind == AnswerKind.Chosen && answer.Index == 1
            ? SharedPathChoice.CrmRecordOnly
            : SharedPathChoice.LeaveEverything;
    }

    /// <summary>
    /// Looks before and after. A delete endpoint answering "success" is not evidence that the
    /// file has gone, so the file is checked first, deleted, then checked again. The CRM record
    /// is removed only once the file is confirmed absent, so the two can never disagree.
    /// </summary>
    private async Task<RemovalOutcome> RemoveOldAsync(ManifestEntry entry, CancellationToken ct)
    {
        var log = new List<string>
        {
            "",
            $"  {entry.FileName}",
            $"    path        {entry.OldFilePath}",
            $"    CRM record  {entry.OldFileId}"
        };

        var before = await _files.DownloadAsync(entry.OldFilePath, ct);
        var wasThere = before.Success && !string.IsNullOrEmpty(before.Data?.File);

        log.Add(wasThere
            ? $"    before      found on the server, {Bytes(before.Data!.File!):N0} bytes"
            : "    before      NOT on the server — nothing there to delete");

        if (wasThere)
        {
            var call = await _files.DeleteAsync(entry.OldFilePath, ct);
            log.Add(call.Success
                ? "    delete      the file server accepted the request"
                : $"    delete      the file server refused: {call.Message}");

            if (!call.Success)
            {
                log.Add("    RESULT      NOT deleted. The CRM record was left alone.");
                return new RemovalOutcome(false, call.Message ?? "the file server refused", log);
            }

            // The only proof that counts.
            var after = await _files.DownloadAsync(entry.OldFilePath, ct);
            if (after.Success && !string.IsNullOrEmpty(after.Data?.File))
            {
                log.Add("    after       STILL ON THE SERVER — the delete did not take effect");
                log.Add("    RESULT      NOT deleted. The CRM record was left alone.");
                return new RemovalOutcome(false, "the file is still on the server after the delete", log);
            }

            log.Add("    after       confirmed gone from the server");
        }

        await _write.DeleteDocumentFileAsync(entry.OldFileId, ct);
        log.Add($"    CRM         mocd_documentfile {entry.OldFileId} deleted");
        log.Add("    RESULT      done — file and CRM record both removed");

        return new RemovalOutcome(true, null, log);
    }

    private static int Bytes(string base64)
    {
        try { return Convert.FromBase64String(base64).Length; }
        catch (FormatException) { return 0; }
    }

    /// <summary>The same log, kept in the document's own reports folder.</summary>
    private void WriteDeleteReport(ManifestEntry entry, RemovalOutcome outcome)
    {
        _reports?.Write(entry.DocumentId, entry.FileName, "04-delete",
            outcome.Removed
                ? "STEP 5 — DELETE: the old file and its CRM record were removed"
                : "STEP 5 — DELETE: NOT DONE",
            new (string, string?)[]
            {
                ("Old path", entry.OldFilePath),
                ("Old CRM record", entry.OldFileId.ToString()),
                ("Result", outcome.Removed ? "deleted" : $"not deleted — {outcome.Reason}"),
                ("Your backup", "the bytes are still in the backup folder, under old\\"),
                ("At", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            },
            outcome.Log.Where(l => l.Trim().Length > 0));
    }

    /// <summary>
    /// Deletes one document's old file, re-running the same safety checks as the bulk step.
    /// Used by the migrate step when the operator chooses to remove a file straight after
    /// repointing it, so there is one implementation of "is this safe to delete", not two.
    /// </summary>
    /// <returns>Null when it was deleted, otherwise the reason it was refused.</returns>
    public async Task<string?> DeleteOneAsync(Guid documentId, CancellationToken ct)
    {
        if (_state.IsAtLeast(documentId, MigrationState.Deleted)) return null;

        var entry = _backups.LoadManifest().FirstOrDefault(m => m.DocumentId == documentId);
        if (entry is null) return "no backup record for this document";

        // Same reconciliation as the bulk step, for the same reason: the answer to "is this
        // repointed" belongs to CRM, not to the note this tool left itself.
        await ReconcileWithCrmAsync(new[] { entry }, ct);

        if (!_state.LoadLatest().TryGetValue(documentId, out var candidate) ||
            candidate.State != MigrationState.Repointed)
        {
            return "the document is not in the repointed state";
        }

        var refusal = await WhyNotSafeAsync(candidate, entry, ct);
        if (refusal is not null)
        {
            _state.Append(new StateRecord(documentId, MigrationState.Failed,
                DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                $"Delete refused: {refusal}"));
            return refusal;
        }

        var outcome = await RemoveOldAsync(entry, ct);
        WriteRemovalLog(outcome.Log);
        WriteDeleteReport(entry, outcome);

        // The log goes into the document's own folder too, so the record of what happened
        // survives the terminal scrolling away.
        _backups.Folder(documentId, entry.FileName).AppendSection("THE OLD FILE — delete attempted",
            outcome.Log
                .Where(l => l.Contains("    ", StringComparison.Ordinal))
                .Select(l => (l.Trim().Split("  ", 2)[0], (string?)l.Trim().Split("  ", 2).Last()))
                .ToList());

        if (!outcome.Removed)
        {
            _state.Append(new StateRecord(documentId, MigrationState.Failed,
                DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
                $"Delete failed: {outcome.Reason}"));
            return outcome.Reason;
        }

        _state.Append(new StateRecord(documentId, MigrationState.Deleted,
            DateTimeOffset.UtcNow, candidate.NewFileId, candidate.NewFilePath,
            $"Deleted {entry.OldFilePath} and documentfile {entry.OldFileId}."));

        return null;
    }

    /// <summary>
    /// Proves the thing about to be removed is the thing that was backed up. Null means it is.
    ///
    /// Every other check in this step reasons about the NEW file — does it download, does it
    /// hash, does the document point at it. These two are the only ones that look at what is
    /// being DELETED, by the two facts written down at backup time: the old record's id and the
    /// old file's path. They are checked together because either alone can be satisfied by the
    /// wrong thing.
    /// </summary>
    private async Task<string?> WhoseRecordIsThisAsync(
        ManifestEntry entry, StateRecord candidate, CancellationToken ct)
    {
        var notTheSame = Verifier.OldAndNewAreDifferentFiles(
            entry.OldFilePath, candidate.NewFilePath, entry.OldFileId, candidate.NewFileId);
        if (!notTheSame.Passed) return notTheSame.Detail;

        var stillOurs = Verifier.OldRecordIsStillTheOneWeBackedUp(
            entry.OldFileId, entry.OldFilePath,
            await _read.GetRawRecordAsync("mocd_documentfiles", entry.OldFileId, ct));

        return stillOurs.Passed ? null : stillOurs.Detail;
    }

    /// <summary>Re-runs the safety checks against live state. Null means safe to delete.</summary>
    private async Task<string?> WhyNotSafeAsync(StateRecord candidate, ManifestEntry entry, CancellationToken ct)
    {
        if (candidate.NewFileId is null || string.IsNullOrWhiteSpace(candidate.NewFilePath))
            return "no new file recorded";

        if (await WhoseRecordIsThisAsync(entry, candidate, ct) is { } wrongThing) return wrongThing;

        var download = await _files.DownloadAsync(candidate.NewFilePath, ct);
        if (!download.Success || download.Data?.File is null)
            return $"the new file no longer downloads ({download.Message})";

        byte[] newBytes;
        try { newBytes = Convert.FromBase64String(download.Data.File); }
        catch (FormatException) { return "the new file did not come back as valid base64"; }

        if (!string.Equals(Verifier.OurHash(newBytes), entry.OurHash, StringComparison.OrdinalIgnoreCase))
            return "the new file's content no longer matches the backup";

        var linked = await _write.GetDocumentFileLinkAsync(entry.DocumentId, ct);
        if (linked != candidate.NewFileId)
            return $"the document points at '{linked?.ToString() ?? "null"}', not the new file";

        // And the record it points at must itself name the new file. Without this, a record whose
        // mocd_filepath still holds the old path passes every other check — and deleting the old
        // file then leaves the document pointing at something that no longer exists.
        var recordPath = ReadFilePath(
            await _read.GetRawRecordAsync("mocd_documentfiles", candidate.NewFileId.Value, ct));

        var pathCheck = Verifier.FileRecordPointsAtTheNewFile(candidate.NewFilePath!, recordPath);
        if (!pathCheck.Passed) return pathCheck.Detail;

        // One file can be referenced by more than one mocd_documentfile. Deleting it would break
        // every record except the one we migrated, and nothing else in the run would notice.
        var sharers = (await _read.FindDocumentFilesByPathAsync(entry.OldFilePath, ct))
            .Where(id => id != entry.OldFileId)
            .ToList();

        if (sharers.Count > 0)
        {
            return $"{sharers.Count} other CRM record(s) still point at this same file — " +
                   string.Join(", ", sharers.Take(5)) +
                   (sharers.Count > 5 ? ", …" : "") +
                   ". Deleting it would break them.";
        }

        return null;
    }

    private static string? ReadFilePath(string? recordJson)
    {
        if (string.IsNullOrWhiteSpace(recordJson)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(recordJson);
            return json.RootElement.TryGetProperty("mocd_filepath", out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
