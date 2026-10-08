using System.Net;
using System.Runtime.CompilerServices;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class StoppedHostTests
{
    [Test]
    public async Task A_stopped_server_that_used_its_outbound_clients_becomes_unreachable()
    {
        // Stopped hosts must leave memory: hundreds of them, each holding its metric streams, stalled the suites in
        // garbage collection (TS-760). Timers that libraries start once per process, such as OpenTelemetry's, capture the
        // execution context of the host that started them, which keeps that one host reachable: let another host start them.
        _ = await StopAfterAuthenticatedRequestAsync();
        var services = await StopAfterAuthenticatedRequestAsync();

        // Shutdown work still finishing releases the host within a few seconds; a rotated client handler held it for
        // its two-minute lifetime. One full collection a second: more would stall the tests running alongside.
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            GC.Collect();
            return !services.IsAlive;
        }, TimeSpan.FromSeconds(30));
    }

    // Not inlined, so no local of the caller keeps the server alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StopAfterAuthenticatedRequestAsync()
    {
        await using var api = await ManagementApi.StartAsync();
        // The bearer token is checked against keys the server fetched through its ts-auth client.
        using var response = await api.Client.GetAsync(ManagementApi.Uri("/v1/management/organizations"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return new WeakReference(api.Server.Services);
    }
}
