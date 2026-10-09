using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Management;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

// TS-647 step 2: client writes of one database share write transactions (group commit), each in its own savepoint, on
// the database's own write thread.
public sealed class GroupCommitTests
{
    [Test]
    public async Task Cancelling_a_native_mutation_rolls_back_only_its_grouped_write_and_reuses_admission()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledId = DocumentApi.NewId();
        var hook = new UsageCommitHook();
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-queue"] = "6", services =>
        {
            services.AddHttpContextAccessor();
            hook.InstallQuota(services);
        }, deadline.Token);
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var store = await StoreAsync(api);
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var context = api.Server.Services.GetRequiredService<IHttpContextAccessor>();
        CancellationTokenRegistration cancellationObserved = default;
        hook.BeforeDocumentQuota = (_, token) =>
        {
            if (context.HttpContext!.Request.Path.Value!.EndsWith("/" + cancelledId, StringComparison.Ordinal))
            {
                // Registered before the mutation's native cancellation callback: LIFO cancellation means
                // the signal arrives only after that callback has finished calling sqlite3_interrupt.
                cancellationObserved = token.Register(() => aborted.TrySetResult());
            }

            return ValueTask.CompletedTask;
        };

        var session = engine.Pool.Rent(store) ?? throw new InvalidOperationException("the database has no idle session");
        try
        {
            session.Connection.CreateFunction<string, int>("qos_pause_insert", id =>
            {
                if (id == cancelledId)
                {
                    entered.TrySetResult();
                    release.Wait(deadline.Token);
                }

                return 0;
            });
            using var trigger = session.Connection.CreateCommand();
            trigger.CommandText = "CREATE TEMP TRIGGER qos_pause_insert BEFORE INSERT ON documents BEGIN SELECT qos_pause_insert(NEW.id); END;";
            trigger.ExecuteNonQuery();
        }
        finally
        {
            engine.Pool.Return(store, session);
        }

        var pending = new List<Task<HttpResponseMessage>>();
        IStorageTransaction? held = await store.BeginTransactionAsync(write: true, deadline.Token);
        try
        {
            Task<HttpResponseMessage> Put(string id, CancellationToken token)
            {
                return api.Client.PutAsJsonAsync(new Uri($"{api.Documents}/by-key?id={id}", UriKind.Relative), new { n = 1 }, token);
            }

            var primer = Put(DocumentApi.NewId(), deadline.Token);
            pending.Add(primer);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.BatchedWrites == 1));
            var firstId = DocumentApi.NewId();
            var lastId = DocumentApi.NewId();
            var first = Put(firstId, deadline.Token);
            pending.Add(first);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites == 1));
            var cancelled = Put(cancelledId, cancel.Token);
            pending.Add(cancelled);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites == 2));
            var last = Put(lastId, deadline.Token);
            pending.Add(last);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites == 3));
            await held.DisposeAsync();
            held = null;

            await entered.Task.WaitAsync(deadline.Token);
            await cancel.CancelAsync();
            await aborted.Task.WaitAsync(deadline.Token);
            release.Set();
            await Assert.ThrowsAsync<TaskCanceledException>(() => cancelled);
            using var primed = await primer.WaitAsync(deadline.Token);
            using var firstResponse = await first.WaitAsync(deadline.Token);
            using var lastResponse = await last.WaitAsync(deadline.Token);
            await Assert.That(primed.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(lastResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var firstBody = await firstResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(deadline.Token);
            var lastBody = await lastResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(deadline.Token);
            await Assert.That(firstBody.GetProperty("offset").GetString()).IsEqualTo(lastBody.GetProperty("offset").GetString());
            await Assert.That((await api.GetAsync(firstId)).Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await api.GetAsync(lastId)).Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await api.GetAsync(cancelledId)).Status).IsEqualTo(HttpStatusCode.NotFound);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(engine.Io.Admitted == 0));
            await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = 2 })).Status).IsEqualTo(HttpStatusCode.Created);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(engine.Io.Admitted == 0));
        }
        finally
        {
            release.Set();
            await cancel.CancelAsync();
            if (held is not null)
            {
                await held.DisposeAsync();
            }

            foreach (var request in pending)
            {
                await StoragePoolTests.DisposeResponseAsync(request);
            }

            cancellationObserved.Dispose();
        }
    }

    [Test]
    public async Task Writes_queued_together_commit_in_one_transaction_and_failures_roll_back_alone()
    {
        await using var api = await DocumentApi.StartAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var existing = DocumentApi.NewId();
        var original = await api.PutAsync(existing, new { n = 0 });
        var store = await StoreAsync(api);

        // Hold the database's writer so the writes below queue up, then release them into one batch.
        var held = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        var ids = Enumerable.Range(0, 8).Select(_ => DocumentApi.NewId()).ToArray();
        var rolledBack = DocumentApi.NewId();
        Task<ApiResponse> primer;
        Task<ApiResponse>[] sending;
        try
        {
            // A primer takes the batch that waits for the writer, so every write below queues for the next one. Without
            // it, how many writes the first batch takes depends on when its loop starts (TS-624).
            primer = api.PutAsync(DocumentApi.NewId(), new { n = 0 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.BatchedWrites == 1));
            sending =
            [
                .. ids.Select(id => api.PutAsync(id, new { n = 1 })),
                api.PutAsync(existing, new { n = 2 }, DocumentApi.IfMatch("stale")),
                api.TransactAsync(
                    new { op = "put", collection = DocumentApi.Collection, id = rolledBack, document = new { n = 3 } },
                    new { op = "assert", collection = DocumentApi.Collection, id = existing, filter = new { eq = new object[] { "/n", 99 } } }),
            ];

            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites == sending.Length));
        }
        finally
        {
            await held.DisposeAsync();
        }

        await Assert.That((await primer).Status).IsEqualTo(HttpStatusCode.Created);
        var answers = await Task.WhenAll(sending);
        var created = answers[..ids.Length];
        await Assert.That(created.All(answer => answer.Status == HttpStatusCode.Created)).IsTrue();
        // One commit, one offset, for every write of the batch.
        await Assert.That(created.Select(answer => answer.Json.GetProperty("offset").GetString()).Distinct().Count()).IsEqualTo(1);

        // The refused write and the failed transaction rolled back only their own savepoints.
        await Assert.That(answers[ids.Length].Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(answers[ids.Length + 1].Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That((await api.GetAsync(existing)).ETag).IsEqualTo(original.ETag);
        await Assert.That((await api.GetAsync(rolledBack)).Status).IsEqualTo(HttpStatusCode.NotFound);
        foreach (var id in ids)
        {
            await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task A_long_wait_for_the_writer_logs_what_held_it_and_for_how_long()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new CapturedLogs();
        await using var api = await DocumentApi.StartAsync(services: services =>
        {
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock);
            services.AddSingleton<ILoggerProvider>(logs);
        });
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var store = await StoreAsync(api);

        // A write transaction holds the writer, idle between its calls, while a batch waits past the warning threshold.
        var held = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        Task<ApiResponse> waiting;
        try
        {
            waiting = api.PutAsync(DocumentApi.NewId(), new { n = 1 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.BatchedWrites == 1));
            clock.Advance(SqliteDatabaseStore.SlowWriterWait + TimeSpan.FromSeconds(1));
        }
        finally
        {
            await held.DisposeAsync();
        }

        await Assert.That((await waiting).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(logs.Messages).Contains(
            "Waited 6 s for a database's writer and took it: a group commit batch queued behind a write transaction, which had held it for 6 s and was between storage calls");
    }

    [Test]
    public async Task A_rolled_back_write_restores_what_an_earlier_write_of_its_batch_captured()
    {
        await using var api = await DocumentApi.StartAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var existing = DocumentApi.NewId();
        await api.PutAsync(existing, new { n = 0 });
        var store = await StoreAsync(api);
        var shared = DocumentApi.NewId();
        var other = DocumentApi.NewId();

        var held = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        Task<ApiResponse> primer;
        Task<ApiResponse> first;
        Task<ApiResponse> failing;
        Task<ApiResponse> last;
        try
        {
            // A primer takes the batch that waits for the writer; the next three then queue, in this order, for one batch.
            primer = api.PutAsync(DocumentApi.NewId(), new { n = 0 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.BatchedWrites == 1));
            first = api.PutAsync(shared, new { n = 1 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 1));
            // Its first put replaces what `first` captured in the same batch; its second put then fails, after it.
            failing = api.TransactAsync(
                new { op = "put", collection = DocumentApi.Collection, id = shared, document = new { n = 2 } },
                new { op = "put", collection = DocumentApi.Collection, id = existing, document = new { n = 5 }, if_match = "tbv1-00000000000000000000000000000000" });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 2));
            last = api.PutAsync(other, new { n = 3 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 3));
        }
        finally
        {
            await held.DisposeAsync();
        }

        await Assert.That((await primer).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await first).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await last).Json.GetProperty("offset").GetString()).IsEqualTo((await first).Json.GetProperty("offset").GetString());
        await Assert.That((await failing).Status).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That((await last).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.GetAsync(shared)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);

        // The change groups replicated to peers carry the first write's state, never the rolled-back one.
        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var captured = new List<DocumentChange>();
        for (var sequence = await reader.GetLastChangeSequenceAsync(CancellationToken.None); sequence > 0 && captured.Count < 3; sequence--)
        {
            captured.AddRange(await reader.ReadChangesAsync(sequence, CancellationToken.None) ?? []);
        }

        var states = captured.Where(change => change.Id == shared).Select(change => System.Text.Encoding.UTF8.GetString(change.Json!.Value.Span)).ToList();
        await Assert.That(states).IsNotEmpty();
        await Assert.That(states.All(json => json.Contains("\"n\":1", StringComparison.Ordinal))).IsTrue();
        await Assert.That(captured.Any(change => change.Id == other)).IsTrue();
    }

    [Test]
    public async Task A_storage_thread_busy_with_a_long_read_does_not_hold_up_writes()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-threads"] = "1");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = 0 })).Status).IsEqualTo(HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();

        // Occupy the only read thread, as a long runSql would.
        using var release = new ManualResetEventSlim();
        var busy = engine.Io.RunAsync(release.Wait, CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var written = await api.PutAsync(DocumentApi.NewId(), new { n = 1 }, cancellationToken: deadline.Token);
            await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(busy.IsCompleted).IsFalse();
        }
        finally
        {
            release.Set();
            await busy;
        }
    }

    private static async Task<SqliteDatabaseStore> StoreAsync(DocumentApi api)
    {
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        return (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
    }

    // The application's log lines, as an operator would read them.
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
