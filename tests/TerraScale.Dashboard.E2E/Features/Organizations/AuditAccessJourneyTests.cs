using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.E2E.Features.Billing;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

[ClassDataSource<DashboardE2EFixture>]
public sealed class AuditAccessJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    [Arguments("member")]
    [Arguments("viewer")]
    public async Task NonManagersCannotReadLocalAuditEventsThroughThePageApiOrExports(string role)
    {
        using var owner = fixture.CreateClient();
        using var member = fixture.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var password = "E2E-audit-access-" + suffix;
        var email = "audit-" + role + "-" + suffix + "@example.test";
        await BillingPlanJourneyTests.SignUpPaidAsync(owner, "audit-owner-" + suffix + "@example.test", password, "owner-" + suffix);
        await BillingPlanJourneyTests.SignUpPaidAsync(member, email, password, "reader-" + suffix);

        // Seeded through real dashboard actions, these local events were previously
        // rendered even when ts-auth refused to return its audit history.
        var ownerAudit = await owner.GetStringAsync("/organization?tab=audit");
        await Assert.That(ownerAudit).Contains("session.created");
        var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=members");
        using var invitation = await HttpJourney.PostFormAsync(owner, "/api/v1/access/invitations", new()
        { ["_csrf"] = csrf, ["email"] = email, ["role"] = role });
        await Assert.That(invitation.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var path = Regex.Match(await invitation.Content.ReadAsStringAsync(), @"/invitations/[^""<>\s]+").Value;
        await Assert.That(path.Length).IsGreaterThan("/invitations/".Length);
        using var accepted = await HttpJourney.PostFormAsync(member, path + "/respond", new()
        { ["_csrf"] = await HttpJourney.CsrfAsync(member, path), ["action"] = "accept" });
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using (var membership = await member.GetAsync("/organization?tab=members"))
            await Assert.That(membership.StatusCode).IsEqualTo(HttpStatusCode.OK);

        foreach (var route in new[] { "/organization?tab=audit", "/audit", "/api/v1/dashboard/audit-events",
            "/audit/export?format=csv", "/audit/export?format=json", "/audit/export?format=ndjson" })
        {
            using var response = await member.GetAsync(route);
            if (route == "/audit")
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                continue;
            }
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because(route + " for " + role);
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(body).DoesNotContain("session.created");
            if (route.StartsWith("/organization", StringComparison.Ordinal))
            {
                await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
                await Assert.That(body).Contains("id=\"ts-navigation\"");
                await Assert.That(body).Contains("Ask an owner or admin for access.");
                await Assert.That(body).Contains("Request ID:");
                await Assert.That(body).Contains(response.Headers.GetValues("X-Request-ID").Single());
            }
        }
        using var datastar = new HttpRequestMessage(HttpMethod.Get, "/organization?tab=audit");
        datastar.Headers.Add("Datastar-Request", "true");
        using var streamed = await member.SendAsync(datastar);
        await Assert.That(streamed.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await streamed.Content.ReadAsStringAsync()).DoesNotContain("session.created");
    }
}
