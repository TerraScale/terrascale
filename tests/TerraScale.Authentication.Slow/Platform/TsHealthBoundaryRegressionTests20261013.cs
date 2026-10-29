using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraScale.Authentication.Features.Observability;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TUnit.Core;

namespace TerraScale.Authentication.Slow.Platform;

// These are ts-auth's operational probes, not a separate ts-health service or ts-status.
public sealed class TsHealthBoundaryRegressionTests20261013
{
    [Test]
    public async Task Pending_startup_observation_is_not_ready_while_liveness_serves()
    {
        var health = new HeldHealth();
        var clock = new FakeTimeProvider();
        using var storage = new HttpClient(new OfflineStorage());
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton(storage);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IStorageHealth>(health);
        });
        try
        {
            await health.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.ServiceUnavailable, "not_ready");
            await AssertProbeAsync(server.Client, "/healthz", HttpStatusCode.OK, "ok");
            health.Reply.TrySetResult(new StorageHealthResult(true, "gru"));
            await WaitForReadyAsync(server.Client);
            await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.OK, "ready", "gru");
        }
        finally
        {
            health.Reply.TrySetResult(new StorageHealthResult(false, null));
        }
    }

    [Test]
    [Arguments("null-result")]
    [Arguments("malformed-json")]
    [Arguments("dependency-canceled")]
    [Arguments("partial-unhealthy")]
    [Arguments("http-failure")]
    public async Task Malformed_or_partial_dependency_results_fail_closed_and_recover(string scenario)
    {
        var marker = new Faker().Random.AlphaNumeric(32);
        var health = new SwitchingHealth(scenario, marker);
        var clock = new FakeTimeProvider();
        var logs = new ObservationLogs();
        using var storage = new HttpClient(new OfflineStorage());
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton(storage);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IStorageHealth>(health);
            services.AddSingleton<ILogger<ReadinessProbe>>(logs);
        });

        var failure = await logs.Failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (scenario == "dependency-canceled")
        {
            await Assert.That(failure).Contains("System.OperationCanceledException");
            await Assert.That(failure.Contains("timed out", StringComparison.Ordinal)).IsFalse();
        }

        await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.ServiceUnavailable, "not_ready", scenario == "partial-unhealthy" ? "gru" : null, marker);
        await AssertProbeAsync(server.Client, "/healthz", HttpStatusCode.OK, "ok", privateValue: marker);
        health.Recover();
        clock.Advance(TimeSpan.FromSeconds(5));
        await logs.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.OK, "ready", "gru", marker);
        await Assert.That(health.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Readiness_deadline_cancels_the_dependency_without_interrupting_liveness()
    {
        var health = new HeldHealth();
        var clock = new FakeTimeProvider();
        var logs = new ObservationLogs();
        using var storage = new HttpClient(new OfflineStorage());
        await using var server = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton(storage);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IStorageHealth>(health);
            services.AddSingleton<ILogger<ReadinessProbe>>(logs);
        });
        try
        {
            await health.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertProbeAsync(server.Client, "/healthz", HttpStatusCode.OK, "ok");
            clock.Advance(TimeSpan.FromSeconds(8));
            await health.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(await logs.Failed.Task.WaitAsync(TimeSpan.FromSeconds(30))).Contains("timed out");
            await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.ServiceUnavailable, "not_ready");
            await AssertProbeAsync(server.Client, "/healthz", HttpStatusCode.OK, "ok");
        }
        finally
        {
            health.Reply.TrySetResult(new StorageHealthResult(false, null));
        }
    }

    private static async Task WaitForReadyAsync(HttpClient client)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        // Poll real HTTP responses, without sleeping or advancing the background probe's clock.
        while (true)
        {
            using var response = await client.GetAsync(new Uri("/readyz", UriKind.Relative), deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }
    }

    private static async Task AssertProbeAsync(HttpClient client, string path, HttpStatusCode expected, string status, string? region = null, string? privateValue = null)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);

        await Assert.That(response.StatusCode).IsEqualTo(expected);
        await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo(status);
        string[] expectedKeys = region is null ? ["status", "version"] : ["status", "version", "region"];
        await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).ToList())
            .IsEquivalentTo(expectedKeys);
        if (region is not null)
        {
            await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo(region);
        }

        if (privateValue is not null)
        {
            await Assert.That(text.Contains(privateValue, StringComparison.Ordinal)).IsFalse();
        }
    }

    private sealed class OfflineStorage : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline health regression fixture"));
    }

    private sealed class HeldHealth : IStorageHealth
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<StorageHealthResult> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                return await Reply.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class SwitchingHealth(string scenario, string marker) : IStorageHealth
    {
        private bool recovered;
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        public void Recover() => Volatile.Write(ref this.recovered, true);

        public Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.calls);
            if (Volatile.Read(ref this.recovered))
            {
                return Task.FromResult(new StorageHealthResult(true, "gru"));
            }

            return scenario switch
            {
                "null-result" => Task.FromResult<StorageHealthResult>(null!),
                "malformed-json" => Task.FromException<StorageHealthResult>(new JsonException(marker)),
                "dependency-canceled" => Task.FromException<StorageHealthResult>(new OperationCanceledException(marker)),
                "partial-unhealthy" => Task.FromResult(new StorageHealthResult(false, "gru")),
                "http-failure" => Task.FromException<StorageHealthResult>(new HttpRequestException(marker)),
                _ => throw new InvalidOperationException("Unknown synthetic health scenario"),
            };
        }
    }

    private sealed class ObservationLogs : ILogger<ReadinessProbe>
    {
        public TaskCompletionSource<string> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Failed.TrySetResult(formatter(state, exception));
            }
            else if (logLevel == LogLevel.Information)
            {
                Recovered.TrySetResult();
            }
        }
    }
}
