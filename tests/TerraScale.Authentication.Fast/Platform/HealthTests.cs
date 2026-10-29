using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Observability;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

public sealed class HealthTests
{
    [Test]
    public async Task Liveness_reports_ok_and_the_service_version()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var response = await server.Client.GetAsync("/healthz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("ok");
        await Assert.That(body.GetProperty("version").GetString()).StartsWith("0.1.0-alpha");
        // Anonymous health never names the build revision (ServiceVersion.Value carries it as +<commit> in CI builds).
        await Assert.That(body.GetProperty("version").GetString()).DoesNotContain("+");
    }

    [Test]
    public async Task Prometheus_metrics_are_served_only_on_the_metrics_listener()
    {
        await using var server = await AuthTestServer.StartAsync();
        using var metrics = new HttpClient { BaseAddress = server.MetricsAddress };
        using var health = await server.Client.GetAsync("/healthz");

        using var scrape = await metrics.GetAsync("/metrics");
        var text = await scrape.Content.ReadAsStringAsync();
        using var publicScrape = await server.Client.GetAsync("/metrics");
        using var otherPath = await metrics.GetAsync("/healthz");

        await Assert.That(scrape.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(text).Contains("# TYPE http_server_request_duration_seconds");
        await Assert.That(publicScrape.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(otherPath.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Readiness_fails_closed_when_storage_is_not_reachable()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var response = await server.Client.GetAsync("/readyz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("not_ready");
        await Assert.That(body.TryGetProperty("region", out _)).IsFalse();
    }

    [Test]
    public async Task Readiness_reports_the_storage_region_once_the_authority_answers()
    {
        var health = new FakeStorageHealth(healthy: true, region: "gru");
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));
        await Assert.That(await ReadinessWait.ReadyWithinAsync(server.Client, TimeSpan.FromSeconds(10))).IsTrue();

        using var response = await server.Client.GetAsync("/readyz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("ready");
        await Assert.That(body.GetProperty("region").GetString()).IsEqualTo("gru");
    }

    [Test]
    public async Task Readiness_treats_a_hung_authority_as_not_ready()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromSeconds(30));
        var logs = new ReadinessLogs();
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IStorageHealth>(health);
        });

        // The first pass gives up at its eight-second deadline and records not ready.
        await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(health.Calls == 1), TimeSpan.FromSeconds(15))).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(8));
        await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(logs.Entries.Length == 1), TimeSpan.FromSeconds(15))).IsTrue();
        await Assert.That(logs.Entries[0].Message).IsEqualTo("Readiness check failed: timed out");
        using var response = await server.Client.GetAsync("/readyz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("not_ready");
    }

    [Test]
    public async Task Readiness_answers_from_the_background_loop_not_one_storage_check_per_request()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var health = new FakeStorageHealth(healthy: false);
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IStorageHealth>(health);
        });
        await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(health.Calls > 0), TimeSpan.FromSeconds(15))).IsTrue();
        var before = health.Calls;

        for (var request = 0; request < 50; request++)
        {
            using var response = await server.Client.GetAsync("/readyz");
        }

        // The clock stands still, so no pass can land in the burst; a check per request would add fifty.
        await Assert.That(health.Calls).IsEqualTo(before);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(health.Calls > before), TimeSpan.FromSeconds(15))).IsTrue();
    }

    [Test]
    public async Task A_closure_storage_failure_arriving_during_shutdown_does_not_fault_the_host()
    {
        using var transport = new ShutdownClosureStorage();
        using var storage = new HttpClient(transport);
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton(storage);
            services.AddSingleton<IStorageHealth>(new FakeStorageHealth(healthy: false));
        });
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using var ready = await server.Client.GetAsync("/readyz");
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);

        await server.App.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await server.App.Services.GetRequiredService<ClosureWorker>().ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(transport.FailedDuringShutdown.Task.IsCompletedSuccessfully).IsTrue();
    }

    private sealed class ShutdownClosureStorage : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FailedDuringShutdown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/" + AuthStore.ClosureJobCollection + "/", StringComparison.Ordinal))
            {
                var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => canceled.TrySetResult());
                this.Entered.TrySetResult();
                await canceled.Task;
                this.FailedDuringShutdown.TrySetResult();
                throw ApiException.Unavailable(new IOException("synthetic storage closed during shutdown"));
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    [Test]
    public async Task A_probe_that_times_out_warns_once_without_a_stack_trace()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromSeconds(30));
        var logs = new ReadinessLogs();
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            // The global Information rule outranks MinLevel, so this provider rule keeps Debug records.
            services.PostConfigure<LoggerFilterOptions>(options => options.Rules.Add(new LoggerFilterRule(typeof(ReadinessLogs).FullName, null, LogLevel.Debug, null)));
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IStorageHealth>(health);
        });

        // Two passes: one at startup, one five seconds after it ends; each ends at its eight-second deadline.
        await TimedOutPassAsync(1);
        clock.Advance(TimeSpan.FromSeconds(5));
        await TimedOutPassAsync(2);

        using var response = await server.Client.GetAsync("/readyz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(logs.Levels).IsEqualTo("Warning,Debug");
        await Assert.That(logs.Entries.Any(entry => entry.Exception is not null)).IsFalse();

        // Waits for the pass to reach the authority, then moves the clock to its deadline and waits for its log line.
        async Task TimedOutPassAsync(int pass)
        {
            await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(health.Calls == pass), TimeSpan.FromSeconds(15))).IsTrue();
            clock.Advance(TimeSpan.FromSeconds(8));
            await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(logs.Entries.Length == pass), TimeSpan.FromSeconds(15))).IsTrue();
            var check = server.App.Services.GetRequiredService<ReadinessProbe>().OutstandingCheck!;
            // The fake's finally precedes completion of Task.Run's unwrapped task, which the no-stacking guard checks.
            try
            {
                await check.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (OperationCanceledException) when (check.IsCanceled)
            {
                // The eight-second probe deadline cancelled this check.
            }

            await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(health.Completed == pass), TimeSpan.FromSeconds(15))).IsTrue();
        }
    }

    [Test]
    public async Task A_readiness_failure_warns_once_and_its_recovery_is_reported_once()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var health = new ToggledStorageHealth();
        var logs = new ReadinessLogs();
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            // The global Information rule outranks MinLevel, so this provider rule keeps Debug records.
            services.PostConfigure<LoggerFilterOptions>(options => options.Rules.Add(new LoggerFilterRule(typeof(ReadinessLogs).FullName, null, LogLevel.Debug, null)));
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IStorageHealth>(health);
        });

        await Assert.That(await ReadinessWait.ReadyWithinAsync(server.Client, TimeSpan.FromSeconds(10))).IsTrue();
        health.Healthy = false;
        // A ready replica rides out fewer than three failed passes (one slow write must not drain every replica), then
        // reports not ready.
        await NextPassAsync(1);
        using var tolerated = await server.Client.GetAsync("/readyz");
        await NextPassAsync(2);
        using var stillTolerated = await server.Client.GetAsync("/readyz");
        await NextPassAsync(3);
        using var failing = await server.Client.GetAsync("/readyz");
        health.Healthy = true;
        await NextPassAsync(4);
        using var recovered = await server.Client.GetAsync("/readyz");

        await Assert.That(tolerated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stillTolerated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(failing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(logs.Levels).IsEqualTo("Warning,Debug,Debug,Information");
        await Assert.That(logs.Entries.First().Message).IsEqualTo("Readiness check failed: storage reported unhealthy");

        // Moves the clock to the next pass and waits until that pass has logged.
        async Task NextPassAsync(int entries)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(logs.Entries.Length == entries), TimeSpan.FromSeconds(15))).IsTrue();
        }
    }

    [Test]
    public async Task Routed_requests_do_not_log_framework_request_lines_at_information()
    {
        var logs = new CapturingLogs();
        await using var server = await AuthTestServer.StartAsync(services: services => services.AddSingleton<ILoggerProvider>(logs));

        using var response = await server.Client.GetAsync("/healthz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(logs.Text).DoesNotContain("Executing endpoint");
        await Assert.That(logs.Text).DoesNotContain("Setting HTTP status code");
    }

    private sealed class ToggledStorageHealth : IStorageHealth
    {
        public bool Healthy { get; set; } = true;

        public Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StorageHealthResult(this.Healthy, this.Healthy ? "gru" : null));
    }

    private sealed class ReadinessLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<ReadinessEntry> entries = new();

        public ReadinessEntry[] Entries => this.entries.ToArray();

        // Levels of the readiness probe's own log lines, in the order they were written.
        public string Levels => string.Join(',', this.entries.Select(entry => entry.Level));

        public ILogger CreateLogger(string categoryName) => new Logger(this.entries, categoryName);

        public void Dispose()
        {
        }

        public sealed record ReadinessEntry(LogLevel Level, string Message, Exception? Exception);

        private sealed class Logger(ConcurrentQueue<ReadinessEntry> entries, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => category == typeof(ReadinessProbe).FullName;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == typeof(ReadinessProbe).FullName)
                {
                    entries.Enqueue(new ReadinessEntry(logLevel, formatter(state, exception), exception));
                }
            }
        }
    }

    [Test]
    public async Task A_failing_storage_probe_is_logged_without_its_message()
    {
        const string secret = "storage-secret-61b2c0";
        var logs = new CapturingLogs();
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IStorageHealth>(new ThrowingStorageHealth(
                new HttpRequestException($"connect https://user:{secret}@db.example/?key={secret}", new IOException($"password={secret}"))));
        });

        // The first pass runs at startup and logs its failure.
        await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(logs.Text.Contains("Readiness check failed", StringComparison.Ordinal)), TimeSpan.FromSeconds(15))).IsTrue();
        using var response = await server.Client.GetAsync("/readyz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(logs.Text).Contains("Readiness check failed");
        await Assert.That(logs.Text).Contains("System.IO.IOException (message withheld)");
        await Assert.That(logs.Text).DoesNotContain(secret);
    }

    private sealed class ThrowingStorageHealth(Exception exception) : IStorageHealth
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        public Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.calls);
            return Task.FromException<StorageHealthResult>(exception);
        }
    }

    [Test]
    public async Task A_log_provider_that_throws_never_breaks_readiness(CancellationToken cancellationToken)
    {
        var clock = new SettledReadinessClock();
        var health = new ThrowingStorageHealth(new IOException("storage down"));
        var logs = new ThrowingWarningLogs();
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            // Only the readiness loop uses this clock, so another worker's timer cannot signal a pass.
            services.AddSingleton(provider => new ReadinessProbe(health, clock, provider.GetRequiredService<ILogger<ReadinessProbe>>()));
            services.PostConfigure<LoggerFilterOptions>(options => options.Rules.Add(new LoggerFilterRule(typeof(ThrowingWarningLogs).FullName, null, LogLevel.Debug, null)));
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IStorageHealth>(health);
        });

        await Assert.That(await logs.NextPassAsync(cancellationToken)).IsEqualTo(LogLevel.Warning);
        using var first = await server.Client.GetAsync("/readyz", cancellationToken);
        // Two more settled passes prove the failed warning did not stop the loop.
        foreach (var pass in new[] { 2, 3 })
        {
            // Logging happens before Task.Delay registers the next timer. Advancing at the log
            // alone can miss that timer and leave the loop waiting forever on an unmoving clock.
            await clock.NextPassAsync(cancellationToken);
            await Assert.That(await logs.NextPassAsync(cancellationToken)).IsEqualTo(LogLevel.Debug);
            await Assert.That(health.Calls).IsEqualTo(pass);
        }

        using var second = await server.Client.GetAsync("/readyz", cancellationToken);

        // Not ready, and still answering: the failed log write did not fault the background loop.
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class SettledReadinessClock : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> intervals = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            // The other timer is the eight-second probe deadline. Signal only the inter-pass wait,
            // after the timer exists, so advancing time cannot race its registration.
            if (dueTime == TimeSpan.FromSeconds(5) && period == Timeout.InfiniteTimeSpan)
            {
                this.intervals.Writer.TryWrite(dueTime);
            }

            return timer;
        }

        public async Task NextPassAsync(CancellationToken cancellationToken) =>
            this.Advance(await this.intervals.Reader.ReadAsync(cancellationToken));
    }

    private sealed class ThrowingWarningLogs : ILoggerProvider
    {
        private readonly Channel<LogLevel> passes = Channel.CreateUnbounded<LogLevel>();

        public ValueTask<LogLevel> NextPassAsync(CancellationToken cancellationToken) => this.passes.Reader.ReadAsync(cancellationToken);

        public ILogger CreateLogger(string categoryName) => new Logger(this.passes.Writer, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(ChannelWriter<LogLevel> passes, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == typeof(ReadinessProbe).FullName && logLevel is LogLevel.Warning or LogLevel.Debug)
                {
                    passes.TryWrite(logLevel);
                }

                if (logLevel == LogLevel.Warning)
                {
                    throw new InvalidOperationException("log provider failure");
                }
            }
        }
    }
}
