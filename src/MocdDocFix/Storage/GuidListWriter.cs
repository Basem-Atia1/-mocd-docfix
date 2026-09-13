using System.Text;
using MocdDocFix.Domain;

namespace MocdDocFix.Storage;

/// <summary>
/// The actionable companion to the grouped report: the document GUIDs of each group, one per
/// line, under '#' headers that say which group they belong to.
///
/// Because --docs-file ignores blank lines and lines starting with '#', the file can be passed
/// straight back to the tool. Groups the tool will not fix (6 and 7) are listed too, but with
/// their GUIDs commented out, so handing the whole file to --docs-file can never act on a
/// document nobody decided about.
/// </summary>
public sealed class GuidListWriter
{
    private const string Rule = "# ================================================================";

    private readonly string _reportsDir;

    public GuidListWriter(string reportsDir) => _reportsDir = reportsDir;

    public string Write(string env, IReadOnlyCollection<ScanRow> rows)
    {
        Directory.CreateDirectory(_reportsDir);
        var path = Path.Combine(_reportsDir, $"guids-{env}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var text = new StringBuilder();
        Header(text, env, rows.Count);

        foreach (var group in rows.GroupBy(r => r.Group).OrderBy(g => g.Key))
            Group(text, DocumentGroups.Get(group.Key), group.ToList());

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static void Header(StringBuilder text, string env, int count)
    {
        text.AppendLine($"# MoCD document file path remediation — {env} — {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine("#");

        if (count == 0)
        {
            text.AppendLine("# The scan found no documents at all. Nothing to list.");
            return;
        }

        text.AppendLine("# The document GUID of every scanned file, grouped by what is wrong with it.");
        text.AppendLine("# The grouped report of the same scan explains each group in full.");
        text.AppendLine("#");
        text.AppendLine("# Lines starting with # are ignored, so this file can be handed straight to:");
        text.AppendLine($"#     docfix targeted --env {env} --docs-file <this file>");
        text.AppendLine("#");
        text.AppendLine("# To work on one group, delete the others — or just pick the group by number");
        text.AppendLine("# in the wizard, which needs no file at all.");
        text.AppendLine();
    }

    private static void Group(StringBuilder text, DocumentGroup group, IReadOnlyList<ScanRow> rows)
    {
        var ids = rows.Select(r => r.DocumentId).Distinct().ToList();

        text.AppendLine(Rule);
        text.AppendLine($"# GROUP {group.Number} — {ids.Count} {(ids.Count == 1 ? "file" : "files")} — {group.ShortLabel}");

        foreach (var service in rows
                     .GroupBy(r => r.ServiceCatalogueName ?? "(no service catalogue)")
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"#     {service.Count(),5}  {service.Key}");
        }

        if (!group.WillBeFixed)
        {
            text.AppendLine("#");
            text.AppendLine("# These are NOT fixed by the tool, so their GUIDs are commented out:");
            text.AppendLine($"#   {group.WhatTheToolDoes}");
        }

        text.AppendLine(Rule);

        var prefix = group.WillBeFixed ? "" : "# ";
        foreach (var id in ids) text.AppendLine($"{prefix}{id}");

        text.AppendLine();
    }
}
