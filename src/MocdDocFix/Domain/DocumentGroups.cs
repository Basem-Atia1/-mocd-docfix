namespace MocdDocFix.Domain;

/// <summary>
/// One kind of corruption, explained. The wizard's menus, the grouped report and the ledger all read
/// their wording from here so they can never drift apart (spec 2026-09-13 section 4).
/// </summary>
/// <param name="Number">1-9. Groups 1-5 are fixed; 6 to 9 are not.</param>
/// <param name="ShortLabel">One line, for menus.</param>
/// <param name="WhatIsInThePath">What the category segment actually contains.</param>
/// <param name="WhyItIsWrong">The defect, in terms of the data.</param>
/// <param name="HowWeKnow">Which record supplies the correct value.</param>
/// <param name="WhatTheToolDoes">The remedy, or why there is none.</param>
public sealed record DocumentGroup(
    int Number,
    string ShortLabel,
    string WhatIsInThePath,
    string WhyItIsWrong,
    string HowWeKnow,
    string WhatTheToolDoes,
    bool WillBeFixed);

public static class DocumentGroups
{
    private const string FromTheDocumentType =
        "the document's own document type carries mocd_servicecatalogue, and that is the " +
        "correct value; the parent request agrees with it.";

    private const string TheStandardRemedy =
        "downloads and backs up the file with its CRM records, re-uploads it under the correct " +
        "catalogue, checks the bytes match, shows you both, then repoints CRM on your say-so. " +
        "The old file is not touched until you run the delete step separately.";

    public static readonly IReadOnlyList<DocumentGroup> All = new[]
    {
        new DocumentGroup(1,
            "path holds a document type id",
            "a GUID, but it is the id of a document type, not of a service catalogue.",
            "the portal passed the document type where the catalogue belonged. It is a " +
            "well-formed GUID so nothing errored, but no such folder exists in the catalogue " +
            "tree — the file is filed nowhere.",
            FromTheDocumentType,
            TheStandardRemedy,
            true),

        new DocumentGroup(2,
            "path holds the literal word docType",
            "the text 'docType', sometimes with a GUID stuck to it with the dashes stripped.",
            "the variable name reached the file server instead of its value — a string " +
            "interpolation bug in the portal.",
            FromTheDocumentType,
            TheStandardRemedy,
            true),

        new DocumentGroup(3,
            "path holds a name instead of an id",
            "a human-readable label such as Document, boardDecision or goodConductCertificate " +
            "— and in a few cases leftovers from testing such as string, Test or 0.",
            "a display name was sent where an identifier belonged, so every document of that " +
            "kind landed in one shared folder regardless of which service it came from.",
            FromTheDocumentType,
            TheStandardRemedy,
            true),

        new DocumentGroup(4,
            "path has no catalogue segment at all",
            "nothing — the date folder sits directly beneath the DigitalServices root.",
            "an empty category was sent, so the file server skipped the folder entirely. This is " +
            "the CRM UploadDocument plugin's path: it recognises only three services, none of " +
            "them in scope, so the category stays empty. This defect is still live.",
            FromTheDocumentType,
            TheStandardRemedy,
            true),

        new DocumentGroup(5,
            "path holds a real but different catalogue",
            "a GUID that is a genuine service catalogue — just not this document's.",
            "on its own this is ambiguous: either the file is misfiled, or the document type is " +
            "wrong and the file is where it belongs. The parent request breaks the tie, and in " +
            "every one of these it sides with the document type. Two authorities against one, " +
            "so the path is the wrong one.",
            "the document type and the parent request name the same catalogue, and only the " +
            "path disagrees.",
            TheStandardRemedy,
            true),

        new DocumentGroup(6,
            "a human must decide - not touched",
            "either a path whose shape we do not recognise, or a path belonging to a document " +
            "whose two authorities contradict each other.",
            "when the document type and the parent request name different catalogues, neither " +
            "can be trusted and the path offers no third opinion. When the path is malformed, " +
            "rewriting it could lose information that is not recorded anywhere else.",
            "nothing does — that is the point. Both values are reported so a person can judge.",
            "nothing. These are listed with both catalogues and the malformation, if any, for " +
            "someone to correct in CRM. Re-scan afterwards and they will move into a fixable group.",
            false),

        new DocumentGroup(7,
            "nothing to do",
            "the correct catalogue already.",
            "nothing is wrong with these. They are the reason the sheet is shorter than the " +
            "population: a document nothing is wrong with is no longer written to it at all.",
            "the path segment already equals the document type's catalogue.",
            "nothing. It never reaches the ledger.",
            false),

        new DocumentGroup(8,
            "the record names no file at all",
            "nothing — the mocd_documentfile record has an empty mocd_filepath.",
            "there is no file to move and nothing in CRM says where it went. The document " +
            "exists, its file record exists, and between them they name no file.",
            "the record's own mocd_filepath is blank.",
            "nothing. It is listed so a person can judge — there is no path to diagnose and " +
            "nothing to re-upload.",
            false),

        new DocumentGroup(9,
            "the document type has no service catalogue",
            "whatever it happens to hold — there is no correct value to compare it against.",
            "the correct catalogue is read off the document's own document type, and that field " +
            "is empty. The path may be right or wrong and there is no way to tell.",
            "nothing does. The record that would say has nothing on it.",
            "nothing. Set mocd_servicecatalogue on the document type in CRM and re-scan, and " +
            "these move into a group that can be judged.",
            false)
    };

    public static DocumentGroup Get(int number) =>
        All.FirstOrDefault(g => g.Number == number)
        ?? throw new ArgumentOutOfRangeException(nameof(number), number, "No such document group.");

    public static IReadOnlyList<DocumentGroup> Fixable => All.Where(g => g.WillBeFixed).ToList();
}
