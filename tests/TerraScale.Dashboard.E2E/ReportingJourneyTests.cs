using System.Net;
using System.Text.Json;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
[Category("Slow")]
public sealed class ReportingJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public Task NativeAnalyticsUsageSpendingAndSettingsDesktopAndMobile() => BrowserJourneys.RunAsync(fixture, "reporting-browser-flow.mjs");

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
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var before = JsonDocument.Parse(await client.GetStringAsync("/__test/reporting/probe"));
        var requestsBefore = before.RootElement.GetProperty("requests").GetArrayLength();
        foreach (var path in new[] { "/analytics?project=foreign-project", "/analytics?database=foreign-database" })
        {
            using var foreign = await client.GetAsync(path);
            await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        using var invalidRegion = await client.GetAsync("/analytics?region=not-a-project-region");
        await Assert.That(invalidRegion.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var legacyBilling = await client.GetAsync("/usage?tab=billing&status=paid");
        await Assert.That(legacyBilling.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(legacyBilling.Headers.Location!.OriginalString).IsEqualTo("/organization?tab=billing&status=paid");
        using var noCsrf = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/usage/spending-controls", new() { ["spendCap"] = "100.00" });
        await Assert.That(noCsrf.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var after = JsonDocument.Parse(await client.GetStringAsync("/__test/reporting/probe"));
        await Assert.That(after.RootElement.GetProperty("requests").GetArrayLength()).IsEqualTo(requestsBefore);
    }
}

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
[Category("Slow")]
public sealed class AccountBrowserJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public Task NativeProfileOrganizationInvitationsRolesAuditAndLifecycleDesktopAndMobile() => BrowserJourneys.RunAsync(fixture, "account-browser-flow.mjs");
}
