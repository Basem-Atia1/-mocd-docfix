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
    public void A_different_but_real_catalogue_is_review_not_fix()
    {
        // GAM Attendance document stored under GAM Request — both are valid catalogues.
        var r = Classify(
            @"DigitalServices\3ff27d73-653e-f111-b119-005056010908\20260518\a.pdf", GamAttendance);

        Assert.Equal(Verdict.Review, r.Verdict);
        Assert.Contains("valid service catalogue", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_foreign_but_real_catalogue_is_review()
    {
        var r = Classify(
            @"DigitalServices\9a39aa75-9933-f111-b119-005056010908\20260709\a.jpg", GamAttendance);

        Assert.Equal(Verdict.Review, r.Verdict);
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

    [Fact]
    public void No_document_type_catalogue_is_skipped_as_unfixable()
    {
        var r = Classify(@"DigitalServices\goodConductCertificate\20260330\a.jpg", null);

        Assert.Equal(Verdict.Skip, r.Verdict);
        Assert.Contains("no service catalogue", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_file_path_is_skipped()
    {
        var r = Classify("", EmployeeAppointment);

        Assert.Equal(Verdict.Skip, r.Verdict);
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
}
