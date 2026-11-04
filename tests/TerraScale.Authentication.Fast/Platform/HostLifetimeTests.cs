using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class HostLifetimeTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_stopped_server_is_collectable_without_waiting_for_http_handler_expiry()
    {
        var (services, metrics) = await StartAndStopAsync();
        // A handler with a lifetime would keep its factory, and so the whole container, on a timer for two minutes,
        // and forever while a singleton still holds its client.
        for (var collection = 0; collection < 10 && (IsAlive(services) || IsAlive(metrics)); collection++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            await Task.Yield();
        }

        await Assert.That(IsAlive(services)).IsFalse();
        await Assert.That(IsAlive(metrics)).IsFalse();
    }

    // TryGetTarget's temporary strong reference must leave the stack before the next collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference)
        where T : class => reference.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(WeakReference<IServiceProvider> Services, WeakReference<MeterProvider> Metrics)> StartAndStopAsync()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var services = oidc.Server.App.Services;
        // Long-lived singletons hold factory clients, as the GitHub Actions key cache and SSO discovery do.
        _ = services.GetRequiredService<GitHubActionsKeys>();
        _ = services.GetRequiredService<SsoConnections>().Discovery("https://idp.example.test");
        using var metrics = await oidc.Client.GetAsync(new Uri(oidc.Server.MetricsAddress, "/metrics"));
        await Assert.That(metrics.IsSuccessStatusCode).IsTrue();
        return (new WeakReference<IServiceProvider>(services), new WeakReference<MeterProvider>(services.GetRequiredService<MeterProvider>()));
    }
}
