using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
public sealed class OrganizationWizardJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task SetupDialogsOpenOnlyOnRequestAndNeverFromOverview()
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

        var organizationId = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, created.Headers.Location!.OriginalString).Query)["organizationId"].ToString();
        using var payment = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "payment-method", ["plan"] = "starter", ["organizationID"] = organizationId });
        await Assert.That(payment.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var checkout = await client.GetAsync(payment.Headers.Location);
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.Found);
        using var paid = await client.GetAsync(checkout.Headers.Location);
        await Assert.That(paid.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var provisioned = await client.GetAsync("/");
        await Assert.That(provisioned.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(provisioned.Headers.Location!.OriginalString).IsEqualTo("/organization?tab=projects");
        await Assert.That(CreateProjectDialog(await client.GetStringAsync(provisioned.Headers.Location))).Contains("data-init=\"void 0\"");
        var requested = await client.GetStringAsync("/organization?newProject=1");
        await Assert.That(CreateProjectDialog(requested)).Contains("data-init=\"el.showModal()\"");
        await Assert.That(CreateProjectDialog(requested)).Contains("data-page-url=\"/organization?tab=projects&amp;organizationId=" + organizationId + "\"");
        await Assert.That(Regex.IsMatch(requested, "name=\"returnUrl\" value=\"[^\"]*newProject", RegexOptions.CultureInvariant)).IsFalse();
    }

    private static string CreateProjectDialog(string html)
    {
        return Regex.Match(html, "<dialog id=\"create-project\"[^>]*>", RegexOptions.CultureInvariant).Value;
    }
}
