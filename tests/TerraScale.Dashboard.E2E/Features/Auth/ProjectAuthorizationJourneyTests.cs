using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class ProjectAuthorizationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task AnalyticsExchangesFreshProjectAuthorityAndRejectsInvalidTokensWithoutLosingSession()
        {
            var reportReads = 0;
            var reportPaths = new List<string>();
            await using var scenario = await AuthProviderScenario.StartReportingAsync(fixture.Root, http =>
            {
                if (http.Request.Method != "GET" || !http.Request.Path.Value!.EndsWith("/analytics", StringComparison.Ordinal))
                {
                    return Task.FromResult<IResult?>(null);
                }

                reportReads++;
                reportPaths.Add(http.Request.Path.Value);
                var projectId = http.Request.Path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];
                var now = DateTimeOffset.UtcNow;
                var to = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
                return Task.FromResult<IResult?>(Results.Json(new { projectId, range = "24h", from = to.AddHours(-24), to, bucketSeconds = 3600, items = Array.Empty<object>() }));
            });

            using var provider = scenario.CreateProviderClient();
            using var client = scenario.CreateClient();
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var initialIdentity = await client.GetAsync("/api/v1/auth/me");
            await Assert.That(initialIdentity.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var identity = JsonDocument.Parse(await initialIdentity.Content.ReadAsStringAsync());
            var userId = identity.RootElement.GetProperty("userId").GetString()!;
            var projectIdFromDashboard = identity.RootElement.GetProperty("projectId").GetString()!;
            var organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;

            // The global issuer's signed project token authorizes
            // the selected project's report request through real HTTP.
            using (var page = await client.GetAsync("/analytics?range=24h"))
            {
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await page.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("No analytics measurements for these filters.");
                await Assert.That(html).DoesNotContain("Analytics unavailable");
            }
            await Assert.That(reportReads).IsEqualTo(1);
            await Assert.That(reportPaths.Single()).IsEqualTo($"/v1/projects/{projectIdFromDashboard}/analytics");
            // The session reuses that exchanged token until shortly before it expires (15 minutes here); past it,
            // every load asks ts-auth again, and a refused or invalid answer is never reused.
            using (var advanced = await provider.PostAsJsonAsync("/__test/control", new { command = "advance", seconds = 900 }))
            {
                await Assert.That(advanced.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            // These producer responses violate one required project-token claim,
            // token-response rule, or fresh authorization decision. None may
            // reach the report producer.
            string[] invalidProjectResponses =
            [
                "project-missing-scope",
                "project-response-scope",
                "project-foreign-project",
                "project-foreign-organization",
                "project-wrong-audience",
                "project-wrong-subject",
                "project-wrong-client",
                "project-bad-signature",
                "project-identity-token",
                "project-expired",
                "project-too-long",
                "project-refreshtoken",
                "project-idtoken",
                "project-refused"
            ];
            foreach (var fault in invalidProjectResponses)
            {
                await SetFaultAsync(provider, fault);
                using var page = await client.GetAsync("/analytics?range=24h");
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(fault);
                var html = await page.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("Analytics unavailable").Because(fault);
                await Assert.That(html).Contains("Could not load analytics measurements. Apply the filters to retry.").Because(fault);
                await Assert.That(html).DoesNotContain("invalid project authorization").Because(fault);
                await Assert.That(reportReads).IsEqualTo(1).Because(fault + " must not reach the reporting producer");

                using var stillSignedIn = await client.GetAsync("/api/v1/auth/me");
                await Assert.That(stillSignedIn.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(fault + " must preserve the dashboard session");
                using var currentIdentity = JsonDocument.Parse(await stillSignedIn.Content.ReadAsStringAsync());
                await Assert.That(currentIdentity.RootElement.GetProperty("userId").GetString()).IsEqualTo(userId).Because(fault);
                await Assert.That(currentIdentity.RootElement.GetProperty("organizationId").GetString()).IsEqualTo(organizationId).Because(fault);
                await Assert.That(currentIdentity.RootElement.GetProperty("projectId").GetString()).IsEqualTo(projectIdFromDashboard).Because(fault);
            }

            using var observationsResponse = await provider.GetAsync("/__test/observations");
            await Assert.That(observationsResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var observations = JsonDocument.Parse(await observationsResponse.Content.ReadAsStringAsync());
            await Assert.That(observations.RootElement.GetProperty("projectExchanges").GetInt32()).IsEqualTo(invalidProjectResponses.Length + 1);
            await Assert.That(observations.RootElement.GetProperty("projectReportReads").GetInt32()).IsEqualTo(1);
            var reportRequests = observations.RootElement.GetProperty("projectRequests").EnumerateArray().ToArray();
            await Assert.That(reportRequests.Length).IsEqualTo(1);
            await Assert.That(reportRequests[0].GetProperty("subject").GetString()).IsEqualTo(userId);
            await Assert.That(reportRequests[0].GetProperty("organizationId").GetString()).IsEqualTo(organizationId);
            await Assert.That(reportRequests[0].GetProperty("projectId").GetString()).IsEqualTo(projectIdFromDashboard);
            await Assert.That(reportRequests[0].GetProperty("scope").GetString()).IsEqualTo("databases:read");
            await Assert.That(reportRequests[0].GetProperty("audience").GetString()).IsEqualTo("https://api.terrascale.tech");
            await Assert.That(reportRequests[0].GetProperty("tokenClass").GetString()).IsEqualTo("project");

            // Clearing the issuer fault demonstrates recoverability without a new
            // login: the original dashboard session can obtain fresh authority.
            await SetFaultAsync(provider, "");
            using (var recovered = await client.GetAsync("/analytics?range=24h"))
            {
                await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await recovered.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("No analytics measurements for these filters.");
                await Assert.That(html).DoesNotContain("Analytics unavailable");
            }
            await Assert.That(reportReads).IsEqualTo(2);
            using var recoveredIdentity = await client.GetAsync("/api/v1/auth/me");
            await Assert.That(recoveredIdentity.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        private static async Task SetFaultAsync(HttpClient client, string value)
        {
            using var response = await client.PostAsJsonAsync("/__test/control", new { command = "fault", value });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }
}
