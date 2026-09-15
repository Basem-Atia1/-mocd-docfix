using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

[Collection(LedgerCollection.Name)]
public class LedgerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-ledger-" + Guid.NewGuid());

    public LedgerStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private LedgerStore Store() => new(Path.Combine(_dir, "repair-dev.xlsx"));

    private static LedgerRow Row(int number) => new()
    {
        Row = number,
        DocId = Guid.Parse($"a3f1b2c4-0000-0000-0000-{number:D12}"),
        DocName = "Board of Director's Decision",
        DocFileId = Guid.Parse($"7c20a1f4-0000-0000-0000-{number:D12}"),
        DocFileName = "cert.jpg",
        OldFilePath = @"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg",
        Verdict = RowVerdicts.Fix,
        Group = 2,
        ReasonOfBug = "Path has 'docTypeCatalogue', the variable name rather than its value"
    };

    [Fact]
    public void A_written_ledger_reads_back_unchanged()
    {
        var store = Store();
        store.Write(new[] { Row(1), Row(2) });

        var back = store.Read();

        Assert.Equal(2, back.Count);
        Assert.Equal(Row(1).DocId, back[0].DocId);
        Assert.Equal("Board of Director's Decision", back[0].DocName);
        Assert.Equal(@"DigitalServices\docTypeCatalogue\20250509\a3f1.jpg", back[0].OldFilePath);
        Assert.Equal(RowVerdict.Fix, back[0].Verdict2());
        Assert.Equal(2, back[1].Row);
    }

    /// <summary>Excel will not show Arabic file names correctly without the BOM.</summary>
    [Fact]
    public void The_file_is_written_with_a_byte_order_mark()
    {
        var store = Store();
        store.Write(new[] { Row(1) });

        var first = File.ReadAllBytes(store.CsvPath).Take(3).ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, first);
    }

    /// <summary>
    /// The point of the whole design: what the operator types in Excel is what the next run acts
    /// on. A rewrite must carry their cells through untouched.
    /// </summary>
    [Fact]
    public void An_edit_made_by_hand_survives_a_rewrite()
    {
        var store = Store();
        store.Write(new[] { Row(1) });

        var edited = store.Read().ToList();
        edited[0].Verdict = RowVerdicts.Ignore;
        edited[0].FinalState = RowStates.Text(RowState.Ignore);
        store.Write(edited);

        var back = store.Read();
        Assert.Equal(RowVerdict.Ignore, back[0].Verdict2());
        Assert.Equal(RowState.Ignore, back[0].State());
    }

    /// <summary>
    /// A blank cell must come back blank, not as null and not as the string "null". Redo writes
    /// these values back into CRM, and a portal record genuinely has no old file id.
    /// </summary>
    [Fact]
    public void A_blank_old_value_stays_blank()
    {
        var store = Store();
        var row = Row(1);
        row.OldFileId = string.Empty;
        row.OldCategory = string.Empty;
        store.Write(new[] { row });

        var back = store.Read();
        Assert.Equal(string.Empty, back[0].OldFileId);
        Assert.Equal(string.Empty, back[0].OldCategory);
    }

    [Fact]
    public void Reading_a_ledger_that_is_not_there_gives_nothing_rather_than_throwing()
    {
        var store = Store();
        Assert.False(store.Exists);
        Assert.Empty(store.Read());
    }

    /// <summary>
    /// The backup is taken once per sitting, not once per row. Taken per row it would leave four
    /// hundred copies and the earliest — the only one worth having — would be lost among them.
    /// </summary>
    [Fact]
    public void The_backup_copy_is_taken_once_however_many_times_the_ledger_is_rewritten()
    {
        var store = Store();
        store.Write(new[] { Row(1) });
        store.Write(new[] { Row(1), Row(2) });
        store.Write(new[] { Row(1), Row(2), Row(3) });

        Assert.Single(Directory.GetFiles(store.PreviousDirectory, "*.xlsx"));
    }

    /// <summary>The first write has nothing to copy, so it takes no backup.</summary>
    [Fact]
    public void A_ledger_that_did_not_exist_yet_gets_no_backup_copy()
    {
        var store = Store();
        store.Write(new[] { Row(1) });

        Assert.False(Directory.Exists(store.PreviousDirectory));
    }

    /// <summary>
    /// There is one ledger for the life of an environment. A backup must not sit beside it
    /// looking like a second one — an old workbook opens perfectly well and shows yesterday's
    /// answers with nothing to say they are stale.
    /// </summary>
    [Fact]
    public void Nothing_but_the_ledger_ever_sits_beside_the_ledger()
    {
        var store = Store();
        store.Write(new[] { Row(1) });
        store.Write(new[] { Row(1), Row(2) });

        var beside = Directory.GetFiles(_dir, "*.xlsx");

        Assert.Single(beside);
        Assert.Equal(store.Path, beside[0]);
    }
}
