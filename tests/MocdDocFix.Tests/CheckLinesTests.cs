using MocdDocFix.Domain;
using MocdDocFix.Storage;
using MocdDocFix.Tests.Fakes;
using MocdDocFix.Ui;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// The step-1 block both modes print. It exists because the full run used to show totals and
/// four file paths while the targeted run showed every authority's opinion per document — so an
/// operator could only read what the tool thought by picking files one at a time.
/// </summary>
public class CheckLinesTests
{
    private static ScanRow Row(string verdict, int group = 3) =>
        new(DocumentId: Guid.Parse("2c9d5572-a77b-f111-b10f-00505601095a"),
            DocumentFileId: Guid.NewGuid(),
            FileName: "cert.jpg",
            DocumentTypeName: "A Medical Examination Certificate",
            ServiceCatalogueId: Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
            ServiceCatalogueName: "Employee Appointment",
            OldFilePath: @"DigitalServices\goodConductCertificate\20260330\a.jpg",
            CurrentSegment: "goodConductCertificate",
            CurrentSegmentName: null,
            CorrectCatalogueId: Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908"),
            Verdict: verdict,
            Group: group,
            GroupLabel: "the path names a document type",
            Reason: "the path names a document type, not a service catalogue",
            Solution: "Re-upload under the correct catalogue and repoint the document.",
            CrossCheckSource: "mocd_employeeappintmentrequest",
            CrmLink: "https://crm/doc");

    private static string Said(ScanRow row, bool? crossCheckAgrees = null)
    {
        var prompts = new FakePrompts();
        CheckLines.Write(prompts, row, crossCheckAgrees: crossCheckAgrees);
        return string.Join("\n", prompts.Messages);
    }

    [Fact]
    public void A_broken_document_shows_every_authority_and_what_will_be_done()
    {
        var said = Said(Row(nameof(Verdict.Fix)));

        Assert.Contains("A Medical Examination Certificate", said);
        Assert.Contains("Employee Appointment", said);
        Assert.Contains("goodConductCertificate", said);
        Assert.Contains("VERDICT  BROKEN", said);
        Assert.Contains("SOLUTION", said);
    }

    /// <summary>
    /// Agreement is printed, not only disagreement — a check that speaks up only to object
    /// cannot be told from one that never ran, which is how the DevOps check first read.
    /// </summary>
    [Fact]
    public void A_correct_document_says_so_in_one_line()
    {
        var said = Said(Row(nameof(Verdict.Skip), group: 0));

        Assert.Contains("VERDICT  OK", said);
        Assert.DoesNotContain("SOLUTION", said);
    }

    [Fact]
    public void The_parent_request_is_shown_agreeing_or_disagreeing_when_there_is_one()
    {
        Assert.Contains("DISAGREES", Said(Row(nameof(Verdict.Fix)), crossCheckAgrees: false));
        Assert.Contains("agrees", Said(Row(nameof(Verdict.Fix)), crossCheckAgrees: true));
    }
}
