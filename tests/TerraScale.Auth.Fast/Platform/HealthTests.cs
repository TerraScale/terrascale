using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.Auth.Features.Observability;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Platform;

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

    // A wall-clock bound: run alone so CPU-heavy tests (Argon2id hashing) cannot starve the
    // shared thread pool and delay the probe's deadline.
    [Test]
    [NotInParallel]
    public async Task Readiness_treats_a_hung_authority_as_not_ready()
    {
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromSeconds(30));
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        // The first pass gives up at its two-second deadline and records not ready.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        using var response = await server.Client.GetAsync("/readyz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("not_ready");
    }

    [Test]
    public async Task Readiness_answers_from_the_background_loop_not_one_storage_check_per_request()
    {
        var health = new FakeStorageHealth(healthy: false);
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));
        await Task.Delay(TimeSpan.FromSeconds(1));
        var before = health.Calls;

        for (var request = 0; request < 50; request++)
        {
            using var response = await server.Client.GetAsync("/readyz");
        }

        // At most one pass can land in the burst; a check per request would add fifty.
        await Assert.That(health.Calls - before).IsLessThanOrEqualTo(1);
        await Task.Delay(ReadinessWait.NextPass);
        await Assert.That(health.Calls).IsGreaterThan(before);
    }

    [Test]
    [NotInParallel]
    public async Task A_probe_that_times_out_warns_once_without_a_stack_trace()
    {
        var logs = new ReadinessLogs();
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            // The global Information rule outranks MinLevel, so this provider rule keeps Debug records.
            services.PostConfigure<LoggerFilterOptions>(options => options.Rules.Add(new LoggerFilterRule(typeof(ReadinessLogs).FullName, null, LogLevel.Debug, null)));
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IStorageHealth>(new FakeStorageHealth(healthy: true, delay: TimeSpan.FromSeconds(30)));
        });

        // Two passes: one at startup, one five seconds later; each ends at its two-second deadline.
        await Task.Delay(TimeSpan.FromSeconds(8));
        using var response = await server.Client.GetAsync("/readyz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(logs.Levels).IsEqualTo("Warning,Debug");
        await Assert.That(logs.Entries.Any(entry => entry.Exception is not null)).IsFalse();
    }

    [Test]
    public async Task A_readiness_failure_warns_once_and_its_recovery_is_reported_once()
    {
        var health = new ToggledStorageHealth();
        var logs = new ReadinessLogs();
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            // The global Information rule outranks MinLevel, so this provider rule keeps Debug records.
            services.PostConfigure<LoggerFilterOptions>(options => options.Rules.Add(new LoggerFilterRule(typeof(ReadinessLogs).FullName, null, LogLevel.Debug, null)));
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IStorageHealth>(health);
        });

        await Assert.That(await ReadinessWait.ReadyWithinAsync(server.Client, TimeSpan.FromSeconds(10))).IsTrue();
        health.Healthy = false;
        await Task.Delay(ReadinessWait.NextPass);
        using var failing = await server.Client.GetAsync("/readyz");
        await Task.Delay(ReadinessWait.NextPass);
        using var stillFailing = await server.Client.GetAsync("/readyz");
        health.Healthy = true;
        await Task.Delay(ReadinessWait.NextPass);
        using var recovered = await server.Client.GetAsync("/readyz");

        await Assert.That(failing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(stillFailing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(logs.Levels).IsEqualTo("Warning,Debug,Information");
        await Assert.That(logs.Entries.First().Message).IsEqualTo("Readiness check failed: storage reported unhealthy");
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

        public IReadOnlyCollection<ReadinessEntry> Entries => this.entries.ToArray();

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
        await Task.Delay(TimeSpan.FromSeconds(1));
        using var response = await server.Client.GetAsync("/readyz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(logs.Text).Contains("Readiness check failed");
        await Assert.That(logs.Text).Contains("System.IO.IOException (message withheld)");
        await Assert.That(logs.Text).DoesNotContain(secret);
    }

    private sealed class ThrowingStorageHealth(Exception exception) : IStorageHealth
    {
        public Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken) => Task.FromException<StorageHealthResult>(exception);
    }

    [Test]
    public async Task A_log_provider_that_throws_never_breaks_readiness()
    {
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton<ILoggerProvider>(new ThrowingWarningLogs());
            services.AddSingleton<IStorageHealth>(new ThrowingStorageHealth(new IOException("storage down")));
        });

        using var first = await server.Client.GetAsync("/readyz");
        await Task.Delay(ReadinessWait.NextPass);
        using var second = await server.Client.GetAsync("/readyz");

        // Not ready, and still answering: the failed log write did not fault the background loop.
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class ThrowingWarningLogs : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger();

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    throw new InvalidOperationException("log provider failure");
                }
            }
        }
    }
}
