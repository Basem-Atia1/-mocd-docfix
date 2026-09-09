using MocdDocFix.Domain;
using MocdDocFix.Storage;
using Xunit;

namespace MocdDocFix.Tests;

public class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docfix-state-" + Guid.NewGuid());
    private string StatePath => Path.Combine(_dir, "state-dev.jsonl");

    public StateStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static StateRecord Rec(Guid id, MigrationState s, Guid? newFileId = null) =>
        new(id, s, DateTimeOffset.UtcNow, newFileId, null, null);

    [Fact]
    public void LoadLatest_on_a_missing_file_is_empty()
        => Assert.Empty(new StateStore(StatePath).LoadLatest());

    [Fact]
    public void Append_then_load_returns_the_record()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(StatePath);

        store.Append(Rec(id, MigrationState.BackedUp));

        Assert.Equal(MigrationState.BackedUp, store.LoadLatest()[id].State);
    }

    [Fact]
    public void The_last_record_for_a_document_wins()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(StatePath);

        store.Append(Rec(id, MigrationState.BackedUp));
        store.Append(Rec(id, MigrationState.Uploaded));
        store.Append(Rec(id, MigrationState.Repointed));

        Assert.Equal(MigrationState.Repointed, store.LoadLatest()[id].State);
        Assert.Single(store.LoadLatest());
    }

    [Fact]
    public void History_is_preserved_on_disk_even_though_only_the_latest_is_returned()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(StatePath);
        store.Append(Rec(id, MigrationState.BackedUp));
        store.Append(Rec(id, MigrationState.Repointed));

        Assert.Equal(2, File.ReadAllLines(StatePath).Length);
    }

    [Fact]
    public void A_new_store_over_the_same_file_sees_earlier_progress()
    {
        var id = Guid.NewGuid();
        new StateStore(StatePath).Append(Rec(id, MigrationState.Verified));

        Assert.Equal(MigrationState.Verified, new StateStore(StatePath).LoadLatest()[id].State);
    }

    [Fact]
    public void IsAtLeast_orders_the_happy_path_states()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(StatePath);
        store.Append(Rec(id, MigrationState.Verified));

        Assert.True(store.IsAtLeast(id, MigrationState.BackedUp));
        Assert.True(store.IsAtLeast(id, MigrationState.Verified));
        Assert.False(store.IsAtLeast(id, MigrationState.Repointed));
        Assert.False(store.IsAtLeast(Guid.NewGuid(), MigrationState.BackedUp));
    }

    [Fact]
    public void Quarantined_and_Failed_never_count_as_progress()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(StatePath);
        store.Append(Rec(id, MigrationState.Quarantined));

        Assert.False(store.IsAtLeast(id, MigrationState.BackedUp));
    }

    [Fact]
    public void The_new_file_id_and_path_survive_the_round_trip()
    {
        var id = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var store = new StateStore(StatePath);

        store.Append(new StateRecord(id, MigrationState.Repointed, DateTimeOffset.UtcNow,
            fileId, @"DigitalServices\cat\20260910\x.jpg", "ok"));

        var latest = store.LoadLatest()[id];
        Assert.Equal(fileId, latest.NewFileId);
        Assert.Equal(@"DigitalServices\cat\20260910\x.jpg", latest.NewFilePath);
    }

    [Fact]
    public void A_corrupt_line_is_skipped_rather_than_killing_the_run()
    {
        var id = Guid.NewGuid();
        var store = new StateStore(StatePath);
        store.Append(Rec(id, MigrationState.BackedUp));
        File.AppendAllText(StatePath, "{ this is not json" + Environment.NewLine);
        store.Append(Rec(id, MigrationState.Uploaded));

        Assert.Equal(MigrationState.Uploaded, store.LoadLatest()[id].State);
    }
}
