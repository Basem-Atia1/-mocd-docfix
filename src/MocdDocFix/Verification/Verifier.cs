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
}
