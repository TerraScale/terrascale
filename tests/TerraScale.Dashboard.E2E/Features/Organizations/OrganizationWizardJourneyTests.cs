using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
public sealed class OrganizationWizardJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task LeavingTheWizardAfterCreatingAnOrganizationKeepsItClosedOnTheNextPage()
    {
        using var client = fixture.CreateClient();
        await DatabaseJourney.NativeWorkspaceAsync(client);
        var csrf = await HttpJourney.CsrfAsync(client);
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "Wizard " + Guid.NewGuid().ToString("N")[..12] });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var wizard = await client.GetStringAsync(created.Headers.Location);
        await Assert.That(wizard).Contains("<dialog id=\"create-organization\"");
        await Assert.That(Regex.IsMatch(wizard, "name=\"returnUrl\" value=\"[^\"]*newOrganization", RegexOptions.CultureInvariant)).IsFalse();
        using var overview = await client.GetAsync("/");
        await Assert.That(overview.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(overview.Headers.Location!.OriginalString).IsEqualTo("/organization");
        var landing = await client.GetStringAsync(overview.Headers.Location);
        await Assert.That(landing).DoesNotContain("id=\"create-organization\"");
    }
}
