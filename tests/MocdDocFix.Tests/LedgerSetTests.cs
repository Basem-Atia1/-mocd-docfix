using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

/// <summary>
/// Two files whose contents do not overlap: the services we work on, and every other. A document
/// therefore lives in exactly one of them, and they can never disagree about it.
/// </summary>
[Collection(LedgerCollection.Name)]
public class LedgerSetTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ledgerset-" + Guid.NewGuid().ToString("N"));

    private static readonly Guid Ours = Guid.Parse("3ff27d73-653e-f111-b119-005056010908");
    private static readonly Guid Theirs = Guid.Parse("9ee8941a-8870-f111-b119-005056010908");

    public LedgerSetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string OursPath => Path.Combine(_dir, "repair-dev.xlsx");
    private string OtherPath => Path.Combine(_dir, "repair-dev-other-services.xlsx");

    private static LedgerRow Row(Guid catalogue) => new()
    {
        DocId = Guid.NewGuid(),
        DocFileName = "a.pdf",
        Verdict = RowVerdicts.Fix,
        ServiceCatalogueId = catalogue.ToString()
    };

    private LedgerSet Set(LedgerScope scope) => new(OursPath, scope, new[] { Ours });

    [Fact]
    public void Working_on_our_services_never_opens_the_other_file()
    {
        Set(LedgerScope.Ours).Write(new[] { Row(Ours) });

        Assert.True(File.Exists(OursPath));
        Assert.False(File.Exists(OtherPath));
    }

    [Fact]
    public void Across_everything_each_row_goes_to_the_file_its_service_belongs_to()
    {
        Set(LedgerScope.All).Write(new[] { Row(Ours), Row(Theirs) });

        Assert.Single(new LedgerStore(OursPath).Read());
        Assert.Single(new LedgerStore(OtherPath).Read());
    }

    [Fact]
    public void Across_everything_reading_gives_both_files_back_as_one_list()
    {
        var set = Set(LedgerScope.All);
        set.Write(new[] { Row(Ours), Row(Theirs) });

        Assert.Equal(2, set.Read().Count);
    }

    /// <summary>
    /// A row whose service is not one of ours, written while scoped to ours. It has nowhere else
    /// to go, so it stays — losing it would lose a pending delete nobody could then find.
    /// </summary>
    [Fact]
    public void A_stray_row_is_kept_rather_than_dropped_when_the_other_file_is_shut()
    {
        var set = Set(LedgerScope.Ours);
        set.Write(new[] { Row(Ours), Row(Theirs) });

        Assert.Equal(2, set.Read().Count);
    }

    [Fact]
    public void A_row_whose_service_changed_is_counted_as_moved()
    {
        var set = Set(LedgerScope.All);

        var row = Row(Ours);
        set.Write(new[] { row });
        set.Read();

        row.ServiceCatalogueId = Theirs.ToString();
        set.Write(new[] { row });

        Assert.Equal(1, set.LastMoved);
        Assert.Single(new LedgerStore(OtherPath).Read());
    }

    [Fact]
    public void Nothing_moving_is_counted_as_nothing()
    {
        var set = Set(LedgerScope.All);
        var row = Row(Ours);

        set.Write(new[] { row });
        set.Read();
        set.Write(new[] { row });

        Assert.Equal(0, set.LastMoved);
    }

    /// <summary>A row with no service on it belongs in the file the operator is looking at.</summary>
    [Fact]
    public void A_row_with_no_service_catalogue_stays_in_the_file_we_work_in()
    {
        var orphan = Row(Ours);
        orphan.ServiceCatalogueId = string.Empty;

        Set(LedgerScope.All).Write(new[] { orphan });

        Assert.Single(new LedgerStore(OursPath).Read());
        Assert.Empty(new LedgerStore(OtherPath).Read());
    }

    /// <summary>
    /// Widening to every catalogue for the first time leaves the other services with no sheet.
    /// Saying the ledger is complete then would present one file as the whole scope.
    /// </summary>
    [Fact]
    public void A_scope_whose_second_file_has_never_been_built_is_not_complete()
    {
        Set(LedgerScope.Ours).Write(new[] { Row(Ours) });

        Assert.False(Set(LedgerScope.All).EveryFileBuilt);
    }

    [Fact]
    public void A_scope_with_both_files_on_disk_is_complete()
    {
        Set(LedgerScope.All).Write(new[] { Row(Ours), Row(Theirs) });

        Assert.True(Set(LedgerScope.All).EveryFileBuilt);
    }

    [Fact]
    public void Our_services_alone_need_only_their_own_file()
    {
        Assert.False(Set(LedgerScope.Ours).EveryFileBuilt);

        Set(LedgerScope.Ours).Write(new[] { Row(Ours) });

        Assert.True(Set(LedgerScope.Ours).EveryFileBuilt);
    }
}
