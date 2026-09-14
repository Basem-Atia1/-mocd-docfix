using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// Writes the opening of a document's own document.txt — what the file is, what is wrong with
/// it, and what the tool intends to do. Steps append their own sections underneath as they run,
/// so the file reads as a record of what happened to that one document.
/// </summary>
public static class DocumentRecord
{
    public static void EnsureHeader(BackupStore backups, ScanRow row, DocumentReportStore? reports = null)
    {
        var folder = backups.Folder(row.DocumentId, row.FileName);
        if (File.Exists(folder.SummaryPath)) return;
        WriteHeader(backups, row, reports);
    }

    /// <summary>The step-1 account of one document, for whoever is writing it out.</summary>
    public static (string, string?)[] PointsFor(ScanRow row) =>
        Points(row, DocumentGroups.Get(row.Group));

    /// <summary>The title that heads a step-1 report.</summary>
    public const string CheckTitle = "STEP 1 — CHECK: what is wrong with this document";

    public static void WriteHeader(BackupStore backups, ScanRow row, DocumentReportStore? reports = null)
    {
        var group = DocumentGroups.Get(row.Group);
        var points = Points(row, group);

        backups.Folder(row.DocumentId, row.FileName)
            .WriteHeader($"DOCUMENT  {row.DocumentId}",
                points.Prepend(("File name", row.FileName)).ToArray());

        // The same account, as step 1 of this document's own reports — without the file name,
        // which that writer already prints in the header this sits under.
        reports?.Write(row.DocumentId, row.FileName, "01-check", CheckTitle, points);
    }

    private static (string, string?)[] Points(ScanRow row, DocumentGroup group) =>
        new (string, string?)[]
        {
            // The file name is already in the header this sits under, so it is not repeated.
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

            ("CRM says", row.ServiceCatalogueName),
            ("Parent request", row.CrossCheckSource),
            ("DevOps says", DevOps(row)),
            ("", null),

            ("Current path", row.OldFilePath),
            ("Filed under", string.IsNullOrWhiteSpace(row.CurrentSegment)
                ? "(nothing — the date folder sits directly under the root)"
                : row.CurrentSegment + (row.CurrentSegmentName is { } n ? $"  = {n}" : "")),
            ("Should be under", row.CorrectCatalogueId?.ToString()),
            ("Checked at", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
        };

    /// <summary>
    /// What the DevOps backlog made of this document type. Recorded whatever it said, agreement
    /// included — a check that only speaks up to disagree cannot be told from one that never ran.
    /// </summary>
    private static string DevOps(ScanRow row)
    {
        var evidence = string.IsNullOrWhiteSpace(row.AdoEvidence)
            ? ""
            : $"   (work items {row.AdoEvidence})";

        return row.AdoVerdict switch
        {
            nameof(AdoVerdict.Agrees) =>
                $"agrees — {(row.AdoService.Length > 0 ? row.AdoService : "the same service")}{evidence}",

            nameof(AdoVerdict.Disagrees) =>
                $"DISAGREES — the backlog says {row.AdoService}{evidence}",

            nameof(AdoVerdict.CannotTell) =>
                "asked, but could not tell — the CRM answer was used",

            _ => "not checked — DevOps was not set up for this run"
        };
    }
}
