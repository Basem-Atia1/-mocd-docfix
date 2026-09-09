namespace MocdDocFix.Domain;

/// <summary>One document with everything needed to classify and migrate it.</summary>
public sealed record DocumentRow(
    Guid DocumentId,
    string DocumentName,
    Guid DocumentFileId,
    string? FilePath,
    string? FileName,
    string? MediaType,
    string? Hash,
    Guid DocumentTypeId,
    string DocumentTypeName,
    Guid? DocTypeCatalogueId,
    Guid? CrossCheckCatalogueId,
    string? CrossCheckSource,
    DateTimeOffset ModifiedOn)
{
    public string Extension => string.IsNullOrEmpty(FileName) ? string.Empty : Path.GetExtension(FileName);
}
