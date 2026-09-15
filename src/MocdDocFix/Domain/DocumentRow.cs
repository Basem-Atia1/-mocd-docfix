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
    DateTimeOffset ModifiedOn,

    /// <summary>mocd_category as the old record holds it. Often the junk that is the bug.</summary>
    string? OldCategory = null,

    /// <summary>mocd_fileid — the vendor's own id. Null on a portal-created record, always.</summary>
    Guid? VendorFileId = null,

    /// <summary>mocd_filename — the vendor's name for the file, distinct from mocd_name.</summary>
    string? VendorFileName = null)
{
    public string Extension => string.IsNullOrEmpty(FileName) ? string.Empty : Path.GetExtension(FileName);
}
