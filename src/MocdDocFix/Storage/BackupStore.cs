using System.Text.Json;
using MocdDocFix.Verification;

namespace MocdDocFix.Storage;

public sealed record BackupResult(string LocalPath, long Bytes, string OurHash);

/// <summary>
/// Everything needed to rebuild a file AND its CRM records (spec section 8.2). Note this
/// restores the bytes and the records, NOT the path — a re-upload always lands under a new
/// FileId and today's date folder (spec section 5.1).
/// </summary>
/// <param name="DocumentFileSnapshotJson">
/// The complete old mocd_documentfile record as raw JSON, captured before any write. Every
/// attribute, so a restore does not depend on us having predicted which ones matter.
/// </param>
/// <param name="DocumentSnapshotJson">
/// The complete old mocd_document record, including the _mocd_documentfile_value that was in
/// place before we repointed it.
/// </param>
public sealed record ManifestEntry(
    Guid DocumentId,
    Guid OldFileId,
    string OldFilePath,
    string? OldVendorHash,
    string? FileName,
    string? MediaType,
    string Extension,
    string? OldCategory,
    Guid CorrectCatalogueId,
    string LocalPath,
    long Bytes,
    string OurHash,
    DateTimeOffset At,
    string? DocumentFileSnapshotJson = null,
    string? DocumentSnapshotJson = null,
    Guid DocumentTypeId = default,
    string? DocumentTypeName = null,
    string? AnnotationsJson = null,
    string? CrmImagePath = null);

/// <summary>
/// The complete CRM-side picture of one document, captured before any write. Written beside
/// the file bytes as &lt;oldFileId&gt;.crm.json so it can be read without parsing the manifest.
/// </summary>
public sealed record CrmImage(
    Guid DocumentId,
    Guid DocumentFileId,
    string OldFilePath,
    DateTimeOffset CapturedAt,
    string Environment,
    System.Text.Json.JsonElement? Document,
    System.Text.Json.JsonElement? DocumentFile,
    System.Text.Json.JsonElement? Annotations,
    string DocumentRaw,
    string DocumentFileRaw,
    string AnnotationsRaw);

public sealed class BackupStore
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly string _backupDir;
    private readonly string _manifestPath;

    public BackupStore(string backupDir, string manifestPath)
    {
        _backupDir = backupDir;
        _manifestPath = manifestPath;
    }

    public string ManifestPath => _manifestPath;

    /// <summary>The per-document folder holding everything about one document.</summary>
    public DocumentFolder Folder(Guid documentId) => new(_backupDir, documentId);

    /// <summary>Saves the original bytes under the document's own folder, in old\.</summary>
    public BackupResult Save(Guid documentId, Guid oldFileId, string extension, byte[] bytes)
    {
        var dir = Folder(documentId).EnsureOld();
        var path = Path.Combine(dir, oldFileId + extension);
        File.WriteAllBytes(path, bytes);
        return new BackupResult(path, bytes.Length, Verifier.OurHash(bytes));
    }

    /// <summary>Saves the corrected copy under the same document folder, in new\.</summary>
    public BackupResult SaveNew(Guid documentId, Guid newFileId, string extension, byte[] bytes)
    {
        var dir = Folder(documentId).EnsureNew();
        var path = Path.Combine(dir, newFileId + extension);
        File.WriteAllBytes(path, bytes);
        return new BackupResult(path, bytes.Length, Verifier.OurHash(bytes));
    }

    public byte[] Read(string localPath) => File.ReadAllBytes(localPath);

    /// <summary>
    /// Writes the CRM image beside the file bytes. The parsed forms are included when the
    /// payload is valid JSON, and the raw text always is, so nothing is lost even if CRM
    /// returns something unexpected.
    /// </summary>
    public string SaveCrmImage(Guid oldFileId, Guid documentId, string oldFilePath, string environment,
        string documentRaw, string documentFileRaw, string annotationsRaw)
    {
        var path = Path.Combine(Folder(documentId).EnsureOld(), "crm.json");

        var image = new CrmImage(
            DocumentId: documentId,
            DocumentFileId: oldFileId,
            OldFilePath: oldFilePath,
            CapturedAt: DateTimeOffset.UtcNow,
            Environment: environment,
            Document: TryParse(documentRaw),
            DocumentFile: TryParse(documentFileRaw),
            Annotations: TryParse(annotationsRaw),
            DocumentRaw: documentRaw,
            DocumentFileRaw: documentFileRaw,
            AnnotationsRaw: annotationsRaw);

        File.WriteAllText(path, JsonSerializer.Serialize(image, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static JsonElement? TryParse(string raw)
    {
        try { return JsonDocument.Parse(raw).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    public void AppendManifest(ManifestEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        File.AppendAllText(_manifestPath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
    }

    public IReadOnlyList<ManifestEntry> LoadManifest()
    {
        var entries = new List<ManifestEntry>();
        if (!File.Exists(_manifestPath)) return entries;

        foreach (var line in File.ReadLines(_manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ManifestEntry>(line, Json);
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException) { /* skip a torn line rather than abort */ }
        }

        return entries;
    }

    public static long FreeSpaceBytes(string anyPathOnTheDrive)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(anyPathOnTheDrive));
        return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
    }
}
