using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Common.Formatting;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Home
{
    [ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
    public sealed class ReportingJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task ReportingRequiresSignInAndRejectsForeignScopesBeforeProviderCalls()
        {
            using var client = fixture.CreateClient();
            foreach (var path in new[] { "/analytics", "/usage" })
            {
                using var anonymous = await client.GetAsync(path);
                await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
                await Assert.That(anonymous.Headers.Location!.OriginalString.StartsWith("/login?returnTo=", StringComparison.Ordinal)).IsTrue();
            }
            await DatabaseJourney.NativeWorkspaceAsync(client);
            using var before = JsonDocument.Parse(await client.GetStringAsync("/__test/reporting/probe"));
            var requestsBefore = before.RootElement.GetProperty("requests").GetArrayLength();
            foreach (var path in new[] { "/analytics?project=foreign-project", "/analytics?database=foreign-database" })
            {
                using var foreign = await client.GetAsync(path);
                await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
            // Valid region identities can be outside current placement (historical or newly admitted regions).
            using var invalidRegion = await client.GetAsync("/analytics?region=invalid_region");
            await Assert.That(invalidRegion.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var legacyBilling = await client.GetAsync("/usage?tab=billing&status=paid");
            await Assert.That(legacyBilling.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(legacyBilling.Headers.Location!.OriginalString).IsEqualTo("/organization?tab=billing&status=paid");
            using var noCsrf = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/usage/spending-cap", new() { ["spendCap"] = "100.00" });
            await Assert.That(noCsrf.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var after = JsonDocument.Parse(await client.GetStringAsync("/__test/reporting/probe"));
            await Assert.That(after.RootElement.GetProperty("requests").GetArrayLength()).IsEqualTo(requestsBefore);
        }

        [Test]
        public async Task SignedInUsagePageStatesTheReportedPeriod()
        {
            using var client = fixture.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            var html = await client.GetStringAsync("/usage");
            var match = Regex.Match(html, "id=\"usage-period\"[\\s\\S]*?<time datetime=\"([^\"]+)\"[\\s\\S]*?<time datetime=\"([^\"]+)\"");
            await Assert.That(match.Success).IsTrue();
            var from = DateTimeOffset.Parse(WebUtility.HtmlDecode(match.Groups[1].Value), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var to = DateTimeOffset.Parse(WebUtility.HtmlDecode(match.Groups[2].Value), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            await Assert.That(to > from).IsTrue();
            await Assert.That(html).Contains(ValueFormat.UtcDate(from)).And.Contains(ValueFormat.UtcDate(to));
        }
    }
}
