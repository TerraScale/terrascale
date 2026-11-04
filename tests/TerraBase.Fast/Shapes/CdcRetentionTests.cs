using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class CdcRetentionTests
{
    [Test]
    public async Task Cleanup_never_trims_past_a_live_shape_backup_replica_or_push_floor_and_bounds_cdc()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var host = await BackupShippingHost.StartAsync();
        host.Time.SetUtcNow(DateTimeOffset.UtcNow);
        using var data = await host.Api.CreateClientAsync(host.Database.Organization, "sql:read", "sql:write");
        var slow = new TableShapeClient(data, ManagementApi.Uri(host.Database.Data).ToString(), "people");
        var synced = await slow.SyncAsync();
        var slowOffset = long.Parse(synced.NextOffset!, CultureInfo.InvariantCulture);
        await host.ShipAsync(TimeSpan.FromSeconds(10), token);
        await host.PutAsync("people", "one", "first", token);
        var fast = new TableShapeClient(data, ManagementApi.Uri(host.Database.Data).ToString(), "people", "&columns=text");
        await fast.SyncAsync();
        for (var round = 0; round < 20; round++)
        {
            await host.PutAsync("people", "one", "value-" + round, token);
        }

        await host.ShipAsync(TimeSpan.FromSeconds(3), token);
        var tenant = new TenantContext(host.Database.Organization, PlanTier.Enterprise, host.Database.Project, host.Database.Id);
        var store = await host.Api.Server.StoreAsync(tenant);
        await Assert.That(await OldestAsync(store, token)).IsLessThanOrEqualTo(slowOffset);
        var kept = await slow.ReadAsync(slow.NextOffset);
        await Assert.That(kept.Ops.Any()).IsTrue();
        var backup = store.BackupAnchor!.Value;
        await host.PutAsync("people", "two", "not-backed-up", token);
        host.S3.FailPut = key => key.StartsWith(host.File + "/wal/", StringComparison.Ordinal);
        await host.ShipAsync(TimeSpan.FromSeconds(3), token);
        await Assert.That(store.BackupAnchor).IsEqualTo(backup);
        host.S3.FailPut = _ => false;
        host.Time.Advance(TimeSpan.FromDays(8));
        var retention = host.Api.Server.Services.GetRequiredService<IShapeRetention>();
        _ = await retention.RunPassAsync(store, slow.Handle!, token);
        _ = await retention.RunPassAsync(store, fast.Handle!, token);
        await Assert.That(await OldestAsync(store, token)).IsLessThanOrEqualTo(backup);
        var push = await ScalarAsync(store, "SELECT max(change_id) FROM turso_cdc WHERE change_type = 2;", token);
        await SetConsumerAsync(store, "replica:sjc", backup, token);
        await SetConsumerAsync(store, "region-to-home", push, token);
        await host.ShipAsync(TimeSpan.FromSeconds(3), token);
        await Assert.That(await OldestAsync(store, token)).IsLessThanOrEqualTo(backup);
        await SetConsumerAsync(store, "replica:sjc", push, token);
        _ = await retention.RunPassAsync(store, fast.Handle!, token);
        await Assert.That(await OldestAsync(store, token)).IsGreaterThan(backup);
        await host.PutAsync("people", "two", "ahead-of-push", token);
        var replica = await ScalarAsync(store, "SELECT max(change_id) FROM turso_cdc WHERE change_type = 2;", token);
        await SetConsumerAsync(store, "replica:sjc", replica, token);
        await host.ShipAsync(TimeSpan.FromSeconds(3), token);
        _ = await retention.RunPassAsync(store, slow.Handle!, token);
        _ = await retention.RunPassAsync(store, fast.Handle!, token);
        await Assert.That(await OldestAsync(store, token)).IsLessThanOrEqualTo(push);
        await SetConsumerAsync(store, "region-to-home", null, token);
        _ = await retention.RunPassAsync(store, fast.Handle!, token);
        await Assert.That(await OldestAsync(store, token)).IsGreaterThan(push);
        await SetConsumerAsync(store, "replica:sjc", null, token);
        for (var round = 0; round < 3; round++)
        {
            for (var write = 0; write < 20; write++)
            {
                await host.PutAsync("people", "two", $"{round}-{write}", token);
            }

            _ = await retention.RunPassAsync(store, slow.Handle!, token);
            _ = await retention.RunPassAsync(store, fast.Handle!, token);
            await host.ShipAsync(TimeSpan.FromSeconds(3), token);
            var count = await ScalarAsync(store, "SELECT count(*) FROM turso_cdc;", token);
            await Assert.That(count).IsLessThan(100L);
        }
    }

    private static async Task SetConsumerAsync(TursoDatabaseStore store, string consumer, long? position, CancellationToken token)
    {
        await using var transaction = await store.BeginTransactionAsync(write: true, token);
        await ((ICdcRetentionTransaction)transaction).SetCdcConsumerFloorAsync(consumer, position, token);
        _ = await transaction.CommitAsync(token);
    }

    private static Task<long> OldestAsync(TursoDatabaseStore store, CancellationToken token) =>
        ScalarAsync(store, "SELECT min(change_id) FROM turso_cdc;", token);

    private static Task<long> ScalarAsync(TursoDatabaseStore store, string sql, CancellationToken token) =>
        store.WithWriterAsync(writer =>
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }, "retention integration observation", token);
}
