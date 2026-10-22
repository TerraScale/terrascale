using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseHealthParityTests
{
    [Test]
    [Arguments(200, "ready")]
    [Arguments(503, "unavailable")]
    public async Task Anonymous_public_probes_preserve_unavailable_bodies(int status, string state)
    {
        var calls = new List<string>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (request.Headers.Authorization is not null) throw new InvalidOperationException("probe leaked a credential");
            if (request.RequestUri.Scheme != "https") throw new InvalidOperationException("probe lost TLS");
            var probeState = request.RequestUri.AbsolutePath == "/healthz" ? "ok"
                : request.RequestUri.AbsolutePath == "/backupz" && status == 200 ? "shipping" : state;
            var probeStatus = request.RequestUri.AbsolutePath == "/healthz" ? HttpStatusCode.OK : (HttpStatusCode)status;
            return Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"status":"{{{probeState}}}","version":"0.0.1","region":"gru","storageFormat":"10","revision":null,"machine":"test-machine"}""", probeStatus));
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseHealthClient(http, new Uri("https://gru.api.terrascale.tech"));
        var health = await client.GetHealthAsync();
        var readiness = await client.GetReadinessAsync();
        var backup = await client.GetBackupHealthAsync();
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(readiness.Body.Status).IsEqualTo(state);
        await Assert.That(backup.Body.Machine).IsEqualTo("test-machine");
        await Assert.That(backup.Body.Revision).IsNull();
        await Assert.That(calls).IsEquivalentTo(["/healthz", "/readyz", "/backupz"]);
    }

    [Test]
    public async Task Replication_degraded_status_and_unknown_lag_are_not_replaced_with_success_or_zero()
    {
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/health/replication") throw new InvalidOperationException("wrong route");
            return Task.FromResult(TerraBaseIntegrationFixture.Json("""{"region":"gru","status":"degraded","maxLagSeconds":null,"peers":[{"region":"fra","status":"degraded","lagSeconds":null}]}"""));
        });
        using var http = new HttpClient(handler);
        var health = await new TerraBaseHealthClient(http, new Uri("https://api.example.test")).GetReplicationHealthAsync();
        await Assert.That(health.Status).IsEqualTo("degraded");
        await Assert.That(health.MaxLagSeconds).IsNull();
        await Assert.That(health.Peers.Single().LagSeconds).IsNull();
    }

    [Test]
    public async Task Unexpected_probe_error_still_surfaces_and_is_sent_once()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("""{"error":{"code":"misdirected_request","message":"wrong region"}}""", HttpStatusCode.MisdirectedRequest));
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() => new TerraBaseHealthClient(http, new Uri("https://api.example.test")).GetReadinessAsync());
        await Assert.That(calls).IsEqualTo(1);
    }
}
