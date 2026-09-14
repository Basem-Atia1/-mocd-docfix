using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

/// <param name="OnServer">Null when the file server could not be asked.</param>
/// <param name="InCrm">Any record still refers to the old file — the record itself, or another.</param>
/// <param name="Answered">False when one of the two systems did not answer at all.</param>
/// <param name="AsExpected">
/// True when both systems say what this document's state says they should: gone once it was
/// deleted, still there while it is only repointed.
/// </param>
public sealed record OldFileResult(
    Guid DocumentId,
    string? FileName,
    Guid OldFileId,
    string? OldPath,
    MigrationState State,
    bool? OnServer,
    bool InCrm,
    bool Answered,
    string Verdict,
    bool AsExpected);

public sealed record OldFileSummary(
    int Checked, int AsExpected, int NotAsExpected, IReadOnlyList<OldFileResult> Results);

/// <summary>
/// The closing question of a run, asked of the OLD file rather than the new one: is it really
/// off the file server, and is its mocd_documentfile really out of CRM?
///
/// It is the same check as the menu's "Is this file still there?", run for every document the
/// run touched, because a run that reports five deletions is still only reporting its own
/// belief. This asks both systems, changes nothing in either, and says where the answer differs
/// from what the tool recorded.
/// </summary>
public sealed class OldFileCheckCommand
{
    private readonly LookupCommand _lookup;
    private readonly BackupStore _backups;
    private readonly StateStore _state;
    private readonly IPrompts _prompts;
    private readonly DocumentReportStore? _reports;

    public OldFileCheckCommand(LookupCommand lookup, BackupStore backups, StateStore state,
        IPrompts prompts, DocumentReportStore? reports = null)
    {
        _lookup = lookup;
        _backups = backups;
        _state = state;
        _prompts = prompts;
        _reports = reports;
    }

    public async Task<OldFileSummary> RunAsync(CancellationToken ct)
    {
        var latest = _state.LoadLatest();
        var results = new List<OldFileResult>();

        foreach (var entry in _backups.LoadManifest())
        {
            ct.ThrowIfCancellationRequested();

            if (!latest.TryGetValue(entry.DocumentId, out var record)) continue;

            // Only documents that actually got as far as having a new file. Anything earlier
            // has no old file to have finished with — its file is simply its file.
            if (record.State is not (MigrationState.Repointed or MigrationState.Deleted)) continue;

            var report = await _lookup.FindAsync(entry.OldFileId.ToString(), ct);
            var result = Judge(entry, record, report);

            results.Add(result);
            Write(result);
            Record(result, report);
        }

        return new OldFileSummary(results.Count, results.Count(r => r.AsExpected),
            results.Count(r => !r.AsExpected), results);
    }

    /// <summary>
    /// What the two answers mean, read against what the run said it did. The same pair of facts
    /// is good news for a deleted document and bad news for one that was only repointed, so the
    /// state has to be part of the judgement rather than a label printed beside it.
    /// </summary>
    private static OldFileResult Judge(ManifestEntry entry, StateRecord record, LookupReport report)
    {
        var inCrm = report.Record is not null || report.PointingAtThePath.Count > 0;
        var deleted = record.State == MigrationState.Deleted;
        var answered = report.CrmProblem is null && report.ServerProblem is null
                       && report.OnTheServer is not null;

        string verdict;
        bool asExpected;

        if (!answered)
        {
            var which = report.CrmProblem is not null ? "CRM" : "the file server";
            verdict = $"NOT ANSWERED — {which} could not be asked, so nothing here says whether " +
                      "the old file has gone.";
            asExpected = false;
        }
        else if (deleted)
        {
            asExpected = report.OnTheServer is false && !inCrm;
            verdict = asExpected
                ? "GONE — correctly off the file server and out of CRM."
                : "STILL THERE — recorded as deleted, but " +
                  Both(report.OnTheServer is true, inCrm) + ".";
        }
        else
        {
            asExpected = report.OnTheServer is true && inCrm;
            verdict = asExpected
                ? "STILL THERE — as it should be. It was repointed, not deleted; the delete " +
                  "step can remove it."
                : "MISSING — nothing recorded deleting it, but " +
                  Gone(report.OnTheServer is false, !inCrm) + ".";
        }

        return new OldFileResult(entry.DocumentId, entry.FileName, entry.OldFileId,
            report.Path ?? entry.OldFilePath, record.State, report.OnTheServer, inCrm,
            answered, verdict, asExpected);
    }

    private static string Both(bool onServer, bool inCrm) =>
        (onServer, inCrm) switch
        {
            (true, true) => "the file is still on the server and CRM still has a record for it",
            (true, false) => "the file is still on the server",
            _ => "CRM still has a record for it"
        };

    private static string Gone(bool offServer, bool outOfCrm) =>
        (offServer, outOfCrm) switch
        {
            (true, true) => "it is on neither the file server nor CRM",
            (true, false) => "the file is not on the file server",
            _ => "no CRM record refers to it"
        };

    // ---- the screen ----

    private void Write(OldFileResult result)
    {
        _prompts.Blank();
        _prompts.Info($"    {result.FileName ?? result.DocumentId.ToString()}",
            result.AsExpected ? Tone.Good : Tone.Danger);

        if (result.OldPath is { } path)
            _prompts.Info($"      old file     {path}", Tone.Muted);

        _prompts.Info($"      record       {result.OldFileId}", Tone.Muted);
        _prompts.Info($"      file server  {Says(result.OnServer)}     " +
                      $"CRM  {(result.InCrm ? "a record still refers to it" : "no record refers to it")}",
            Tone.Muted);

        foreach (var line in Screen.Wrap(result.Verdict, Screen.Width - 6))
            _prompts.Info($"      {line}", result.AsExpected ? Tone.Good : Tone.Danger);
    }

    private static string Says(bool? onServer) => onServer switch
    {
        true => "the file is there",
        false => "the file is not there",
        _ => "could not be asked"
    };

    private void Record(OldFileResult result, LookupReport report)
    {
        _reports?.Write(result.DocumentId, result.FileName, "06-old-file",
            result.AsExpected
                ? "THE OLD FILE — asked of both systems afterwards"
                : "THE OLD FILE — NOT WHAT WAS EXPECTED",
            new (string, string?)[]
            {
                ("Old path", result.OldPath),
                ("Old record", result.OldFileId.ToString()),
                ("Recorded as", result.State.ToString()),
                ("", null),
                ("File server", Says(result.OnServer)),
                ("In CRM", result.InCrm ? "a record still refers to it" : "no record refers to it"),
                ("Records on path", report.PointingAtThePath.Count == 0
                    ? null
                    : string.Join(", ", report.PointingAtThePath.Take(10))),
                ("CRM problem", report.CrmProblem),
                ("Server problem", report.ServerProblem),
                ("", null),
                ("Verdict", result.Verdict)
            });
    }
}
