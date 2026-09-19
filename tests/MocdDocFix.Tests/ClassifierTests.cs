using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

public class ClassifierTests
{
    private static readonly Guid EmployeeAppointment = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid GamAttendance       = Guid.Parse("24db2387-c15d-f111-b119-005056010908");
    private static readonly Guid GamRequest          = Guid.Parse("3ff27d73-653e-f111-b119-005056010908");
    private static readonly Guid RequestToJoinNpo    = Guid.Parse("9a39aa75-9933-f111-b119-005056010908");

    /// <summary>Only these GUIDs are real service catalogues in these tests.</summary>
    private static bool IsCatalogue(string segment) =>
        Guid.TryParse(segment, out var g) &&
        (g == EmployeeAppointment || g == GamAttendance || g == GamRequest || g == RequestToJoinNpo);

    private static Classification Classify(string path, Guid? docTypeCat, Guid? crossCheck = null) =>
        Classifier.Classify(FilePathParser.Parse(path), docTypeCat, crossCheck, IsCatalogue);

    [Fact]
    public void Correct_catalogue_is_skipped()
    {
        var r = Classify(
            @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260330\a.jpg", EmployeeAppointment);

        Assert.Equal(Verdict.Skip, r.Verdict);
        Assert.Contains("already correct", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalogue_match_is_case_insensitive()
    {
        var r = Classify(
            @"DigitalServices\CD97BF8D-BEA8-F011-B116-005056010908\20260330\a.jpg", EmployeeAppointment);

        Assert.Equal(Verdict.Skip, r.Verdict);
    }

    [Fact]
    public void Document_type_name_segment_is_fixed()
    {
        var r = Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment);

        Assert.Equal(Verdict.Fix, r.Verdict);
        Assert.Equal(EmployeeAppointment, r.CorrectCatalogueId);
        Assert.Equal("goodConductCertificate", r.CurrentSegment);
    }

    [Fact]
    public void DocType_prefixed_segment_is_fixed()
    {
        var r = Classify(
            @"DigitalServices\docType2746f51e7e3ef111b119005056010908\20260518\a.pdf", GamRequest);

        Assert.Equal(Verdict.Fix, r.Verdict);
    }

    [Fact]
    public void Numeric_segment_is_fixed()
    {
        var r = Classify(@"DigitalServices\0\20260423\a.png", EmployeeAppointment);

        Assert.Equal(Verdict.Fix, r.Verdict);
    }

    [Fact]
    public void Missing_category_segment_is_fixed()
    {
        var r = Classify(@"DigitalServices\20260707\a.doc", GamRequest);

        Assert.Equal(Verdict.Fix, r.Verdict);
        Assert.Null(r.CurrentSegment);
    }

    [Fact]
    public void A_different_but_real_catalogue_is_fixed()
    {
        // GAM Attendance document stored under GAM Request — both are valid catalogues.
        // Spec 2026-09-13 section 3.1: the document type decides, so this is group 5 and is fixed.
        var r = Classify(
            @"DigitalServices\3ff27d73-653e-f111-b119-005056010908\20260518\a.pdf", GamAttendance);

        Assert.Equal(Verdict.Fix, r.Verdict);
        Assert.Equal(5, r.Group);
        Assert.Equal(GamAttendance, r.CorrectCatalogueId);
        Assert.Contains("valid service catalogue", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_foreign_but_real_catalogue_is_fixed()
    {
        var r = Classify(
            @"DigitalServices\9a39aa75-9933-f111-b119-005056010908\20260709\a.jpg", GamAttendance);

        Assert.Equal(Verdict.Fix, r.Verdict);
        Assert.Equal(5, r.Group);
    }

    [Fact]
    public void A_cross_check_conflict_beats_a_real_but_different_catalogue()
    {
        // Group 6 must win over group 5: when the parent request disagrees with the document
        // type we have no trustworthy answer, whatever the path happens to hold.
        var r = Classify(
            @"DigitalServices\3ff27d73-653e-f111-b119-005056010908\20260518\a.pdf",
            GamAttendance, EmployeeAppointment);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Equal(6, r.Group);
    }

    [Fact]
    public void Cross_check_disagreement_is_review_even_when_segment_is_junk()
    {
        var r = Classify(
            @"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment, GamRequest);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Contains("cross-check", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cross_check_agreement_still_fixes()
    {
        var r = Classify(
            @"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment, EmployeeAppointment);

        Assert.Equal(Verdict.Fix, r.Verdict);
    }

    /// <summary>
    /// Group 9, not 7. The correct catalogue is read off the document's own type, and that field
    /// is empty — so the path may be right or wrong and there is no way to tell. Leaving it as
    /// skip would sweep it out of the sheet along with the documents that really are correct,
    /// when it is a thing to be put right in CRM.
    /// </summary>
    [Fact]
    public void A_document_type_with_no_catalogue_is_group_nine_and_wants_a_human()
    {
        var r = Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", null);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Equal(9, r.Group);
        Assert.Contains("no service catalogue", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Group 8. A record naming no file is not a document that is fine — it is a document with
    /// nothing behind it.
    /// </summary>
    [Fact]
    public void A_record_with_no_file_path_is_group_eight_and_wants_a_human()
    {
        var r = Classify("", EmployeeAppointment);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Equal(8, r.Group);
        Assert.Contains("no file path", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doubled_separator_path_is_review_not_fix()
    {
        var r = Classify(@"DigitalServices\\POD\\20250911\\a.png", EmployeeAppointment);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Contains("malformed", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_fix_states_the_remedy_including_the_catalogue_it_will_use()
    {
        var r = Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment);

        Assert.Contains("Re-upload", r.Solution);
        Assert.Contains(EmployeeAppointment.ToString(), r.Solution);
        Assert.Contains("repoint", r.Solution, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_verdict_carries_a_non_empty_solution()
    {
        _ = IsCatalogue(RequestToJoinNpo.ToString());   // keep the helper referenced

        var cases = new[]
        {
            Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment),
            Classify($@"DigitalServices\{EmployeeAppointment}\20260330\a.jpg", EmployeeAppointment),
            Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", null),
            Classify($@"DigitalServices\{GamRequest}\20260518\a.pdf", GamAttendance),
            Classify("", EmployeeAppointment)
        };

        Assert.All(cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Solution)));
    }

    // ---- group numbering, spec 2026-09-13 section 3 ----

    [Fact]
    public void A_document_type_guid_segment_is_group_one()
    {
        // A real GUID, but not a service catalogue — this is a document type id.
        var r = Classify(
            @"DigitalServices\9b1121f4-e30b-f111-b117-005056010908\20260312\a.png", EmployeeAppointment);

        Assert.Equal(Verdict.Fix, r.Verdict);
        Assert.Equal(1, r.Group);
    }

    [Fact]
    public void A_docType_prefixed_segment_is_group_two()
    {
        Assert.Equal(2, Classify(
            @"DigitalServices\docType2746f51e7e3ef111b119005056010908\20260518\a.pdf", GamRequest).Group);

        Assert.Equal(2, Classify(@"DigitalServices\docType\20260429\a.pdf", EmployeeAppointment).Group);
    }

    [Theory]
    [InlineData("goodConductCertificate")]
    [InlineData("Document")]
    [InlineData("boardDecision")]
    [InlineData("string")]
    [InlineData("0")]
    public void A_name_segment_is_group_three(string segment)
    {
        Assert.Equal(3, Classify($@"DigitalServices\{segment}\20260330\a.jpg", EmployeeAppointment).Group);
    }

    [Fact]
    public void A_missing_segment_is_group_four()
    {
        Assert.Equal(4, Classify(@"DigitalServices\20260707\a.doc", GamRequest).Group);
    }

    /// <summary>
    /// Group 7 is now one thing only: a document that is filed correctly. It used to hold three,
    /// and the other two were not "nothing to do" at all — they were problems with no remedy in
    /// this tool, which is a different statement. They are groups 8 and 9.
    /// </summary>
    [Fact]
    public void Group_seven_is_only_a_document_that_is_filed_correctly()
    {
        Assert.Equal(7, Classify(
            $@"DigitalServices\{EmployeeAppointment}\20260330\a.jpg", EmployeeAppointment).Group);

        Assert.Equal(8, Classify("", EmployeeAppointment).Group);

        Assert.Equal(9, Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", null).Group);
    }

    [Fact]
    public void Every_group_number_the_classifier_produces_is_a_known_group()
    {
        var cases = new[]
        {
            Classify(@"DigitalServices\9b1121f4-e30b-f111-b117-005056010908\20260312\a.png", EmployeeAppointment),
            Classify(@"DigitalServices\docType\20260429\a.pdf", EmployeeAppointment),
            Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment),
            Classify(@"DigitalServices\20260707\a.doc", GamRequest),
            Classify($@"DigitalServices\{GamRequest}\20260518\a.pdf", GamAttendance),
            Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", EmployeeAppointment, GamRequest),
            Classify("", EmployeeAppointment),
            Classify(@"DigitalServices\\POD\\20250911\\a.png", EmployeeAppointment)
        };

        Assert.All(cases, c => Assert.Equal(
            DocumentGroups.Get(c.Group).WillBeFixed, c.Verdict == Verdict.Fix));
    }

    [Fact]
    public void A_malformed_path_is_group_six_because_a_human_must_decide()
    {
        var r = Classify(@"DigitalServices\\POD\\20250911\\a.png", EmployeeAppointment);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Equal(6, r.Group);
        Assert.Contains("malformed", r.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
