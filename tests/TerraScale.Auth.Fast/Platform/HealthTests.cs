using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        await Assert.That(body.GetProperty("version").GetString()).StartsWith("0.1.0");
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

        using var response = await server.Client.GetAsync("/readyz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("ready");
        await Assert.That(body.GetProperty("region").GetString()).IsEqualTo("gru");
    }

    [Test]
    public async Task Readiness_treats_a_hung_authority_as_not_ready()
    {
        var health = new FakeStorageHealth(healthy: true, delay: TimeSpan.FromSeconds(30));
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        var started = DateTimeOffset.UtcNow;
        using var response = await server.Client.GetAsync("/readyz");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        // The deadline is two seconds; allow scheduling slack but nothing near a second probe.
        await Assert.That(DateTimeOffset.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(3.5));
    }

    [Test]
    public async Task Readiness_probes_storage_again_after_the_one_second_cache()
    {
        var health = new FakeStorageHealth(healthy: false);
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton<IStorageHealth>(health));

        using var first = await server.Client.GetAsync("/readyz");
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        using var second = await server.Client.GetAsync("/readyz");

        await Assert.That(health.Calls).IsEqualTo(2);
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
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        using var second = await server.Client.GetAsync("/readyz");

        // Not ready, and still answering: the failed log write did not fault the shared probe.
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
