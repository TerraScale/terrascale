using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Platform;

public sealed class HostLifecycleJourneyTests
{
    [Test]
    public async Task SimultaneousApplicationPairsBindDistinctLivePortsAndServeTheirOwnRequests()
    {
        await using var first = new DashboardE2EFixture();
        await using var second = new DashboardE2EFixture();
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
        var origins = new[] { first.Origin, first.ReplicaOrigin, second.Origin, second.ReplicaOrigin };
        await Assert.That(origins.Select(origin => origin.Port).Distinct().Count()).IsEqualTo(4)
            .Because("Every actual listener receives a free port, including simultaneous runs from the same checkout.");
        foreach (var origin in origins)
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
            using var ready = await client.GetAsync("/readyz");
            await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await ready.Content.ReadAsStringAsync()).IsEqualTo("ok");
            // ts-status probes this original URL and reads the commit after '+' in version.
            using var legacy = await client.GetAsync("/health/ready");
            await Assert.That(legacy.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var probe = JsonDocument.Parse(await legacy.Content.ReadAsStringAsync());
            await Assert.That(probe.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
            await Assert.That(probe.RootElement.GetProperty("version").GetString()!.Split('+')[^1]).Matches("^[0-9a-f]{40}$");
            using var live = await client.GetAsync("/health/live");
            await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var login = await client.GetAsync("/login");
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await login.Content.ReadAsStringAsync()).Contains("Sign in");
            using var anonymous = await client.GetAsync("/api/v1/auth/me");
            await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }
}
