using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class GroupedReportWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-grouped-" + Guid.NewGuid());

    public GroupedReportWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static ScanRow Row(
        int group, string service, string docType, string? segment = "docType",
        string? segmentName = null, string verdict = "Fix", string? fileName = "a.pdf") =>
        new(Guid.NewGuid(), Guid.NewGuid(), fileName, docType,
            Guid.NewGuid(), service,
            segment is null ? @"DigitalServices\20260101\a.pdf" : $@"DigitalServices\{segment}\20260101\a.pdf",
            segment, segmentName, Guid.NewGuid(), verdict,
            group, DocumentGroups.Get(group).ShortLabel,
            "because", "do this", null, "https://crm/x");

    private string Write(params ScanRow[] rows) =>
        File.ReadAllText(new GroupedReportWriter(_dir).Write("dev", rows, sevenServices: 7));

    [Fact]
    public void Every_group_present_in_the_data_gets_a_heading()
    {
        var text = Write(Row(1, "Employee Appointment Request", "Medical Certificate"),
                         Row(6, "By-Laws Amendment Requests", "Other", verdict: "Review"));

        Assert.Contains("GROUP 1", text);
        Assert.Contains("GROUP 6", text);
    }

    [Fact]
    public void A_group_with_no_rows_is_left_out_entirely()
    {
        var text = Write(Row(1, "Employee Appointment Request", "Medical Certificate"));

        Assert.Contains("GROUP 1", text);
        Assert.DoesNotContain("GROUP 3", text);
    }

    /// <summary>The paragraphs are wrapped to fit a terminal, so compare on the words.</summary>
    private static string Flat(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Each_heading_carries_the_full_reason_for_that_group()
    {
        var text = Flat(Write(Row(4, "Employee Appointment Request", "FAHR Document", segment: null)));
        var group = DocumentGroups.Get(4);

        Assert.Contains("What is in the path:", text);
        Assert.Contains(Flat(group.WhatIsInThePath), text);
        Assert.Contains(Flat(group.WhyItIsWrong), text);
        Assert.Contains(Flat(group.HowWeKnow), text);
        Assert.Contains(Flat(group.WhatTheToolDoes), text);
    }

    [Fact]
    public void A_heading_says_whether_the_group_will_be_fixed()
    {
        var text = Write(Row(1, "Employee Appointment Request", "Medical Certificate"),
                         Row(6, "By-Laws Amendment Requests", "Other", verdict: "Review"));

        Assert.Contains("WILL BE FIXED", text);
        Assert.Contains("NOT TOUCHED", text);
    }

    [Fact]
    public void Rows_are_counted_per_group_per_service_and_per_document_type()
    {
        var text = Write(
            Row(1, "Employee Appointment Request", "Medical Certificate"),
            Row(1, "Employee Appointment Request", "Medical Certificate"),
            Row(1, "Employee Appointment Request", "Academic Certificate"),
            Row(1, "By-Laws Amendment Requests", "Other"));

        Assert.Contains("GROUP 1 — 4 files", text);
        Assert.Contains("Employee Appointment Request  (3 files)", text);
        Assert.Contains("By-Laws Amendment Requests  (1 file)", text);
        Assert.Contains("2  Medical Certificate", text);
        Assert.Contains("1  Academic Certificate", text);
    }

    [Fact]
    public void Services_are_ordered_with_the_biggest_problem_first()
    {
        var text = Write(
            Row(1, "Small Service", "X"),
            Row(1, "Big Service", "Y"),
            Row(1, "Big Service", "Y"));

        Assert.True(text.IndexOf("Big Service", StringComparison.Ordinal)
                  < text.IndexOf("Small Service", StringComparison.Ordinal));
    }

    [Fact]
    public void An_example_document_and_file_record_id_is_given_for_each_document_type()
    {
        var row = Row(1, "Employee Appointment Request", "Medical Certificate");
        var text = File.ReadAllText(new GroupedReportWriter(_dir).Write("dev", new[] { row }, 7));

        Assert.Contains(row.DocumentId.ToString(), text);
        Assert.Contains(row.DocumentFileId.ToString(), text);
        Assert.Contains(row.OldFilePath!, text);
    }

    [Fact]
    public void The_segment_a_file_sits_under_is_named_when_it_is_a_real_service()
    {
        var text = Write(Row(5, "GAM - Attendance", "Approved Attendance List",
            segment: "3ff27d73-653e-f111-b119-005056010908",
            segmentName: "General Assembly Meeting Request"));

        Assert.Contains("General Assembly Meeting Request", text);
    }

    [Fact]
    public void A_missing_segment_reads_as_none_rather_than_as_a_blank()
    {
        var text = Write(Row(4, "Employee Appointment Request", "FAHR Document", segment: null));

        Assert.Contains("(none)", text);
    }

    [Fact]
    public void The_summary_totals_the_fixable_groups()
    {
        var text = Write(
            Row(1, "A", "X"),
            Row(3, "A", "X"),
            Row(6, "A", "X", verdict: "Review"),
            Row(7, "A", "X", verdict: "Skip"));

        Assert.Contains("SUMMARY", text);
        Assert.Contains("2  TOTAL to fix (groups 1-5)", text);
    }

    [Fact]
    public void The_header_states_the_scope_so_nobody_reads_it_out_of_context()
    {
        var text = Write(Row(1, "A", "X"));

        Assert.Contains("7 services", text);
        Assert.Contains("Membership", text);   // named as excluded
        Assert.Contains("dev", text);
    }

    [Fact]
    public void The_file_is_named_for_the_environment_and_the_moment()
    {
        var path = new GroupedReportWriter(_dir).Write("dev", new[] { Row(1, "A", "X") }, 7);

        Assert.StartsWith("groups-dev-", Path.GetFileName(path));
        Assert.EndsWith(".txt", path);
    }

    [Fact]
    public void An_empty_scan_still_writes_a_readable_file()
    {
        var text = File.ReadAllText(new GroupedReportWriter(_dir).Write("dev", Array.Empty<ScanRow>(), 7));

        Assert.Contains("0 documents in scope", text);
    }
}
