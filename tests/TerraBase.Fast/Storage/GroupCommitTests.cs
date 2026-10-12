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
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

// TS-647 step 2: client writes of one database share write transactions (group commit), each in its own savepoint, on
// the database's own write thread.
public sealed class GroupCommitTests
{
    [Test]
    public async Task Cancelling_a_running_mutation_rolls_back_only_its_grouped_write_and_reuses_admission()
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
            if (context.HttpContext!.Request.Query["id"] == cancelledId)
            {
                // Turso cannot interrupt a running statement: the cancellation lands after the paused insert, before the
                // write's next statement, and its savepoint rolls back.
                cancellationObserved = token.Register(() => aborted.TrySetResult());
            }

            return ValueTask.CompletedTask;
        };

        // The writes run on the database's writer connection: it gets the pausing function, and a trigger (created on a
        // connection of its own, as the provider cannot run a trigger body) calls it before each insert.
        await store.WithWriterAsync(writer =>
        {
            writer.Connection.CreateFunction<string, int>("qos_pause_insert", id =>
            {
                if (id == cancelledId)
                {
                    entered.TrySetResult();
                    release.Wait(deadline.Token);
                }

                return 0;
            });
            return true;
        }, "a test", deadline.Token);
        await StorageFixture.ExecuteWhileWriterHeldAsync(api.Server, await api.TenantOfAsync(),
            "CREATE TRIGGER qos_pause_insert BEFORE INSERT ON \"items\" BEGIN SELECT qos_pause_insert(NEW.id); END;");

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
    public async Task A_rolled_back_write_keeps_what_an_earlier_write_of_its_batch_wrote()
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
        await Assert.That((await api.GetAsync(other)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task A_read_its_work_budget_stops_inside_a_batch_is_too_expensive_and_the_writer_keeps_working()
    {
        await using var api = await DocumentApi.StartAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "pro");
        foreach (var _ in Enumerable.Range(0, 4))
        {
            var creates = Enumerable.Range(0, 1000)
                .Select(object (_) => new { op = "create", collection = DocumentApi.Collection, document = new { n = 1 } }).ToArray();
            await Assert.That((await api.TransactAsync(creates)).Status).IsEqualTo(HttpStatusCode.OK);
        }

        // 248 conditions on 4000 documents cost more engine work than Pro's budget, though no document matches. Stopping
        // the read makes Turso roll the whole batch transaction back, its savepoints included.
        var expensive = new
        {
            or = Enumerable.Range(0, 4).Select(group => new { or = Enumerable.Range(0, 62).Select(i => new { eq = new object[] { "/n", -(62 * group) - i } }) }),
        };
        var stopped = await api.TransactAsync(new { op = "assert_count", collection = DocumentApi.Collection, filter = expensive, lte = 0 });
        var error = stopped.Json.GetProperty("error");
        await Assert.That((stopped.Status, error.GetProperty("code").GetString(), error.GetProperty("op").GetInt32()))
            .IsEqualTo((HttpStatusCode.BadRequest, "invalid_query", 0));
        var next = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(next, new { n = 2 })).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.GetAsync(next)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task A_write_whose_failure_rolls_back_the_whole_batch_fails_alone_and_the_others_commit_again()
    {
        await using var api = await DocumentApi.StartAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var store = await StoreAsync(api);
        var failingId = DocumentApi.NewId();
        // Like a read the work budget stops, RAISE(ROLLBACK) ends the whole write transaction, every savepoint included.
        await StorageFixture.ExecuteWhileWriterHeldAsync(api.Server, await api.TenantOfAsync(),
            $"CREATE TRIGGER roll_back_batch BEFORE INSERT ON \"items\" WHEN NEW.id = '{failingId}' BEGIN SELECT RAISE(ROLLBACK, 'the test rolls the batch back'); END;");
        var firstId = DocumentApi.NewId();
        var lastId = DocumentApi.NewId();

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
            first = api.PutAsync(firstId, new { n = 1 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 1));
            failing = api.PutAsync(failingId, new { n = 2 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 2));
            last = api.PutAsync(lastId, new { n = 3 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 3));
        }
        finally
        {
            await held.DisposeAsync();
        }

        await Assert.That((await primer).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await failing).Status).IsEqualTo(HttpStatusCode.InternalServerError);
        // The rollback took `first` with it: it ran again, with `last`, in the next batch.
        await Assert.That((await first).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await last).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await first).Json.GetProperty("offset").GetString()).IsEqualTo((await last).Json.GetProperty("offset").GetString());
        await Assert.That((await api.GetAsync(firstId)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await Assert.That((await api.GetAsync(lastId)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(3);
        await Assert.That((await api.GetAsync(failingId)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    // insideCommit fails the work CommitAsync still does after the SQLite commit; otherwise the batch fails after CommitAsync.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_failure_after_the_commit_answers_the_committed_writes_and_logs_it(bool insideCommit)
    {
        var logs = new CapturedLogs();
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<ILoggerProvider>(logs));
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var store = await StoreAsync(api);
        var failed = 0;
        void Fail()
        {
            Interlocked.Increment(ref failed);
            throw new IOException("the test fails the work after the commit");
        }

        if (insideCommit)
        {
            store.AfterSqliteCommit = Fail;
        }
        else
        {
            store.AfterGroupCommit = Fail;
        }

        var id = DocumentApi.NewId();
        var written = await api.PutAsync(id, new { n = 1 });
        var transacted = DocumentApi.NewId();
        var transaction = await api.TransactAsync(new { op = "put", collection = DocumentApi.Collection, id = transacted, document = new { n = 3 } });

        // Answered as the commits they are, not as a 500 a client would retry.
        await Assert.That(Volatile.Read(ref failed)).IsGreaterThanOrEqualTo(2);
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(written.Json.GetProperty("offset").GetString()).IsNotEqualTo("0");
        await Assert.That(written.Json.GetProperty("durability").GetString()).IsEqualTo("local_durable");
        await Assert.That(transaction.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transaction.Json.GetProperty("offset").GetString()).IsNotEqualTo("0");
        var read = await api.GetAsync(id);
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.ETag).IsEqualTo(written.ETag);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await Assert.That((await api.GetAsync(transacted)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(3);
        await Assert.That(logs.Messages.Any(message => message.Contains("committed, but the work after its commit failed", StringComparison.Ordinal))).IsTrue();

        // The writer keeps working.
        store.AfterSqliteCommit = store.AfterGroupCommit = null;
        var next = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(next, new { n = 2 })).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.GetAsync(next)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task A_failure_after_a_catalog_group_commit_still_fails_the_write()
    {
        await using var api = await DocumentApi.StartAsync();
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var system = (SqliteDatabaseStore)await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        system.AfterGroupCommit = () => throw new IOException("the test fails the work after the commit");
        var id = DocumentApi.NewId();

        // The catalog fails closed: a write whose commit landed is still answered as a failure.
        await Assert.ThrowsAsync<IOException>(async () => await system.WriteAsync(
            (transaction, token) => transaction.PutAsync("group_commit_probe", id, """{"n":1}"""u8.ToArray(), token), _ => true, CancellationToken.None));
        system.AfterGroupCommit = null;
        var read = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        await using (read)
        {
            await Assert.That(await read.GetAsync("group_commit_probe", id, CancellationToken.None)).IsNotNull();
        }
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
