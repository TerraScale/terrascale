using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Engine.Sqlite;
using TerraScale.TerraBase.Tenancy;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Storage;

/// <summary>
/// The SQLite session pool, through the running server's engine: no listener can dispose a
/// transaction twice, delete a database under an open transaction, or start one pre-cancelled.
/// </summary>
public sealed class StorageTransactionTests
{
    // Every test runs its own server and data directory, so they can share the ids.
    private static readonly TenantContext Database = Tenant(Guid.NewGuid().ToString());

    [Test]
    public async Task Disposing_a_transaction_twice_returns_its_session_once()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (_, store) = await OpenAsync(server);
        // Creation leaves the migration session idle.
        await Assert.That(store.IdleCount).IsEqualTo(1);

        var first = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await first.DisposeAsync();
        // The reader rents the session `first` returned; disposing `first` again must not return it a second time.
        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await first.DisposeAsync();
        await Assert.That(store.IdleCount).IsEqualTo(0);

        await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await writer.CreateCollectionAsync(new CollectionInfo("items", 0), CancellationToken.None);
        var version = await writer.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);
        await writer.CommitAsync(CancellationToken.None);
        await Assert.That(version).IsEqualTo(1);
        await Assert.That((await reader.GetAsync("items", "a", CancellationToken.None))?.Version).IsEqualTo(1);
    }

    [Test]
    public async Task A_deleted_database_hands_out_and_keeps_no_session()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (engine, store) = await OpenAsync(server);
        var open = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await (await store.BeginTransactionAsync(write: false, CancellationToken.None)).DisposeAsync();
        await Assert.That(store.IdleCount).IsEqualTo(1);

        await Assert.That(await engine.DeleteDatabaseAsync(Database, CancellationToken.None)).IsTrue();

        await Assert.That(store.IdleCount).IsEqualTo(0);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.BeginTransactionAsync(write: false, CancellationToken.None).AsTask());
        // The transaction that was open during the delete closes its session instead of pooling it.
        await open.DisposeAsync();
        await Assert.That(store.IdleCount).IsEqualTo(0);
        await Assert.That(await engine.OpenDatabaseAsync(Database, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task A_cancelled_request_takes_no_idle_session()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (_, store) = await OpenAsync(server);
        await Assert.That(store.IdleCount).IsEqualTo(1);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.BeginTransactionAsync(write: true, cancelled.Token).AsTask());

        await Assert.That(store.IdleCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_disposed_transaction_refuses_every_operation()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (_, store) = await OpenAsync(server);
        var stale = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await stale.DisposeAsync();
        // The reader now holds the session `stale` used; nothing `stale` does may reach it.
        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(store.IdleCount).IsEqualTo(0);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.GetAsync("items", "a", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.PutAsync("items", "a", Encoding.UTF8.GetBytes("{}"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.DeleteAsync("items", "a", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.ListAsync("items", null, 10, 1 << 20, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stale.CommitAsync(CancellationToken.None).AsTask());

        await Assert.That(await reader.GetAsync("items", "a", CancellationToken.None)).IsNull();
        await Assert.That((await reader.ListAsync("items", null, 10, 1 << 20, CancellationToken.None)).Documents).IsEmpty();
    }

    [Test]
    public async Task Idle_connections_stay_bounded_across_many_databases()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = (SqliteStorageEngine)server.Services.GetRequiredService<IStorageEngine>();
        var stores = new List<SqliteDatabaseStore>();
        for (var i = 0; i < engine.Pool.MaxIdle + 8; i++)
        {
            var name = Database with { DatabaseId = Guid.NewGuid().ToString() };
            await engine.CreateDatabaseAsync(name, CancellationToken.None);
            var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(name, CancellationToken.None))!;
            await (await store.BeginTransactionAsync(write: false, CancellationToken.None)).DisposeAsync();
            stores.Add(store);
        }

        await Assert.That(engine.Pool.IdleCount).IsEqualTo(engine.Pool.MaxIdle);
        // The least recently used databases gave up their connections, the most recent kept theirs,
        // and no other database file is still open.
        await Assert.That(stores[0].IdleCount).IsEqualTo(0);
        await Assert.That(stores[^1].IdleCount).IsEqualTo(1);
        var openFiles = new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos()
            .Select(descriptor => descriptor.LinkTarget)
            .Where(target => target is not null && target.StartsWith(server.DataDirectory + "/", StringComparison.Ordinal) && target.EndsWith(".db", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Count();
        await Assert.That(openFiles).IsEqualTo(engine.Pool.MaxIdle);

        // A database whose connection was closed opens a new one when it is used again.
        await using var again = await stores[0].BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await again.GetAsync("items", "a", CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task A_shut_down_engine_opens_no_database()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (engine, _) = await OpenAsync(server);
        await engine.OpenSystemDatabaseAsync(CancellationToken.None);

        // The server disposes the engine again when it stops; the second call is a no-op.
        await engine.DisposeAsync();

        // Even a database that was opened, and so cached, before the shutdown.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.OpenDatabaseAsync(Database, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.CreateDatabaseAsync(Database, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.OpenSystemDatabaseAsync(CancellationToken.None).AsTask());
    }

    private static TenantContext Tenant(string database) => new(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), database);

    private static async Task<(IStorageEngine Engine, SqliteDatabaseStore Store)> OpenAsync(TerraBaseTestServer server)
    {
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        await engine.CreateDatabaseAsync(Database, CancellationToken.None);
        return (engine, (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(Database, CancellationToken.None))!);
    }
}
