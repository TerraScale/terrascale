using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Analytics
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class AnalyticsContractJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task AnalyticsUsesSignedProjectTokenAndCurrentUnpagedWireContract()
        {
            var responseMode = 0;
            var producerRequests = 0;
            var observedPath = "";
            var observedQuery = "";
            var bearerPresent = false;
            await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, http =>
            {
                if (http.Request.Method != "GET" || !http.Request.Path.Value!.EndsWith("/analytics", StringComparison.Ordinal))
                {
                    return Task.FromResult<IResult?>(null);
                }

                Interlocked.Increment(ref producerRequests);
                observedPath = http.Request.Path.Value;
                observedQuery = http.Request.QueryString.Value ?? "";
                bearerPresent = http.Request.Headers["Authorization"].ToString().StartsWith("Bearer ", StringComparison.Ordinal);
                var mode = Volatile.Read(ref responseMode);
                if (mode >= 400)
                {
                    return Task.FromResult<IResult?>(Results.StatusCode(mode));
                }

                var projectId = http.Request.Path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAt(2);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var to = DateTimeOffset.FromUnixTimeSeconds((now / 60 + 1) * 60);
                var from = to.AddHours(-1);
                object body = mode == 0
                    ? new { projectId, range = "1h", from, to, bucketSeconds = 60, items = Array.Empty<object>() }
                    : new { projectId = "foreign-project", range = "1h", from, to, bucketSeconds = 60, items = Array.Empty<object>() };
                return Task.FromResult<IResult?>(Results.Json(body));
            });

            using var client = scenario.CreateClient();
            using var controls = scenario.CreateProviderClient();
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var identityResponse = await client.GetAsync("/api/v1/auth/me");
            await Assert.That(identityResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync());
            var organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
            var projectId = identity.RootElement.GetProperty("projectId").GetString()!;

            using (var page = await client.GetAsync("/analytics?range=1h"))
            {
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await page.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("No analytics measurements for these filters.");
                await Assert.That(html.Contains("Analytics unavailable", StringComparison.Ordinal)).IsFalse();
            }
            await Assert.That(producerRequests).IsEqualTo(1);
            await Assert.That(observedPath).IsEqualTo("/v1/projects/" + projectId + "/analytics");
            await Assert.That(observedQuery).IsEqualTo("?range=1h");
            await Assert.That(bearerPresent).IsTrue();
            var firstObservation = await ReadAsync(controls, "/__test/observations");
            await Assert.That(firstObservation.GetProperty("projectExchanges").GetInt32()).IsEqualTo(1);
            var request = firstObservation.GetProperty("projectRequests").EnumerateArray().Single();
            await Assert.That(request.GetProperty("path").GetString()).IsEqualTo(observedPath);
            await Assert.That(request.GetProperty("query").GetString()).IsEqualTo(observedQuery);
            await Assert.That(request.GetProperty("projectId").GetString()).IsEqualTo(projectId);
            await Assert.That(request.GetProperty("organizationId").GetString()).IsEqualTo(organizationId);
            await Assert.That(request.GetProperty("subject").GetString()).IsEqualTo(identity.RootElement.GetProperty("userId").GetString());
            await Assert.That(request.GetProperty("audience").GetString()).IsEqualTo("https://api.terrascale.tech");
            await Assert.That(request.GetProperty("scope").GetString()).IsEqualTo("databases:read");
            await Assert.That(request.GetProperty("tokenClass").GetString()).IsEqualTo("project");

            var sloBefore = await Platform.ServiceSloJourneyTests.ReadAsync(client);
            Volatile.Write(ref responseMode, 1);
            using (var page = await client.GetAsync("/analytics?range=1h"))
            {
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await page.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("Could not load analytics measurements.");
                await Assert.That(html.Contains("No analytics measurements for these filters.", StringComparison.Ordinal)).IsFalse();
            }
            await Platform.ServiceSloJourneyTests.CountsAsync(client, sloBefore.Good, sloBefore.Bad + 1);
            await Assert.That(producerRequests).IsEqualTo(2);
            var observations = await ReadAsync(controls, "/__test/observations");
            await Assert.That(observations.GetProperty("projectReportReads").GetInt32()).IsEqualTo(2);
            await Assert.That(observations.GetProperty("contractFailures").GetInt32()).IsEqualTo(0);

            // Real upstream refusals still render HTTP 200; only server failures burn availability.
            foreach (var status in new[] { 403, 429, 503 })
            {
                var before = await Platform.ServiceSloJourneyTests.ReadAsync(client);
                Volatile.Write(ref responseMode, status);
                using var page = await client.GetAsync("/analytics?range=1h");
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Could not load analytics measurements.");
                await Platform.ServiceSloJourneyTests.CountsAsync(client,
                    before.Good + (status < 500 ? 1 : 0), before.Bad + (status >= 500 ? 1 : 0));
            }
            await Assert.That(producerRequests).IsEqualTo(5);
        }

        private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
    }
}
