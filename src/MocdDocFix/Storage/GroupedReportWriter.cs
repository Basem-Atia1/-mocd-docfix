using System.Text;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// Writes the operator's working document: every scanned file grouped by the kind of corruption,
/// with each group's full reason spelled out above its rows (spec 2026-09-13 section 5).
///
/// The CSV is for filtering; this is for reading. It answers "what is wrong, why, and what will
/// you do about it" without the reader having to open Excel or know any GUIDs.
/// </summary>
public sealed class GroupedReportWriter
{
    private const int Rule = 100;

    private readonly string _reportsDir;

    public GroupedReportWriter(string reportsDir) => _reportsDir = reportsDir;

    /// <param name="sevenServices">How many services are in scope, for the header line.</param>
    public string Write(string env, IReadOnlyCollection<ScanRow> rows, int sevenServices)
    {
        Directory.CreateDirectory(_reportsDir);
        var path = Path.Combine(_reportsDir, $"groups-{env}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var text = new StringBuilder();
        Header(text, env, rows.Count, sevenServices);

        foreach (var group in rows.GroupBy(r => r.Group).OrderBy(g => g.Key))
            Group(text, DocumentGroups.Get(group.Key), group.ToList());

        Summary(text, rows);

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static void Header(StringBuilder text, string env, int count, int services)
    {
        text.AppendLine($"MoCD document file path remediation — {env} — {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine($"Scope: {services} services, Employee Appointment through By-Laws Amendment.");
        text.AppendLine("Membership Managment is excluded, at the operator's instruction (2026-09-13).");
        text.AppendLine();
        text.AppendLine($"{count} documents in scope.");
        text.AppendLine();
        text.AppendLine("The correct catalogue always comes from the document's own document type:");
        text.AppendLine("    mocd_document -> mocd_documenttype -> mocd_servicecatalogue");
        text.AppendLine("and is compared against the folder the file actually sits in:");
        text.AppendLine(@"    DigitalServices\<folder>\<yyyyMMdd>\<fileGuid>.<ext>");
        text.AppendLine();
    }

    private static void Group(StringBuilder text, DocumentGroup group, IReadOnlyList<ScanRow> rows)
    {
        text.AppendLine(new string('=', Rule));
        text.AppendLine($"GROUP {group.Number} — {rows.Count} {Files(rows.Count)} — " +
                        (group.WillBeFixed ? "WILL BE FIXED" : "NOT TOUCHED"));
        Wrapped(text, "What is in the path", group.WhatIsInThePath);
        Wrapped(text, "Why it is wrong", group.WhyItIsWrong);
        Wrapped(text, "How we know", group.HowWeKnow);
        Wrapped(text, "What the tool does", group.WhatTheToolDoes);
        text.AppendLine(new string('=', Rule));

        foreach (var service in rows
                     .GroupBy(r => r.ServiceCatalogueName ?? "(no service catalogue)")
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine();
            text.AppendLine($"  --- {service.Key}  ({service.Count()} {Files(service.Count())}) ---");

            foreach (var docType in service
                         .GroupBy(r => r.DocumentTypeName)
                         .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                text.AppendLine($"      {docType.Count(),4}  {docType.Key}");

                foreach (var segment in docType
                             .GroupBy(r => r.CurrentSegment)
                             .OrderByDescending(g => g.Count()))
                {
                    var shown = string.IsNullOrWhiteSpace(segment.Key) ? "(none)" : segment.Key;
                    var name = segment.First().CurrentSegmentName;
                    var suffix = string.IsNullOrWhiteSpace(name) ? "" : $"  = {name}";
                    text.AppendLine($"            {segment.Count(),4}  under: {shown}{suffix}");
                }

                var example = docType.First();
                text.AppendLine($"            e.g.  {example.FileName}");
                text.AppendLine($"                  {example.OldFilePath}");
                text.AppendLine($"                  document   {example.DocumentId}");
                text.AppendLine($"                  file rec.  {example.DocumentFileId}");
            }
        }

        text.AppendLine();
    }

    private static void Summary(StringBuilder text, IReadOnlyCollection<ScanRow> rows)
    {
        text.AppendLine(new string('=', Rule));
        text.AppendLine("SUMMARY");
        text.AppendLine(new string('=', Rule));

        foreach (var group in rows.GroupBy(r => r.Group).OrderBy(g => g.Key))
        {
            var definition = DocumentGroups.Get(group.Key);
            text.AppendLine($"  {group.Count(),5}  fix:{(definition.WillBeFixed ? "YES" : "no"),-4}  " +
                            $"{definition.Number} - {definition.ShortLabel}");
        }

        var fixable = rows.Count(r => DocumentGroups.Get(r.Group).WillBeFixed);
        text.AppendLine($"  {fixable,5}  TOTAL to fix (groups 1-5)");
        text.AppendLine();
    }

    /// <summary>A labelled paragraph, wrapped so the file reads in an 100-column terminal.</summary>
    private static void Wrapped(StringBuilder text, string label, string body)
    {
        const int indent = 24;
        var width = Rule - indent;
        var first = true;

        foreach (var line in Lines(body, width))
        {
            text.AppendLine(first
                ? $"  {label + ":",-20}  {line}"
                : new string(' ', indent) + line);
            first = false;
        }
    }

    private static IEnumerable<string> Lines(string body, int width)
    {
        var line = new StringBuilder();

        foreach (var word in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }

    private static string Files(int count) => count == 1 ? "file" : "files";
}
