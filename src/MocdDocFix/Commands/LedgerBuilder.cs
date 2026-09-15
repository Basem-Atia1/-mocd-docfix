using MocdDocFix.Clients;
using MocdDocFix.Domain;

namespace MocdDocFix.Commands;

/// <summary>
/// CRM to ledger. Every document under the configured services gets a row — the correct ones
/// and the legacy ones with no file at all, not only the broken ones, because the verdict
/// column is what separates them and a census without a denominator cannot be read.
///
/// It reads and classifies. It writes nothing, to CRM or to disk; the caller decides where the
/// rows go.
/// </summary>
public sealed class LedgerBuilder
{
    private readonly ICrmReadClient _crm;
    private readonly IReadOnlyList<Guid> _catalogues;
    private readonly string _crmUrl;

    public LedgerBuilder(ICrmReadClient crm, IReadOnlyList<Guid> catalogues, string crmUrl)
    {
        _crm = crm;
        _catalogues = catalogues;
        _crmUrl = crmUrl;
    }

    public async Task<IReadOnlyList<LedgerRow>> BuildAsync(CancellationToken ct)
    {
        var documents = await _crm.GetInScopeDocumentsAsync(_catalogues, ct);
        var rows = new List<LedgerRow>(documents.Count);
        var number = 1;

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(await RowFor(document, number++, ct));
        }

        return rows;
    }

    private async Task<LedgerRow> RowFor(DocumentRow document, int number, CancellationToken ct)
    {
        var parsed = FilePathParser.Parse(document.FilePath);

        // Resolved live against mocd_servicecatalogue; the client caches, so repeated ids cost
        // one query between them.
        var filedUnderName = parsed.CategorySegment is { } segment
            ? await _crm.GetServiceCatalogueNameAsync(segment, ct)
            : null;

        var serviceName = document.DocTypeCatalogueId is { } catalogue
            ? await _crm.GetServiceCatalogueNameAsync(catalogue.ToString(), ct)
            : null;

        var classification = Classifier.Classify(
            parsed, document.DocTypeCatalogueId, document.CrossCheckCatalogueId,
            _ => filedUnderName is not null);

        var correctName = classification.CorrectCatalogueId is { } correct
            ? await _crm.GetServiceCatalogueNameAsync(correct.ToString(), ct)
            : null;

        return new LedgerRow
        {
            Row = number,
            DocId = document.DocumentId,
            DocName = document.DocumentName,
            DocFileId = document.DocumentFileId,
            DocFileName = document.FileName ?? string.Empty,
            DocTypeName = document.DocumentTypeName,
            ServiceCatalogueId = document.DocTypeCatalogueId?.ToString() ?? string.Empty,
            ServiceCatalogueName = serviceName ?? string.Empty,
            CorrectServiceCatalogueId = classification.CorrectCatalogueId?.ToString() ?? string.Empty,
            CorrectServiceCatalogueName = correctName ?? string.Empty,
            OldFilePath = document.FilePath ?? string.Empty,
            NewFilePathPredicted = Predict(classification.CorrectCatalogueId, document.Extension),

            // Off the record, not off the path. These are what a revert writes back, so they
            // must be what the record actually holds — blanks included.
            OldCategory = document.OldCategory ?? string.Empty,
            OldHash = document.Hash ?? string.Empty,
            OldFileName = document.VendorFileName ?? string.Empty,
            OldFileId = document.VendorFileId?.ToString() ?? string.Empty,

            Verdict = VerdictFor(classification.Verdict),
            Group = classification.Group,
            ReasonOfBug = classification.Reason,
            Solution = classification.Solution,
            FinalState = string.Empty,
            CrmLinkOfDoc = CrmLinks.Document(_crmUrl, document.DocumentId),
            CrmLinkOfDocFile = CrmLinks.DocumentFile(_crmUrl, document.DocumentFileId),
            WayOfUpload = document.VendorFileId is null ? "portal" : "plugin"
        };
    }

    /// <summary>
    /// The shape the corrected path will take. The file id and the date folder are the server's
    /// to choose, so the id is written literally rather than guessed at.
    /// </summary>
    private static string Predict(Guid? correct, string extension) =>
        correct is null
            ? string.Empty
            : $@"DigitalServices\{correct}\{DateTime.Now:yyyyMMdd}\(new id){extension}";

    private static string VerdictFor(Verdict verdict) => verdict switch
    {
        Verdict.Fix => RowVerdicts.Fix,
        Verdict.Review => RowVerdicts.Review,
        _ => RowVerdicts.Skip
    };
}
