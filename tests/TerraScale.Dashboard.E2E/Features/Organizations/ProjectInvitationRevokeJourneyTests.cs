using System.Net;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    public sealed partial class TsAuthPlatformJourneyTests
    {
        [Test, Arguments("viewer"), Arguments("member"), Arguments("admin")]
        public async Task ProjectInvitationRevocationBindsCurrentPageAndAuthorityWithoutOrganizationWrites(string role)
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("invitation-revoker");
            var owner = platform.Principal("invitation-revoke-owner");
            using var client = platform.Client();
            using var login = await HttpJourney.LoginAsync(client, user.Email, user.Password);
            var csrf = await HttpJourney.CsrfAsync(client, "/project");
            var org = platform.Producer.AddOrganization("Project invitation scope", owner.Subject);
            var project = platform.Producer.AddProject(org, "Selected", owner.Subject);
            var sibling = platform.Producer.AddProject(org, "Sibling", owner.Subject);
            platform.Producer.AddCollaborator(org, user.Subject, project, role);
            platform.Producer.AddCollaborator(org, user.Subject, sibling, "admin");
            await platform.StoreAsync(new OrganizationRecord { Id = org });
            var version = await platform.VersionAsync(org);
            var ordered = new[] { "one@example.test", "two@example.test", "three@example.test" }
                .Select(email => platform.Producer.AddInvitation(org, email, "member", owner.Subject, project))
                .OrderBy(token => platform.Producer.InvitationId(org, token), StringComparer.Ordinal).ToArray();
            var first = ordered[0]; platform.Producer.AgeProjectInvitation(org, first);
            var middle = ordered[1]; platform.Producer.SetInvitationStatus(org, middle, "accepted");
            var last = ordered[2];
            var siblingToken = platform.Producer.AddInvitation(org, "sibling@example.test", "member", owner.Subject, sibling);
            var orgToken = platform.Producer.AddInvitation(org, "org@example.test", "member", owner.Subject);
            var scope = "?organizationId=" + org + "&projectId=" + project;
            var path = "/v1/terrascale/organizations/" + org + "/invitations/";
            var lastId = platform.Producer.InvitationId(org, last);
            await Assert.That(await Post(lastId, "2")).IsEqualTo(role == "admin" ? HttpStatusCode.SeeOther : HttpStatusCode.Forbidden);
            if (role != "admin")
            {
                await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
                return;
            }
            await Assert.That(platform.Producer.InvitationStatus(org, lastId)).IsEqualTo("revoked");
            await Assert.That(await Post(lastId, "2")).IsEqualTo(HttpStatusCode.Conflict);
            foreach (var token in new[] { siblingToken, orgToken })
            {
                var id = platform.Producer.InvitationId(org, token);
                await Assert.That(await Post(id)).IsEqualTo(HttpStatusCode.NotFound);
                await Assert.That(platform.Producer.Count("DELETE", path + id, user.Subject)).IsEqualTo(0);
                await Assert.That(platform.Producer.InvitationStatus(org, id)).IsEqualTo("pending");
            }
            var firstId = platform.Producer.InvitationId(org, first);
            await Assert.That(await Post(firstId, "2")).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(await Post(firstId, token: "invalid")).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await Post(firstId)).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(platform.Producer.InvitationStatus(org, firstId)).IsEqualTo("revoked");
            await Assert.That(await Post(platform.Producer.InvitationId(org, middle))).IsEqualTo(HttpStatusCode.Conflict);
            platform.Producer.AddCollaborator(org, user.Subject, project, "viewer");
            await Assert.That(await Post(lastId, "2")).IsEqualTo(HttpStatusCode.Forbidden);
            platform.Producer.RemoveGrant(org, user.Subject, project);
            await Assert.That(await Post(lastId, "2")).IsEqualTo(HttpStatusCode.NotFound);
            foreach (var resource in new[] { "members", "ownership-transfer", "audit-events" })
            {
                await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
            }

            await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(0);
            await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
            await Assert.That(platform.Producer.Failures).IsEmpty();

            async Task<HttpStatusCode> Post(string id, string after = "", string? token = null)
            {
                using var response = await client.PostAsync("/api/v1/dashboard/project/invitations/revoke" + scope,
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = token ?? csrf, ["invitationId"] = id, ["after"] = after }));
                return response.StatusCode;
            }
        }
    }
}
