using System.Security.Cryptography;
using MocdDocFix.Domain;

namespace MocdDocFix.Verification;

/// <summary>
/// The six checks of spec section 6.2. Comparisons are always like-for-like: vendor hash to
/// vendor hash, our hash to our hash, bytes to bytes. We never assume the vendor's algorithm.
/// </summary>
public static class Verifier
{
    /// <summary>Our own hash. SHA-256, never compared against a vendor hash.</summary>
    public static string OurHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Check 1 — CRM's stored hash agrees with what the file server reports today.</summary>
    public static CheckResult BackupIntegrity(string? crmHash, string? serverHash)
    {
        if (string.IsNullOrWhiteSpace(crmHash) || string.IsNullOrWhiteSpace(serverHash))
            return new CheckResult("backup-integrity", false,
                $"A hash is missing (CRM='{crmHash ?? "null"}', server='{serverHash ?? "null"}').", false);

        var same = string.Equals(crmHash, serverHash, StringComparison.OrdinalIgnoreCase);
        return new CheckResult("backup-integrity", same,
            same ? $"CRM and file server agree ({crmHash})."
                 : $"CRM hash '{crmHash}' does not match the file server's '{serverHash}'. " +
                   "This record was already inconsistent — quarantined.",
            false);
    }

    /// <summary>Check 2 — the vendor computed the same hash for the bytes we just uploaded.</summary>
    public static CheckResult UploadHashMatches(string? oldVendorHash, string? newVendorHash)
    {
        if (string.IsNullOrWhiteSpace(oldVendorHash) || string.IsNullOrWhiteSpace(newVendorHash))
            return new CheckResult("upload-hash", false,
                $"A vendor hash is missing (old='{oldVendorHash ?? "null"}', new='{newVendorHash ?? "null"}').", false);

        var same = string.Equals(oldVendorHash, newVendorHash, StringComparison.OrdinalIgnoreCase);
        return new CheckResult("upload-hash", same,
            same ? $"Vendor hash unchanged ({oldVendorHash})."
                 : $"Vendor hash changed: '{oldVendorHash}' → '{newVendorHash}'. Content differs.",
            false);
    }

    /// <summary>
    /// Check 3 — the new file downloads and is byte-identical. This is the real proof:
    /// Success=true only means the upload was accepted, not that a readable file exists.
    /// </summary>
    public static CheckResult RoundTrip(byte[] oldBytes, byte[] newBytes)
    {
        if (oldBytes.Length != newBytes.Length)
            return new CheckResult("round-trip", false,
                $"Length differs: {oldBytes.Length:N0} vs {newBytes.Length:N0} bytes.", false);

        if (!oldBytes.AsSpan().SequenceEqual(newBytes))
            return new CheckResult("round-trip", false,
                $"Same length ({oldBytes.Length:N0}) but content differs. " +
                $"our hash {OurHash(oldBytes)} vs {OurHash(newBytes)}.", false);

        return new CheckResult("round-trip", true,
            $"Downloaded copy is byte-for-byte identical ({oldBytes.Length:N0} bytes, {OurHash(newBytes)}).", false);
    }

    /// <summary>Check 4 — the new path really is under the correct catalogue.</summary>
    public static CheckResult PathIsFixed(FilePathParts newPath, Guid correctCatalogue, Guid newFileId)
    {
        if (newPath.CategorySegment is null ||
            !Guid.TryParse(newPath.CategorySegment, out var segment) ||
            segment != correctCatalogue)
        {
            return new CheckResult("path-fixed", false,
                $"New path segment is '{newPath.CategorySegment ?? "(absent)"}', expected {correctCatalogue}. " +
                $"Path: {newPath.Raw}", false);
        }

        if (!Guid.TryParse(newPath.FileStem, out var stem) || stem != newFileId)
        {
            return new CheckResult("path-fixed", false,
                $"File stem '{newPath.FileStem}' does not equal the returned FileId {newFileId}.", false);
        }

        return new CheckResult("path-fixed", true, $"Filed under {correctCatalogue}.", false);
    }

    /// <summary>
    /// Check 5 — HALTS THE RUN. Guards against content-hash deduplication handing us back the
    /// existing file: we would repoint to the old file and the delete pass would then destroy
    /// the only copy.
    /// </summary>
    public static CheckResult IsGenuinelyNew(string oldPath, string newPath, Guid oldFileId, Guid newFileId)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return new CheckResult("genuinely-new", false,
                $"The vendor returned the SAME path ({newPath}). It may be deduplicating by content hash. " +
                "Stopping — continuing risks deleting the only copy.", true);

        if (oldFileId == newFileId)
            return new CheckResult("genuinely-new", false,
                $"The vendor returned the SAME FileId ({newFileId}). Possible deduplication. Stopping.", true);

        return new CheckResult("genuinely-new", true, $"New file {newFileId} is distinct from {oldFileId}.", true);
    }

    /// <summary>Check 6 — HALTS THE RUN. The document really points at the new file.</summary>
    public static CheckResult CrmTookTheChange(Guid expectedFileId, Guid? actualLinkedFileId)
    {
        var ok = actualLinkedFileId == expectedFileId;
        return new CheckResult("crm-repointed", ok,
            ok ? $"Document now points at {expectedFileId}."
               : $"After the update the document points at '{actualLinkedFileId?.ToString() ?? "null"}', " +
                 $"expected {expectedFileId}. Stopping.",
            true);
    }

    /// <summary>
    /// Check 7 — HALTS THE RUN. The new mocd_documentfile record really holds the new path.
    ///
    /// Check 6 proves the document points at the right RECORD; this proves that record points at
    /// the right FILE. Without it, a record whose mocd_filepath still names the old file looks
    /// perfectly healthy — right up until the old file is deleted and the CRM View button, which
    /// reads mocd_filepath and nothing else, finds nothing.
    /// </summary>
    public static CheckResult FileRecordPointsAtTheNewFile(string expectedPath, string? actualPath)
    {
        var ok = !string.IsNullOrWhiteSpace(actualPath) &&
                 string.Equals(Normalise(expectedPath), Normalise(actualPath!),
                     StringComparison.OrdinalIgnoreCase);

        return new CheckResult("file-record-path", ok,
            ok ? "The new documentfile record holds the new path."
               : $"The new documentfile's mocd_filepath is '{actualPath ?? "null"}', expected " +
                 $"'{expectedPath}'. The record and the file disagree. Stopping.",
            true);
    }

    /// <summary>
    /// Before the delete — proves the row about to be removed is the row that was backed up.
    ///
    /// Everything else in the delete step reasons about the NEW file. This is the only check that
    /// looks at the OLD one, and it is the one that matters most, because the delete is what
    /// cannot be undone. It re-reads the old record by the id saved at backup time and requires
    /// its mocd_filepath still to be the path saved at backup time.
    ///
    /// The two can drift apart. A record can be edited by hand between the backup and the delete
    /// — which has already happened once in dev — or repointed by another run. Once they have
    /// drifted, deleting by the saved path removes a file the record no longer claims, and
    /// deleting the record removes a row that now describes something else. Neither is recoverable
    /// and neither would be noticed.
    /// </summary>
    /// <param name="recordJson">The old record as CRM returns it now, or null if it has gone.</param>
    public static CheckResult OldRecordIsStillTheOneWeBackedUp(
        Guid oldFileId, string backedUpPath, string? recordJson)
    {
        if (string.IsNullOrWhiteSpace(recordJson))
            return new CheckResult("old-record-identity", false,
                $"The old documentfile {oldFileId} is no longer in CRM. Nothing here is safe to " +
                "delete by this record's say-so — the row that described this file has gone, so " +
                "the path saved at backup time can no longer be confirmed to belong to it.", true);

        var actualPath = ReadFilePath(recordJson);

        if (string.IsNullOrWhiteSpace(actualPath))
            return new CheckResult("old-record-identity", false,
                $"The old documentfile {oldFileId} no longer holds any mocd_filepath, so it " +
                $"cannot be confirmed as the record for '{backedUpPath}'.", true);

        var ok = string.Equals(Normalise(backedUpPath), Normalise(actualPath!),
            StringComparison.OrdinalIgnoreCase);

        return new CheckResult("old-record-identity", ok,
            ok ? $"The old record {oldFileId} still holds the path that was backed up."
               : $"The old documentfile {oldFileId} now holds '{actualPath}', but the backup " +
                 $"recorded '{backedUpPath}'. It has been changed since the backup, so this is no " +
                 "longer certainly the same file. Nothing was deleted.",
            true);
    }

    /// <summary>
    /// Before the delete — proves the old file and the new file are not the same file.
    ///
    /// If the two paths have converged, by a hand-edit or by the vendor deduplicating, then
    /// deleting "the old file" destroys the file the document now depends on. Every other check
    /// passes in that case, because the new file downloads, hashes correctly, and the record
    /// points at it — right up to the moment it is deleted.
    /// </summary>
    public static CheckResult OldAndNewAreDifferentFiles(
        string oldPath, string? newPath, Guid oldFileId, Guid? newFileId)
    {
        if (string.IsNullOrWhiteSpace(newPath))
            return new CheckResult("old-is-not-new", false,
                "No new file is recorded for this document, so there is nothing proving the old " +
                "file is safe to remove.", true);

        if (string.Equals(Normalise(oldPath), Normalise(newPath!), StringComparison.OrdinalIgnoreCase))
            return new CheckResult("old-is-not-new", false,
                $"The old and new paths are the same file ('{newPath}'). Deleting it would destroy " +
                "the copy the document now uses. Nothing was deleted.", true);

        if (newFileId is not null && oldFileId == newFileId)
            return new CheckResult("old-is-not-new", false,
                $"The old and new documentfile are the same record ({oldFileId}). Deleting it " +
                "would remove the record the document now points at. Nothing was deleted.", true);

        return new CheckResult("old-is-not-new", true,
            $"The old file '{oldPath}' is a different file from the new one.", true);
    }

    private static string? ReadFilePath(string recordJson)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(recordJson);
            return json.RootElement.TryGetProperty("mocd_filepath", out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// The vendor returns a UNC-rooted path on download and a relative one on upload, so compare
    /// on the part that identifies the file rather than on the prefix.
    /// </summary>
    private static string Normalise(string path)
    {
        var trimmed = path.Replace('/', '\\').TrimStart('\\');
        var at = trimmed.IndexOf(@"DigitalServices\", StringComparison.OrdinalIgnoreCase);
        return at >= 0 ? trimmed[at..] : trimmed;
    }
}
