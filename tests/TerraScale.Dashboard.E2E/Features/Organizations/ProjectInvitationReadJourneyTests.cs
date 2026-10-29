using System.Net;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    [Test, Arguments("viewer"), Arguments("member"), Arguments("admin")]
    public async Task ProjectInvitationPagesRequireCurrentExactAdminAndNeverReadOrganizationInventory(string role)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("invitation-reader");
        var owner = platform.Principal("invitation-owner");
        using var client = platform.Client();
        using var login = await HttpJourney.LoginAsync(client, user.Email, user.Password);
        var own = (await Me(client)).GetProperty("organizationId").GetString()!;
        var org = platform.Producer.AddOrganization("Invited project workspace", owner.Subject);
        var project = platform.Producer.AddProject(org, "Shared project", owner.Subject);
        var sibling = platform.Producer.AddProject(org, "Hidden sibling", owner.Subject);
        platform.Producer.AddCollaborator(org, user.Subject, project, role);
        await platform.StoreAsync(new OrganizationRecord { Id = org });
        var version = await platform.VersionAsync(org);
        var expected = new List<string>();
        foreach (var email in new[] { "one@example.test", "two@example.test", "three@example.test" })
        {
            var token = platform.Producer.AddInvitation(org, email, "member", owner.Subject, project);
            expected.Add(platform.Producer.InvitationId(org, token));
        }
        platform.Producer.AddInvitation(org, "org-sentinel@example.test", "admin", owner.Subject);
        platform.Producer.AddInvitation(org, "sibling-sentinel@example.test", "viewer", owner.Subject, sibling);
        var scope = "?organizationId=" + org + "&projectId=" + project;
        var url = "/project/invitations" + scope;
        var providerPath = "/v1/terrascale/organizations/" + org + "/invitations";
        using var response = await client.GetAsync(url);
        if (role != "admin")
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(platform.Producer.Count("GET", providerPath, user.Subject)).IsEqualTo(0);
            return;
        }
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(html).DoesNotContain("org-sentinel").And.DoesNotContain("sibling-sentinel").And.DoesNotContain(sibling);
        var seen = Regex.Matches(html, "data-project-invitation=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToList();
        var next = WebUtility.HtmlDecode(Regex.Match(html, "rel=\"next\" href=\"([^\"]+)\"").Groups[1].Value);
        var second = await client.GetStringAsync(next);
        seen.AddRange(Regex.Matches(second, "data-project-invitation=\"([^\"]+)\"").Select(match => match.Groups[1].Value));
        await Assert.That(seen).IsEquivalentTo(expected);
        platform.Producer.InvitationProjectReply = sibling;
        using (var invalid = await client.GetAsync(url)) await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        platform.Producer.InvitationProjectReply = null;
        platform.Producer.RefuseNext("GET", providerPath, HttpStatusCode.ServiceUnavailable, "unavailable", "Private producer error");
        using (var unavailable = await client.GetAsync(url))
        {
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await unavailable.Content.ReadAsStringAsync()).Contains("could not be loaded").And.DoesNotContain("No project invitations").And.DoesNotContain("Private producer error");
        }
        foreach (var invalidScope in new[] { "?organizationId=" + org + "&projectId=" + sibling, "?organizationId=" + own + "&projectId=" + project })
            using (var denied = await client.GetAsync("/project/invitations" + invalidScope)) await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var badCursor = await client.GetAsync(url + "&after=a&after=b")) await Assert.That(badCursor.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        platform.Producer.AddCollaborator(org, user.Subject, project, "viewer");
        using (var demoted = await client.GetAsync(url)) await Assert.That(demoted.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        platform.Producer.RemoveGrant(org, user.Subject, project);
        using (var revoked = await client.GetAsync(url)) await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        foreach (var resource in new[] { "members", "ownership-transfer", "audit-events" })
            await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(0);
        await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }
}
