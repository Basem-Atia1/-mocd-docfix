using System.Text.Json;
using MocdDocFix.Verification;

namespace MocdDocFix.Storage;

/// <param name="PreviousKeptAs">
/// Where an earlier backup of the same file was moved to, when this one replaced it and the
/// bytes had changed. Null when there was nothing to keep — which is the normal case.
/// </param>
public sealed record BackupResult(string LocalPath, long Bytes, string OurHash,
    string? PreviousKeptAs = null);

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
    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true };

    private const string RestoreFile = "restore.json";

    private readonly string _backupDir;

    /// <param name="backupDir">&lt;DataRoot&gt;\backup\&lt;env&gt; — one folder per document beneath it.</param>
    public BackupStore(string backupDir) => _backupDir = backupDir;

    /// <summary>The folder holding one folder per document.</summary>
    public string Root => _backupDir;

    /// <summary>The per-document folder holding everything about one document.</summary>
    public DocumentFolder Folder(Guid documentId) => new(_backupDir, documentId);

    /// <summary>
    /// Saves the original bytes under the document's own folder, in old\.
    ///
    /// Backing the same file up again is allowed and expected. If an earlier copy is there and
    /// its bytes are IDENTICAL it is simply replaced. If they DIFFER, the file on the server has
    /// changed since last time, so the earlier copy is kept beside the new one rather than
    /// overwritten — re-running a backup must never destroy the only record of what a file used
    /// to be.
    /// </summary>
    public BackupResult Save(Guid documentId, Guid oldFileId, string extension, byte[] bytes)
    {
        var dir = Folder(documentId).EnsureOld();
        var path = Path.Combine(dir, oldFileId + extension);

        string? previousKeptAs = null;

        if (File.Exists(path) && !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            previousKeptAs = Path.Combine(dir,
                $"{oldFileId}.superseded-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");
            File.Move(path, previousKeptAs);
        }

        File.WriteAllBytes(path, bytes);
        return new BackupResult(path, bytes.Length, Verifier.OurHash(bytes), previousKeptAs);
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

    /// <summary>
    /// Writes the document's restore record into its own folder, replacing any earlier one.
    /// It used to be appended to a single restore-manifest.jsonl beside the document folders,
    /// which left one file that belonged to no document; keeping it with its document means a
    /// folder can be copied, moved or inspected on its own and still be complete.
    /// </summary>
    public void AppendManifest(ManifestEntry entry)
    {
        var path = Path.Combine(Folder(entry.DocumentId).EnsureRoot(), RestoreFile);
        File.WriteAllText(path, JsonSerializer.Serialize(entry, Readable));
    }

    /// <summary>Every restore record under this environment, oldest first.</summary>
    public IReadOnlyList<ManifestEntry> LoadManifest()
    {
        var entries = new List<ManifestEntry>();
        if (!Directory.Exists(_backupDir)) return entries;

        foreach (var folder in Directory.EnumerateDirectories(_backupDir))
        {
            var path = Path.Combine(folder, RestoreFile);
            if (!File.Exists(path)) continue;

            try
            {
                var entry = JsonSerializer.Deserialize<ManifestEntry>(File.ReadAllText(path), Json);
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException) { /* skip a torn record rather than abort the run */ }
            catch (IOException) { /* likewise for a file we cannot read right now */ }
        }

        return entries.OrderBy(e => e.At).ThenBy(e => e.DocumentId).ToList();
    }

    public static long FreeSpaceBytes(string anyPathOnTheDrive)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(anyPathOnTheDrive));
        return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
    }
}
