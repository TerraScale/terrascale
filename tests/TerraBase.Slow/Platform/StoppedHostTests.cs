using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class StoppedHostTests
{
    [Test]
    public async Task A_stopped_server_that_used_its_outbound_clients_becomes_unreachable()
    {
        const string ChildFlag = "TERRABASE_TEST_STOPPED_HOST_CHILD";
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            // Forced collections must not pause the other integration tests (TS-760).
            await IsolatedTestChild.RunAsync(typeof(StoppedHostTests), nameof(A_stopped_server_that_used_its_outbound_clients_becomes_unreachable), ChildFlag);
            return;
        }

        var (services, metrics, clients) = await StopAfterAuthenticatedRequestAsync();
        var cancellationToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;

        // A rotated handler's expiry timer retained the provider and its metric streams for two minutes.
        try
        {
            // One full collection a second while shutdown work finishes.
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                GC.Collect();
                if (!IsAlive(services) && !IsAlive(metrics) && !IsAlive(clients))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), token);
                return false;
            }, TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Stopped host still reachable after 30 seconds: services={IsAlive(services)}, metrics={IsAlive(metrics)}, clients={IsAlive(clients)}.", error);
        }
    }

    // Not inlined, so no local of the caller keeps the server alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<IServiceProvider> Services, WeakReference<MeterProvider> Metrics, WeakReference<IHttpClientFactory> Clients)>
        StopAfterAuthenticatedRequestAsync()
    {
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var transport = await issuer.ListenAsync();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            TestIssuer.Configure(settings);
        }, transport.Route);
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));
        // The bearer token is checked against keys the server fetched through its ts-auth client.
        using var response = await client.GetAsync(ManagementApi.Uri("/v1/management/organizations"), TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(issuer.DiscoveryFetches).IsGreaterThan(0);
        await Assert.That(issuer.JwksFetches).IsGreaterThan(0);
        return (new(server.Services), new(server.Services.GetRequiredService<MeterProvider>()), new(server.Services.GetRequiredService<IHttpClientFactory>()));
    }

    // TryGetTarget's temporary strong reference must leave the stack before the next collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference) where T : class => reference.TryGetTarget(out _);
}
