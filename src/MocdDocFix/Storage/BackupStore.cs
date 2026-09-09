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
    string? DocumentTypeName = null);

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

    public BackupResult Save(Guid oldFileId, string extension, byte[] bytes)
    {
        Directory.CreateDirectory(_backupDir);
        var path = Path.Combine(_backupDir, oldFileId + extension);
        File.WriteAllBytes(path, bytes);
        return new BackupResult(path, bytes.Length, Verifier.OurHash(bytes));
    }

    public byte[] Read(string localPath) => File.ReadAllBytes(localPath);

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
