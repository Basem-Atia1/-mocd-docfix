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
}
