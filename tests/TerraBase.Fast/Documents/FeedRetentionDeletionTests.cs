using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Turso.Data.Sqlite;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Documents;

/// <summary>
/// Changes feed retention (docs/terrabase/multi-region-replication.md section 11) racing a database deletion, as backup
/// retention expiring a snapshot does: the closed store fails its own trim alone, and no pass skips or moves another trim.
/// </summary>
public sealed class FeedRetentionDeletionTests
{
    private const string Survivor = "survivor";

    [Test]
    public async Task A_database_deleted_between_its_retention_batches_fails_alone_and_this_and_the_next_pass_trim_the_rest()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        TursoDatabaseStore? victim = null;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (step != "writer.reset" || Volatile.Read(ref victim) is not { } store || database.DatabaseId != store.Tenant!.DatabaseId
                    || store.CurrentWriter() is not { } writer)
                {
                    return;
                }

                // Retention's first batch committed and still holds the writer: one of the victim's 1025 events is left.
                using var command = writer.Connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM _tb_feed;";
                if ((long)command.ExecuteScalar()! == 1 && paused.TrySetResult())
                {
                    resume.Wait(token);
                }
            }));
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath(Survivor), cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath(Survivor)}/collections", new { name = DocumentApi.Collection }, cancellationToken: token))
            .Status.ShouldBe(HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var turso = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var victimDatabase = await api.TenantOfAsync(cancellationToken: token);
        var survivorDatabase = await api.TenantOfAsync(Survivor, token);
        var now = DateTimeOffset.UtcNow;
        var aged = ReplicaId.Of(4518, 41, 1);
        var fresh = ReplicaId.Of(4518, 42, 1);
        // More than one retention batch (1024) on the victim, so its trim begins a second transaction after the deletion.
        _ = await JoinAsync(engine, victimDatabase, aged, now - FeedCursor.Retention - TimeSpan.FromDays(1), 1025);
        var floor = await JoinAsync(engine, survivorDatabase, aged, now - FeedCursor.Retention - TimeSpan.FromDays(1), 3);
        _ = await JoinAsync(engine, survivorDatabase, fresh, now, 1);
        var store = (TursoDatabaseStore)(await turso.OpenDatabaseAsync(victimDatabase, token))!;
        var warnings = new Warnings();
        var retention = new FeedRetention(turso, warnings);

        Volatile.Write(ref victim, store);
        var pass = retention.TrimAsync(token);
        try
        {
            await paused.Task.WaitAsync(token);
            // What backup retention's sweep runs for an expired snapshot.
            await Assert.That(await turso.DeleteDatabaseAsync(victimDatabase, token)).IsTrue();
        }
        finally
        {
            resume.Set();
        }
        await pass.WaitAsync(token);

        // Only the deleted database failed, once, on its closed store; whichever came first, the survivor was trimmed.
        await Assert.That(warnings.Errors.Single()).IsTypeOf<ObjectDisposedException>();
        await AssertTrimmedAsync(api, aged, floor, fresh);
        // The closed store kept no connection, no operation intent, and did not fail the node closed.
        await Assert.That(store.IsClosed).IsTrue();
        await Assert.That(store.CurrentWriter()).IsNull();
        await Assert.That(turso.Pool.IdleCountOf(store)).IsEqualTo(0);
        await Assert.That(turso.Ledger.FailedClosed).IsNull();
        var intent = await turso.IntentStore!.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM _tb_write_intent WHERE database = $database AND incarnation = $incarnation;";
            command.Parameters.AddWithValue("$database", victimDatabase.DatabaseId);
            command.Parameters.AddWithValue("$incarnation", store.Incarnation.ToString());
            return command.ExecuteScalar() is not null;
        }, "checking the deleted store's intent", token);
        await Assert.That(intent).IsFalse();

        // The next pass trims an event that aged since, and still keeps the fresh one.
        var later = await JoinAsync(engine, survivorDatabase, aged, now - FeedCursor.Retention - TimeSpan.FromHours(1), 1);
        await retention.TrimAsync(token);
        await Assert.That(warnings.Errors.Count).IsEqualTo(1);
        await AssertTrimmedAsync(api, aged, later, fresh);
    }

    // The survivor holds no event of `aged`, whose floor is `floor`, and still holds the one event of `fresh`.
    private static async Task AssertTrimmedAsync(DocumentApi api, ReplicaId aged, Hlc floor, ReplicaId fresh)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync(Survivor), readOnly: true));
        await connection.OpenAsync();
        async Task<long> ScalarAsync(string sql, ReplicaId origin)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$origin", unchecked((long)origin.Value));
            return (long)(await command.ExecuteScalarAsync())!;
        }

        await Assert.That(await ScalarAsync("SELECT count(*) FROM _tb_feed WHERE origin = $origin;", aged)).IsEqualTo(0);
        await Assert.That(await ScalarAsync("SELECT stamp FROM _tb_feed_floor WHERE origin = $origin;", aged)).IsEqualTo((long)floor.Value);
        await Assert.That(await ScalarAsync("SELECT count(*) FROM _tb_feed WHERE origin = $origin;", fresh)).IsEqualTo(1);
    }

    // Joins `count` documents written by `origin` from `at` on in one transaction, as a peer stream does; returns the last stamp.
    private static async Task<Hlc> JoinAsync(IStorageEngine engine, TenantContext database, ReplicaId origin, DateTimeOffset at, int count)
    {
        var store = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
        using var body = JsonDocument.Parse("""{"joined":true}""");
        var stamp = default(Hlc);
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        for (var i = 0; i < count; i++)
        {
            stamp = new Hlc(((ulong)at.ToUnixTimeMilliseconds() << 16) + (ulong)i);
            var version = new EntryVersion(stamp, origin);
            _ = await transaction.JoinAsync(DocumentApi.Collection, DocumentApi.NewId(), DocumentState.Bottom.Put(version, body.RootElement).DeltaAt(stamp), version, CancellationToken.None);
        }

        _ = await transaction.CommitAsync(CancellationToken.None);
        return stamp;
    }

    /// <summary>The exceptions of the warnings retention logged.</summary>
    private sealed class Warnings : ILogger<FeedRetention>
    {
        public ConcurrentQueue<Exception?> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Errors.Enqueue(exception);
            }
        }
    }
}
