using System.Net;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    public sealed partial class TsAuthPlatformJourneyTests
    {
        [Test]
        [Arguments("viewer")]
        [Arguments("member")]
        [Arguments("admin")]
        public async Task ExplicitProjectGrantsArePagedWithoutOrganizationInventory(string role)
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("grant-reader");
            using var client = platform.Client();
            using var login = await HttpJourney.LoginAsync(client, user.Email, user.Password);
            var own = (await Me(client)).GetProperty("organizationId").GetString()!;
            var owner = platform.Principal("grant-owner");
            var org = platform.Producer.AddOrganization("Granted organization", owner.Subject);
            var project = platform.Producer.AddProject(org, "Granted project", owner.Subject);
            var sibling = platform.Producer.AddProject(org, "Hidden sibling", owner.Subject);
            platform.Producer.AddCollaborator(org, user.Subject, project, role);
            var expected = new List<string> { owner.Subject, user.Subject };
            foreach (var name in new[] { "grant-a", "grant-b" })
            {
                var target = platform.Principal(name);
                platform.Producer.AddMember(org, target.Subject, target.Email, "viewer");
                platform.Producer.AddCollaborator(org, target.Subject, project, "member");
                expected.Add(target.Subject);
            }
            var implied = platform.Principal("implied-only");
            platform.Producer.AddMember(org, implied.Subject, implied.Email, "admin");
            await platform.StoreAsync(new OrganizationRecord { Id = org });
            var version = await platform.VersionAsync(org);
            var scope = "?organizationId=" + org + "&projectId=" + project;
            var path = "/v1/terrascale/organizations/" + org + "/projects/" + project + "/members";
            var url = "/project/members" + scope;
            var seen = new List<string>();
            for (var page = 0; page < 3; page++)
            {
                var html = await client.GetStringAsync(url);
                await Assert.That(html).Contains("Organization-implied access is not listed").And.DoesNotContain(implied.Email).And.DoesNotContain(sibling);
                var rows = Regex.Matches(html, "data-project-grant-user=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
                await Assert.That(rows.Length).IsEqualTo(2);
                seen.AddRange(rows);
                var next = Regex.Match(html, "rel=\"next\" href=\"([^\"]+)\"");
                if (!next.Success)
                {
                    break;
                }

                url = WebUtility.HtmlDecode(next.Groups[1].Value);
            }
            await Assert.That(seen).IsEquivalentTo(expected);
            await Assert.That(platform.Producer.Count("GET", path, user.Subject)).IsEqualTo(2);
            platform.Producer.RefuseNext("GET", path, HttpStatusCode.ServiceUnavailable, "unavailable", "Private backend text");
            using (var failed = await client.GetAsync("/project/members" + scope))
            {
                await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                await Assert.That(await failed.Content.ReadAsStringAsync()).Contains("could not be loaded").And.DoesNotContain("No explicit project grants").And.DoesNotContain("Private backend text");
            }
            foreach (var invalid in new[] { "?organizationId=" + org + "&projectId=" + sibling, "?organizationId=" + own + "&projectId=" + project })
            {
                using (var denied = await client.GetAsync("/project/members" + invalid))
                {
                    await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }
            }

            using (var invalid = await client.GetAsync("/project/members" + scope + "&after=a&after=b"))
            {
                await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            foreach (var resource in new[] { "members", "invitations", "ownership-transfer", "audit-events" })
            {
                await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
            }

            await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
            await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(0);
            await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
            await Assert.That(platform.Producer.Failures).IsEmpty();
        }

        [Test]
        public async Task EmptyExplicitGrantListDoesNotDenyImpliedOrganizationAccess()
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("implied-reader");
            using var client = platform.Client();
            using var login = await HttpJourney.LoginAsync(client, user.Email, user.Password);
            var org = (await Me(client)).GetProperty("organizationId").GetString()!;
            var project = (await Me(client)).GetProperty("projectId").GetString()!;
            platform.Producer.RemoveGrant(org, user.Subject, project);
            var html = await client.GetStringAsync("/project/members?organizationId=" + org + "&projectId=" + project);
            await Assert.That(html).Contains("No explicit project grants on this page").And.Contains("Organization-implied access may still apply");
            await Assert.That(html).DoesNotContain("data-project-grant-user=");
            await Assert.That(platform.Producer.Failures).IsEmpty();
        }
    }
}
