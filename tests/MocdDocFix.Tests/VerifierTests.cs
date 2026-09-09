using System.Text;
using MocdDocFix.Domain;
using MocdDocFix.Verification;
using Xunit;

namespace MocdDocFix.Tests;

public class VerifierTests
{
    private static readonly Guid Correct   = Guid.Parse("cd97bf8d-bea8-f011-b116-005056010908");
    private static readonly Guid NewFileId = Guid.Parse("a41c0b77-1111-2222-3333-444444444444");
    private static readonly Guid OldFileId = Guid.Parse("5b05398a-b0bc-4c26-a6a6-40b7e0ece187");

    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("the file contents");

    private const string NewPath =
        @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\a41c0b77-1111-2222-3333-444444444444.jpg";
    private const string OldPath =
        @"DigitalServices\goodConductCertificate\20260330\5b05398a-b0bc-4c26-a6a6-40b7e0ece187.jpg";

    // --- check 1 -----------------------------------------------------------

    [Fact]
    public void Backup_integrity_passes_when_the_two_vendor_hashes_agree()
        => Assert.True(Verifier.BackupIntegrity("abc123", "abc123").Passed);

    [Fact]
    public void Backup_integrity_is_case_insensitive()
        => Assert.True(Verifier.BackupIntegrity("ABC123", "abc123").Passed);

    [Fact]
    public void Backup_integrity_fails_when_CRM_and_the_file_server_disagree()
    {
        var r = Verifier.BackupIntegrity("abc123", "def456");

        Assert.False(r.Passed);
        Assert.False(r.HaltsRun);                       // quarantine this file only
        Assert.Contains("abc123", r.Detail);
        Assert.Contains("def456", r.Detail);
    }

    [Fact]
    public void Backup_integrity_fails_when_either_hash_is_missing()
    {
        Assert.False(Verifier.BackupIntegrity(null, "abc").Passed);
        Assert.False(Verifier.BackupIntegrity("abc", null).Passed);
    }

    // --- check 2 -----------------------------------------------------------

    [Fact]
    public void Upload_hash_must_equal_the_old_vendor_hash()
    {
        Assert.True(Verifier.UploadHashMatches("h1", "h1").Passed);
        Assert.False(Verifier.UploadHashMatches("h1", "h2").Passed);
    }

    // --- check 3 -----------------------------------------------------------

    [Fact]
    public void Round_trip_passes_only_on_byte_for_byte_equality()
    {
        var r = Verifier.RoundTrip(Bytes, Bytes.ToArray());

        Assert.True(r.Passed);
        Assert.Contains("byte-for-byte", r.Detail);
    }

    [Fact]
    public void Round_trip_fails_on_a_length_difference()
    {
        var r = Verifier.RoundTrip(Bytes, Bytes.Take(5).ToArray());

        Assert.False(r.Passed);
        Assert.Contains("length", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Round_trip_fails_on_a_single_flipped_byte_of_the_same_length()
    {
        var corrupted = Bytes.ToArray();
        corrupted[3] ^= 0xFF;

        Assert.False(Verifier.RoundTrip(Bytes, corrupted).Passed);
    }

    [Fact]
    public void Our_hash_is_stable_and_content_derived()
    {
        Assert.Equal(Verifier.OurHash(Bytes), Verifier.OurHash(Bytes.ToArray()));
        Assert.NotEqual(Verifier.OurHash(Bytes), Verifier.OurHash(Encoding.UTF8.GetBytes("different")));
        Assert.Equal(64, Verifier.OurHash(Bytes).Length);      // SHA-256 hex
    }

    // --- check 4 -----------------------------------------------------------

    [Fact]
    public void Path_is_fixed_when_the_segment_matches_and_the_stem_is_the_file_id()
        => Assert.True(Verifier.PathIsFixed(FilePathParser.Parse(NewPath), Correct, NewFileId).Passed);

    [Fact]
    public void Path_is_fixed_ignores_catalogue_casing()
    {
        var upper = NewPath.Replace("cd97bf8d-bea8-f011-b116-005056010908",
                                    "CD97BF8D-BEA8-F011-B116-005056010908");

        Assert.True(Verifier.PathIsFixed(FilePathParser.Parse(upper), Correct, NewFileId).Passed);
    }

    [Fact]
    public void Path_is_fixed_fails_when_the_vendor_filed_it_somewhere_else()
    {
        var wrong = FilePathParser.Parse(
            @"DigitalServices\DigitalServices\20260910\a41c0b77-1111-2222-3333-444444444444.jpg");

        var r = Verifier.PathIsFixed(wrong, Correct, NewFileId);

        Assert.False(r.Passed);
        Assert.Contains("DigitalServices", r.Detail);
    }

    [Fact]
    public void Path_is_fixed_fails_when_the_file_stem_is_not_the_returned_file_id()
    {
        var mismatched = FilePathParser.Parse(
            @"DigitalServices\cd97bf8d-bea8-f011-b116-005056010908\20260910\99999999-9999-9999-9999-999999999999.jpg");

        var r = Verifier.PathIsFixed(mismatched, Correct, NewFileId);

        Assert.False(r.Passed);
        Assert.Contains("stem", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // --- check 5 (halts the run) ------------------------------------------

    [Fact]
    public void Genuinely_new_passes_for_a_distinct_path_and_id()
    {
        var r = Verifier.IsGenuinelyNew(OldPath, NewPath, OldFileId, NewFileId);

        Assert.True(r.Passed);
        Assert.True(r.HaltsRun);          // the flag describes the consequence of failure
    }

    [Fact]
    public void Genuinely_new_fails_and_halts_when_the_vendor_returns_the_same_path()
    {
        var r = Verifier.IsGenuinelyNew(OldPath, OldPath, OldFileId, NewFileId);

        Assert.False(r.Passed);
        Assert.True(r.HaltsRun);
        Assert.Contains("deduplicat", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Genuinely_new_fails_and_halts_when_the_vendor_returns_the_same_file_id()
    {
        var r = Verifier.IsGenuinelyNew(OldPath, NewPath, OldFileId, OldFileId);

        Assert.False(r.Passed);
        Assert.True(r.HaltsRun);
    }

    // --- check 6 (halts the run) ------------------------------------------

    [Fact]
    public void Crm_took_the_change_passes_when_the_lookup_reads_back_as_the_new_file()
        => Assert.True(Verifier.CrmTookTheChange(NewFileId, NewFileId).Passed);

    [Fact]
    public void Crm_took_the_change_fails_and_halts_when_the_lookup_is_stale_or_empty()
    {
        Assert.False(Verifier.CrmTookTheChange(NewFileId, OldFileId).Passed);
        Assert.True(Verifier.CrmTookTheChange(NewFileId, OldFileId).HaltsRun);
        Assert.False(Verifier.CrmTookTheChange(NewFileId, null).Passed);
    }

    // --- report ------------------------------------------------------------

    [Fact]
    public void Report_summarises_pass_fail_and_halt()
    {
        var ok    = new CheckResult("a", true,  "fine", false);
        var soft  = new CheckResult("b", false, "bad",  false);
        var hard  = new CheckResult("c", false, "very bad", true);

        Assert.True(new VerificationReport(new[] { ok }).AllPassed);
        Assert.False(new VerificationReport(new[] { ok, soft }).AllPassed);
        Assert.False(new VerificationReport(new[] { ok, soft }).MustHalt);
        Assert.True(new VerificationReport(new[] { ok, hard }).MustHalt);
        Assert.Equal(2, new VerificationReport(new[] { ok, soft, hard }).Failures.Count());
    }
}
