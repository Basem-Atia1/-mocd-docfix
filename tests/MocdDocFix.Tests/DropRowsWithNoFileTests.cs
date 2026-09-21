using MocdDocFix.Commands;
using MocdDocFix.Domain;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Rows whose record names no file. There is no path to diagnose and no file to move, so the
/// scan stopped writing them — but a scan cannot un-write the ones already on disk, and this is
/// what takes them out. It runs on every open, not only on a run that goes and reads CRM.
/// </summary>
public class DropRowsWithNoFileTests
{
    private static readonly Guid Doc = Guid.Parse("2cd3b04c-391c-f111-b119-005056010908");

    /// <summary>What an earlier build wrote for a document with no file path.</summary>
    private static LedgerRow NoFile(string verdict = RowVerdicts.Review, string finalState = "") =>
        new()
        {
            Row = 1,
            DocId = Doc,
            DocFileName = "Application Summary.png",
            Group = 8,
            OldFilePath = string.Empty,
            Verdict = verdict,
            FinalState = finalState
        };

    private static LedgerRow WithAFile() => new()
    {
        Row = 2,
        DocId = Guid.Parse("9d0a2c3b-0000-0000-0000-000000000001"),
        OldFilePath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg",
        Verdict = RowVerdicts.Fix
    };

    [Fact]
    public void A_row_with_no_file_path_is_taken_out()
    {
        var result = DropRowsWithNoFile.From(new[] { NoFile(), WithAFile() });

        Assert.Equal(1, result.Removed);
        Assert.Equal(2, Assert.Single(result.Rows).Row);
    }

    /// <summary>
    /// The file path is the authority, not the verdict. They were written as "review" and a hand
    /// can change that to anything — what makes the row impossible to act on is the missing file.
    /// </summary>
    [Theory]
    [InlineData(RowVerdicts.Review)]
    [InlineData(RowVerdicts.Fix)]
    [InlineData(RowVerdicts.Done)]
    [InlineData("whatever somebody typed")]
    public void The_verdict_does_not_save_it(string verdict)
    {
        var result = DropRowsWithNoFile.From(new[] { NoFile(verdict) });

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.Removed);
    }

    /// <summary>
    /// A note is not a reason to keep it. There is still nothing this tool can do with the row,
    /// and leaving it in the sheet for ever was the complaint.
    /// </summary>
    [Fact]
    public void A_note_does_not_save_it()
    {
        var row = NoFile();
        row.Notes = "asked the business on 12 Sep";

        Assert.Empty(DropRowsWithNoFile.From(new[] { row }).Rows);
    }

    /// <summary>
    /// The rule that must hold unconditionally: a final state is the record that something was
    /// really done to the document, and no tidy-up may throw that away. One of these cannot have
    /// a final state honestly — but the guard is what makes that true rather than hoped for.
    /// </summary>
    [Theory]
    [InlineData(RowStates.Corrected)]
    [InlineData("old files deleted")]
    [InlineData("ignore")]
    [InlineData("failed")]
    public void A_row_with_a_final_state_is_never_taken_out(string finalState)
    {
        var result = DropRowsWithNoFile.From(new[] { NoFile(finalState: finalState) });

        Assert.Single(result.Rows);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void A_sheet_with_none_of_them_is_left_exactly_as_it_is()
    {
        var rows = new[] { WithAFile() };

        var result = DropRowsWithNoFile.From(rows);

        Assert.Equal(0, result.Removed);
        Assert.Single(result.Rows);
    }
}
