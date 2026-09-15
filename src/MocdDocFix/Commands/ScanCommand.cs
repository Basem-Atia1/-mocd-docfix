using MocdDocFix.Clients;
using MocdDocFix.Domain;
using MocdDocFix.Storage;

namespace MocdDocFix.Commands;

/// <param name="TotalInScope">
/// Stage-one count: every document under the configured services, before classification.
/// Reported alongside the corrupted subset so the problem is always sized against the
/// population (spec section 5.0).
/// </param>
/// <param name="GroupsPath">The readable grouped report — spec 2026-09-13 section 5.</param>
/// <param name="GuidsPath">
/// The document GUIDs of each group, one per line under '#' headers — readable, and usable
/// directly as --docs-file.
/// </param>
public sealed record ScanResult(
    int TotalInScope,
    IReadOnlyList<ScanRow> All,
    IReadOnlyList<ScanRow> Fix,
    IReadOnlyList<ScanRow> Review,
    IReadOnlyList<ScanRow> Skip,
    string ScanPath,
    string ReviewPath,
    string GroupsPath = "",
    string GuidsPath = "",

    /// <summary>
    /// How many documents got their own step-1 report. Zero means the run wrote the whole-run
    /// spreadsheets instead — the direct CLI, and tests.
    /// </summary>
    int PerDocumentReports = 0)
{
    /// <summary>How many of each group were found, for the wizard's group picker.</summary>
    public IReadOnlyDictionary<int, int> CountByGroup =>
        All.GroupBy(r => r.Group).ToDictionary(g => g.Key, g => g.Count());


    public int WithFilePath => All.Count(r => !string.IsNullOrWhiteSpace(r.OldFilePath));
    public int WithoutFilePath => All.Count(r => string.IsNullOrWhiteSpace(r.OldFilePath));

    /// <summary>The banner printed at the top of every run — spec section 5.0.</summary>
    public string Banner() => string.Join(Environment.NewLine,
        $"In scope (document types across the configured services) ... {TotalInScope}",
        $"  with a file path ......................................... {WithFilePath}",
        $"    already correct / nothing to do ........................ {Skip.Count}",
        $"    BROKEN — will be fixed ................................. {Fix.Count}",
        $"    AMBIGUOUS — needs a human decision ..................... {Review.Count}",
        $"  no file path (legacy records, out of scope) .............. {WithoutFilePath}");
}

/// <summary>
/// Phase 1. Reads and classifies only — issues no writes to CRM or the file service.
/// Always scans fresh: broken files are still being produced (spec section 1.2), so a stale
/// scan is never reused.
/// </summary>
public sealed class ScanCommand
{
    private readonly ICrmReadClient _crm;
    private readonly Reporter _reporter;
    private readonly string _crmUrl;
    private readonly IReadOnlyList<Guid> _catalogues;
    private readonly GroupedReportWriter? _grouped;
    private readonly GuidListWriter? _guids;
    private readonly DocumentReportStore? _perDocument;
    private readonly BackupStore? _backups;

    /// <param name="perDocument">
    /// Where each document's own step-1 report goes. Supplied by the wizard, so a full run
    /// writes what a targeted run writes: one readable file in the document's own folder,
    /// instead of a spreadsheet in the whole-run folder that has to be opened in Excel to find
    /// out what is wrong with anything. Without it the scan writes the spreadsheets as before —
    /// which is what the direct CLI wants.
    /// </param>
    public ScanCommand(ICrmReadClient crm, Reporter reporter, string crmUrl,
        IReadOnlyList<Guid> catalogues,
        GroupedReportWriter? grouped = null, GuidListWriter? guids = null,
        DocumentReportStore? perDocument = null, BackupStore? backups = null)
    {
        _crm = crm;
        _reporter = reporter;
        _crmUrl = crmUrl;
        _catalogues = catalogues;
        _grouped = grouped;
        _guids = guids;
        _perDocument = perDocument;
        _backups = backups;
    }

    public async Task<ScanResult> RunAsync(string env, CancellationToken ct)
    {
        var documents = await _crm.GetInScopeDocumentsAsync(_catalogues, ct);
        return await ClassifyAsync(documents, env, writeReports: true, ct);
    }

    public async Task<ScanResult> ClassifyAsync(
        IReadOnlyList<DocumentRow> documents, string env, bool writeReports, CancellationToken ct)
    {
        var rows = new List<ScanRow>(documents.Count);

        foreach (var document in documents)
        {
            var parsed = FilePathParser.Parse(document.FilePath);

            // Resolved live against mocd_servicecatalogue, cached inside the client. The name
            // comes from the same cached lookup, so showing it costs nothing extra.
            var currentSegmentName = parsed.CategorySegment is { } segment
                ? await _crm.GetServiceCatalogueNameAsync(segment, ct)
                : null;
            var isCatalogue = currentSegmentName is not null;

            var serviceCatalogueName = document.DocTypeCatalogueId is { } catalogueId
                ? await _crm.GetServiceCatalogueNameAsync(catalogueId.ToString(), ct)
                : null;

            var classification = Classifier.Classify(
                parsed,
                document.DocTypeCatalogueId,
                document.CrossCheckCatalogueId,
                _ => isCatalogue);

            rows.Add(new ScanRow(
                DocumentId: document.DocumentId,
                DocumentFileId: document.DocumentFileId,
                FileName: document.FileName,
                DocumentTypeName: document.DocumentTypeName,
                ServiceCatalogueId: document.DocTypeCatalogueId,
                ServiceCatalogueName: serviceCatalogueName,
                OldFilePath: document.FilePath,
                CurrentSegment: classification.CurrentSegment,
                CurrentSegmentName: currentSegmentName,
                CorrectCatalogueId: classification.CorrectCatalogueId,
                Verdict: classification.Verdict.ToString(),
                Group: classification.Group,
                GroupLabel: DocumentGroups.Get(classification.Group).ShortLabel,
                Reason: classification.Reason,
                Solution: classification.Solution,
                CrossCheckSource: document.CrossCheckSource,
                CrmLink: Reporter.CrmLink(_crmUrl, document.DocumentId)));
        }

        var fix = rows.Where(r => r.Verdict == nameof(Verdict.Fix)).ToList();
        var review = rows.Where(r => r.Verdict == nameof(Verdict.Review)).ToList();
        var skip = rows.Where(r => r.Verdict == nameof(Verdict.Skip)).ToList();

        // Where the account of each document goes. With a per-document store the whole-run
        // spreadsheets are not written at all: the same facts are in each document's own
        // 01-check.txt, and two copies of the truth is one too many. The grouped report and the
        // GUID list stay — they are about the population, which no per-document file can be.
        var perDocument = writeReports && _perDocument is not null
            ? WritePerDocument(fix, review)
            : 0;

        var scanPath = writeReports && _perDocument is null ? _reporter.WriteScan(env, rows) : string.Empty;
        var reviewPath = writeReports && _perDocument is null ? _reporter.WriteReview(env, review) : string.Empty;
        var groupsPath = writeReports && _grouped is not null
            ? _grouped.Write(env, rows, _catalogues.Count)
            : string.Empty;
        var guidsPath = writeReports && _guids is not null
            ? _guids.Write(env, rows)
            : string.Empty;

        return new ScanResult(documents.Count, rows, fix, review, skip,
            scanPath, reviewPath, groupsPath, guidsPath, perDocument);
    }

    /// <summary>
    /// One step-1 report per document, in that document's own folder — for the broken ones and
    /// for the ones needing a human. Not for the documents with nothing wrong: a folder each for
    /// several hundred correct files buries the handful that matter, and the grouped report
    /// already counts them.
    /// </summary>
    private int WritePerDocument(IReadOnlyList<ScanRow> fix, IReadOnlyList<ScanRow> review)
    {
        var written = 0;

        foreach (var row in fix)
        {
            // Both files, exactly as a targeted run writes them: the document's own record in
            // the backup folder, ready for the steps that will append to it, and 01-check.txt
            // in its reports folder.
            if (_backups is not null) DocumentRecord.WriteHeader(_backups, row, _perDocument);
            else WriteCheckOnly(row);

            written++;
        }

        // A document needing a human is never backed up, so it gets no backup folder — an empty
        // one would read as work that had started. It still gets its own check report, because
        // the reason it needs a human is the thing worth having written down.
        foreach (var row in review)
        {
            WriteCheckOnly(row);
            written++;
        }

        return written;
    }

    private void WriteCheckOnly(ScanRow row) =>
        _perDocument!.Write(row.DocumentId, row.FileName, "01-check",
            DocumentRecord.CheckTitle, DocumentRecord.PointsFor(row));
}
