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
        if (path.SegmentCount == 0)
            return new Classification(Verdict.Skip,
                "No file path on the document file record.",
                "Nothing to do — this is a legacy record with no file on the vendor server.",
                null, null);

        if (docTypeCatalogue is null)
            return new Classification(Verdict.Skip,
                "The document type has no service catalogue, so there is no correct value to write.",
                "Cannot be fixed by this tool. Set mocd_servicecatalogue on the document type " +
                "in CRM first, then re-scan.",
                null, path.CategorySegment);

        var correct = docTypeCatalogue.Value;

        // Already correct?
        if (path.CategorySegment is not null &&
            Guid.TryParse(path.CategorySegment, out var segmentGuid) &&
            segmentGuid == correct)
        {
            return new Classification(Verdict.Skip,
                "Path already correct — matches the document type's catalogue.",
                "No action needed.",
                correct, path.CategorySegment);
        }

        // Malformed structure we should not rewrite blindly.
        if (path.HasDoubledSeparators || (path.SegmentCount != 4 && path.SegmentCount != 3))
        {
            return new Classification(Verdict.Review,
                $"Path is malformed ({path.SegmentCount} segments" +
                (path.HasDoubledSeparators ? ", doubled separators" : "") + ").",
                "Not touched automatically — the path does not have the expected shape, so " +
                "rewriting it could lose information. Inspect this record by hand.",
                correct, path.CategorySegment);
        }

        // The two authorities disagree — see spec section 4.2.
        if (crossCheckCatalogue is not null && crossCheckCatalogue.Value != correct)
        {
            return new Classification(Verdict.Review,
                $"Cross-check conflict: the parent request says {crossCheckCatalogue.Value} " +
                $"but the document type says {correct}.",
                "Not touched automatically — the two authorities disagree, so we cannot tell " +
                "which catalogue is right. A human must decide which one applies.",
                null, path.CategorySegment);
        }

        // The segment is a real catalogue, just a different one — may already be right.
        if (path.CategorySegment is not null && isKnownCatalogue(path.CategorySegment))
        {
            return new Classification(Verdict.Review,
                $"Path segment '{path.CategorySegment}' is a valid service catalogue, but not the " +
                $"document type's ({correct}). It may be correctly filed.",
                $"Not touched automatically — the file may already be in the right place. Decide " +
                $"whether the file belongs under '{path.CategorySegment}' (leave it, and correct " +
                $"the document type's catalogue in CRM) or under {correct} (re-run with " +
                $"--force-review to move it).",
                correct, path.CategorySegment);
        }

        var describe = path.CategorySegment is null
            ? "no category segment at all"
            : $"'{path.CategorySegment}', which is not a service catalogue id";

        return new Classification(Verdict.Fix,
            $"Path has {describe}; correct catalogue is {correct}.",
            $"Re-upload the file with Category = {correct}, create a new mocd_documentfile with " +
            $"the vendor's new FileId, repoint the document at it, then delete the old file.",
            correct, path.CategorySegment);
    }
}
