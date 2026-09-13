namespace MocdDocFix.Domain;

public static class Classifier
{
    /// <param name="isKnownCatalogue">
    /// Resolves a path segment against the live mocd_servicecatalogue table. Injected so the
    /// set is never hardcoded — see spec section 4.3.
    /// </param>
    public static Classification Classify(
        FilePathParts path,
        Guid? docTypeCatalogue,
        Guid? crossCheckCatalogue,
        Func<string, bool> isKnownCatalogue)
    {
        // ---- group 7: nothing to do ----

        if (path.SegmentCount == 0)
            return new Classification(Verdict.Skip, 7,
                "No file path on the document file record.",
                "Nothing to do — this is a legacy record with no file on the vendor server.",
                null, null);

        if (docTypeCatalogue is null)
            return new Classification(Verdict.Skip, 7,
                "The document type has no service catalogue, so there is no correct value to write.",
                "Cannot be fixed by this tool. Set mocd_servicecatalogue on the document type " +
                "in CRM first, then re-scan.",
                null, path.CategorySegment);

        var correct = docTypeCatalogue.Value;

        if (path.CategorySegment is not null &&
            Guid.TryParse(path.CategorySegment, out var segmentGuid) &&
            segmentGuid == correct)
        {
            return new Classification(Verdict.Skip, 7,
                "Path already correct — matches the document type's catalogue.",
                "No action needed.",
                correct, path.CategorySegment);
        }

        // ---- group 6: a human must decide ----
        //
        // Both arms land here rather than inside a fixable group, because the operator reads a
        // group heading as true of every row beneath it. See spec 2026-09-13 section 3.

        if (path.HasDoubledSeparators || (path.SegmentCount != 4 && path.SegmentCount != 3))
        {
            return new Classification(Verdict.Review, 6,
                $"Path is malformed ({path.SegmentCount} segments" +
                (path.HasDoubledSeparators ? ", doubled separators" : "") + ").",
                "Not touched automatically — the path does not have the expected shape, so " +
                "rewriting it could lose information. Inspect this record by hand.",
                correct, path.CategorySegment);
        }

        // The two authorities disagree. Tested before group 5 deliberately: a conflict makes the
        // document type untrustworthy, whatever the path happens to hold.
        if (crossCheckCatalogue is not null && crossCheckCatalogue.Value != correct)
        {
            return new Classification(Verdict.Review, 6,
                $"Cross-check conflict: the parent request says {crossCheckCatalogue.Value} " +
                $"but the document type says {correct}.",
                "Not touched automatically — the two authorities disagree, so we cannot tell " +
                "which catalogue is right. A human must decide which one applies.",
                null, path.CategorySegment);
        }

        // ---- group 5: a real catalogue, but the wrong one ----
        //
        // Fixed, not reviewed: we only reach here when the parent request agrees with the
        // document type (or is absent), so two authorities outvote the path. Spec section 3.1.

        if (path.CategorySegment is not null && isKnownCatalogue(path.CategorySegment))
        {
            return new Classification(Verdict.Fix, 5,
                $"Path segment '{path.CategorySegment}' is a valid service catalogue, but not the " +
                $"document type's ({correct}), and the parent request sides with the document type.",
                Remedy(correct), correct, path.CategorySegment);
        }

        // ---- groups 1-4: the segment is not a catalogue at all ----

        var (group, describe) = Shape(path.CategorySegment);

        return new Classification(Verdict.Fix, group,
            $"Path has {describe}; correct catalogue is {correct}.",
            Remedy(correct), correct, path.CategorySegment);
    }

    /// <summary>Which of groups 1-4 a non-catalogue segment falls into, and how to say so.</summary>
    private static (int Group, string Describe) Shape(string? segment)
    {
        if (string.IsNullOrEmpty(segment))
            return (4, "no category segment at all");

        if (segment.StartsWith("docType", StringComparison.OrdinalIgnoreCase))
            return (2, $"'{segment}', the variable name rather than its value");

        if (Guid.TryParse(segment, out _))
            return (1, $"'{segment}', which is a document type id, not a service catalogue");

        return (3, $"'{segment}', a name rather than a service catalogue id");
    }

    private static string Remedy(Guid correct) =>
        $"Re-upload the file with Category = {correct}, create a new mocd_documentfile with " +
        $"the vendor's new FileId, repoint the document at it, then delete the old file.";
}
