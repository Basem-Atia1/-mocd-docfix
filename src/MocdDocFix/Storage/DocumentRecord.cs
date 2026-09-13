using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// Writes the opening of a document's own document.txt — what the file is, what is wrong with
/// it, and what the tool intends to do. Steps append their own sections underneath as they run,
/// so the file reads as a record of what happened to that one document.
/// </summary>
public static class DocumentRecord
{
    public static void EnsureHeader(BackupStore backups, ScanRow row)
    {
        var folder = backups.Folder(row.DocumentId);
        if (File.Exists(folder.SummaryPath)) return;
        WriteHeader(backups, row);
    }

    public static void WriteHeader(BackupStore backups, ScanRow row)
    {
        var group = DocumentGroups.Get(row.Group);

        backups.Folder(row.DocumentId).WriteHeader($"DOCUMENT  {row.DocumentId}", new (string, string?)[]
        {
            ("File name", row.FileName),
            ("Document type", row.DocumentTypeName),
            ("Service", row.ServiceCatalogueName),
            ("Service catalogue", row.ServiceCatalogueId?.ToString()),
            ("Open in CRM", row.CrmLink),
            ("", null),

            ("Verdict", row.Verdict.ToUpperInvariant()),
            ("Group", $"{group.Number} — {group.ShortLabel}"),
            ("In the path", group.WhatIsInThePath),
            ("Why it is wrong", row.Reason),
            ("What we will do", row.Solution),
            ("", null),

            ("Current path", row.OldFilePath),
            ("Filed under", string.IsNullOrWhiteSpace(row.CurrentSegment)
                ? "(nothing — the date folder sits directly under the root)"
                : row.CurrentSegment + (row.CurrentSegmentName is { } n ? $"  = {n}" : "")),
            ("Should be under", row.CorrectCatalogueId?.ToString()),
            ("Checked at", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
        });
    }
}
