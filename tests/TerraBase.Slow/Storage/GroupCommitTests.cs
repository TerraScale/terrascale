using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Slow.Documents;
using TerraBase.Slow.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

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
        }, cancellationToken: deadline.Token);
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var store = await StoreAsync(api);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var context = api.Server.Services.GetRequiredService<IHttpContextAccessor>();
        CancellationTokenRegistration cancellationObserved = default;
        hook.BeforeDocumentQuota = (_, token) =>
        {
            if (context.HttpContext!.Request.Query["id"] == cancelledId)
            {
                cancellationObserved = token.Register(() => aborted.TrySetResult());
            }

            return ValueTask.CompletedTask;
        };

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
                await StoragePoolHelpers.DisposeResponseAsync(request);
            }

            cancellationObserved.Dispose();
        }
    }

    [Test]
    public async Task Writes_queued_together_commit_in_one_transaction_and_failures_roll_back_alone()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var existing = DocumentApi.NewId();
        var original = await api.PutAsync(existing, new { n = 0 });
        var store = await StoreAsync(api);

        var held = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        var ids = Enumerable.Range(0, 8).Select(_ => DocumentApi.NewId()).ToArray();
        var rolledBack = DocumentApi.NewId();
        Task<ApiResponse> primer;
        Task<ApiResponse>[] sending;
        try
        {
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
        await Assert.That(created.Select(answer => answer.Json.GetProperty("offset").GetString()).Distinct().Count()).IsEqualTo(1);

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
    public async Task A_batch_queues_about_one_call_per_write_on_the_write_thread_and_runs_the_rest_there()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        _ = await api.PutAsync(DocumentApi.NewId(), new { n = 0 });
        var store = await StoreAsync(api);
        var diagnostics = new TursoRunnerDiagnostics();
        var ids = Enumerable.Range(0, 20).Select(_ => DocumentApi.NewId()).ToArray();
        store.WriteLane.Diagnostics = diagnostics;
        try
        {
            foreach (var id in ids)
            {
                await Assert.That((await api.PutAsync(id, new { n = 1 })).Status).IsEqualTo(HttpStatusCode.Created);
            }
        }
        finally
        {
            store.WriteLane.Diagnostics = null;
        }

        await Assert.That(diagnostics.Sample().Submitted).IsLessThanOrEqualTo(ids.Length * 2);
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

        var (held, heldAt) = await BeginWriteTransactionAsync(store);
        Task<ApiResponse> waiting;
        try
        {
            waiting = api.PutAsync(DocumentApi.NewId(), new { n = 1 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.BatchedWrites == 1));
            clock.Advance(TursoDatabaseStore.SlowWriterWait + TimeSpan.FromSeconds(1));
        }
        finally
        {
            await held.DisposeAsync();
        }

        await Assert.That((await waiting).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(logs.Messages).Contains(
            $"Waited 6 s for a database's writer and took it: a group commit batch (an engine caller) queued behind a write transaction ({heldAt}), which had held it for 6 s and was between storage calls");
    }

    [Test]
    public async Task A_writer_held_a_second_or_more_logs_its_holder_and_call_site_with_nothing_waiting()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new CapturedLogs();
        await using var api = await DocumentApi.StartAsync(services: services =>
        {
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock);
            services.AddSingleton<ILoggerProvider>(logs);
        });
        var store = await StoreAsync(api);

        var (brief, briefAt) = await BeginWriteTransactionAsync(store);
        await using (brief)
        {
            clock.Advance(TursoDatabaseStore.LongWriterHold / 2);
        }

        var (held, heldAt) = await BeginWriteTransactionAsync(store);
        await using (held)
        {
            clock.Advance(TursoDatabaseStore.LongWriterHold + TimeSpan.FromSeconds(0.5));
        }

        await Assert.That(logs.Messages).Contains($"A database's writer was held 1.5 s by a write transaction ({heldAt})");
        await Assert.That(logs.Messages).DoesNotContain($"A database's writer was held 0.5 s by a write transaction ({briefAt})");
    }

    [Test]
    public async Task A_rolled_back_write_keeps_what_an_earlier_write_of_its_batch_wrote()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
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
            primer = api.PutAsync(DocumentApi.NewId(), new { n = 0 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.BatchedWrites == 1));
            first = api.PutAsync(shared, new { n = 1 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(store.QueuedWrites >= 1));
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
        await using var api = await DocumentApi.StartIsolatedAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        foreach (var _ in Enumerable.Range(0, 4))
        {
            var creates = Enumerable.Range(0, 1000)
                .Select(object (_) => new { op = "create", collection = DocumentApi.Collection, document = new { n = 1 } }).ToArray();
            await Assert.That((await api.TransactAsync(creates)).Status).IsEqualTo(HttpStatusCode.OK);
        }

        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "pro");

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
        await using var api = await DocumentApi.StartIsolatedAsync();
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var store = await StoreAsync(api);
        var failingId = DocumentApi.NewId();
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
        await Assert.That(((await failing).Status, (await failing).Code))
            .IsEqualTo((HttpStatusCode.ServiceUnavailable, "write_outcome_unknown"));
        await Assert.That((await first).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await last).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await first).Json.GetProperty("offset").GetString()).IsEqualTo((await last).Json.GetProperty("offset").GetString());
        await Assert.That((await api.GetAsync(firstId)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await Assert.That((await api.GetAsync(lastId)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(3);
        await Assert.That((await api.GetAsync(failingId)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

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
            store.AfterTursoCommit = Fail;
        }
        else
        {
            store.AfterGroupCommit = Fail;
        }

        var id = DocumentApi.NewId();
        var written = await api.PutAsync(id, new { n = 1 });
        var transacted = DocumentApi.NewId();
        var transaction = await api.TransactAsync(new { op = "put", collection = DocumentApi.Collection, id = transacted, document = new { n = 3 } });

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
        const string step = "the after-commit hook after its commit failed";
        var logged = logs.Messages.Any(message => message.Contains(step, StringComparison.Ordinal));
        await Assert.That(logged).IsEqualTo(insideCommit);

        store.AfterTursoCommit = store.AfterGroupCommit = null;
        var next = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(next, new { n = 2 })).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.GetAsync(next)).Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(2);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_failure_after_a_catalog_group_commit_still_fails_the_write(bool insideCommit)
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var system = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        static void Fail()
        {
            throw new IOException("the test fails the work after the commit");
        }

        if (insideCommit)
        {
            system.AfterTursoCommit = Fail;
        }
        else
        {
            system.AfterGroupCommit = Fail;
        }

        var id = DocumentApi.NewId();
        var sequence = system.CommitSequence;
        var feed = system.WaitForCommitAsync(sequence, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(async () => await system.WriteAsync(
            (transaction, token) => transaction.PutAsync("group_commit_probe", id, """{"n":1}"""u8.ToArray(), token), _ => true, CancellationToken.None));

        await feed.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(system.CommitSequence).IsGreaterThan(sequence);
        system.AfterTursoCommit = system.AfterGroupCommit = null;
        var read = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        await using (read)
        {
            await Assert.That(await read.GetAsync("group_commit_probe", id, CancellationToken.None)).IsNotNull();
        }
    }

    [Test]
    public async Task A_writer_close_that_fails_after_a_commit_still_releases_the_writer_and_the_admission()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        await store.WithWriterAsync(writer =>
        {
            writer.Connection.StateChange += (_, change) =>
            {
                if (change.CurrentState == ConnectionState.Closed)
                {
                    throw new IOException("the test fails closing the writer connection");
                }
            };
            return true;
        }, "a test", CancellationToken.None);
        store.StatisticsChanged();

        var created = await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "more" });
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.InternalServerError);

        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(engine.Io.Admitted == 0), TimeSpan.FromSeconds(10));
        await Assert.That((await api.SendAsync(HttpMethod.Get, $"{api.Collections}/more")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Accounting_that_fails_after_a_commit_answers_the_committed_write_and_logs_it()
    {
        var logs = new CapturedLogs();
        await using var api = await DocumentApi.StartAsync(services: services => services
            .AddSingleton<ILoggerProvider>(logs)
            .AddSingleton<IUsageMeter>(provider => new FailingWriteCounts(provider.GetRequiredService<UsageMeter>())));
        var id = DocumentApi.NewId();

        var written = await api.PutAsync(id, new { n = 1 });

        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(written.Json.GetProperty("offset").GetString()).IsNotEqualTo("0");
        var read = await api.GetAsync(id);
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await Assert.That(logs.Messages.Any(message => message.Contains("Accounting a committed write", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task A_failure_right_after_the_commit_still_wakes_what_waits_for_commits()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var sequence = store.CommitSequence;
        var committed = store.WaitForCommitAsync(sequence, CancellationToken.None);
        store.AfterTursoCommit = () => throw new IOException("the test fails the work right after the commit");

        var written = await api.PutAsync(DocumentApi.NewId(), new { n = 1 });

        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
        await committed.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(store.CommitSequence).IsGreaterThan(sequence);
        store.AfterTursoCommit = null;
    }

    [Test]
    public async Task A_log_call_that_throws_after_the_commit_skips_no_step_and_keeps_the_write_queue_working()
    {
        var logs = new CapturedLogs { ThrowOn = "after its commit failed" };
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<ILoggerProvider>(logs));
        var store = await StoreAsync(api);
        var sequence = store.CommitSequence;
        store.AfterTursoCommit = () => throw new IOException("the test fails the work right after the commit");

        var written = await api.PutAsync(DocumentApi.NewId(), new { n = 1 }).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(written.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(store.CommitSequence).IsGreaterThan(sequence);
        store.AfterTursoCommit = null;
        var next = await api.PutAsync(DocumentApi.NewId(), new { n = 2 }).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(next.Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task A_failure_right_after_creating_a_collection_still_serves_the_new_collection()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var id = DocumentApi.NewId();
        await Assert.That((await api.GetAsync(id)).Code).IsEqualTo("not_found");
        store.AfterTursoCommit = () => throw new IOException("the test fails the work right after the commit");

        var created = await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "fresh" });
        store.AfterTursoCommit = null;

        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.InternalServerError);
        var read = await api.SendAsync(HttpMethod.Get, $"{api.Collections}/fresh/documents/by-key?id={id}");
        await Assert.That((read.Status, read.Code)).IsEqualTo((HttpStatusCode.NotFound, "not_found"));
    }

    [Test]
    public async Task A_failure_right_after_an_index_build_still_moves_the_statistics_generation()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var generation = store.Statistics;
        var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await using (transaction)
        {
            await Assert.That(await transaction.CreateIndexAsync(
                new IndexDefinition("items_id", DocumentApi.Collection, [DocumentField.Id]), CancellationToken.None)).IsTrue();
            store.AfterTursoCommit = () => throw new IOException("the test fails the work right after the commit");
            await Assert.ThrowsAsync<IOException>(async () => await transaction.CommitAsync(CancellationToken.None));
            store.AfterTursoCommit = null;
        }

        await Assert.That(store.Statistics).IsEqualTo(generation + 1);
    }

    [Test]
    public async Task A_storage_thread_busy_with_a_long_read_does_not_hold_up_writes()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-threads"] = "1", services =>
        {
            var usage = services.Single(service => service.ImplementationType == typeof(UsageFlushService));
            services[services.IndexOf(usage)] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                ActivatorUtilities.CreateInstance<UsageFlushService>(provider, new FakeTimeProvider(DateTimeOffset.UtcNow)));
        });
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = 0 })).Status).IsEqualTo(HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();

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

    private static async Task<(IStorageTransaction Transaction, string At)> BeginWriteTransactionAsync(
        TursoDatabaseStore store, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        (await store.BeginTransactionAsync(write: true, CancellationToken.None, file, line), $"{Path.GetFileName(file)}:{line}");

    private static async Task<TursoDatabaseStore> StoreAsync(DocumentApi api)
    {
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        return (TursoDatabaseStore)(await engine.OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
    }

    private sealed class FailingWriteCounts(IUsageMeter inner) : IUsageMeter
    {
        public void RecordRead(TenantContext database, long units) => inner.RecordRead(database, units);

        public void RecordWrites(TenantContext database, int writes, long units) => throw new InvalidOperationException("the test fails counting committed writes");

        public void RecordOperation(TenantContext database, string kind, int count) => inner.RecordOperation(database, kind, count);

        public void RecordRequest(TenantContext database, string method, int status, TimeSpan elapsed, long received, long sent) =>
            inner.RecordRequest(database, method, status, elapsed, received, sent);

        public void RecordStatement(TenantContext database, string protocol, bool failed, TimeSpan elapsed) =>
            inner.RecordStatement(database, protocol, failed, elapsed);

        public void RecordClientBytes(TenantContext database, long received, long sent, DateTimeOffset? transferredAt = null) =>
            inner.RecordClientBytes(database, received, sent, transferredAt);

        public IDisposable TrackActive(Action report) => inner.TrackActive(report);

    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public string? ThrowOn { get; init; }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                logs.Messages.Enqueue(message);
                if (logs.ThrowOn is { } text && message.Contains(text, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("the test fails this log call");
                }
            }
        }
    }
}
