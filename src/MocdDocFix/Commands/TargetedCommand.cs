using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Ui;

namespace MocdDocFix.Commands;

public sealed record TargetedSummary(
    int Resolved, int Fixed, int Reviewed, int Skipped, int NotFound, int Ambiguous,
    string ScanPath);

/// <summary>
/// Targeted mode (spec section 5.3). Accepts document ids, documentfile ids and file names —
/// all three are usable because mocd_documentfileid == vendor FileId == the path's file stem.
/// Runs the same code as the bulk path.
/// </summary>
public sealed class TargetedCommand
{
    private readonly ICrmReadClient _read;
    private readonly ScanCommand _scan;
    private readonly Reporter _reporter;
    private readonly IPrompts _prompts;
    private readonly Func<IReadOnlyList<ScanRow>, Task<int>> _runPipelineAsync;
    private readonly DocumentReportStore? _reports;

    /// <param name="runPipelineAsync">
    /// Backup → migrate → (optionally) delete for the chosen rows. Injected so this command's
    /// resolution and reporting behaviour is testable without the network.
    /// </param>
    /// <param name="reports">
    /// Where a targeted run writes its account: one readable file in each document's own folder,
    /// beside everything else that happens to it. A run about three documents has no business
    /// leaving a spreadsheet in the whole-run folder, which is for the population.
    /// </param>
    public TargetedCommand(ICrmReadClient read, ScanCommand scan, Reporter reporter, IPrompts prompts,
        Func<IReadOnlyList<ScanRow>, Task<int>> runPipelineAsync, DocumentReportStore? reports = null)
    {
        _read = read;
        _scan = scan;
        _reporter = reporter;
        _prompts = prompts;
        _runPipelineAsync = runPipelineAsync;
        _reports = reports;
    }

    public async Task<TargetedSummary> RunAsync(string env, IReadOnlyList<string> identifiers,
        bool forceReview, bool isProduction, CancellationToken ct)
    {
        int resolved = 0, notFound = 0, ambiguous = 0, reviewed = 0, skipped = 0;
        var queued = new List<ScanRow>();
        var classified = new List<ScanRow>();   // every resolved row, whatever its verdict

        foreach (var identifier in identifiers)
        {
            ct.ThrowIfCancellationRequested();

            var matches = await _read.ResolveIdentifierAsync(identifier, ct);

            if (matches.Count == 0)
            {
                _prompts.Blank();
                _prompts.Warn($"NOT FOUND — '{identifier}' matched no document.");
                notFound++;
                continue;
            }

            if (matches.Count > 1)
            {
                _prompts.Section($"AMBIGUOUS — '{identifier}' matches {matches.Count} documents",
                    Tone.Warn);
                foreach (var m in matches)
                {
                    _prompts.Info($"      {m.DocumentId}  {m.FileName}", Tone.Muted);
                    _prompts.Info($"      {m.FilePath}", Tone.Muted);
                }
                _prompts.Blank();
                _prompts.Say("Re-run with the specific document id you want.");
                ambiguous++;
                continue;
            }

            resolved++;
            var document = matches[0];
            var result = await _scan.ClassifyAsync(new[] { document }, env, writeReports: false, ct);
            var row = result.All[0];
            classified.Add(row);

            _prompts.Section(identifier);
            _prompts.Field("document", document.DocumentId.ToString(), Tone.Muted);
            _prompts.Field("document type", document.DocumentTypeName ?? "(not known)", Tone.Muted);
            _prompts.Field("current path", document.FilePath ?? "(none)", Tone.Muted);

            // Said out loud on every document, agreement included. A check whose agreement is
            // silent is indistinguishable from a check that never ran — which is exactly how it
            // read the first time this was used.
            WriteDevOpsLine(row);

            _prompts.Blank();

            if (row.Verdict == nameof(Verdict.Skip))
            {
                _prompts.Say($"VERDICT  OK — {row.Reason}", Tone.Good);
                skipped++;
                continue;
            }

            if (row.Verdict == nameof(Verdict.Review))
            {
                // --force-review cannot conjure a catalogue that does not exist. When the parent
                // request and the document type disagree there is no correct value to write, so
                // the flag is refused rather than silently queuing a row with a null target.
                if (row.CorrectCatalogueId is null)
                {
                    _prompts.Say($"VERDICT  NEEDS A DECISION — {row.Reason}", Tone.Warn);
                    _prompts.Say("Not touched, and --force-review cannot help: the two authorities " +
                                 "disagree, so there is no correct catalogue to write. Fix the " +
                                 "document type or the parent request in CRM, then re-scan.",
                        Tone.Muted);
                    reviewed++;
                    continue;
                }

                if (!forceReview)
                {
                    _prompts.Say($"VERDICT  AMBIGUOUS — {row.Reason}", Tone.Warn);
                    _prompts.Say("Not touched. Re-run with --force-review to act on it anyway.",
                        Tone.Muted);
                    reviewed++;
                    continue;
                }
            }

            _prompts.Say($"VERDICT  BROKEN — {row.Reason}", Tone.Danger);
            _prompts.Blank();
            _prompts.Field("correct", row.CorrectCatalogueId?.ToString() ?? "(none)", Tone.Muted);
            if (document.CrossCheckSource is not null)
                _prompts.Field("cross-check", $"{document.CrossCheckSource} " +
                    $"{(document.CrossCheckCatalogueId == row.CorrectCatalogueId ? "agrees" : "DISAGREES")}",
                    Tone.Muted);
            _prompts.Blank();
            _prompts.Say($"SOLUTION  {row.Solution}");

            queued.Add(row);
        }

        // The report comes first in every mode, not only bulk (spec section 8.1) — so there is
        // always a written record of what was found and what was intended, before any change.
        // For a targeted run that record goes where the work is: one readable file per document,
        // in that document's own folder, rather than a spreadsheet in the whole-run folder.
        var scanPath = WriteTheAccount(env, classified);

        _prompts.Blank();
        _prompts.Say($"report → {scanPath}", Tone.Muted);

        var fixedCount = queued.Count == 0 ? 0 : await _runPipelineAsync(queued);
        return new TargetedSummary(resolved, fixedCount, reviewed, skipped, notFound, ambiguous, scanPath);
    }

    /// <summary>
    /// Writes step 1 as text, in each document's own folder. Falls back to the whole-run
    /// spreadsheet only where no per-document store was supplied — the direct CLI, and tests.
    /// </summary>
    /// <returns>The one document's report, or the folder holding them when there are several.</returns>
    private string WriteTheAccount(string env, IReadOnlyList<ScanRow> classified)
    {
        if (_reports is null || classified.Count == 0) return _reporter.WriteScan(env, classified);

        var written = classified
            .Select(row => _reports.Write(row.DocumentId, row.FileName, "01-check",
                DocumentRecord.CheckTitle, DocumentRecord.PointsFor(row)))
            .ToList();

        return written.Count == 1
            ? written[0]
            : _reports.FolderFor(classified[0].DocumentId, classified[0].FileName) + "  (and others)";
    }

    /// <summary>
    /// What the DevOps backlog made of this document's type — printed whatever it said, because
    /// "agrees" and "was never asked" look identical when only disagreement is announced.
    /// </summary>
    private void WriteDevOpsLine(ScanRow row)
    {
        var evidence = string.IsNullOrWhiteSpace(row.AdoEvidence)
            ? ""
            : $"   (work items {row.AdoEvidence})";

        var (text, tone) = row.AdoVerdict switch
        {
            nameof(AdoVerdict.Agrees) =>
                ($"agrees — {(row.AdoService.Length > 0 ? row.AdoService : "same service")}{evidence}",
                    Tone.Good),

            nameof(AdoVerdict.Disagrees) =>
                ($"DISAGREES — the backlog says {row.AdoService}{evidence}", Tone.Danger),

            nameof(AdoVerdict.CannotTell) =>
                ("asked, but could not tell — left to the CRM answer", Tone.Warn),

            _ => ("not checked — DevOps is not set up for this run", Tone.Muted)
        };

        _prompts.Field("DevOps", text, tone);
    }
}
