using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Turso.Data.Sqlite;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Documents;

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
        var now = DateTimeOffset.UtcNow + FeedCursor.Retention + TimeSpan.FromDays(1);
        var clock = new RetentionClock(now - FeedCursor.Retention - TimeSpan.FromDays(1));
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (step != "writer.reset" || Volatile.Read(ref victim) is not { } store || database.DatabaseId != store.Tenant!.DatabaseId
                    || store.CurrentWriter() is not { } writer)
                {
                    return;
                }
                using var command = writer.Connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM _tb_feed;";
                if ((long)command.ExecuteScalar()! == 1 && paused.TrySetResult())
                {
                    resume.Wait(token);
                }
            }), time: clock);
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath(Survivor), cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath(Survivor)}/collections", new { name = DocumentApi.Collection }, cancellationToken: token))
            .Status.ShouldBe(HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var turso = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var victimDatabase = await api.TenantOfAsync(cancellationToken: token);
        var survivorDatabase = await api.TenantOfAsync(Survivor, token);
        var survivorFile = await api.DatabaseFileAsync(Survivor);
        _ = await PutAsync(engine, victimDatabase, 1025, token);
        var floor = await PutAsync(engine, survivorDatabase, 3, token);
        clock.SetUtcNow(now - FeedCursor.Retention + TimeSpan.FromMinutes(30));
        var later = await PutAsync(engine, survivorDatabase, 1, token);
        clock.SetUtcNow(now);
        _ = await PutAsync(engine, survivorDatabase, 1, token);
        var store = (TursoDatabaseStore)(await turso.OpenDatabaseAsync(victimDatabase, token))!;
        var warnings = new Warnings();
        var retention = new FeedRetention(turso, warnings);

        Volatile.Write(ref victim, store);
        var pass = retention.TrimAsync(token);
        try
        {
            await Assert.That(await Task.WhenAny(paused.Task, pass).WaitAsync(token)).IsSameReferenceAs(paused.Task);
            await Assert.That(await turso.DeleteDatabaseAsync(victimDatabase, token)).IsTrue();
        }
        finally
        {
            resume.Set();
            await pass;
        }
        await Assert.That(warnings.Errors.Single()).IsTypeOf<ObjectDisposedException>();
        await AssertTrimmedAsync(survivorFile, floor, remaining: 2);
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
        clock.SetUtcNow(now + TimeSpan.FromHours(1));
        await retention.TrimAsync(token);
        await Assert.That(warnings.Errors.Count).IsEqualTo(1);
        await AssertTrimmedAsync(survivorFile, later, remaining: 1);
    }

    private static async Task AssertTrimmedAsync(string file, Hlc floor, int remaining)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file, readOnly: true));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM _tb_feed;";
        await Assert.That((long)(await command.ExecuteScalarAsync())!).IsEqualTo(remaining);
        command.CommandText = "SELECT stamp FROM _tb_feed_floor;";
        await Assert.That((long)(await command.ExecuteScalarAsync())!).IsEqualTo((long)floor.Value);
        command.CommandText = "SELECT min(stamp) FROM _tb_feed;";
        await Assert.That((long)(await command.ExecuteScalarAsync())!).IsGreaterThan((long)floor.Value);
    }

    private static async Task<Hlc> PutAsync(IStorageEngine engine, TenantContext database, int count, CancellationToken token)
    {
        var store = (await engine.OpenDatabaseAsync(database, token))!;
        for (var offset = 0; offset < count; offset += 512)
        {
            await using var transaction = await store.BeginTransactionAsync(write: true, token);
            for (var i = offset; i < Math.Min(count, offset + 512); i++)
            {
                _ = await transaction.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), """{"local":true}"""u8.ToArray(), token);
            }
            _ = await transaction.CommitAsync(token);
        }

        await using var read = await store.BeginTransactionAsync(write: false, token);
        return (await read.GetFeedHeadAsync(token)).Single().Value;
    }

    private sealed class RetentionClock(DateTimeOffset now) : TimeProvider
    {
        private long _milliseconds = now.ToUnixTimeMilliseconds();
        public void SetUtcNow(DateTimeOffset value) => Volatile.Write(ref _milliseconds, value.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref _milliseconds));
    }

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
