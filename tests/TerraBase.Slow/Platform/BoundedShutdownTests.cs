using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TerraBase.Engine.Turso;
using TerraBase.Slow.Documents;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class BoundedShutdownTests
{
    [Test]
    public async Task Shutdown_rejects_new_work_while_health_probes_keep_their_response()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var held = new HeldShutdown();
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddHostedService(_ => held));
        using var healthy = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
        await Assert.That(healthy.StatusCode).IsEqualTo(HttpStatusCode.OK);
        server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await held.Stopping.Task.WaitAsync(token);
        try
        {
            using var refused = await server.Client.GetAsync(new Uri("/v1/databases", UriKind.Relative), token);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            using var probe = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), token);
            await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await probe.Content.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
        }
        finally
        {
            held.Release.TrySetResult();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Active_changes_stop_before_storage_closes_within_budget(bool stalledBackup)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var bucket = new FakeS3();
        await using var api = await DocumentApi.StartAsync(bucket.Configure, services =>
        {
            bucket.Route(services);
            ManualBackupHosting.Configure(services);
        });
        var server = api.Server;
        var lifetime = server.Services.GetRequiredService<IHostApplicationLifetime>();
        var completion = server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var hostedBudget = server.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
        var streams = new List<HttpResponseMessage>();
        var readers = new List<StreamReader>();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            for (var index = 0; index < 4; index++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api.DatabasePath() + "/changes?cursor=now", UriKind.Relative));
                var response = await api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                streams.Add(response);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
                readers.Add(reader);
                await ReadEventAsync(reader, "cursor", token);
            }

            var text = new Faker().Lorem.Sentence();
            (await api.PutAsync(DocumentApi.NewId(), new { text })).Status.ShouldBe(HttpStatusCode.Created);
            foreach (var reader in readers)
            {
                await ReadEventAsync(reader, "change", token);
            }

            await server.Services.GetRequiredService<ContinuousBackup>().RunOnceAsync(token);
            var committed = DocumentApi.NewId();
            (await api.PutAsync(committed, new { text })).Status.ShouldBe(HttpStatusCode.Created);
            if (stalledBackup)
            {
                bucket.BeforePut = async (key, cancellation) =>
                {
                    if (lifetime.ApplicationStopping.IsCancellationRequested && key.Contains("/wal/", StringComparison.Ordinal))
                    {
                        blocked.TrySetResult();
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                        }
                        finally
                        {
                            cancelled.TrySetResult();
                        }
                    }
                };
            }
            var elapsed = Stopwatch.StartNew();
            lifetime.StopApplication();
            await completion.WaitAsync(TimeSpan.FromSeconds(45), token);
            TestContext.Current!.OutputWriter.WriteLine($"shutdown_seconds={elapsed.Elapsed.TotalSeconds:F3} stalled_backup={stalledBackup}");
            await Assert.That(elapsed.Elapsed).IsLessThan(Shutdown.Budget);
            await Assert.That(hostedBudget).IsEqualTo(Shutdown.HostedBudget);
            await Assert.That(engine.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
            await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsFalse();
            if (stalledBackup)
            {
                await Assert.That(blocked.Task.IsCompleted).IsTrue();
                await Assert.That(cancelled.Task.IsCompleted).IsTrue();
            }

            foreach (var reader in readers)
            {
                while (await reader.ReadLineAsync(token) is not null)
                {
                }
            }

            await using var restarted = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = server.DataDirectory);
            using var saved = await restarted.Client.GetAsync(new Uri(api.Document(committed), UriKind.Relative), token);
            await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.GetProperty("document").GetProperty("text").GetString()).IsEqualTo(text);
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }

            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    [Test]
    public async Task A_changes_write_in_progress_at_shutdown_completes_before_clean_eof()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var barrier = new ChangesWriteBarrier();
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IStartupFilter>(barrier));
        var lifetime = api.Server.Services.GetRequiredService<IHostApplicationLifetime>();
        var completion = api.Server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api.DatabasePath() + "/changes?cursor=now", UriKind.Relative));
        var pending = api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            await barrier.Entered.Task.WaitAsync(token);
            lifetime.StopApplication();
        }
        finally
        {
            barrier.Release.TrySetResult();
        }

        using var response = await pending;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        await ReadEventAsync(reader, "cursor", token);
        while (await reader.ReadLineAsync(token) is not null)
        {
        }

        await completion.WaitAsync(TimeSpan.FromSeconds(45), token);
        await Assert.That(engine.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
        await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_changes_producer_cancelled_before_the_linked_stream_token_ends_the_response_cleanly()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartIsolatedAsync();
        var lifetime = api.Server.Services.GetRequiredService<IHostApplicationLifetime>();
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(), token))!;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api.DatabasePath() + "/changes?cursor=now", UriKind.Relative));
        using var response = await api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        await ReadEventAsync(reader, "cursor", token);

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15), token))
            {
                throw new TimeoutException("shutdown callback ordering check was not released");
            }
        });
        var stopping = Task.Run(lifetime.StopApplication, token);
        try
        {
            await entered.Task.WaitAsync(token);
            await using (var transaction = await store.BeginTransactionAsync(write: true, token))
            {
                _ = await transaction.PutAsync(DocumentApi.Collection, DocumentApi.NewId(), "{\"n\":1}"u8.ToArray(), token);
                _ = await transaction.CommitAsync(token);
            }

            using var drain = CancellationTokenSource.CreateLinkedTokenSource(token);
            drain.CancelAfter(TimeSpan.FromSeconds(10));
            while (await reader.ReadLineAsync(drain.Token) is not null)
            {
            }
        }
        finally
        {
            release.Set();
            await stopping;
        }

        await api.Server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task.WaitAsync(TimeSpan.FromSeconds(45), token);
        await Assert.That(engine.CleanupCompletion.IsCompletedSuccessfully).IsTrue();
        await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_healthy_reader_can_retire_its_intent_after_the_shutdown_grace_expires()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(settings =>
        {
            settings["shutdown-grace-seconds"] = "1";
        });
        var directory = api.Server.DataDirectory;
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var replica = engine.ReplicaId;
        var tenant = await api.TenantOfAsync();
        var store = (await engine.OpenDatabaseAsync(tenant, token))!;
        await using var reader = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await reader.GetAsync(DocumentApi.Collection, id, token)).IsNotNull();
        try
        {
            await engine.DisposeAsync();
            await Assert.That(engine.Io.Closed).IsTrue();
            await Assert.That(engine.CleanupCompletion.IsCompleted).IsFalse();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(directory)).IsTrue();
        }
        finally
        {
            await reader.DisposeAsync();
        }

        await engine.CleanupCompletion.WaitAsync(token);
        await Assert.That(engine.Ledger.FailedClosed).IsNull();
        await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
        await using var restarted = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
        var reopened = restarted.Services.GetRequiredService<TursoStorageEngine>();
        await Assert.That(reopened.ReplicaId).IsEqualTo(replica).Because("healthy reader cleanup must record a clean stop");
        var saved = (await reopened.OpenDatabaseAsync(tenant, token))!;
        await using var check = await saved.BeginTransactionAsync(write: false, token);
        await Assert.That(await check.GetAsync(DocumentApi.Collection, id, token)).IsNotNull();
        await Assert.That(reopened.Ledger.Refusal((TursoDatabaseStore)saved)).IsNull();
    }

    private static async Task ReadEventAsync(StreamReader reader, string expected, CancellationToken cancellationToken)
    {
        var found = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            found |= line == "event: " + expected;
            if (line.Length == 0)
            {
                await Assert.That(found).IsTrue();
                return;
            }
        }

        throw new EndOfStreamException("changes stream ended before its event");
    }

    private sealed class ChangesWriteBarrier : IStartupFilter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path.Value?.EndsWith("/changes", StringComparison.Ordinal) == true)
                {
                    context.Response.OnStarting(async () =>
                    {
                        Entered.TrySetResult();
                        await Release.Task.WaitAsync(context.RequestAborted);
                    });
                }

                await continuation(context);
            });
            next(app);
        };
    }

    private sealed class HeldShutdown : IHostedService
    {
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Stopping.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
