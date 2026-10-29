using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Accounts;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseListCountsJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("partial")]
        [Arguments("negative")]
        [Arguments("overflow")]
        [Arguments("duplicate")]
        public async Task CompleteCountsAllowSharedCollectionNamesWhileAnyFailureSuppressesTotals(string failure)
        {
            var measurement = "";
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                var path = http.Request.Path.Value!;
                if (http.Request.Method != "GET" || !path.StartsWith("/v1/projects/", StringComparison.Ordinal) ||
                    !path.EndsWith("/schema", StringComparison.Ordinal))
                {
                    return Task.FromResult<IResult?>(null);
                }

                var first = path.EndsWith("/databases/count-first/schema", StringComparison.Ordinal);
                var state = Volatile.Read(ref measurement);
                if (!first && state == "partial")
                {
                    return Task.FromResult<IResult?>(Results.Json(new { error = new { code = "unavailable" } }, statusCode: 503));
                }

                if (!first && state == "duplicate")
                {
                    return Task.FromResult<IResult?>(Results.Json(new
                { items = new[] { new { name = "items", documents = 5L, bytes = 50L }, new { name = "items", documents = 1L, bytes = 10L } } }));
                }

                if (state == "zero")
                {
                    return Task.FromResult<IResult?>(Results.Json(new { items = new[] { new { name = "items", documents = 0L, bytes = 0L } } }));
                }

                var documents = state == "overflow" ? first ? long.MaxValue : 1L : !first && state == "negative" ? -1L : first ? 7L : 5L;
                return Task.FromResult<IResult?>(Results.Json(new { items = new[] { new { name = "items", documents, bytes = first ? 70L : 50L } } }));
            });
            using var client = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var empty = await PageAsync(client);
            await Assert.That(empty).Contains("No databases yet.");
            await Assert.That(empty.Contains("id=\"database-document-totals\"", StringComparison.Ordinal)).IsFalse();
            using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            // Test admission permits two databases; it does not simulate a paid checkout.
            using var admission = await producer.PostAsJsonAsync("/__test/control", new
            { command = "plan", organizationId = identity.RootElement.GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId });
            await Assert.That(admission.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            foreach (var name in new[] { "count-first", "count-second" })
            {
                using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
                { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }
            var known = await TotalsAsync(replica);
            await Assert.That(known).Contains(">12</dd>");
            await Assert.That(known).Contains("Across 2 collections, 120 B");
            await Assert.That(known.Contains("Try again", StringComparison.Ordinal)).IsFalse();
            Volatile.Write(ref measurement, failure);
            var unknown = await TotalsAsync(replica);
            await Assert.That(unknown).Contains(">Unknown</dd>");
            await Assert.That(unknown).Contains("No zero count is inferred.");
            await Assert.That(unknown.Contains("Across ", StringComparison.Ordinal)).IsFalse();
            await Assert.That(unknown).Contains("href=\"/databases?range=24h\">Try again</a>");
            Volatile.Write(ref measurement, "");
            await Assert.That(await TotalsAsync(replica)).Contains(">12</dd>");
            // A real zero stays a factual 0, not the outage state.
            Volatile.Write(ref measurement, "zero");
            var zero = await TotalsAsync(replica);
            await Assert.That(zero).Contains(">0</dd>");
            await Assert.That(zero).Contains("Across 2 collections, 0 B");
            await Assert.That(zero.Contains("Unknown", StringComparison.Ordinal)).IsFalse();

            static async Task<string> TotalsAsync(HttpClient instance)
            {
                var html = await PageAsync(instance);
                var panel = Regex.Match(html, "<div[^>]*id=\"database-document-totals\"[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
                await Assert.That(panel.Success).IsTrue();
                return panel.Groups[1].Value;
            }

            static async Task<string> PageAsync(HttpClient instance)
            {
                using var response = await instance.GetAsync("/databases");
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            }
        }
    }
}
