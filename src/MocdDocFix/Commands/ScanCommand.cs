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
    string GuidsPath = "")
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

    public ScanCommand(ICrmReadClient crm, Reporter reporter, string crmUrl,
        IReadOnlyList<Guid> catalogues,
        GroupedReportWriter? grouped = null, GuidListWriter? guids = null)
    {
        _crm = crm;
        _reporter = reporter;
        _crmUrl = crmUrl;
        _catalogues = catalogues;
        _grouped = grouped;
        _guids = guids;
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

        var scanPath = writeReports ? _reporter.WriteScan(env, rows) : string.Empty;
        var reviewPath = writeReports ? _reporter.WriteReview(env, review) : string.Empty;
        var groupsPath = writeReports && _grouped is not null
            ? _grouped.Write(env, rows, _catalogues.Count)
            : string.Empty;
        var guidsPath = writeReports && _guids is not null
            ? _guids.Write(env, rows)
            : string.Empty;

        return new ScanResult(documents.Count, rows, fix, review, skip,
            scanPath, reviewPath, groupsPath, guidsPath);
    }
}
