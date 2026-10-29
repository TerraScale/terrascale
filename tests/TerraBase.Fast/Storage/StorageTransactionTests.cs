using System.Diagnostics.Metrics;
using System.Text;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

/// <summary>
/// The Turso session pool, through the running server's engine: no listener can dispose a
/// transaction twice, delete a database under an open transaction, or start one pre-cancelled.
/// </summary>
public sealed partial class StorageTransactionTests
{
    // Every test runs its own server and data directory, so they can share the ids.
    private static readonly TenantContext Database = Tenant(EntityIds.New().ToString());

    [Test]
    public async Task Turso_enforces_query_only_for_reads_and_never_for_writes()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (engine, store) = await OpenAsync(server);
        await using (var seed = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await seed.PutAsync("items", "a", "{}"u8.ToArray(), CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }

        var session = ((TursoStorageEngine)engine).Pool.Rent(store) ?? throw new InvalidOperationException("missing idle session");
        try
        {
            using var view = session.Connection.CreateCommand();
            // Report the native connection flag through the engine's normal document read, without exposing the rented
            // transaction's connection or depending on private implementation fields: the temporary view hides the table.
            view.CommandText = """
                CREATE TEMP VIEW items AS SELECT 'a' AS id, json_object('readOnly', (SELECT query_only FROM pragma_query_only)) AS doc;
                """;
            await view.ExecuteNonQueryAsync();
        }
        finally
        {
            store.Return(session);
        }

        await using (var read = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(Encoding.UTF8.GetString((await read.GetAsync("items", "a", CancellationToken.None))!.Json.Span))
                .IsEqualTo("{\"readOnly\":1}");
        }

        // Pooled sessions serve reads only, so the read leaves its session read-only: changing the setting makes Turso prepare
        // every kept statement again (TursoSession.Settings).
        var reused = ((TursoStorageEngine)engine).Pool.Rent(store) ?? throw new InvalidOperationException("missing idle session");
        try
        {
            using var flag = reused.Connection.CreateCommand();
            flag.CommandText = "SELECT query_only FROM pragma_query_only;";
            await Assert.That(await flag.ExecuteScalarAsync()).IsEqualTo(1L);
        }
        finally
        {
            store.Return(reused);
        }

        // Writes run on the store's own writer connection, which is never read-only.
        var writer = await ((TursoDatabaseStore)store).WithWriterAsync(session =>
        {
            using var flag = session.Connection.CreateCommand();
            flag.CommandText = "SELECT query_only FROM pragma_query_only;";
            return flag.ExecuteScalar();
        }, "a test", CancellationToken.None);
        await Assert.That(writer).IsEqualTo(0L);
    }

    // The node-local intent journal is never replicated or backed up: recording and retiring intents captures nothing, while
    // the tenant database's own write is captured.
    [Test]
    public async Task Write_intents_capture_no_changes_and_the_tenant_write_is_captured()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (engine, store) = await OpenAsync(server);
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            _ = await writer.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);
            _ = await writer.CommitAsync(CancellationToken.None);
        }

        static Task<long> CapturedAsync(TursoDatabaseStore target)
        {
            return target.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name = 'turso_cdc';";
                if ((long)command.ExecuteScalar()! == 0)
                {
                    return 0L;
                }

                command.CommandText = "SELECT count(*) FROM turso_cdc;";
                return (long)command.ExecuteScalar()!;
            }, "a test counting captured changes", CancellationToken.None);
        }

        await Assert.That(await CapturedAsync(((TursoStorageEngine)engine).IntentStore!)).IsEqualTo(0L);
        await Assert.That(await CapturedAsync(store)).IsGreaterThan(0L);
    }

    // Each commit's marker names its own final CDC row, whether the marker statement is kept from an earlier transaction or
    // read fresh after this transaction's DDL (a collection created by the put).
    [Test]
    public async Task Each_commit_marker_names_the_last_cdc_commit_row_across_commits_and_collection_creation()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var (_, store) = await OpenAsync(server);
        Task<long> LastCommitRowAsync()
        {
            return store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT max(change_id) FROM turso_cdc WHERE change_type = 2;";
                return (long)command.ExecuteScalar()!;
            }, "a test reading the change log", CancellationToken.None);
        }

        var markers = new List<long>();
        foreach (var collection in new[] { "items", "items", "created", "items", "created" })
        {
            CommitResult commit;
            await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                _ = await writer.PutAsync(collection, EntityIds.New().ToString(), Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);
                commit = await writer.CommitAsync(CancellationToken.None);
            }

            await Assert.That(commit.Marker).IsNotNull();
            await Assert.That(commit.Marker!.Value.Incarnation).IsEqualTo(store.Incarnation);
            await Assert.That(commit.Marker.Value.ChangeId).IsEqualTo(await LastCommitRowAsync());
            markers.Add(commit.Marker.Value.ChangeId);
        }

        await Assert.That(markers.Zip(markers.Skip(1)).All(pair => pair.First < pair.Second)).IsTrue();
    }

    // The feed sequence and the commit offset are read and set under the writer: each feed row takes the next number, a
    // rolled-back savepoint gives its number back, each commit takes one offset, and an exhausted sequence refuses the write.

    // A write transaction remembers the collections it found until it drops one or rolls back to a savepoint: a collection
    // the rolled-back write created, or one the transaction dropped, is looked up again and created anew by the next put.

    private static TenantContext Tenant(string database) => new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), database);

    private static async Task<(IStorageEngine Engine, TursoDatabaseStore Store)> OpenAsync(TerraBaseTestServer server)
    {
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        await engine.CreateDatabaseAsync(Database, CancellationToken.None);
        return (engine, (TursoDatabaseStore)(await engine.OpenDatabaseAsync(Database, CancellationToken.None))!);
    }
}
