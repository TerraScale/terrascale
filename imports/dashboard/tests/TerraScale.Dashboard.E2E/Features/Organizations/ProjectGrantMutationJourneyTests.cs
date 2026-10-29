using System.Net;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

public sealed partial class TsAuthPlatformJourneyTests
{
    [Test]
    [Arguments("viewer", "")]
    [Arguments("member", "")]
    [Arguments("admin", "")]
    [Arguments("admin", "viewer")]
    public async Task ExplicitGrantMutationsRequireCurrentProjectAdminWithoutOrganizationAuthority(string role, string organizationRole)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("grant-admin");
        var owner = platform.Principal("grant-owner");
        var target = platform.Principal("grant-target");
        using var client = platform.Client();
        using var login = await HttpJourney.LoginAsync(client, user.Email, user.Password);
        var own = (await Me(client)).GetProperty("organizationId").GetString()!;
        var org = platform.Producer.AddOrganization("Project authority", owner.Subject);
        var project = platform.Producer.AddProject(org, "Exact project", user.Subject);
        var sibling = platform.Producer.AddProject(org, "Sibling project", owner.Subject);
        platform.Producer.AddCollaborator(org, user.Subject, project, role);
        platform.Producer.AddMember(org, target.Subject, target.Email, "viewer");
        if (organizationRole.Length > 0) platform.Producer.AddMember(org, user.Subject, user.Email, organizationRole);
        await platform.StoreAsync(new OrganizationRecord { Id = org });
        var scope = "?organizationId=" + org + "&projectId=" + project;
        var url = "/api/v1/dashboard/project/grants" + scope;
        using var targetClient = platform.Client();
        using var targetLogin = await HttpJourney.LoginAsync(targetClient, target.Email, target.Password);
        // Establish the ordinary member's plan hydration before measuring collaborator actions.
        await targetClient.GetStringAsync("/project" + scope);
        var csrf = await HttpJourney.CsrfAsync(client, "/project/members" + scope);
        var version = await platform.VersionAsync(org);
        var publications = platform.Producer.PlanPublications(org);
        var prefix = "/v1/terrascale/organizations/" + org + "/projects/" + project + "/members/";
        Task<HttpResponseMessage> Change(string operation, string userId, string newRole = "viewer", string? token = null) =>
            HttpJourney.PostFormAsync(client, url, new() { ["_csrf"] = token ?? csrf, ["operation"] = operation, ["userId"] = userId, ["role"] = newRole });
        using (var forged = await Change("set", target.Subject, token: "forged"))
            await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using (var forged = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["operation"] = "set", ["userId"] = target.Subject, ["role"] = "viewer" }) })
        {
            forged.Headers.Add("Origin", "https://attacker.example");
            using var denied = await client.SendAsync(forged);
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
        if (role != "admin")
        {
            foreach (var operation in new[] { "set", "remove" })
                using (var denied = await Change(operation, target.Subject))
                    await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await client.GetStringAsync("/project/members" + scope)).DoesNotContain("id=\"grant-user-id\"");
            await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
        }
        else
        {
            var html = await client.GetStringAsync("/project/members" + scope);
            await Assert.That(html).Contains("Existing organization member user ID").And.Contains("independent service keys").And.Contains("Remove only this grant");
            foreach (var invalid in new[] { (Id: "not-a-ulid", Role: "viewer"), (Id: target.Subject, Role: "owner") })
                using (var denied = await Change("set", invalid.Id, invalid.Role))
                    await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
            var unknown = AccountService.NewId();
            using (var missing = await Change("set", unknown, "member"))
            {
                await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                await Assert.That(await missing.Content.ReadAsStringAsync()).Contains("value=\"" + unknown + "\"").And.Contains("project invitation").And.Contains("Invitations are not available here");
            }
            platform.Producer.RefuseNext("PUT", prefix + target.Subject, HttpStatusCode.ServiceUnavailable, "unavailable", "Private backend text");
            using (var failed = await Change("set", target.Subject, "admin"))
            {
                await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                await Assert.That(await failed.Content.ReadAsStringAsync()).Contains("value=\"" + target.Subject + "\"").And.Contains("value=\"admin\" selected").And.Contains("could not be confirmed").And.DoesNotContain("Private backend text");
            }
            using (var created = await Change("set", target.Subject, "admin"))
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(platform.Producer.Bodies("PUT", prefix + target.Subject).Last()).IsEqualTo("{\"role\":\"admin\"}");
            await Assert.That(await client.GetStringAsync("/project/members" + scope + "&changed=1")).Contains("Explicit project grant updated");
            await Assert.That(await targetClient.GetStringAsync("/project" + scope)).Contains("Your effective project role</dt><dd>admin");
            using (var changed = await Change("set", target.Subject, "member"))
                await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(await targetClient.GetStringAsync("/project" + scope)).Contains("Your effective project role</dt><dd>member");
            using (var removed = await Change("remove", target.Subject))
                await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            // Removing the explicit role preserves the target's implied organization viewer access.
            await Assert.That(await targetClient.GetStringAsync("/project" + scope)).Contains("Your effective project role</dt><dd>viewer");
            foreach (var invalid in new[] { "?organizationId=" + own + "&projectId=" + project, "?organizationId=" + org + "&projectId=" + sibling })
                using (var denied = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/project/grants" + invalid,
                    new() { ["_csrf"] = csrf, ["operation"] = "set", ["userId"] = target.Subject, ["role"] = "admin" }))
                    await Assert.That(denied.StatusCode).IsEqualTo(organizationRole.Length > 0 && invalid.Contains(sibling, StringComparison.Ordinal) ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound);
            using (var demoteSelf = await Change("set", user.Subject, "viewer"))
                await Assert.That(demoteSelf.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var mutations = platform.Producer.Mutations(org, user.Subject);
            using (var demoted = await Change("set", target.Subject, "admin"))
                await Assert.That(demoted.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(mutations);
            platform.Producer.AddCollaborator(org, user.Subject, project, "admin");
            using (var removeSelf = await Change("remove", user.Subject))
            {
                await Assert.That(removeSelf.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(await removeSelf.Content.ReadAsStringAsync()).Contains("Your explicit project grant was removed").And.DoesNotContain("data-workspace-project");
            }
            mutations = platform.Producer.Mutations(org, user.Subject);
            using (var revoked = await Change("set", target.Subject, "admin"))
                await Assert.That(revoked.StatusCode).IsEqualTo(organizationRole.Length > 0 ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound);
            await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(mutations);
        }
        await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
        await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(publications);
        if (organizationRole.Length == 0)
            foreach (var resource in new[] { "members", "invitations", "ownership-transfer", "audit-events" })
                await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    [Arguments("member")]
    [Arguments("admin")]
    public async Task LoweringAndRemovingExplicitGrantPreservesHigherOrganizationRole(string impliedRole)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("implied-grant-admin");
        using var client = platform.Client();
        using var login = await HttpJourney.LoginAsync(client, user.Email, user.Password);
        var org = (await Me(client)).GetProperty("organizationId").GetString()!;
        var project = (await Me(client)).GetProperty("projectId").GetString()!;
        platform.Producer.AddMember(org, user.Subject, user.Email, impliedRole);
        var scope = "?organizationId=" + org + "&projectId=" + project;
        var csrf = await HttpJourney.CsrfAsync(client, "/project/members" + scope);
        var version = await platform.VersionAsync(org);
        var publications = platform.Producer.PlanPublications(org);
        using (var lower = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/project/grants" + scope,
            new() { ["_csrf"] = csrf, ["operation"] = "set", ["userId"] = user.Subject, ["role"] = "viewer" }))
            await Assert.That(lower.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(await client.GetStringAsync("/project" + scope)).Contains("Your effective project role</dt><dd>" + impliedRole);
        platform.Producer.AddCollaborator(org, user.Subject, project, "admin");
        using (var remove = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/project/grants" + scope,
            new() { ["_csrf"] = csrf, ["operation"] = "remove", ["userId"] = user.Subject }))
            await Assert.That(remove.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await client.GetStringAsync("/project/members" + scope);
        await Assert.That(html).Contains("Your effective project role is " + impliedRole);
        await Assert.That(html.Contains("id=\"grant-user-id\"", StringComparison.Ordinal)).IsEqualTo(impliedRole == "admin");
        await Assert.That(platform.Producer.Role(org, user.Subject)).IsEqualTo(impliedRole);
        await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
        await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(publications);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }
}
