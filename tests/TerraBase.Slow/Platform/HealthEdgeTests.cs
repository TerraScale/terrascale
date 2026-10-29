using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Testing;
using TUnit.Core.Enums;

namespace TerraBase.Slow.Platform;

public sealed class HealthEdgeTests
{

    [Test]
    public async Task Readyz_is_unavailable_once_the_storage_engine_is_closed()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);

        // What shutdown does to the engine, while the listeners still serve.
        await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready");
        await server.Services.GetRequiredService<IStorageEngine>().DisposeAsync();

        await AssertProbeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable");
        await AssertProbeAsync(client, "/healthz", HttpStatusCode.OK, "ok");
    }

    [Test]
    public async Task Readyz_is_unavailable_when_shutdown_begins_during_a_probe()
    {
        using var tls = new ClientTls();
        var shutdown = new HeldShutdown();
        var providerDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ProbeClock();
        var barrier = new ProbeBarrier();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services =>
        {
            ConfigureReadiness(services, clock, barrier);
            services.AddHostedService(_ => shutdown);
            services.AddSingleton<IStartupFilter>(_ => new ObserveProviderDisposal(providerDisposed));
        });
        using var client = tls.CreateClient(server.ClientAddress!);
        var provider = server.Services;
        var engine = provider.GetRequiredService<TursoStorageEngine>();
        var pending = AssertProbeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable");
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.DeadlineCreated.Task.WaitAsync(TimeSpan.FromSeconds(30));
            server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await shutdown.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await pending;
            await Assert.That(engine.ReadinessProbeTask.IsCompleted).IsFalse();
            barrier.Release.TrySetResult();
            await engine.ReadinessProbeTask.WaitAsync(TimeSpan.FromSeconds(30));
            // Shutdown ended the HTTP wait even while the shared disk probe was still held.
            await Assert.That(await engine.IsReadyAsync(CancellationToken.None)).IsTrue();
        }
        finally
        {
            barrier.Release.TrySetResult();
            try
            {
                await engine.ReadinessProbeTask.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                shutdown.Release.TrySetResult();
            }
        }

        // Program has disposed the root provider before await-using cleans up the harness.
        await providerDisposed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(() => { _ = provider.GetRequiredService<IStorageEngine>(); }).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Readyz_keeps_one_disk_probe_after_timeout_or_cancellation_and_recovers(bool cancelCaller)
    {
        var requests = new ObserveCanceledRequest();
        var clock = new ProbeClock(expectedWaiters: 2);
        var barrier = new ProbeBarrier();
        await using var server = await TerraBaseTestServer.StartAsync(services: services =>
        {
            ConfigureReadiness(services, clock, barrier);
            services.AddSingleton<IStartupFilter>(requests);
        });
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        using var cancellation = new CancellationTokenSource();
        var pending = server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), cancellation.Token);
        try
        {
            await requests.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.DeadlineCreated.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var diskProbe = engine.ReadinessProbeTask;
            if (cancelCaller)
            {
                await cancellation.CancelAsync();
                await Assert.That(async () => { using var response = await pending; }).Throws<OperationCanceledException>();
                await requests.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }

            var overlapping = AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable");
            await clock.WaitersReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(overlapping.IsCompleted).IsFalse();
            clock.Expire();
            await overlapping;
            if (!cancelCaller)
            {
                using var response = await pending;
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            }

            await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable");
            await Assert.That(ReferenceEquals(diskProbe, engine.ReadinessProbeTask)).IsTrue();
            await Assert.That(barrier.Calls).IsEqualTo(1);
            barrier.Release.TrySetResult();
            await diskProbe.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.OK, "ready");
            await Assert.That(barrier.Calls).IsEqualTo(1);
            clock.Advance(TimeSpan.FromSeconds(1));
            await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.OK, "ready");
            await Assert.That(barrier.Calls).IsEqualTo(2);
            await Assert.That(Directory.EnumerateFiles(server.DataDirectory, "*.probe")).IsEmpty();
        }
        finally
        {
            barrier.Release.TrySetResult();
            await engine.ReadinessProbeTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Test]
    public async Task Readyz_concurrent_requests_await_one_probe_when_the_clock_starts_at_zero()
    {
        using var tls = new ClientTls();
        var clock = new ProbeClock(expectedWaiters: 16);
        var barrier = new ProbeBarrier();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services => ConfigureReadiness(services, clock, barrier));
        using var client = tls.CreateClient(server.ClientAddress!);
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var pending = Enumerable.Range(0, 16).Select(_ => AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready")).ToArray();
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.WaitersReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(pending.All(request => !request.IsCompleted)).IsTrue();
            await Assert.That(barrier.Calls).IsEqualTo(1);
            barrier.Release.TrySetResult();
            await Task.WhenAll(pending);
            await Assert.That(barrier.Calls).IsEqualTo(1);
        }
        finally
        {
            barrier.Release.TrySetResult();
            await engine.ReadinessProbeTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Test]
    [Arguments(null, 1)]
    [Arguments("2", 2)]
    [Arguments("60", 60)]
    public async Task Readyz_reuses_completed_disk_checks_for_the_configured_ttl(string? configured, int seconds)
    {
        using var tls = new ClientTls();
        var clock = new ProbeClock();
        var probes = new ProbeBarrier();
        probes.Release.TrySetResult();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            if (configured is not null)
            {
                settings["readiness-cache-seconds"] = configured;
            }
        }, services => ConfigureReadiness(services, clock, probes));
        using var client = tls.CreateClient(server.ClientAddress!);

        // Sequential requests finish the previous check before sending the next one, so single-flight alone cannot pass.
        for (var request = 0; request < 16; request++)
        {
            await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready");
        }

        await Assert.That(probes.Calls).IsEqualTo(1);
        clock.Advance(TimeSpan.FromMilliseconds((seconds * 1000) - 1));
        await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.OK, "ready");
        await Assert.That(probes.Calls).IsEqualTo(1);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready")));
        await Assert.That(probes.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Readyz_shutdown_bypasses_a_cached_healthy_result()
    {
        using var tls = new ClientTls();
        var shutdown = new HeldShutdown();
        var clock = new ProbeClock();
        var probes = new ProbeBarrier();
        probes.Release.TrySetResult();
        // Freeze the TTL at zero so the successful result stays cached throughout shutdown.
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply, services =>
        {
            ConfigureReadiness(services, clock, probes);
            services.AddHostedService(_ => shutdown);
        });
        using var client = tls.CreateClient(server.ClientAddress!);
        await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready");
        await Assert.That(probes.Calls).IsEqualTo(1);

        server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await shutdown.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await AssertProbeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable");
            await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable");
            await AssertProbeAsync(client, "/healthz", HttpStatusCode.OK, "ok");
            await Assert.That(probes.Calls).IsEqualTo(1);
        }
        finally
        {
            shutdown.Release.TrySetResult();
        }
    }

    // Removing the data directory under the running engine, unlike a permission change, also holds for a test run as root;
    // it needs a platform that unlinks open files. TerraBase ships only for Linux.
    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task Readyz_is_unavailable_when_the_data_directory_is_not_writable()
    {
        var root = Directory.CreateTempSubdirectory("terrabase-readyz-").FullName;
        try
        {
            var dataDirectory = Path.Combine(root, "data");
            using var tls = new ClientTls();
            var clock = new ProbeClock();
            var probes = new ProbeBarrier();
            probes.Release.TrySetResult();
            await using var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["data-directory"] = dataDirectory;
            }, services => ConfigureReadiness(services, clock, probes));
            using var client = tls.CreateClient(server.ClientAddress!);
            await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready");

            var identityPath = Path.Combine(dataDirectory, TerraBase.Engine.Turso.ReplicaIdentityFile.FileName);
            var identity = await File.ReadAllBytesAsync(identityPath);

            // A lost volume: nothing can be created in the data directory any more.
            Directory.Delete(dataDirectory, recursive: true);

            await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready");
            await Assert.That(probes.Calls).IsEqualTo(1);
            clock.Advance(TimeSpan.FromSeconds(1));
            await AssertProbeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable");
            await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable");
            await Assert.That(probes.Calls).IsEqualTo(2);
            await AssertProbeAsync(client, "/healthz", HttpStatusCode.OK, "ok");

            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllBytesAsync(identityPath, identity);
            await AssertProbeAsync(client, "/readyz", HttpStatusCode.ServiceUnavailable, "unavailable");
            await Assert.That(probes.Calls).IsEqualTo(2);
            clock.Advance(TimeSpan.FromSeconds(1));
            await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready");
            await Assert.That(probes.Calls).IsEqualTo(3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Readyz_leaves_no_probe_files_in_the_data_directory()
    {
        // Only the public requests create probes while this test compares the directory before and after them.
        await using var server = await TerraBaseTestServer.StartAsync(services: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider()));
        var before = Directory.GetFileSystemEntries(server.DataDirectory).Order(StringComparer.Ordinal).ToList();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.OK, "ready")));
        await server.Services.GetRequiredService<TursoStorageEngine>().ReadinessProbeTask.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(Directory.GetFileSystemEntries(server.DataDirectory).Order(StringComparer.Ordinal).ToList()).IsEquivalentTo(before);
    }

    // Set the hooks before the engine can be observed by the background pressure sampler or any HTTP request.
    private static void ConfigureReadiness(IServiceCollection services, ProbeClock clock, ProbeBarrier barrier)
    {
        // Keep background pressure sampling independent of the manually advanced readiness clock.
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        var registration = services.Single(service => service.ServiceType == typeof(TursoStorageEngine));
        services.Remove(registration);
        services.AddSingleton(provider =>
        {
            var engine = (TursoStorageEngine)registration.ImplementationFactory!(provider);
            engine.ReadinessClock = clock;
            engine.BeforeReadinessProbe = barrier.HoldAsync;
            return engine;
        });
    }

    // Registered after the application's services, so it stops before the listeners do: shutdown has begun, and the
    // listeners keep serving, until the test releases it.
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

    // The factory registration gives DI ownership; startup eagerly resolves the filter.
    private sealed class ObserveProviderDisposal(TaskCompletionSource disposed) : IStartupFilter, IDisposable
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => next;

        public void Dispose() => disposed.TrySetResult();
    }

    private sealed class ProbeBarrier
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HoldAsync()
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            return Release.Task;
        }
    }

    // Observe cancellation reaching the real server, rather than just HttpClient ceasing to wait.
    private sealed class ObserveCanceledRequest : IStartupFilter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path == "/readyz")
                {
                    Entered.TrySetResult();
                }

                try
                {
                    await continuation(context);
                }
                finally
                {
                    if (context.Request.Path == "/readyz" && context.RequestAborted.IsCancellationRequested)
                    {
                        Finished.TrySetResult();
                    }
                }
            });
            next(app);
        };
    }

    private sealed class ProbeClock(int expectedWaiters = 1) : TimeProvider
    {
        private long _timestamp;
        private readonly Lock _gate = new();
        private readonly List<ITimer> _deadlines = [];

        public TaskCompletionSource DeadlineCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WaitersReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime <= TimeSpan.Zero || dueTime > TimeSpan.FromSeconds(2) || period != Timeout.InfiniteTimeSpan)
            {
                throw new InvalidOperationException("the readiness deadline must not exceed two seconds");
            }

            var deadline = base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
            lock (_gate)
            {
                _deadlines.Add(deadline);
                DeadlineCreated.TrySetResult();
                if (_deadlines.Count >= expectedWaiters)
                {
                    WaitersReady.TrySetResult();
                }
            }

            return deadline;
        }

        public void Expire()
        {
            Advance(TimeSpan.FromSeconds(2));
            List<ITimer> deadlines;
            lock (_gate)
            {
                deadlines = [.. _deadlines];
            }

            foreach (var deadline in deadlines)
            {
                // A caller may have cancelled and disposed its timer; the remaining waiters still expire.
                _ = deadline.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        public void Advance(TimeSpan interval) => Interlocked.Add(ref _timestamp, (long)interval.TotalMilliseconds);
    }

    // The whole body is node metadata with no tenant data. Anonymous callers on the public listener (the one that sets
    // X-TerraBase-Region) get status, version and region only; build detail (storage format, revision, node) needs the
    // internal listener's peer credential.
    private static async Task AssertProbeAsync(HttpClient client, string path, HttpStatusCode status, string state)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var anonymous = response.Headers.Contains("X-TerraBase-Region");

        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo(state);
        await Assert.That(body.RootElement.GetProperty("version").GetString()).IsEqualTo("0.1.0");
        await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("test-region");
        // The source commit the build was made from: the checkout's HEAD here, TERRABASE_REVISION in a CI image.
        await Assert.That(ServiceVersion.Revision).Matches("^[0-9a-f]{40}$");
        if (anonymous)
        {
            await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).ToList()).IsEquivalentTo(["status", "version", "region"]);
            await Assert.That(text.Contains(ServiceVersion.Revision!, StringComparison.Ordinal)).IsFalse();
        }
        else
        {
            await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).ToList()).IsEquivalentTo(["status", "version", "region", "storageFormat", "revision", "machine"]);
            await Assert.That(body.RootElement.GetProperty("revision").GetString()).IsEqualTo(ServiceVersion.Revision);
            await Assert.That(body.RootElement.GetProperty("machine").GetString()).IsEqualTo("local");
            await Assert.That(body.RootElement.GetProperty("storageFormat").GetString()).IsEqualTo(StorageFormat.Id);
        }

        if (client.BaseAddress?.Scheme == Uri.UriSchemeHttps)
        {
            await Assert.That(response.Headers.GetValues("X-TerraBase-Region").Single()).IsEqualTo("test-region");
        }
    }
}
