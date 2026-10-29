using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Platform
{
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
                using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
                { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
                using var ready = await client.GetAsync("/readyz");
                await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using (var receipt = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()))
                {
                    await Assert.That(receipt.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
                }
                // ts-status probes this original URL; public bodies no longer carry the build revision.
                using var legacy = await client.GetAsync("/health/ready");
                await Assert.That(legacy.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using var probe = JsonDocument.Parse(await legacy.Content.ReadAsStringAsync());
                await Assert.That(probe.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
                await Assert.That(probe.RootElement.TryGetProperty("version", out _)).IsFalse();
                using var live = await client.GetAsync("/health/live");
                await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using var login = await client.GetAsync("/login");
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
                await Assert.That(login.Headers.Location!.OriginalString).StartsWith("/__test/oidc?state=");
                using var anonymous = await client.GetAsync("/api/v1/auth/me");
                await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }
        }

        [Test]
        public async Task HostedSignInRedirectUsesThePublicOriginForwardedByTheLoopbackEdge()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(DashboardE2EFixture.FindRoot());
            using var client = scenario.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/login");
            request.Headers.Host = "dash.terrascale.tech";
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-For", "203.0.113.7");
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            var authorize = response.Headers.Location!;
            await Assert.That(authorize.GetLeftPart(UriPartial.Path)).IsEqualTo("https://auth.terrascale.tech/terrascale/oauth/authorize");
            var redirect = authorize.Query.TrimStart('?').Split('&').Single(field => field.StartsWith("redirect_uri=", StringComparison.Ordinal));
            await Assert.That(Uri.UnescapeDataString(redirect["redirect_uri=".Length..])).IsEqualTo("https://dash.terrascale.tech/login/callback");
        }
    }
}
