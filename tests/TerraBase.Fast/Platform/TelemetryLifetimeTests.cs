using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class TelemetryLifetimeTests
{
    [Test]
    public async Task A_disposed_host_releases_telemetry_without_waiting_for_http_handler_expiry()
    {
        const string ChildFlag = "TERRABASE_TEST_TELEMETRY_LIFETIME_CHILD";
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            // Full collections must not pause unrelated fixtures in the parallel suite.
            await IsolatedTestChild.RunAsync(ChildFlag, "TerraBase.Fast.Platform", nameof(TelemetryLifetimeTests),
                nameof(A_disposed_host_releases_telemetry_without_waiting_for_http_handler_expiry));
            return;
        }

        var (metrics, http) = await ExerciseAndDisposeHostAsync();
        // The factory's default handler expiry is two minutes; disposed host buffers must be released now.
        await TerraBaseTestServer.EventuallyAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            return Task.FromResult(!IsAlive(metrics) && !IsAlive(http));
        }, timeout: TimeSpan.FromSeconds(5));
    }

    // TryGetTarget's temporary strong reference must leave the stack before the next collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference) where T : class => reference.TryGetTarget(out _);

    private static async Task<(WeakReference<MeterProvider> Metrics, WeakReference<IHttpClientFactory> Http)> ExerciseAndDisposeHostAsync()
    {
        var disposals = new HandlerDisposals();
        (WeakReference<MeterProvider> Metrics, WeakReference<IHttpClientFactory> Http) references;
        await using (var api = await ManagementApi.StartAsync(services: services =>
            services.AddHttpClient(TsAuthKeys.HttpClientName).AddHttpMessageHandler(() => new DisposalProbe(disposals))))
        {
            // Management's real HTTPS requests also exercise the server's named ts-auth client pipeline.
            await api.CreateManagedDatabaseAsync();
            var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
            meter.CreateCounter<long>("terrabase_test_host_lifetime").Add(1);
            var metrics = await api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative),
                TestContext.Current!.Execution.CancellationToken);
            await Assert.That(metrics).Contains("terrabase_test_host_lifetime_total");
            await Assert.That(metrics).Contains("http_server_request_duration_seconds");

            references = (new WeakReference<MeterProvider>(api.Server.Services.GetRequiredService<MeterProvider>()),
                new WeakReference<IHttpClientFactory>(api.Server.Services.GetRequiredService<IHttpClientFactory>()));
        }

        // Collection alone would miss an undisposed pipeline after disabling its expiry timer.
        await Assert.That(Volatile.Read(ref disposals.Count)).IsEqualTo(1);
        return references;
    }

    private sealed class HandlerDisposals
    {
        internal int Count;
    }

    private sealed class DisposalProbe(HandlerDisposals disposals) : DelegatingHandler
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Interlocked.Increment(ref disposals.Count);
            }
        }
    }
}
