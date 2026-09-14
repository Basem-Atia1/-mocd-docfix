using System.Text.Json;

namespace MocdDocFix.Domain;

/// <summary>
/// Which code path created a mocd_documentfile. They fill different columns and use different id
/// conventions, so a migrated record has to be rebuilt in the same shape it was found in.
/// </summary>
public enum FileRecordStyle
{
    /// <summary>
    /// The portal (DocumentDataService). Sets the record's key to the vendor's FileId and writes
    /// six columns. mocd_fileid is never used.
    /// </summary>
    Portal,

    /// <summary>
    /// The CRM plugin (UploadDocument.cs, action mocd_UploadDocument). Lets CRM generate the key
    /// and puts the vendor's FileId in mocd_fileid, alongside mocd_filename, mocd_extension,
    /// mocd_filesize and mocd_applicationid.
    /// </summary>
    Plugin
}

/// <summary>
/// Builds the new mocd_documentfile as the old one, with only what must change replaced.
///
/// The alternative — writing a fixed set of columns — silently drops whatever the creating code
/// path happened to fill in. For a plugin-created record that is five populated columns and the
/// id convention, which is not a migration but a quiet loss.
/// </summary>
public static class FileRecordCopier
{
    /// <summary>Columns this tool decides; everything else is carried across untouched.</summary>
    private static readonly string[] Replaced =
        { "mocd_filepath", "mocd_hash", "mocd_category", "mocd_fileid", "mocd_filename" };

    /// <summary>
    /// Never copied: the key is set separately, and the rest are CRM's to maintain.
    /// </summary>
    private static readonly string[] NeverCopied =
    {
        "mocd_documentfileid", "createdon", "modifiedon", "versionnumber",
        "statecode", "statuscode", "importsequencenumber", "overriddencreatedon",
        "timezoneruleversionnumber", "utcconversiontimezonecode"
    };

    /// <summary>
    /// Reads the style off the old record. mocd_fileid is the reliable signal: verified across
    /// dev and pre-prod, no record that has it also has a key equal to its path's file stem, and
    /// no record without it fails to.
    /// </summary>
    public static FileRecordStyle StyleOf(string? oldRecordJson) =>
        ReadString(oldRecordJson, "mocd_fileid") is not null ? FileRecordStyle.Plugin : FileRecordStyle.Portal;

    /// <summary>
    /// The id the new record should be created with, or null to let CRM generate one — which is
    /// what the plugin does, and what keeps a plugin-created document plugin-shaped.
    /// </summary>
    public static Guid? NewRecordId(FileRecordStyle style, Guid vendorFileId) =>
        style == FileRecordStyle.Portal ? vendorFileId : null;

    /// <summary>
    /// The extension to send to the vendor. The plugin path sends "png" (the browser's
    /// fileName.split('.').pop()), the portal sends ".png" (Path.GetExtension). Keeping the old
    /// record's form means the new record's mocd_extension matches what it had.
    /// </summary>
    public static string ExtensionFor(string? oldRecordJson, string fallback) =>
        ReadString(oldRecordJson, "mocd_extension") ?? fallback;

    /// <summary>
    /// The applicationId to send. The plugin passes the document's own id and stores the echo in
    /// mocd_applicationid; the portal sends nothing at all.
    /// </summary>
    public static Guid ApplicationIdFor(FileRecordStyle style, Guid documentId) =>
        style == FileRecordStyle.Plugin ? documentId : Guid.Empty;

    /// <summary>
    /// Every column of the new record: the old record's own values, with the five that describe
    /// where the file now lives replaced.
    /// </summary>
    /// <param name="newVendorFileName">The vendor's own name for the new file, e.g. "guid.png".</param>
    public static Dictionary<string, object?> BuildPayload(
        string? oldRecordJson,
        Guid vendorFileId,
        string newFilePath,
        string? newHash,
        Guid correctCatalogue,
        string? newVendorFileName)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in SimpleAttributes(oldRecordJson))
        {
            if (NeverCopied.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (Replaced.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            payload[name] = value;
        }

        payload["mocd_filepath"] = newFilePath;
        payload["mocd_hash"] = newHash;
        payload["mocd_category"] = correctCatalogue.ToString();

        // Only where the old record used them — adding them to a portal record would change its
        // shape, which is the thing this exists to avoid.
        if (ReadString(oldRecordJson, "mocd_fileid") is not null)
            payload["mocd_fileid"] = vendorFileId.ToString();

        if (ReadString(oldRecordJson, "mocd_filename") is not null)
            payload["mocd_filename"] = FileNameOnTheServer(newFilePath) ?? newVendorFileName;

        return payload;
    }

    /// <summary>
    /// What the file is actually called on the server: the last segment of its path.
    ///
    /// The plugin writes whatever the vendor returns as fileName, and the vendor is not
    /// consistent about it — observed in dev returning the bare id with no extension while the
    /// path in the same response ended ".png". Records created the normal way have mocd_filename
    /// equal to their path's last segment, and several portal data services read this column as
    /// the name to show, so the path is the more reliable of the two sources.
    /// </summary>
    private static string? FileNameOnTheServer(string newFilePath)
    {
        var leaf = newFilePath.Replace('/', '\\').Split('\\').LastOrDefault();
        return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
    }

    /// <summary>
    /// The old record's plain columns. Lookups (_x_value), annotations (name@…) and nested
    /// objects are left out: they are not ours to recreate on a new row.
    /// </summary>
    private static IEnumerable<(string Name, object? Value)> SimpleAttributes(string? recordJson)
    {
        if (string.IsNullOrWhiteSpace(recordJson)) yield break;

        JsonDocument document;
        try { document = JsonDocument.Parse(recordJson); }
        catch (JsonException) { yield break; }

        using (document)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var name = property.Name;

                if (name.Contains('@', StringComparison.Ordinal)) continue;
                if (name.StartsWith('_') && name.EndsWith("_value", StringComparison.Ordinal)) continue;
                if (!name.StartsWith("mocd_", StringComparison.OrdinalIgnoreCase)) continue;

                object? value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null            // null, objects and arrays are not carried across
                };

                if (value is not null) yield return (name, value);
            }
        }
    }

    private static string? ReadString(string? recordJson, string attribute)
    {
        if (string.IsNullOrWhiteSpace(recordJson)) return null;
        try
        {
            using var json = JsonDocument.Parse(recordJson);
            return json.RootElement.TryGetProperty(attribute, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
