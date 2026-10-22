using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Memory;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Replication;

public sealed class CaptureFrontierMemoryTests
{
    [Test]
    public async Task A_frontier_admits_the_captured_image_without_materializing_unused_state_text()
    {
        ImageBudget? memory = null;
        await using var api = await Documents.DocumentApi.StartAsync(services: services =>
        {
            var original = services.Single(service => service.ServiceType == typeof(IMemoryBudget));
            services.AddSingleton<IMemoryBudget>(provider =>
                memory = new ImageBudget((IMemoryBudget)original.ImplementationFactory!(provider)));
        });
        var token = TestContext.Current!.Execution.CancellationToken;
        var document = Enumerable.Range(0, 1000).ToDictionary(
            index => new string('x', 64) + index.ToString(CultureInfo.InvariantCulture), index => index);
        var response = await api.PutAsync(EntityIds.New().ToString(), document);
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.Created);
        var tenant = await api.TenantOfAsync();
        var store = (SqliteDatabaseStore)(await api.Server.Services.GetRequiredService<SqliteStorageEngine>()
            .OpenDatabaseAsync(tenant, token))!;
        var (imageBytes, feed) = await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT max(octet_length(after)), (SELECT seq FROM _tb_feed_sequence WHERE id = 1) FROM turso_cdc WHERE table_name = '_tb_state';";
            using var reader = command.ExecuteReader();
            _ = reader.Read();
            return (reader.GetInt64(0), reader.GetInt64(1));
        }, "a frontier test measuring its captured image", token);
        await Assert.That(imageBytes).IsGreaterThan(32 * 1024L);
        var source = api.Server.Services.GetRequiredService<TursoChangeGroupSource>();
        var head = await source.HeadAsync(tenant, store.Incarnation, token);
        await Assert.That(head).IsNotNull();
        // Single-region application requests do not walk the remote frontier; only this explicit proof does.
        memory!.Limit = imageBytes + 4096;
        await Assert.That(await source.ProveAsync(tenant, store.Incarnation, 0, head!.Value,
            FrozenSet<ReplicaId>.Empty, token)).IsTrue();
        await using var transaction = await store.BeginTransactionAsync(write: false, token);
        var frontier = await ((SqliteStorageTransaction)transaction).ReadShapeFrontierAsync(remote: true, token);
        await Assert.That(frontier).IsEqualTo(feed)
            .Because("origin proof needs the captured bytes, without decoding their unused state text");
    }

    private sealed class ImageBudget(IMemoryBudget inner) : IMemoryBudget
    {
        private long _limit = long.MaxValue;

        public long Limit
        {
            get => Volatile.Read(ref _limit);
            set => Volatile.Write(ref _limit, value);
        }

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) =>
            lane == MemoryLane.Bulk && bytes > Volatile.Read(ref _limit) ? null : inner.TryReserve(lane, bytes);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant) =>
            lane == MemoryLane.Bulk && bytes > Volatile.Read(ref _limit) ? null : inner.TryReserve(lane, bytes, tenant);
    }
}
