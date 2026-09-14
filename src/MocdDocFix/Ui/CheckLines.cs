using MocdDocFix.Domain;
using MocdDocFix.Storage;

namespace MocdDocFix.Ui;

/// <summary>
/// One document's step-1 account, on screen: what it is, what every authority said about it,
/// and what will be done. Shared by the targeted and the full run so the two cannot drift —
/// a full run that shows only totals gives an operator nothing to disagree with, which is the
/// one thing step 1 is for.
/// </summary>
public static class CheckLines
{
    public static void Write(IPrompts prompts, ScanRow row, string? heading = null,
        bool? crossCheckAgrees = null)
    {
        prompts.Section(heading ?? row.FileName ?? row.DocumentId.ToString());

        prompts.Field("document", row.DocumentId.ToString(), Tone.Muted);
        prompts.Field("document type", row.DocumentTypeName ?? "(not known)", Tone.Muted);
        prompts.Field("service", row.ServiceCatalogueName ?? "(none on the type)", Tone.Muted);
        prompts.Field("current path", row.OldFilePath ?? "(none)", Tone.Muted);
        prompts.Field("filed under", FiledUnder(row), Tone.Muted);

        // Said out loud on every document, agreement included. A check whose agreement is silent
        // is indistinguishable from a check that never ran — which is how it read the first time
        // this was used.
        WriteDevOps(prompts, row);

        if (crossCheckAgrees is { } agrees && row.CrossCheckSource is { } source)
            prompts.Field("parent request", $"{source} {(agrees ? "agrees" : "DISAGREES")}",
                agrees ? Tone.Muted : Tone.Warn);

        prompts.Blank();
        WriteVerdict(prompts, row);
    }

    private static string FiledUnder(ScanRow row) =>
        string.IsNullOrWhiteSpace(row.CurrentSegment)
            ? "(nothing — the date folder sits directly under the root)"
            : row.CurrentSegment + (row.CurrentSegmentName is { } name ? $"  = {name}" : "");

    private static void WriteVerdict(IPrompts prompts, ScanRow row)
    {
        // Looked up only where it is used: a row with nothing wrong need not carry a group, and
        // asking for one it does not have would turn a clean document into a crash.
        if (row.Verdict == nameof(Verdict.Skip))
        {
            prompts.Say($"VERDICT  OK — {row.Reason}", Tone.Good);
            return;
        }

        var group = DocumentGroups.Get(row.Group);

        if (row.Verdict == nameof(Verdict.Review))
        {
            prompts.Say($"VERDICT  NEEDS A HUMAN — group {group.Number}, {group.ShortLabel}",
                Tone.Warn);
            prompts.Say($"         {row.Reason}", Tone.Warn);
            prompts.Blank();
            prompts.Say($"NOT TOUCHED  {row.Solution}", Tone.Muted);
            return;
        }

        prompts.Say($"VERDICT  BROKEN — group {group.Number}, {group.ShortLabel}", Tone.Danger);
        prompts.Say($"         {row.Reason}", Tone.Danger);
        prompts.Blank();
        prompts.Field("should be under", row.CorrectCatalogueId?.ToString() ?? "(none)", Tone.Muted);
        prompts.Say($"SOLUTION  {row.Solution}");
    }

    /// <summary>What the DevOps backlog made of this document's type, whatever it said.</summary>
    public static void WriteDevOps(IPrompts prompts, ScanRow row) =>
        WriteDevOps(prompts, row.AdoVerdict, row.AdoService, row.AdoEvidence);

    public static void WriteDevOps(IPrompts prompts, string verdict, string? service, string? evidence)
    {
        var (text, tone) = DevOps(verdict, service, evidence);
        prompts.Field("DevOps", text, tone);
    }

    /// <summary>
    /// The one sentence that says where the backlog stands — shared by the screen, the step-1
    /// report and the upload briefing, so a document cannot be described one way at the check
    /// and another way at the moment it is moved.
    /// </summary>
    public static (string Text, Tone Tone) DevOps(string verdict, string? service, string? evidence)
    {
        var items = string.IsNullOrWhiteSpace(evidence) ? "" : $"   (work items {evidence})";

        return verdict switch
        {
            nameof(AdoVerdict.Agrees) =>
                ($"agrees — {(string.IsNullOrWhiteSpace(service) ? "same service" : service)}{items}",
                    Tone.Good),

            nameof(AdoVerdict.Disagrees) =>
                ($"DISAGREES — the backlog says {service}{items}", Tone.Danger),

            nameof(AdoVerdict.CannotTell) =>
                ("asked, but could not tell — left to the CRM answer", Tone.Warn),

            _ => ("not checked — DevOps is not set up for this run", Tone.Muted)
        };
    }
}
