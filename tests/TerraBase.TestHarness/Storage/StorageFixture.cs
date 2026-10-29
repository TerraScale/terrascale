using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

/// <summary>Stable, ordered ULID addresses and explicit collection creation for document fixtures.</summary>
internal static class StorageFixture
{
    /// <summary>Maps a short ASCII fixture name to an ordered ULID.</summary>
    public static string Id(string name)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Encoding.UTF8.GetByteCount(name), 16);
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        Encoding.UTF8.GetBytes(name, bytes);
        return new Ulid(bytes).ToString();
    }

    /// <summary>Creates one collection through the actual client listener.</summary>
    public static async Task CreateCollectionAsync(Tenant tenant, string database, string collection, CancellationToken cancellationToken = default)
    {
        using var response = await tenant.Data.PostAsJsonAsync(
            tenant.Database(database, "/collections"), new { name = collection }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Runs <paramref name="sql"/> against the file at <paramref name="path"/>.</summary>
    public static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Replaces the body of every live document of <paramref name="collection"/> in the file at <paramref name="path"/> behind
    /// the server's back, as healthy rows with matching keyed digests (<see cref="StoredStates.ReplaceAsync"/>).
    /// </summary>
    public static async Task ReplaceBodiesAsync(string path, string collection, string body)
    {
        await using var connection = await OpenAsync(path);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await StoredStates.ReplaceAsync(connection, collection, null, body, transaction);
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Observes real row-body evaluation of a collection without replacing its rows or their order: the collection
    /// becomes a view of its renamed table that reads every document through <paramref name="functionName"/>, which the
    /// reading session registers, and it takes no more writes. Turso has no generated columns, and only the database's
    /// writer connection may change its schema.
    /// </summary>
    public static Task ObserveBodiesAsync(TursoDatabaseStore store, string functionName, string collection = "items") =>
        store.WithWriterAsync(writer =>
        {
            foreach (var sql in new[]
            {
                $"ALTER TABLE \"{collection}\" RENAME TO \"{collection}_observed\";",
                // Ordered by id, so Turso reads it lazily along the id key instead of evaluating every body to sort.
                $"CREATE VIEW \"{collection}\" AS SELECT id, {functionName}(doc) AS doc FROM \"{collection}_observed\" ORDER BY id;",
            })
            {
                using var command = writer.Connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            return true;
        }, "a test fixture", CancellationToken.None);

    /// <summary>
    /// Rents an idle read session of <paramref name="store"/>, opening one first if none is idle: writes use the store's
    /// own writer connection, so earlier writes leave no idle session.
    /// </summary>
    public static async Task<TursoSession> RentReaderAsync(TursoStorageEngine engine, TursoDatabaseStore store)
    {
        await using (await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
        }

        return engine.Pool.Rent(store)!;
    }

    /// <summary>
    /// Runs <paramref name="sql"/>, which may create a trigger, on the file of <paramref name="database"/> (the system
    /// database when null) while its writer is held, so no other connection writes meanwhile. The provider splits command
    /// text at every ';', which breaks a trigger body, so this uses a connection of its own.
    /// </summary>
    public static async Task ExecuteWhileWriterHeldAsync(TerraBaseTestServer server, TenantContext? database, string sql)
    {
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)(database is null
            ? await engine.OpenSystemDatabaseAsync(CancellationToken.None)
            : (await engine.OpenDatabaseAsync(database, CancellationToken.None))!);
        await store.WithWritersHeldAsync(() =>
        {
            using var connection = new Turso.TursoConnection(TestMasterKey.ConnectionString(store.FilePath));
            connection.Open();
            connection.ExecuteNonQuery(sql);
            return Task.FromResult(true);
        }, CancellationToken.None, "a test fixture");
    }

    /// <summary>Takes the file's write lock (<c>BEGIN IMMEDIATE</c>) until the result is disposed.</summary>
    public static async Task<IAsyncDisposable> LockAsync(string path)
    {
        var connection = await OpenAsync(path);
        try
        {
            return new Lock(connection, connection.BeginTransaction(deferred: false));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // Unpooled, so the file is released on dispose and the server can delete it.
    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30,
        }));
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Holds one storage worker inside real Turso row evaluation, on a scratch database of its own, until the result
    /// is disposed. The held read counts as one admission and one running call; other databases stay untouched.
    /// </summary>
    public static async Task<IAsyncDisposable> HoldWorkerAsync(TursoStorageEngine engine, CancellationToken cancellationToken)
    {
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, cancellationToken);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, cancellationToken))!;
        await using (var seed = await store.BeginTransactionAsync(write: true, cancellationToken))
        {
            await seed.PutAsync("held", Id("a"), "{}"u8.ToArray(), cancellationToken);
            await seed.CommitAsync(cancellationToken);
        }

        var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        var session = await RentReaderAsync(engine, store);
        try
        {
            session.Connection.CreateFunction<string, string>("held_body", body =>
            {
                if (Volatile.Read(ref armed))
                {
                    entered.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(60));
                }

                return body;
            }, isDeterministic: true);
        }
        finally
        {
            engine.Pool.Return(store, session);
        }

        await ObserveBodiesAsync(store, "held_body", "held");

        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken);
        Volatile.Write(ref armed, true);
        var read = transaction.GetAsync("held", Id("a"), CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(cancellationToken);
        return new HeldWorker(release, read, transaction);
    }

    private sealed class HeldWorker(ManualResetEventSlim release, Task read, IStorageTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            release.Set();
            try
            {
                await read;
            }
            finally
            {
                await transaction.DisposeAsync();
                release.Dispose();
            }
        }
    }

    private sealed class Lock(SqliteConnection connection, SqliteTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    public static async Task<TenantContext> ContextAsync(this Tenant tenant, TerraBaseTestServer server, string name, CancellationToken cancellationToken = default)
    {
        using var response = await tenant.Data.PutAsync(tenant.Database(name), null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(
            tenant.OrganizationId, tenant.ProjectId, name, cancellationToken) switch
        {
            TenantContext database => database,
            _ => throw new InvalidOperationException("The database created through the data listener was not found in the tenant directory."),
        };
    }

    public static async Task<string> FileAsync(this Tenant tenant, TerraBaseTestServer server, string name, CancellationToken cancellationToken = default)
    {
        var context = await tenant.ContextAsync(server, name, cancellationToken);
        return Path.Combine(server.DataDirectory, tenant.OrganizationId, tenant.ProjectId, context.DatabaseId + ".db");
    }
}
