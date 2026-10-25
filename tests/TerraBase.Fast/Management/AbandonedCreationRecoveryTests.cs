using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>
/// TS-994: a creation whose registration was refused keeps its file and cleanup intent until recovery. Recovery ran only
/// at start, and only when the catalog gate was already open, which on a cell it never is then: every region kept such
/// files (1.5 GB each in production), unlisted by the API and still backed up. A cell's restart now recovers once its
/// gate opens.
/// </summary>
public sealed class AbandonedCreationRecoveryTests
{
    [Test]
    public async Task A_restarted_cell_member_removes_an_abandoned_creation_once_its_gate_opens()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        var members = new FakeMemberStore();
        await using var gru = new RestartingMachine("gru", bucket, members);
        await using var sjc = new RestartingMachine("sjc", bucket, members);
        await using var fra = new RestartingMachine("fra", bucket, members);
        foreach (var machine in (RestartingMachine[])[gru, sjc, fra])
        {
            await machine.StartAsync();
        }

        foreach (var machine in (RestartingMachine[])[gru, sjc, fra])
        {
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(machine.Gate.IsOpen), timeout: TimeSpan.FromSeconds(30));
        }

        // As a create whose registration the catalog refused leaves it: the file, under its committed cleanup intent, and no
        // catalog entry.
        var abandoned = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var engine = gru.Api.Server.Services.GetRequiredService<IStorageEngine>();
        var system = await engine.OpenSystemDatabaseAsync(token);
        await Assert.That(await engine.CreateDatabaseWithIntentAsync(abandoned, null, async cancellationToken =>
        {
            await using var write = await system.BeginDurableTransactionAsync(cancellationToken);
            _ = await write.PutAsync("pending_database_cleanup", abandoned.DatabaseId!,
                JsonSerializer.SerializeToUtf8Bytes(abandoned, TerraBaseJsonContext.Default.TenantContext), cancellationToken);
            _ = await write.CommitAsync(cancellationToken);
            return true;
        }, token)).IsTrue();
        List<string> files = [.. engine.EnumerateDatabaseFiles(token).Where(stored => stored.Database?.DatabaseId == abandoned.DatabaseId).Select(stored => stored.Path)];
        await Assert.That(files).IsNotEmpty();

        // A restart starts with the gate closed until the peers verify the catalog.
        await gru.StopAsync();
        await gru.StartAsync();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gru.Gate.IsOpen), timeout: TimeSpan.FromSeconds(60));

        await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
        {
            var catalog = await gru.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken);
            await using var read = await catalog.BeginTransactionAsync(write: false, cancellationToken);
            return !files.Any(File.Exists) && await read.GetAsync("pending_database_cleanup", abandoned.DatabaseId!, cancellationToken) is null;
        }, timeout: TimeSpan.FromSeconds(60));
    }
}
