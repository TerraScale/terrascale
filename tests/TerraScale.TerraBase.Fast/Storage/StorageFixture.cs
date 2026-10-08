using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

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

    /// <summary>Observes real row-body evaluation without replacing the table or its live-row index.</summary>
    public static void ObserveBodies(SqliteConnection connection, string functionName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            ALTER TABLE documents RENAME COLUMN body TO stored_body;
            ALTER TABLE documents ADD COLUMN body TEXT GENERATED ALWAYS AS ({functionName}(stored_body)) VIRTUAL;
            DROP INDEX documents_live;
            CREATE INDEX documents_live ON documents (collection, id) WHERE body IS NOT NULL;
            """;
        command.ExecuteNonQuery();
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
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
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
    /// Holds one storage worker inside real SQLite row evaluation, on a scratch database of its own, until the result
    /// is disposed. The held read counts as one admission and one running call; other databases stay untouched.
    /// </summary>
    public static async Task<IAsyncDisposable> HoldWorkerAsync(SqliteStorageEngine engine, CancellationToken cancellationToken)
    {
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, cancellationToken);
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(database, cancellationToken))!;
        await using (var seed = await store.BeginTransactionAsync(write: true, cancellationToken))
        {
            await seed.PutAsync("held", Id("a"), "{}"u8.ToArray(), cancellationToken);
            await seed.CommitAsync(cancellationToken);
        }

        var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        // The seed returned the only session; the held read rents it back.
        var session = engine.Pool.Rent(store)!;
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
            ObserveBodies(session.Connection, "held_body");
        }
        finally
        {
            engine.Pool.Return(store, session);
        }

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
            tenant.OrganizationId, tenant.ProjectId, name, cancellationToken)
            ?? throw new InvalidOperationException("The database created through the data listener was not found in the tenant directory.");
    }

    public static async Task<string> FileAsync(this Tenant tenant, TerraBaseTestServer server, string name, CancellationToken cancellationToken = default)
    {
        var context = await tenant.ContextAsync(server, name, cancellationToken);
        return Path.Combine(server.DataDirectory, tenant.OrganizationId, tenant.ProjectId, context.DatabaseId + ".db");
    }
}
