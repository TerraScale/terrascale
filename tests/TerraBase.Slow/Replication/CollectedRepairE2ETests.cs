using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

/// <summary>
/// A region away past the GC horizon comes back with a document every other region deleted and collected
/// (docs/terrabase/replication/10-anti-entropy-and-refill.md, rounds step 3). Two real nodes, real peer streams and
/// anti-entropy over gRPC: the collection advances the node's persisted GC floor (<c>_tb_gc_floor</c>,
/// docs/terrabase/replication/2026-10-22-engine-gc.md), so its rounds never bring the document back, before or after the
/// stream delivers the deletion to the node that was away.
/// </summary>
public sealed class CollectedRepairE2ETests
{
    private const string Items = "items";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Test]
    public async Task A_document_deleted_and_collected_while_a_region_was_away_stays_deleted_on_both()
    {
        var members = new FakeMemberStore();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var user = EntityIds.New().ToString();
        await using var gru = await Node.StartAsync("gru", members, "sjc", database, user);
        await using var sjc = await Node.StartAsync("sjc", members, "gru", database, user);
        await gru.RefreshAsync(peers: 1);
        await sjc.RefreshAsync(peers: 1);
        var id = EntityIds.New().ToString();
        await gru.WriteAsync(async transaction =>
        {
            _ = await transaction.CreateCollectionAsync(new CollectionInfo(Items, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), CancellationToken.None);
            _ = await transaction.PutAsync(Items, id, JsonSerializer.SerializeToUtf8Bytes(new { name = new Faker().Name.FullName() }), CancellationToken.None);
        });
        await Assert.That(await Until(async () => await sjc.ReadAsync(id) is not null)).IsTrue().Because("sjc receives the document");

        // sjc goes away; gru deletes the document and, past the horizon, collects it.
        var listed = (await members.ListAsync(CancellationToken.None)).ToList();
        listed.ForEach(member => members.Leave(member.Name));
        await gru.RefreshAsync(peers: 0);
        await sjc.RefreshAsync(peers: 0);
        await Assert.That(await Until(() => Task.FromResult(gru.Status.Snapshot().Peers.Count == 0))).IsTrue();
        await gru.WriteAsync(async transaction => _ = await transaction.DeleteAsync(Items, id, CancellationToken.None));
        await Assert.That(await gru.CollectAsync()).IsGreaterThanOrEqualTo(1);
        await Assert.That(await gru.HasStateAsync(id)).IsFalse();
        var floor = await gru.GcFloorAsync();
        await Assert.That(floor).IsGreaterThan(0L).Because("the collection persists its floor");

        // sjc is back holding the document live: gru's rounds compare with that stale copy.
        listed.ForEach(members.Join);
        await gru.RefreshAsync(peers: 1);
        await sjc.RefreshAsync(peers: 1);
        await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await Assert.That(await gru.HasStateAsync(id)).IsFalse();

        // The stream delivers the deletion to sjc; gru's rounds then compare with its tombstone, which stays collected.
        await Assert.That(await Until(async () => await sjc.ReadAsync(id) is null)).IsTrue().Because("sjc receives the deletion");
        await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await gru.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await sjc.AntiEntropy.RunRoundAsync(CancellationToken.None);
        await Assert.That(await gru.HasStateAsync(id)).IsFalse();
        await Assert.That(await gru.GcFloorAsync()).IsEqualTo(floor).Because("a stale copy's repairs never move the floor back");
        await Assert.That(await gru.ReadAsync(id)).IsNull();
        await Assert.That(await sjc.ReadAsync(id)).IsNull();
    }

    private static async Task<bool> Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    // A node of the pair: its own data directory and listeners, the same database as the other node.
    private sealed class Node(ManagementApi api, TenantContext database, ManualRoundsTime time) : IAsyncDisposable
    {
        public AntiEntropy AntiEntropy => api.Server.Services.GetRequiredService<AntiEntropy>();

        public IReplicationStatus Status => api.Server.Services.GetRequiredService<IReplicationStatus>();

        public static async Task<Node> StartAsync(string region, FakeMemberStore members, string replica, TenantContext database, string user)
        {
            var time = new ManualRoundsTime();
            var api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
            }, services => services.AddSingleton<IMemberDirectory>(members), time: time, userId: user);
            try
            {
                await CatalogFixture.RegisterAsync(api, api.User, database, "main", [replica]);
                return new Node(api, database, time);
            }
            catch
            {
                await api.DisposeAsync();
                throw;
            }
        }

        public async Task RefreshAsync(int peers)
        {
            time.AdvanceMembership();
            var view = await api.Server.Services.GetRequiredService<IMembership>().RefreshAsync(CancellationToken.None);
            await Assert.That(view.Elsewhere.Length).IsEqualTo(peers);
        }

        public async Task WriteAsync(Func<IStorageTransaction, Task> work)
        {
            await using var transaction = await (await OpenAsync()).BeginDurableTransactionAsync(CancellationToken.None);
            await work(transaction);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        public async Task<string?> ReadAsync(string id)
        {
            await using var transaction = await (await OpenAsync()).BeginTransactionAsync(write: false, CancellationToken.None);
            return await transaction.GetAsync(Items, id, CancellationToken.None) is { Deleted: false } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
        }

        // Garbage collection once every replica is past the deletion: the horizon of a machine whose peer left 24 h ago.
        public async Task<int> CollectAsync()
        {
            var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
            await using var transaction = (TursoStorageTransaction)await (await OpenAsync()).BeginTransactionAsync(write: true, CancellationToken.None);
            var collected = await transaction.CollectAsync(engine.Member.Clock.Tick(), CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
            return collected;
        }

        public async Task<bool> HasStateAsync(string id) =>
            await ((TursoDatabaseStore)await OpenAsync()).WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM _tb_state WHERE collection = $collection AND id = $id;";
                command.Parameters.AddWithValue("$collection", Items);
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteScalar() is not null;
            }, "a test looking for a collected state", CancellationToken.None);

        // The stamp below which this file collected states: a join of older entries into an absent key is a deleted clear.
        public async Task<long> GcFloorAsync() =>
            await ((TursoDatabaseStore)await OpenAsync()).WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT stamp FROM _tb_gc_floor WHERE id = 1;";
                return (long)command.ExecuteScalar()!;
            }, "a test reading the GC floor", CancellationToken.None);

        public ValueTask DisposeAsync() => api.DisposeAsync();

        private async Task<IDatabaseStore> OpenAsync() =>
            await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the database is not held");
    }

    // Membership time moves only when a test advances it and rounds run only when a test runs them; deadlines use real timers.
    // Clocks lead the wall clock, so a document written right after its database file was born is after its birth.
    private sealed class ManualRoundsTime : TimeProvider
    {
        private readonly FakeTimeProvider _rounds = new();

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromSeconds(10);

        public override long TimestampFrequency => _rounds.TimestampFrequency;

        public override long GetTimestamp() => _rounds.GetTimestamp();

        public void AdvanceMembership() => _rounds.Advance(DirectoryMembership.Heartbeat);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            period == AntiEntropy.Round ? _rounds.CreateTimer(callback, state, dueTime, period) : base.CreateTimer(callback, state, dueTime, period);
    }
}
