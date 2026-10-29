using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Usage
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class UsageReplicationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task ProjectReplicationUsesSignedProjectTokenAndCurrentWireShape()
        {
            var reportMode = 0;
            var requests = 0;
            var observedPath = "";
            var observedRange = "";
            var bearerPresent = false;
            await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, http =>
            {
                if (http.Request.Method != "GET" || !http.Request.Path.Value!.EndsWith("/replication", StringComparison.Ordinal))
                {
                    return Task.FromResult<IResult?>(null);
                }

                Interlocked.Increment(ref requests);
                observedPath = http.Request.Path.Value;
                observedRange = http.Request.Query["range"].ToString();
                bearerPresent = http.Request.Headers["Authorization"].ToString().StartsWith("Bearer ", StringComparison.Ordinal);
                var projectId = http.Request.Path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAt(2);
                var utc = DateTimeOffset.UtcNow;
                var to = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
                var from = to.AddHours(-24);
                object body = Volatile.Read(ref reportMode) == 0
                    ? new { projectId, range = "24h", from, to, bucketSeconds = 3600, items = Array.Empty<object>() }
                    : new { projectId = "foreign-project", range = "24h", from, to, bucketSeconds = 3600, items = Array.Empty<object>() };
                return Task.FromResult<IResult?>(Results.Json(body));
            });

            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using (var seeded = await producer.PostAsJsonAsync("/__test/control", new { command = "internal-add-database" }))
            {
                await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            using var identityResponse = await client.GetAsync("/api/v1/auth/me");
            await Assert.That(identityResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync());
            var projectId = identity.RootElement.GetProperty("projectId").GetString()!;

            using (var page = await client.GetAsync("/usage?range=24h"))
            {
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await page.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("No replication observations are available for this project.");
                await Assert.That(html.Contains("Replication unavailable", StringComparison.Ordinal)).IsFalse();
            }
            await Assert.That(requests).IsEqualTo(1);
            await Assert.That(observedPath).IsEqualTo("/v1/projects/" + projectId + "/replication");
            await Assert.That(observedRange).IsEqualTo("24h");
            await Assert.That(bearerPresent).IsTrue();

            Volatile.Write(ref reportMode, 1);
            using (var page = await client.GetAsync("/usage?range=24h"))
            {
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await page.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("Replication unavailable");
                await Assert.That(html.Contains("No replication observations are available for this project.", StringComparison.Ordinal)).IsFalse();
            }
            await Assert.That(requests).IsEqualTo(2);
            using var observedResponse = await producer.GetAsync("/__test/observations");
            await Assert.That(observedResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var observed = JsonDocument.Parse(await observedResponse.Content.ReadAsStringAsync());
            var root = observed.RootElement;
            // Scoped usage, monthly usage and replication on both pages share the session's one exchanged read token.
            await Assert.That(root.GetProperty("projectExchanges").GetInt32()).IsEqualTo(1);
            foreach (var requested in root.GetProperty("projectScopeExchanges").EnumerateArray())
            {
                await Assert.That(requested.GetProperty("projectId").GetString()).IsEqualTo(projectId);
                await Assert.That(requested.GetProperty("organizationId").GetString()).IsEqualTo(identity.RootElement.GetProperty("organizationId").GetString());
                await Assert.That(requested.GetProperty("requested").EnumerateArray().Select(scope => scope.GetString()).ToArray())
                    .IsEquivalentTo(new string?[] { "databases:read" });
                await Assert.That(requested.GetProperty("granted").EnumerateArray().Select(scope => scope.GetString()).ToArray())
                    .IsEquivalentTo(new string?[] { "databases:read" });
            }
            await Assert.That(root.GetProperty("projectReportReads").GetInt32()).IsEqualTo(2);
            var exchange = root.GetProperty("projectRequests").EnumerateArray().First();
            await Assert.That(exchange.GetProperty("path").GetString()).IsEqualTo("/v1/projects/" + projectId + "/replication");
            await Assert.That(exchange.GetProperty("projectId").GetString()).IsEqualTo(projectId);
            await Assert.That(exchange.GetProperty("audience").GetString()).IsEqualTo("https://api.terrascale.tech");
            await Assert.That(exchange.GetProperty("scope").GetString()).IsEqualTo("databases:read");
            await Assert.That(exchange.GetProperty("tokenClass").GetString()).IsEqualTo("project");
        }
    }
}
