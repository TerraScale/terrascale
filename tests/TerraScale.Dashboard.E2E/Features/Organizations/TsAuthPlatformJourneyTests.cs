using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

// Two dashboard replicas call ts-auth's /v1/terrascale platform API through the
// production TsAuthGateway transport. The producer fake follows terrascale
// e30ffdc2 PlatformEndpoints/InvitationEndpoints; it must be rechecked against
// the corrected exported ts-dotnet auth fixtures before this counts as parity.
// Collaborator listing and read boundaries follow terrascale 1d823d8534fc PlatformDirectory.
// Project invitation lookup/acceptance follows local terrascale a095400a1 InvitationEndpoints/Invitations
// and TerraScale.DotNet 0.0.12's Named, InvitationLookup and AcceptedInvitation DTOs.
public sealed class TsAuthPlatformJourneyTests
{
    [Test]
    [Arguments("viewer")]
    [Arguments("member")]
    [Arguments("admin")]
    public async Task ProjectInviteesAcceptAndReadOnlyTheirLiveProjectsWithoutOrganizationAuthority(string role)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("project-" + role);
        var owner = platform.Principal("project-owner");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, user.Email, user.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var ownOrganization = (await Me(client)).GetProperty("organizationId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/profile");
        var org = platform.Producer.AddOrganization("Shared projects", owner.Subject);
        var project = platform.Producer.AddProject(org, "Granted", owner.Subject);
        var sibling = platform.Producer.AddProject(org, "Hidden sibling", owner.Subject);
        var token = platform.Producer.AddInvitation(org, user.Email, role, owner.Subject, project);
        using (var preview = await client.GetAsync("/invitations/" + token))
        {
            await Assert.That(preview.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(preview.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(preview.Headers.GetValues("Referrer-Policy").Single()).IsEqualTo("strict-origin");
            var html = await preview.Content.ReadAsStringAsync();
            foreach (var copy in new[] { "Project invitation", "Join Granted", "This invitation grants " + role + " access to Granted in Shared projects.", "Your organization role is unchanged." })
                await Assert.That(html).Contains(copy);
            await Assert.That(html).DoesNotContain("join this organization");
        }
        using (var noCsrf = await HttpJourney.PostFormAsync(client, "/invitations/" + token + "/respond", new() { ["action"] = "accept" }))
            await Assert.That(noCsrf.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using (var accepted = await HttpJourney.PostFormAsync(client, "/invitations/" + token + "/respond?projectId=" + sibling,
            new() { ["_csrf"] = csrf, ["action"] = "accept" }))
        {
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(accepted.Headers.CacheControl?.NoStore).IsTrue();
            await Assert.That(accepted.Headers.Location!.OriginalString).IsEqualTo("/databases?organizationId=" + org + "&projectId=" + project);
            using var landing = await client.GetAsync(accepted.Headers.Location);
            await Assert.That(landing.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That((await Me(client)).GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That(platform.Producer.Role(org, user.Subject)).IsNull();
        var database = AccountService.NewId();
        platform.SeedDatabase(new(database, project, "granted-db", DateTimeOffset.UtcNow, HomeRegion: "gru", ReplicaRegions: []));
        var scope = "?organizationId=" + org + "&projectId=" + project;
        using (var first = await client.GetAsync("/api/v1/dashboard/context" + scope))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            var visible = body.RootElement.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == org);
            await Assert.That(visible.GetProperty("projects").EnumerateObject().Select(item => item.Name).ToArray()).IsEquivalentTo(new[] { project });
            foreach (var unknown in new[] { "role", "kind", "members", "invitations", "ownerUserId", "pendingOwnerUserId", "planId", "billingStatus", "paymentMethodValidated", "archived", "deletionRequested" })
                await Assert.That(visible.GetProperty(unknown).ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(visible.GetProperty("projects").GetProperty(project).GetProperty("role").GetString()).IsEqualTo(role);
            await Assert.That(visible.GetProperty("projects").GetProperty(project).GetProperty("operationLimits").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
        using (var discoveredWithoutState = await client.GetAsync("/api/v1/dashboard/databases" + scope))
        {
            await Assert.That(discoveredWithoutState.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await discoveredWithoutState.Content.ReadAsStringAsync()).Contains(database);
        }
        await Assert.That(await platform.StoredAsync(AccountService.OrganizationsCollection, org)).IsFalse();

        var saved = new OrganizationRecord
        {
            Id = org,
            Kind = "business",
            PlanId = "private-plan",
            BillingStatus = "private-billing",
            PaymentMethodValidated = true,
            SpendCapCents = 987654,
            PaidExcess = true
        };
        saved.AuditEvents.Add(new(AccountService.NewId(), "private-owner", "private-audit", "private-target", "project", DateTimeOffset.UtcNow));
        saved.Projects[project] = new() { Id = project, Region = "GRU", SpendCapCents = 987654 };
        saved.Projects[project].Databases[database] = new() { Id = database, ProjectId = project, Name = "granted-db", Region = "GRU", SpendCapCents = 987654 };
        saved.Projects[sibling] = new() { Id = sibling, Region = "GRU" };
        await platform.StoreAsync(saved);
        var version = await platform.VersionAsync(org);
        var discovered = AccountService.NewId();
        platform.SeedDatabase(new(discovered, project, "discovered-db", DateTimeOffset.UtcNow, HomeRegion: "gru", ReplicaRegions: []));
        platform.SeedDatabase(new(AccountService.NewId(), sibling, "private-sibling-db", DateTimeOffset.UtcNow, HomeRegion: "gru", ReplicaRegions: []));
        using (var probe = await client.GetAsync("/__test/project-context" + scope + "&refresh=project"))
        {
            await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
            await Assert.That(body.RootElement.GetProperty("role").GetString()).IsEqualTo(role);
            await Assert.That(body.RootElement.GetProperty("restricted").GetBoolean()).IsTrue();
            await Assert.That(body.RootElement.GetProperty("roleStored").GetBoolean()).IsFalse();
            var json = body.RootElement.GetRawText();
            foreach (var secret in new[] { "private-", sibling, "987654" }) await Assert.That(json).DoesNotContain(secret);
        }
        using (var listed = await client.GetAsync("/api/v1/dashboard/databases" + scope))
        {
            await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            await Assert.That(body.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray())
                .IsEquivalentTo(new string?[] { database, discovered });
            foreach (var record in body.RootElement.EnumerateArray())
                foreach (var field in new[] { "spendCapCents", "operationLimits", "backupRetentionDays", "branchedFrom" })
                    await Assert.That(record.GetProperty(field).ValueKind).IsEqualTo(JsonValueKind.Null);
        }
        using (var page = await client.GetAsync("/databases" + scope))
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-project",
            new() { ["_csrf"] = csrf, ["projectId"] = project, ["returnUrl"] = "/databases" }))
        {
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(switched.Headers.Location!.OriginalString).Contains("projectId=" + project);
        }
        await Assert.That((await Me(client)).GetProperty("projectId").GetString()).IsEqualTo(project);
        using (var refused = await client.GetAsync("/__test/project-context" + scope + "&refresh=organization"))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        foreach (var invalid in new[] { "?organizationId=" + org + "&projectId=" + sibling,
            "?organizationId=" + ownOrganization + "&projectId=" + project })
            using (var refused = await client.GetAsync("/databases" + invalid))
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        foreach (var tab in new[] { "organization", "projects", "members", "invitations", "billing", "audit", "sso", "danger" })
            using (var refused = await client.GetAsync("/organization" + scope + "&tab=" + tab))
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        foreach (var action in new[] { "rename", "projects", "members/invite", "mfa-policy", "deletion" })
            using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/" + action + scope,
                new() { ["_csrf"] = csrf, ["name"] = "Unchanged", ["email"] = "invite@example.test", ["role"] = "member", ["region"] = "GRU" }))
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using (var write = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections" + scope,
            new() { ["_csrf"] = csrf, ["databaseId"] = database, ["collection"] = "denied" }))
            await Assert.That(write.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        foreach (var resource in new[] { "members", "ownership-transfer", "invitations", "audit-events" })
            await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(0);
        await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);

        platform.Producer.RequireMfa(org);
        using (var blocked = await client.GetAsync("/databases" + scope))
            await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var blocked = await client.GetAsync("/__test/project-context?organizationId=" + org))
        {
            await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await blocked.Content.ReadAsStringAsync()).DoesNotContain(sibling).And.DoesNotContain("private-");
        }
        platform.Producer.Enroll(user.Subject, 10);
        platform.Producer.RemoveGrant(org, user.Subject, project);
        using (var revoked = await client.GetAsync("/databases" + scope))
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var revoked = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-project",
            new() { ["_csrf"] = csrf, ["projectId"] = project, ["returnUrl"] = "/databases" }))
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await Context(client)).GetProperty("organizations").EnumerateArray().Any(item => item.GetProperty("id").GetString() == org)).IsFalse();
        await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
        platform.Producer.AddCollaborator(org, user.Subject, project, role);
        await platform.ProjectionAsync(org, current => current.Projects[project].Status = "inactive");
        using (var inactive = await client.GetAsync("/__test/project-context" + scope + "&refresh=project"))
            await Assert.That(inactive.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var inactive = await client.GetAsync("/databases" + scope))
            await Assert.That(inactive.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await platform.ProjectionAsync(org, current => current.Projects[project].Status = "active");
        await platform.ProjectionAsync(org, current => current.Archived = true);
        using (var archived = await client.GetAsync("/databases" + scope))
            await Assert.That(archived.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await platform.ProjectionAsync(org, current => { current.Archived = false; current.Projects.Remove(project); current.DeletedProjectIds.Add(project); });
        using (var deleted = await client.GetAsync("/databases" + scope))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await platform.FenceAsync(org);
        using (var erased = await client.GetAsync("/databases" + scope))
            await Assert.That(erased.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DecliningAnInvitationKeepsSelectionAndAccessAndWrongEmailCannotRespond(bool projectInvite)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("decline-invitee");
        var other = platform.Principal("wrong-email");
        using var client = platform.Client();
        using var wrong = platform.Client(replica: true);
        using (var login = await HttpJourney.LoginAsync(client, user.Email, user.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using (var login = await HttpJourney.LoginAsync(wrong, other.Email, other.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var before = await Me(client);
        var org = platform.Producer.AddOrganization("Declined workspace", other.Subject);
        var project = platform.Producer.AddProject(org, "Declined project", other.Subject);
        var token = platform.Producer.AddInvitation(org, user.Email, "admin", other.Subject, projectInvite ? project : null);
        var path = "/invitations/" + token;
        var wrongCsrf = await HttpJourney.CsrfAsync(wrong, path);
        var html = await wrong.GetStringAsync(path);
        await Assert.That(html).Contains("Sign out and switch account");
        await Assert.That(html).DoesNotContain("Accept invitation");
        foreach (var action in new[] { "accept", "decline" })
            using (var refused = await HttpJourney.PostFormAsync(wrong, path + "/respond", new() { ["_csrf"] = wrongCsrf, ["action"] = action }))
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        foreach (var action in new[] { "accept", "decline" })
            await Assert.That(platform.Producer.Count("POST", "/v1/terrascale/invitations/" + action, other.Subject)).IsEqualTo(0);
        var csrf = await HttpJourney.CsrfAsync(client, path);
        using (var declined = await HttpJourney.PostFormAsync(client, path + "/respond?organizationId=" + org + "&projectId=" + project,
            new() { ["_csrf"] = csrf, ["action"] = "decline" }))
        {
            await Assert.That(declined.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(declined.Headers.Location!.OriginalString).IsEqualTo("/databases?invitation=declined");
            using var landing = await client.GetAsync(declined.Headers.Location);
            await Assert.That(landing.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await landing.Content.ReadAsStringAsync()).Contains("Invitation declined. Your access has not changed.");
        }
        var after = await Me(client);
        foreach (var field in new[] { "organizationId", "projectId" })
            await Assert.That(after.GetProperty(field).GetString()).IsEqualTo(before.GetProperty(field).GetString());
        await Assert.That(platform.Producer.Role(org, user.Subject)).IsNull();
        await Assert.That(platform.Producer.InvitationStatus(org, platform.Producer.InvitationId(org, token))).IsEqualTo("declined");
        using (var refused = await client.GetAsync("/databases?organizationId=" + org + "&projectId=" + project))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var consumed = await client.GetAsync(path)) await Assert.That(consumed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var expired = platform.Producer.AddInvitation(org, user.Email, "viewer", other.Subject, projectInvite ? project : null);
        platform.Producer.ExpireInvitation(org, expired);
        using (var unavailable = await client.GetAsync("/invitations/" + expired)) await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using (var refused = await HttpJourney.PostFormAsync(client, "/invitations/" + expired + "/respond", new() { ["_csrf"] = csrf, ["action"] = "accept" }))
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(platform.Producer.Count("POST", "/v1/terrascale/invitations/accept", user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(0);
        foreach (var resource in new[] { "members", "ownership-transfer", "invitations", "audit-events" })
            await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    [Arguments("lookup-org-id")]
    [Arguments("lookup-project-id")]
    [Arguments("lookup-project-name")]
    [Arguments("lookup-org-name")]
    [Arguments("lookup-same-id")]
    [Arguments("lookup-project-owner")]
    [Arguments("accept-org")]
    [Arguments("accept-project")]
    [Arguments("accept-null")]
    [Arguments("accept-org-to-project")]
    [Arguments("accept-role")]
    [Arguments("accept-cross-org-project")]
    public async Task InvalidProducerInvitationScopeFailsClosedWithoutChangingSelection(string fault)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("scope-invitee");
        var owner = platform.Principal("scope-owner");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, user.Email, user.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var before = await Me(client);
        var org = platform.Producer.AddOrganization("Offered organization", owner.Subject);
        var project = platform.Producer.AddProject(org, "Offered project", owner.Subject);
        var sibling = platform.Producer.AddProject(org, "Other project", owner.Subject);
        var other = platform.Producer.AddOrganization("Other organization", owner.Subject);
        var foreignProject = platform.Producer.AddProject(other, "Foreign project", owner.Subject);
        var offered = fault == "accept-org-to-project" ? null : project;
        var token = platform.Producer.AddInvitation(org, user.Email, "member", owner.Subject, offered);
        var path = "/invitations/" + token;
        var csrf = await HttpJourney.CsrfAsync(client, path);
        if (fault.StartsWith("lookup-", StringComparison.Ordinal) || fault == "accept-cross-org-project")
            platform.Producer.InvitationReplies["lookup"] = new
            {
                organization = new { id = fault == "lookup-org-id" ? "invalid" : org, name = fault == "lookup-org-name" ? "" : "Offered organization" },
                project = new
                {
                    id = fault switch { "lookup-project-id" => "invalid", "lookup-same-id" => org, "accept-cross-org-project" => foreignProject, _ => project },
                    name = fault == "lookup-project-name" ? "" : "Offered project"
                },
                email = user.Email,
                role = fault == "lookup-project-owner" ? "owner" : "member",
                expires_at = DateTimeOffset.UtcNow.AddDays(1)
            };
        else
            platform.Producer.InvitationReplies["accept"] = new
            {
                organization_id = fault == "accept-org" ? other : org,
                project_id = fault switch { "accept-project" => sibling, "accept-null" => null, _ => project },
                role = fault == "accept-role" ? "admin" : "member"
            };
        if (fault == "accept-cross-org-project")
            platform.Producer.InvitationReplies["accept"] = new { organization_id = org, project_id = foreignProject, role = "member" };
        using (var refused = await HttpJourney.PostFormAsync(client, path + "/respond", new() { ["_csrf"] = csrf, ["action"] = "accept" }))
            await Assert.That(refused.StatusCode).IsEqualTo(fault == "accept-cross-org-project" ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable);
        var after = await Me(client);
        foreach (var field in new[] { "organizationId", "projectId" })
            await Assert.That(after.GetProperty(field).GetString()).IsEqualTo(before.GetProperty(field).GetString());
        if (fault.StartsWith("lookup-", StringComparison.Ordinal))
            await Assert.That(platform.Producer.Count("POST", "/v1/terrascale/invitations/accept", user.Subject)).IsEqualTo(0);
        if (offered is not null) await Assert.That(platform.Producer.Role(org, user.Subject)).IsNull();
        await Assert.That(platform.Producer.Role(other, user.Subject)).IsNull();
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    [Arguments("viewer", "admin", "admin")]
    [Arguments("viewer", "member", "member")]
    [Arguments("member", "viewer", "member")]
    public async Task EffectiveProjectRolesAddAccessWithoutPromotingOrganizationMembership(string organizationRole, string grant, string expected)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("overlapping-roles");
        using var client = platform.Client();
        using var login = await HttpJourney.LoginAsync(client, user.Email, user.Password);
        var org = platform.Producer.AddOrganization("Overlapping roles", platform.Principal("overlap-owner").Subject);
        var project = platform.Producer.AddProject(org, "Explicit grant", user.Subject);
        platform.Producer.AddMember(org, user.Subject, user.Email, organizationRole);
        platform.Producer.AddCollaborator(org, user.Subject, project, grant);
        using var response = await client.GetAsync("/api/v1/dashboard/context?organizationId=" + org + "&projectId=" + project);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var visible = body.RootElement.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == org);
        await Assert.That(visible.GetProperty("role").GetString()).IsEqualTo(organizationRole);
        await Assert.That(visible.GetProperty("projects").GetProperty(project).GetProperty("role").GetString()).IsEqualTo(expected);
    }

    [Test]
    public async Task FirstSignInOnTwoReplicasCreatesOnePersonalWorkspaceAndFollowsTsAuthMembership()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var user = platform.Principal("platform-first");
        using var first = platform.Client();
        using var second = platform.Client(replica: true);
        var logins = await Task.WhenAll(HttpJourney.LoginAsync(first, user.Email, user.Password), HttpJourney.LoginAsync(second, user.Email, user.Password));
        foreach (var login in logins) using (login) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var identities = await Task.WhenAll(Me(first), Me(second));
        var organizationId = identities[0].GetProperty("organizationId").GetString()!;
        var projectId = identities[0].GetProperty("projectId").GetString()!;
        await Assert.That(TerraScaleIds.IsValid(organizationId) && TerraScaleIds.IsValid(projectId)).IsTrue();
        await Assert.That(identities[1].GetProperty("organizationId").GetString()).IsEqualTo(organizationId);
        await Assert.That(identities[1].GetProperty("projectId").GetString()).IsEqualTo(projectId);
        await Assert.That(platform.Producer.Count("POST", "/v1/terrascale/organizations", user.Subject)).IsEqualTo(1)
            .Because("concurrent first sign-ins on two replicas create one Personal organization at ts-auth");
        await Assert.That(platform.Producer.Count("POST", "/v1/terrascale/organizations/" + organizationId + "/projects", user.Subject)).IsEqualTo(1);

        // ts-auth stays the authority: a rename and a project made outside the dashboard appear on the next read.
        platform.Producer.Rename(organizationId, "Renamed at ts-auth");
        var outside = platform.Producer.AddProject(organizationId, "Made outside", user.Subject);
        var business = platform.Producer.AddOrganization("Shared business", platform.Principal("platform-owner").Subject);
        platform.Producer.AddMember(business, user.Subject, user.Email, "admin");
        var collaboration = platform.Producer.AddOrganization("Collaboration only", platform.Principal("platform-other").Subject);
        platform.Producer.AddCollaborator(collaboration, user.Subject);
        var paged = platform.Producer.AddOrganization("Paged membership", user.Subject);
        for (var index = 0; index < 3; index++) platform.Producer.AddOrganization("Unrelated " + index, platform.Principal("platform-unrelated-" + index).Subject);
        var context = await Context(second);
        var organizations = context.GetProperty("organizations").EnumerateArray().ToArray();
        var personal = organizations.Single(org => org.GetProperty("id").GetString() == organizationId);
        await Assert.That(personal.GetProperty("name").GetString()).IsEqualTo("Renamed at ts-auth");
        await Assert.That(personal.GetProperty("kind").GetString()).IsEqualTo("personal");
        await Assert.That(personal.GetProperty("projects").EnumerateObject().Select(item => item.Name).Order().ToArray())
            .IsEquivalentTo(new[] { projectId, outside }.Order().ToArray());
        var shared = organizations.Single(org => org.GetProperty("id").GetString() == business);
        await Assert.That(organizations.Any(org => org.GetProperty("id").GetString() == paged)).IsTrue();
        await Assert.That(shared.GetProperty("role").GetString()).IsEqualTo("admin");
        await Assert.That(personal.GetProperty("projects").GetProperty(projectId).GetProperty("role").GetString()).IsEqualTo("admin");
        await Assert.That(shared.GetProperty("members").GetProperty(user.Subject).GetProperty("role").GetString()).IsEqualTo("admin");
        await Assert.That(organizations.Any(org => org.GetProperty("id").GetString() == collaboration)).IsFalse()
            .Because("a collaborator without a live project grant has no workspace access");

        platform.Producer.RemoveMember(business, user.Subject);
        await Assert.That((await Context(first)).GetProperty("organizations").EnumerateArray().Any(org => org.GetProperty("id").GetString() == business)).IsFalse();
        var csrf = await HttpJourney.CsrfAsync(first, "/organization");
        using (var switched = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = business, ["returnUrl"] = "/" }))
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        await Assert.That(platform.Producer.PagedRequests).IsGreaterThan(0).Because("organization lists span producer pages");
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    public async Task MembersInvitationsOwnershipAndAuditUseTheProducerWire()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("platform-owner");
        var member = platform.Principal("platform-member");
        var invitee = platform.Principal("platform-invitee");
        using var ownerClient = platform.Client();
        using (var login = await HttpJourney.LoginAsync(ownerClient, owner.Email, owner.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var business = platform.Producer.AddOrganization("Wire company", owner.Subject, owner.Email);
        platform.Producer.AddMember(business, member.Subject, member.Email, "member");
        var token = platform.Producer.AddInvitation(business, invitee.Email, "member", owner.Subject);
        var unused = platform.Producer.AddInvitation(business, "unused-" + invitee.Email, "viewer", owner.Subject);
        platform.Producer.StartTransfer(business, owner.Subject, member.Subject);
        platform.Producer.AddAudit(business, owner.Subject, "member.role_changed", "audit-target-" + business);

        var csrf = await HttpJourney.CsrfAsync(ownerClient, "/organization");
        using (var switched = await HttpJourney.PostFormAsync(ownerClient, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = business, ["returnUrl"] = "/organization" }))
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var members = await ownerClient.GetStringAsync("/organization?tab=members");
        await Assert.That(members.Contains(member.Email, StringComparison.Ordinal)).IsTrue();
        var invitations = await ownerClient.GetStringAsync("/organization?tab=invitations");
        await Assert.That(invitations.Contains(invitee.Email, StringComparison.Ordinal)).IsTrue();
        var audit = await ownerClient.GetStringAsync("/organization?tab=audit");
        await Assert.That(audit.Contains("audit-target-" + business, StringComparison.Ordinal)).IsTrue();
        var context = await Context(ownerClient);
        var organization = context.GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == business);
        await Assert.That(organization.GetProperty("ownerUserId").GetString()).IsEqualTo(owner.Subject);

        csrf = await HttpJourney.CsrfAsync(ownerClient, "/organization?tab=members");
        using (var viewer = await HttpJourney.PostFormAsync(ownerClient, "/api/v1/access/members/" + member.Subject + "/role?tab=members", new()
        { ["_csrf"] = csrf, ["role"] = "viewer" }))
            await Assert.That(viewer.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(platform.Producer.Role(business, member.Subject)).IsEqualTo("viewer");
        await Assert.That(platform.Producer.Bodies("PUT", "/v1/terrascale/organizations/" + business + "/members/" + member.Subject + "/role"))
            .IsEquivalentTo(new[] { "{\"role\":\"viewer\"}" });

        // ts-auth looks an invitation up only for a signed-in holder of its token.
        using (var anonymous = platform.Client())
        {
            var page = await anonymous.GetStringAsync("/invitations/" + unused);
            await Assert.That(page.Contains("Sign in to view this invitation", StringComparison.Ordinal)).IsTrue();
        }
        using var inviteeClient = platform.Client(replica: true);
        using (var login = await HttpJourney.LoginAsync(inviteeClient, invitee.Email, invitee.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var preview = await inviteeClient.GetStringAsync("/invitations/" + token);
        await Assert.That(preview.Contains("Join Wire company", StringComparison.Ordinal)).IsTrue();
        csrf = await HttpJourney.CsrfAsync(inviteeClient, "/invitations/" + token);
        using (var accepted = await HttpJourney.PostFormAsync(inviteeClient, "/invitations/" + token + "/respond?organizationId=" + AccountService.NewId() + "&projectId=" + AccountService.NewId(),
            new() { ["_csrf"] = csrf, ["action"] = "accept" }))
        {
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(accepted.Headers.Location?.OriginalString).IsEqualTo("/organization?tab=members&invitation=accepted");
            using var landing = await inviteeClient.GetAsync(accepted.Headers.Location);
            await Assert.That(landing.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That(platform.Producer.Role(business, invitee.Subject)).IsEqualTo("member");
        await Assert.That((await Me(inviteeClient)).GetProperty("organizationId").GetString()).IsEqualTo(business);

        // A ts-auth refusal reaches the page with its status and the dashboard's message for its code; nothing is stored.
        using var personalClient = platform.Client();
        using (var login = await HttpJourney.LoginAsync(personalClient, member.Email, member.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var personal = (await Me(personalClient)).GetProperty("organizationId").GetString()!;
        await platform.PlanAsync(personal, OrganizationRecord.StarterPlanId); // a Free plan would refuse the second project before ts-auth
        platform.Producer.RefuseNext("POST", "/v1/terrascale/organizations/" + personal + "/projects", HttpStatusCode.Conflict, "name_taken", "Producer text Taken@example.test is never shown.");
        csrf = await HttpJourney.CsrfAsync(personalClient, "/organization?tab=projects");
        using (var refused = await HttpJourney.PostFormAsync(personalClient, "/api/v1/dashboard/organization/projects", new()
        { ["_csrf"] = csrf, ["name"] = "Taken", ["regions"] = "GRU", ["region"] = "GRU" }))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            var html = await refused.Content.ReadAsStringAsync();
            await Assert.That(html.Contains("That name is already used in this organization.", StringComparison.Ordinal)).IsTrue();
            await Assert.That(html.Contains("Producer text", StringComparison.Ordinal)).IsFalse().Because("only ts-auth's fixed code reaches the page");
        }
        await Assert.That((await Context(personalClient)).GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == personal)
            .GetProperty("projects").EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    public async Task WritesValidateAgainstCurrentTsAuthIdentityNotStoredCopies()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("platform-writer");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var personal = (await Me(client)).GetProperty("organizationId").GetString()!;
        // A project made at ts-auth stays visible without exposing a customer spending setter.
        var outside = platform.Producer.AddProject(personal, "Made outside", owner.Subject);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using (var cap = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/usage/spending-controls", new() { ["_csrf"] = csrf, ["projCap_" + outside] = "12.50" }))
            await Assert.That(cap.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var project = (await Context(client)).GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == personal)
            .GetProperty("projects").GetProperty(outside);
        await Assert.That(project.TryGetProperty("spendCapCents", out _)).IsFalse();
        await Assert.That(project.GetProperty("name").GetString()).IsEqualTo("Made outside");

        // Review of #13 at 2a04b403: confirming deletion with the name ts-auth now holds answered 422.
        var business = platform.Producer.AddOrganization("Named before", owner.Subject, owner.Email);
        csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using (var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = business, ["returnUrl"] = "/organization" }))
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        platform.Producer.Rename(business, "Renamed at ts-auth");
        var businessProject = platform.Producer.AddProject(business, "Business project", owner.Subject);
        // A project with a database refuses the deletion before anything is deleted (Ku TS-916).
        await platform.ProjectionAsync(business, org => org.Projects[businessProject] = new()
        { Id = businessProject, OrganizationId = business, Name = "Business project", Databases = { ["db"] = new() { Id = "db", ProjectId = businessProject, Name = "orders" } } });
        csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=danger");
        using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/deletion", new()
        { ["_csrf"] = csrf, ["confirmation"] = "Renamed at ts-auth" }))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That((await refused.Content.ReadAsStringAsync()).Contains("Delete this organization&#x27;s databases before deleting it.", StringComparison.Ordinal)).IsTrue();
        }
        await Assert.That(platform.Producer.Count("DELETE", "/v1/terrascale/organizations/" + business, owner.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Count("DELETE", "/v1/terrascale/organizations/" + business + "/projects/" + businessProject, owner.Subject)).IsEqualTo(0);
        await Assert.That((await platform.ProjectionAsync(business)).DeletionRequested).IsFalse();

        // An organization stuck at the old deletion request completes like a new one, with the name ts-auth now holds.
        await platform.ProjectionAsync(business, org => { org.Projects[businessProject].Databases.Clear(); org.DeletionRequested = true; });
        await Assert.That((await client.GetStringAsync("/organization?tab=danger")).Contains(">Delete organization</button>", StringComparison.Ordinal)).IsTrue();
        csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=danger");
        using (var deletion = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/deletion", new()
        { ["_csrf"] = csrf, ["confirmation"] = "Renamed at ts-auth" }))
        {
            await Assert.That(deletion.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(deletion.Headers.Location?.OriginalString).StartsWith("/organization?deleted=1&organizationId=" + personal);
            await Assert.That(await client.GetStringAsync(deletion.Headers.Location)).Contains("Organization deleted.");
        }
        await Assert.That(platform.Producer.Count("DELETE", "/v1/terrascale/organizations/" + business + "/projects/" + businessProject, owner.Subject)).IsEqualTo(1);
        await Assert.That(platform.Producer.Count("DELETE", "/v1/terrascale/organizations/" + business, owner.Subject)).IsEqualTo(1);
        await Assert.That(platform.Producer.Exists(business)).IsFalse();
        var archived = await platform.ProjectionAsync(business);
        await Assert.That(archived.Archived && archived.Status == "deleted").IsTrue();
        await Assert.That((await Me(client)).GetProperty("organizationId").GetString()).IsEqualTo(personal);
        await Assert.That((await Context(client)).GetProperty("organizations").EnumerateArray().Any(org => org.GetProperty("id").GetString() == business)).IsFalse();
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    // Page audit UAN-01: ts-auth audit events carry no severity, which must not become "info".
    [Test]
    public async Task AuditEventsWithoutSeverityStayNotReportedInFiltersAndExports()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("platform-auditor");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var business = platform.Producer.AddOrganization("Audit company", owner.Subject, owner.Email);
        var target = "unreported-" + business;
        platform.Producer.AddAudit(business, owner.Subject, "member.role_changed", target);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using (var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = business, ["returnUrl"] = "/organization" }))
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);

        var all = await client.GetStringAsync("/organization?tab=audit&range=24h");
        await Assert.That(all.Contains(target, StringComparison.Ordinal)).IsTrue();
        await Assert.That(all.Contains("Severity is not reported for some provider events.", StringComparison.Ordinal)).IsTrue();
        await Assert.That((await client.GetStringAsync("/organization?tab=audit&range=24h&severity=info")).Contains(target, StringComparison.Ordinal)).IsFalse();
        await Assert.That((await client.GetStringAsync("/organization?tab=audit&range=24h&severity=unknown")).Contains(target, StringComparison.Ordinal)).IsTrue();

        var csv = (await client.GetStringAsync("/audit/export?format=csv&range=24h")).Split("\r\n").Single(line => line.Contains(target, StringComparison.Ordinal));
        await Assert.That(csv.Split(',')[7]).IsEqualTo("\"unknown\"");
        using var info = JsonDocument.Parse(await client.GetStringAsync("/audit/export?format=json&range=24h&severity=info"));
        await Assert.That(info.RootElement.GetProperty("count").GetInt32()).IsEqualTo(0);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    // Round-3 review probes, verbatim.
    [Test]
    public async Task InviteAndRevokeRefuseADemotedCallerFromLiveTsAuth()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("live-invite-owner");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var business = platform.Producer.AddOrganization("Live invitation authority", owner.Subject, owner.Email);
        var token = platform.Producer.AddInvitation(business, "invitee@example.test", "member", owner.Subject);
        var invitation = platform.Producer.InvitationId(business, token);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using (var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = business, ["returnUrl"] = "/organization" }))
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=invitations");
        platform.Producer.AddMember(business, owner.Subject, owner.Email, "viewer");
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/access/invitations?tab=invitations", new()
        { ["_csrf"] = csrf, ["email"] = "unauthorized@example.test", ["role"] = "admin" }))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using (var revoked = await HttpJourney.PostFormAsync(client, "/api/v1/access/invitations/" + invitation + "/revoke?tab=invitations", new()
        { ["_csrf"] = csrf }))
            await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(platform.Producer.Count("POST", "/v1/terrascale/organizations/" + business + "/invitations", owner.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Count("DELETE", "/v1/terrascale/organizations/" + business + "/invitations/" + invitation, owner.Subject)).IsEqualTo(0);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    public async Task ResendMustNotWidenAProjectAdminInvitationToOrganizationAdmin()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("project-invite-owner");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var business = platform.Producer.AddOrganization("Project invitation scope", owner.Subject, owner.Email);
        var project = platform.Producer.AddProject(business, "Limited project", owner.Subject);
        var token = platform.Producer.AddInvitation(business, "project-admin@example.test", "admin", owner.Subject, project);
        var invitation = platform.Producer.InvitationId(business, token);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using (var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = business, ["returnUrl"] = "/organization" }))
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await platform.PlanAsync(business, OrganizationRecord.StarterPlanId);
        csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=invitations");
        using var resent = await HttpJourney.PostFormAsync(client, "/api/v1/access/invitations/" + invitation + "/resend?tab=invitations", new()
        { ["_csrf"] = csrf });
        await Assert.That(resent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = platform.Producer.Bodies("POST", "/v1/terrascale/organizations/" + business + "/invitations").Single();
        using var created = JsonDocument.Parse(body);
        var scope = created.RootElement.TryGetProperty("project_id", out var scoped) ? scoped.GetString() : null;
        Console.WriteLine($"PROJECT-RESEND role={created.RootElement.GetProperty("role").GetString()}, old-project={project}, new-project={scope ?? "null (organization)"}");
        await Assert.That(platform.Producer.Failures).IsEmpty();
        await Assert.That(scope).IsEqualTo(project).Because("resending a project invitation must preserve its grant boundary");
    }

    // A resend keeps a project invitation's project and role; it never becomes an organization invitation.
    [Test]
    public async Task ResendKeepsAProjectInvitationsProjectAndRole()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("project-resend-owner");
        using var client = platform.Client(replica: true);
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var business = platform.Producer.AddOrganization("Project resend", owner.Subject, owner.Email);
        var project = platform.Producer.AddProject(business, "Scoped project", owner.Subject);
        var invitation = platform.Producer.InvitationId(business, platform.Producer.AddInvitation(business, "project-member@example.test", "member", owner.Subject, project));
        var csrf = await SwitchAsync(client, business);
        await platform.PlanAsync(business, OrganizationRecord.StarterPlanId);
        using (var resent = await HttpJourney.PostFormAsync(client, "/api/v1/access/invitations/" + invitation + "/resend?tab=invitations", new() { ["_csrf"] = csrf }))
            await Assert.That(resent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var created = JsonDocument.Parse(platform.Producer.Bodies("POST", "/v1/terrascale/organizations/" + business + "/invitations").Single());
        await Assert.That(created.RootElement.GetProperty("role").GetString()).IsEqualTo("member");
        await Assert.That(created.RootElement.GetProperty("project_id").GetString()).IsEqualTo(project);
        await Assert.That(platform.Producer.InvitationStatus(business, invitation)).IsEqualTo("revoked");
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    // A project identity or role the dashboard cannot confirm refuses the resend before anything changes at
    // ts-auth: the old invitation stays pending and nothing is created from defaults in its place.
    [Test]
    public async Task ResendRefusesAProjectInvitationWithoutAKnownProjectOrRole()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("project-identity-owner");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var business = platform.Producer.AddOrganization("Project identity", owner.Subject, owner.Email);
        var csrf = await SwitchAsync(client, business);
        var deleted = new TerraScaleIdGenerator().NewId();
        var project = platform.Producer.AddProject(business, "Known project", owner.Subject);
        foreach (var (email, scope, role, status) in new[]
        {
            ("empty@example.test", "", "admin", HttpStatusCode.Conflict), ("malformed@example.test", "../projects/other", "admin", HttpStatusCode.Conflict),
            ("deleted@example.test", deleted, "admin", HttpStatusCode.Conflict), ("blank-role@example.test", project, "", HttpStatusCode.UnprocessableEntity),
            ("unknown-role@example.test", project, "superadmin", HttpStatusCode.UnprocessableEntity),
        })
        {
            var invitation = platform.Producer.InvitationId(business, platform.Producer.AddInvitation(business, email, role, owner.Subject, scope));
            using var resent = await HttpJourney.PostFormAsync(client, "/api/v1/access/invitations/" + invitation + "/resend?tab=invitations", new() { ["_csrf"] = csrf });
            await Assert.That(resent.StatusCode).IsEqualTo(status).Because(email);
            await Assert.That(platform.Producer.InvitationStatus(business, invitation)).IsEqualTo("pending").Because(email);
            await Assert.That(platform.Producer.Count("DELETE", "/v1/terrascale/organizations/" + business + "/invitations/" + invitation, owner.Subject)).IsEqualTo(0).Because(email);
        }
        await Assert.That(platform.Producer.Bodies("POST", "/v1/terrascale/organizations/" + business + "/invitations")).IsEmpty();
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    private static async Task<string> SwitchAsync(HttpClient client, string organizationId)
    {
        var csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using (var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = organizationId, ["returnUrl"] = "/organization" }))
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        return await HttpJourney.CsrfAsync(client, "/organization?tab=invitations");
    }

    [Test]
    [Arguments("closure_conflict")]
    [Arguments("binding_conflict")]
    public async Task AccountClosureConflictsKeepDashboardIdentityTokensAndSession(string code)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("pending-account");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var personal = (await Me(client)).GetProperty("organizationId").GetString()!;
        await HttpJourney.CreatePatAsync(client, "Pending deletion proof", owner.Password);
        {
            platform.Producer.RefuseNext("DELETE", "/v1/terrascale/account", HttpStatusCode.Conflict, code, "Private producer text must stay hidden.");
            var csrf = await HttpJourney.CsrfAsync(client, "/profile?tab=danger");
            using var deletion = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete", new()
            { ["_csrf"] = csrf, ["email"] = owner.Email });
            await Assert.That(deletion.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            var html = await deletion.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("Deletion is paused");
            await Assert.That(html.Contains("Private producer text", StringComparison.Ordinal)).IsFalse();
            var key = AccountService.AccountStorageKey(owner.Subject);
            foreach (var collection in new[] { AccountService.AccountsCollection, AccountService.IdentitiesCollection, AccessTokens.Collection })
                await Assert.That(await platform.StoredAsync(collection, key)).IsTrue();
            await Assert.That(await platform.StoredAsync(AccountService.OrganizationsCollection, personal)).IsTrue();
            await Assert.That((await Me(client)).GetProperty("organizationId").GetString()).IsEqualTo(personal);
        }
        platform.Producer.DeletionStatus = HttpStatusCode.Accepted;
        platform.Producer.RefuseNext("DELETE", "/v1/terrascale/account", HttpStatusCode.Accepted, "pending", "");
        using var retry = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete", new()
        { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/profile?tab=danger"), ["email"] = owner.Email });
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(platform.Producer.Count("DELETE", "/v1/terrascale/account", owner.Subject)).IsEqualTo(2);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OneAccountSubmitPollsProofWithoutBearerAndPreservesSurvivingOrganizations(bool becomesShared)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("receipt-account");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var personal = (await Me(client)).GetProperty("organizationId").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/profile?tab=danger");
        platform.Producer.RefuseNext("DELETE", "/v1/terrascale/account", HttpStatusCode.Accepted, "pending", "");
        using (var pending = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete", new() { ["_csrf"] = csrf, ["email"] = owner.Email }))
        {
            await Assert.That(pending.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var progress = await client.GetAsync(pending.Headers.Location);
            await Assert.That(progress.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            var html = await progress.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("Deletion in progress").And.Contains("requestSubmit()").And.Contains("/profile/delete/continue");
            await Assert.That(platform.Producer.ReceiptAppearsIn(html)).IsFalse();
        }
        var key = AccountService.AccountStorageKey(owner.Subject);
        foreach (var status in new[] { HttpStatusCode.Accepted, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable })
        {
            platform.Producer.DeletionStatus = status;
            using var pending = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete/continue", new() { ["_csrf"] = csrf });
            await Assert.That(pending.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(pending.Headers.Location!.OriginalString.StartsWith("/account-deletion", StringComparison.Ordinal)).IsTrue();
            using var retryPage = await client.GetAsync(pending.Headers.Location);
            await Assert.That(retryPage.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
            await Assert.That(await retryPage.Content.ReadAsStringAsync()).Contains("requestSubmit()");
            await Assert.That(await platform.StoredAsync(AccountService.AccountsCollection, key)).IsTrue();
            await Assert.That(await platform.StoredAsync(AccountService.OrganizationsCollection, personal)).IsTrue();
        }
        if (becomesShared) platform.Producer.AddMember(personal, "other-owner", "other@example.test", "owner");
        using (var logout = await HttpJourney.PostFormAsync(client, "/api/v1/auth/logout", new() { ["_csrf"] = csrf }))
            await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        platform.Producer.DeletionStatus = HttpStatusCode.NotFound;
        using (var renewed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete/continue", new() { ["_csrf"] = csrf }))
            await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using (var logout = await HttpJourney.PostFormAsync(client, "/api/v1/auth/logout", new() { ["_csrf"] = csrf }))
            await Assert.That(logout.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        platform.Producer.CompleteAccountDeletion(owner.Subject);
        using (var refreshed = await client.GetAsync("/account-deletion"))
            await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        using (var forbidden = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete/continue", new()))
            await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using (var completed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/delete/continue", new() { ["_csrf"] = csrf }))
        {
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(completed.Headers.Location!.OriginalString).IsEqualTo("/account-deleted");
        }
        await Assert.That(await platform.ErasedAccountAsync(key)).IsTrue();
        await Assert.That(await platform.StoredAsync(AccountService.OrganizationsCollection, personal)).IsTrue();
        await Assert.That(await platform.ErasedOrganizationAsync(personal)).IsEqualTo(!becomesShared);
        await Assert.That(await client.GetStringAsync("/account-deleted")).Contains("Account deleted");
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    public async Task PendingProjectClosuresAndConflictsKeepOrganizationUntilAConfirmedRetry()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("pending-organization");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var personal = (await Me(client)).GetProperty("organizationId").GetString()!;
        var business = platform.Producer.AddOrganization("Pending organization", owner.Subject, owner.Email);
        var project = platform.Producer.AddProject(business, "Pending project", owner.Subject);
        var csrf = await SwitchAsync(client, business);
        foreach (var (status, code) in new[] { (HttpStatusCode.Accepted, "pending"), (HttpStatusCode.Conflict, "closure_conflict"), (HttpStatusCode.Conflict, "binding_conflict") })
        {
            platform.Producer.RefuseNext("DELETE", "/v1/terrascale/organizations/" + business + "/projects/" + project, status, code, "Private producer text must stay hidden.");
            using var deletion = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/deletion", new()
            { ["_csrf"] = csrf, ["confirmation"] = "Pending organization" });
            await Assert.That(deletion.StatusCode).IsEqualTo(status == HttpStatusCode.Accepted ? HttpStatusCode.Accepted : HttpStatusCode.Conflict);
            var html = await deletion.Content.ReadAsStringAsync();
            await Assert.That(html).Contains(status == HttpStatusCode.Accepted ? "Deletion in progress" : "Deletion is paused");
            await Assert.That(html.Contains("Private producer text", StringComparison.Ordinal)).IsFalse();
            await Assert.That(platform.Producer.Count("DELETE", "/v1/terrascale/organizations/" + business, owner.Subject)).IsEqualTo(0);
            var saved = await platform.ProjectionAsync(business);
            await Assert.That(saved.Archived).IsFalse();
            await Assert.That(saved.Status).IsEqualTo("active");
            await Assert.That((await Me(client)).GetProperty("organizationId").GetString()).IsEqualTo(business);
        }
        using (var confirmed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/deletion", new()
        { ["_csrf"] = csrf, ["confirmation"] = "Pending organization" }))
            await Assert.That(confirmed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(platform.Producer.Exists(business)).IsFalse();
        await Assert.That((await platform.ProjectionAsync(business)).Archived).IsTrue();
        await Assert.That((await Me(client)).GetProperty("organizationId").GetString()).IsEqualTo(personal);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingStandaloneProjectDeletionKeepsItsSettingsAndSelection(bool settings)
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("pending-project");
        using var client = platform.Client();
        using (var login = await HttpJourney.LoginAsync(client, owner.Email, owner.Password))
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var business = platform.Producer.AddOrganization("Project company", owner.Subject, owner.Email);
        platform.Producer.AddProject(business, "Keep project", owner.Subject);
        var project = platform.Producer.AddProject(business, "Pending project", owner.Subject);
        var csrf = await SwitchAsync(client, business);
        await platform.ProjectionAsync(business, org => org.Projects[project] = new() { Id = project, OrganizationId = business, Region = "GRU" });
        platform.Producer.RefuseNext("DELETE", "/v1/terrascale/organizations/" + business + "/projects/" + project, HttpStatusCode.Accepted, "pending", "");
        var path = settings ? "/api/v1/dashboard/settings/delete-project" : "/api/v1/dashboard/organization/projects/" + project + "/delete";
        using var deletion = await HttpJourney.PostFormAsync(client, path + "?organizationId=" + business + "&projectId=" + project, new()
        { ["_csrf"] = csrf, ["confirmation"] = "Pending project", ["confirmName"] = "Pending project", ["projectId"] = project });
        await Assert.That(deletion.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(await deletion.Content.ReadAsStringAsync()).Contains("Deletion in progress").And.Contains("requestSubmit()");
        await Assert.That((await deletion.Content.ReadAsStringAsync()).Contains("projectId=", StringComparison.Ordinal)).IsFalse();
        await Assert.That((await platform.ProjectionAsync(business)).Projects[project].Region).IsEqualTo("GRU");
        await Assert.That((await Me(client)).GetProperty("organizationId").GetString()).IsEqualTo(business);
        platform.Producer.RemoveProject(business, project); // The background closure wins before the automatic continuation.
        using (var rename = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/rename?organizationId=" + business, new()
        { ["_csrf"] = csrf, ["name"] = "Renamed company" }))
            await Assert.That(rename.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var completed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/projects/" + project + "/delete?organizationId=" + business, new()
        { ["_csrf"] = csrf, ["confirmation"] = "Pending project" });
        await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That((await platform.ProjectionAsync(business)).Projects.ContainsKey(project)).IsFalse();
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    [Test]
    public async Task TwoFactorStatusPolicyAndEnforcementFollowTsAuth()
    {
        await using var platform = await PlatformScenario.StartAsync();
        var owner = platform.Principal("mfa-owner");
        var member = platform.Principal("mfa-member");
        using var ownerClient = platform.Client();
        using var memberClient = platform.Client(replica: true);
        using (var login = await HttpJourney.LoginAsync(ownerClient, owner.Email, owner.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using (var login = await HttpJourney.LoginAsync(memberClient, member.Email, member.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var business = platform.Producer.AddOrganization("Secure company", owner.Subject, owner.Email);
        platform.Producer.AddMember(business, member.Subject, member.Email, "member");
        platform.Producer.AddAudit(business, owner.Subject, "organization.mfa_required", "mfa-audit-" + business);
        await SelectAsync(ownerClient, business);

        var security = await ownerClient.GetStringAsync("/profile?tab=security");
        await Assert.That(security).Contains("Not enabled").And.Contains("Set up two-factor").And.Contains("https://auth.terrascale.tech/ui/terrascale/security");
        var csrf = await HttpJourney.CsrfAsync(ownerClient, "/organization");
        using (var refused = await HttpJourney.PostFormAsync(ownerClient, "/api/v1/dashboard/organization/mfa-policy", new() { ["_csrf"] = csrf, ["requireMfa"] = "true" }))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Turn on two-factor for your own account first.").And.Contains("Manage your own two-factor");
        }
        platform.Producer.Enroll(owner.Subject, 7);
        security = await ownerClient.GetStringAsync("/profile?tab=security");
        await Assert.That(security).Contains("Enabled").And.Contains("Manage two-factor").And.Contains(">7<");
        using (var required = await HttpJourney.PostFormAsync(ownerClient, "/api/v1/dashboard/organization/mfa-policy", new() { ["_csrf"] = csrf, ["requireMfa"] = "true" }))
            await Assert.That(required.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(platform.Producer.Bodies("PUT", "/v1/terrascale/organizations/" + business + "/mfa-policy")).IsEquivalentTo(new[] { "{\"require_mfa\":true}", "{\"require_mfa\":true}" }).Because("the refused and the accepted request");
        await Assert.That(await ownerClient.GetStringAsync("/organization")).Contains("Stop requiring two-factor");
        await Assert.That(await ownerClient.GetStringAsync("/organization?tab=audit")).Contains("Two-factor required for members");

        // A member without two-factor still lists the organization, but every page of it is blocked; account pages stay open.
        await SelectAsync(memberClient, business);
        using (var blocked = await memberClient.GetAsync("/"))
        {
            await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await blocked.Content.ReadAsStringAsync()).Contains("Secure company requires two-factor authentication").And.Contains("Set up two-factor");
        }
        using (var json = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dashboard/context"))
        {
            json.Headers.Accept.ParseAdd("application/json");
            using var refused = await memberClient.SendAsync(json);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("mfa_required");
        }
        await Assert.That(await memberClient.GetStringAsync("/profile?tab=security")).Contains("Not enabled");
        await Assert.That((await Me(memberClient)).GetProperty("organizationId").GetString()).IsEqualTo(business).Because("account pages stay open while the organization is blocked");
        platform.Producer.Enroll(member.Subject, 10);
        using (var open = await memberClient.GetAsync("/organization")) await Assert.That(open.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(platform.Producer.Failures).IsEmpty();
    }

    // Selects an organization without opening it, which a blocked member cannot.
    private static async Task SelectAsync(HttpClient client, string organizationId)
    {
        var csrf = await HttpJourney.CsrfAsync(client, "/profile");
        using var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = organizationId, ["returnUrl"] = "/organization" });
        await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
    }

    private static async Task<JsonElement> Me(HttpClient client) => JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me")).RootElement.Clone();
    private static async Task<JsonElement> Context(HttpClient client) => JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/context")).RootElement.Clone();

    private sealed record Principal(string Subject, string Email, string Password);

    private sealed class PlatformScenario : IAsyncDisposable
    {
        private readonly DemoEnvironment environment = new();
        private readonly List<WebApplication> hosts = [];
        private readonly ConcurrentDictionary<string, Principal> principals = new();
        private TestAuth? auth;
        private IDashboardState? state;
        private TestDatabases? databases;
        public PlatformProducer Producer { get; private set; } = null!;
        private Uri origin = null!;
        private Uri replica = null!;

        public static async Task<PlatformScenario> StartAsync()
        {
            var scenario = new PlatformScenario();
            try
            {
                await scenario.environment.InitializeAsync();
                scenario.origin = await scenario.HostAsync("http://127.0.0.1");
                scenario.replica = await scenario.HostAsync(scenario.origin.ToString().TrimEnd('/'));
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }

        private async Task<Uri> HostAsync(string publicOrigin)
        {
            var app = environment.CreateTestHost(Root, 0, publicOrigin, builder =>
            {
                auth ??= (TestAuth)builder.Services.Last(service => service.ServiceType == typeof(IAuthGateway)).ImplementationInstance!;
                state ??= (IDashboardState)builder.Services.Last(service => service.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
                databases ??= (TestDatabases)builder.Services.Last(service => service.ServiceType == typeof(IDatabaseBackend)).ImplementationInstance!;
                Producer ??= new(auth.ResolveAccessToken);
                var fallback = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new Factory(Producer, fallback)));
                // The production transport, not the in-memory platform used by other journeys.
                builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthPlatform, TsAuthGateway>());
            });
            app.MapGet("/__test/project-context", async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databaseService) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null) return Results.Unauthorized();
                if (http.Request.Query["refresh"].Count > 0)
                    context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: http.Request.Query["refresh"] == "project");
                await databaseService.SyncAsync(context, http.RequestAborted);
                var org = context.Organization!;
                return Results.Json(new
                {
                    role = context.Project?.Role,
                    restricted = !context.CanManage && !context.CanWrite && !org.CanProvision(context.UserId) && org.Members.Count == 0 &&
                        org.Invitations.Count == 0 && org.AuditEvents.Count == 0 && org.OwnerUserId == "" && org.PendingOwnerUserId == "" && org.InternalBilling is null,
                    roleStored = JsonSerializer.SerializeToElement(context.Project).TryGetProperty("Role", out _),
                    projection = JsonSerializer.Serialize(org),
                    view = TerraScale.Dashboard.Features.Accounts.AccountContextView.From(context)
                });
            });
            hosts.Add(app);
            await app.StartAsync();
            return new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        }

        public Principal Principal(string name) => principals.GetOrAdd(name, key =>
        {
            var principal = new Principal(new TerraScaleIdGenerator().NewId(), key + "-" + Guid.NewGuid().ToString("N")[..8] + "@example.test", "E2E-" + Guid.NewGuid().ToString("N"));
            auth!.RegisterFixturePrincipal(new(principal.Subject, key, principal.Email, principal.Password));
            return principal;
        });

        public async Task<bool> StoredAsync(string collection, string key) => await state!.GetAsync(collection, key, default) is not null;
        public Task StoreAsync(OrganizationRecord org) => state!.PutAsync(AccountService.OrganizationsCollection, org.Id,
            JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Absent, default);
        public async Task<string?> VersionAsync(string org) => (await state!.GetAsync(AccountService.OrganizationsCollection, org, default))?.Version;
        public Task FenceAsync(string org) => AccountService.FenceOrganizationDeletionAsync(state!, org, AccountService.NewId(), default);
        public void SeedDatabase(ManagedDatabase database) => databases!.SeedFixtureDatabase(database);
        public async Task<bool> ErasedAccountAsync(string key) => AccountService.IsDeletionFence((await state!.GetAsync(AccountService.AccountsCollection, key, default))!);
        public async Task<bool> ErasedOrganizationAsync(string key) => AccountService.IsDeletionFence((await state!.GetAsync(AccountService.OrganizationsCollection, key, default))!);

        public Task PlanAsync(string organizationId, string planId) => ProjectionAsync(organizationId, organization => organization.PlanId = planId);

        public async Task<OrganizationRecord> ProjectionAsync(string organizationId, Action<OrganizationRecord>? change = null)
        {
            var current = await state!.GetAsync(AccountService.OrganizationsCollection, organizationId, default)
                ?? throw new InvalidOperationException("The fixture targets an existing organization");
            var organization = current.Document.Deserialize<OrganizationRecord>()!;
            if (change is null) return organization;
            change(organization);
            await state.PutAsync(AccountService.OrganizationsCollection, organizationId, JsonSerializer.SerializeToElement(organization),
                TerraBasePrecondition.Matches(current.Version), default);
            return organization;
        }

        public HttpClient Client(bool replica = false) => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = replica ? this.replica : origin, Timeout = TimeSpan.FromSeconds(30) };

        public async ValueTask DisposeAsync()
        {
            foreach (var host in hosts) { await host.StopAsync(); await host.DisposeAsync(); }
            hosts.Clear();
        }

        private static string Root { get; } = FindRoot();
        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "TerraScale.Dashboard.slnx"))) return directory.FullName;
            throw new DirectoryNotFoundException("The E2E executable must run from the dashboard checkout.");
        }
    }

    private sealed class Factory(PlatformProducer producer, IHttpClientFactory fallback) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Router(producer, fallback.CreateClient(name)), disposeHandler: true);
    }

    private sealed class Router(PlatformProducer producer, HttpClient fallback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            producer.ObservePlan(request);
            return request.RequestUri is { Host: "auth.terrascale.tech" } uri && uri.AbsolutePath.StartsWith("/v1/terrascale/", StringComparison.Ordinal)
                ? producer.HandleAsync(request, cancellationToken)
                : HttpForwarding.SendAsync(fallback, request, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) fallback.Dispose(); base.Dispose(disposing); }
    }

    // Producer-shaped ts-auth platform API: snake_case bodies, {items,next} pages,
    // {"error":{"code","message"}} refusals and Bearer sign-in tokens.
    private sealed class PlatformProducer(Func<string, (string UserId, string Email)?> resolve)
    {
        private const int PageSize = 2;
        private readonly object gate = new();
        private readonly TerraScaleIdGenerator ids = new();
        private readonly List<Organization> organizations = [];
        private readonly Dictionary<string, int> enrolled = [];
        private readonly ConcurrentQueue<(string Method, string Path, string User, string Body)> calls = new();
        private readonly ConcurrentQueue<string> failures = new();
        private readonly ConcurrentQueue<string> plans = new();
        private (string Method, string Path, HttpStatusCode Status, string Code, string Message)? refusal;
        private int pagedRequests;
        private readonly Dictionary<string, string> deletionReceipts = [];
        private readonly HashSet<string> deletedUsers = [];
        private bool deletionRefused;
        public Dictionary<string, object> InvitationReplies { get; } = [];
        public HttpStatusCode DeletionStatus { get; set; } = HttpStatusCode.Accepted;
        public bool ReceiptAppearsIn(string html) { lock (gate) return deletionReceipts.Keys.Any(receipt => html.Contains(receipt, StringComparison.Ordinal)); }
        public void CompleteAccountDeletion(string userId)
        {
            lock (gate)
            {
                foreach (var org in organizations.ToArray())
                    if (org.Members.Remove(userId) && org.Members.Count == 0) organizations.Remove(org);
                DeletionStatus = HttpStatusCode.NoContent;
                deletedUsers.Add(userId);
            }
        }
        public IReadOnlyList<string> Failures => failures.ToArray();
        public int PagedRequests => pagedRequests;

        public int Count(string method, string path, string user) => calls.Count(call => call.Method == method && call.Path == path && call.User == user);
        public int Mutations(string org, string user) => calls.Count(call => call.User == user && call.Method != "GET" && call.Path.StartsWith("/v1/terrascale/organizations/" + org, StringComparison.Ordinal));
        public void ObservePlan(HttpRequestMessage request) { if (request.Method == HttpMethod.Put) plans.Enqueue(request.RequestUri!.AbsolutePath); }
        public int PlanPublications(string org) => plans.Count(path => path == "/admin/organizations/" + org + "/plan");
        public IReadOnlyList<string> Bodies(string method, string path) => calls.Where(call => call.Method == method && call.Path == path).Select(call => call.Body).ToArray();
        public void RefuseNext(string method, string path, HttpStatusCode status, string code, string message) { lock (gate) refusal = (method, path, status, code, message); }

        public string AddOrganization(string name, string ownerId, string ownerEmail = "owner@example.test")
        {
            lock (gate)
            {
                var org = new Organization(ids.NewId(), ownerId, DateTimeOffset.UtcNow) { Name = name };
                org.Members[ownerId] = new(ownerEmail, "owner", DateTimeOffset.UtcNow);
                organizations.Add(org);
                return org.Id;
            }
        }
        public void Rename(string organizationId, string name) { lock (gate) Find(organizationId)!.Name = name; }
        public void RemoveProject(string organizationId, string projectId) { lock (gate) Find(organizationId)!.Projects.RemoveAll(project => project.Id == projectId); }
        public bool Exists(string organizationId) { lock (gate) return Find(organizationId) is not null; }
        public string AddProject(string organizationId, string name, string creator)
        { lock (gate) { var project = new Project(ids.NewId(), name, creator, DateTimeOffset.UtcNow); Find(organizationId)!.Projects.Add(project); AddCollaborator(organizationId, creator, project.Id, "admin"); return project.Id; } }
        public void AddMember(string organizationId, string userId, string email, string role) { lock (gate) Find(organizationId)!.Members[userId] = new(email, role, DateTimeOffset.UtcNow); }
        public void AddCollaborator(string organizationId, string userId, string? projectId = null, string role = "viewer")
        { lock (gate) { var org = Find(organizationId)!; if (!org.Collaborators.ContainsKey(userId)) org.Collaborators[userId] = []; if (projectId is not null) org.Collaborators[userId][projectId] = role; } }
        public void RemoveGrant(string org, string user, string project) { lock (gate) Find(org)!.Collaborators[user].Remove(project); }
        public void RequireMfa(string org) { lock (gate) Find(org)!.RequireMfa = true; }
        public void Enroll(string userId, int recoveryCodes) { lock (gate) enrolled[userId] = recoveryCodes; }
        public void RemoveMember(string organizationId, string userId) { lock (gate) { var org = Find(organizationId)!; org.Members.Remove(userId); org.Collaborators.Remove(userId); } }
        public string? Role(string organizationId, string userId) { lock (gate) return Find(organizationId)!.Members.GetValueOrDefault(userId)?.Role; }
        public string InvitationId(string organizationId, string token) { lock (gate) return Find(organizationId)!.Invitations.Single(invitation => invitation.Token == token).Id; }
        public void ExpireInvitation(string org, string token) { lock (gate) Find(org)!.Invitations.Single(invitation => invitation.Token == token).ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1); }
        public string? InvitationStatus(string organizationId, string invitationId) { lock (gate) return Find(organizationId)!.Invitations.SingleOrDefault(invitation => invitation.Id == invitationId)?.Status; }
        public string AddInvitation(string organizationId, string email, string role, string invitedBy, string? projectId = null)
        {
            lock (gate)
            {
                var token = "tsi_" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..20];
                Find(organizationId)!.Invitations.Add(new(ids.NewId(), token, email, role, invitedBy, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), projectId));
                return token;
            }
        }
        public void StartTransfer(string organizationId, string from, string to) { lock (gate) Find(organizationId)!.Transfer = (from, to, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7)); }
        public void AddAudit(string organizationId, string actor, string action, string target)
        { lock (gate) Find(organizationId)!.Audit.Add(new { id = ids.NewId(), actor, action, target, detail = (string?)null, occurred_at = DateTimeOffset.UtcNow }); }

        public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                Require(request.RequestUri.Scheme == "https" && request.RequestUri.IsDefaultPort, "ts-auth is called at its HTTPS global issuer origin.");
                Require(request.Headers.Accept.Any(value => value.MediaType == "application/json"), "Platform calls accept JSON.");
                Require(request.Content is null || request.Content.Headers.ContentType?.MediaType == "application/json", "Platform bodies are JSON.");
                var receipt = request.Headers.TryGetValues("X-TerraScale-Deletion-Receipt", out var values) ? values.Single() : "";
                if (path == "/v1/terrascale/account/deletion")
                {
                    Require(request.Headers.Authorization is null, "A deletion receipt is polled without bearer credentials.");
                    lock (gate)
                    {
                        if (!deletionReceipts.ContainsKey(receipt)) return Error(HttpStatusCode.NotFound, "not_found", "Unknown receipt.");
                        var organization = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query).GetValueOrDefault("organization").ToString();
                        return organization.Length > 0 ? new(Find(organization) is null ? HttpStatusCode.NoContent : HttpStatusCode.Accepted)
                            : DeletionStatus == HttpStatusCode.Conflict ? Error(DeletionStatus, deletionRefused ? "deletion_refused" : "closure_conflict", "Deletion refused.") : new(DeletionStatus);
                    }
                }
                if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } bearer } || resolve(bearer) is not { } caller)
                    return Error(HttpStatusCode.Unauthorized, "unauthorized", "Sign in again.");
                calls.Enqueue((request.Method.Method, path, caller.UserId, body));
                lock (gate)
                {
                    if (deletedUsers.Contains(caller.UserId)) return Error(HttpStatusCode.Unauthorized, "unauthorized", "User deleted.");
                    if (path == "/v1/terrascale/account")
                    {
                        Require(receipt.Length == 43, "Account deletion carries a canonical receipt.");
                        deletionReceipts[receipt] = caller.UserId;
                    }
                    if (refusal is { } refused && refused.Method == request.Method.Method && refused.Path == path)
                    {
                        refusal = null;
                        if (path == "/v1/terrascale/account" && refused.Status == HttpStatusCode.Conflict) { DeletionStatus = HttpStatusCode.Conflict; deletionRefused = true; }
                        return Error(refused.Status, refused.Code, refused.Message);
                    }
                    return Route(request.Method.Method, path.Split('/', StringSplitOptions.RemoveEmptyEntries)[2..], request.RequestUri.Query, body, caller);
                }
            }
            catch (InvalidOperationException error)
            {
                failures.Enqueue(error.Message);
                return Error(HttpStatusCode.InternalServerError, "test_contract_failure", "Test-only contract failure.");
            }
        }

        private HttpResponseMessage Route(string method, string[] parts, string query, string body, (string UserId, string Email) caller)
        {
            if (parts is ["account", "providers"] && method == "GET")
                return Json(new { providers = Array.Empty<string>() });
            using var json = body.Length == 0 ? null : JsonDocument.Parse(body);
            string Field(string name) => json?.RootElement.TryGetProperty(name, out var value) == true ? value.GetString() ?? "" : "";
            if (parts is ["account"] && method == "DELETE") return new(HttpStatusCode.Accepted);
            if (parts is ["organizations"] && method == "GET")
                return Page(organizations.Where(org => org.Members.ContainsKey(caller.UserId) || org.Collaborators.TryGetValue(caller.UserId, out var grants) &&
                    org.Projects.Any(project => grants.ContainsKey(project.Id))).Select(org => Wire(org, caller.UserId)), query);
            if (parts is ["organizations"] && method == "POST")
            {
                var created = new Organization(ids.NewId(), caller.UserId, DateTimeOffset.UtcNow) { Name = Field("name") };
                created.Members[caller.UserId] = new(caller.Email, "owner", DateTimeOffset.UtcNow);
                organizations.Add(created);
                return Json(Wire(created, caller.UserId), HttpStatusCode.Created);
            }
            if (parts is ["account", "mfa"] && method == "GET")
                return Json(new { enabled = enrolled.ContainsKey(caller.UserId), recovery_codes_remaining = enrolled.GetValueOrDefault(caller.UserId), manage_url = "https://auth.terrascale.tech/ui/terrascale/security" });
            if (parts is ["invitations", var action] && method == "POST")
            {
                var (org, invitation) = organizations.SelectMany(item => item.Invitations.Select(invite => (item, invite)))
                    .FirstOrDefault(item => item.invite.Token == Field("token") && item.invite.Status == "pending");
                if (invitation is null) return Error(HttpStatusCode.NotFound, "not_found", "Invitation not found.");
                if (action == "lookup")
                {
                    if (invitation.ExpiresAt <= DateTimeOffset.UtcNow) return Error(HttpStatusCode.NotFound, "not_found", "Invitation unavailable.");
                    var offered = org.Projects.SingleOrDefault(project => project.Id == invitation.ProjectId);
                    if (invitation.ProjectId is not null && offered is null) return Error(HttpStatusCode.NotFound, "not_found", "Project unavailable.");
                    return InvitationJson(action, new
                    {
                        organization = new { id = org.Id, name = org.Name },
                        project = offered is null ? null : new { id = offered.Id, name = offered.Name },
                        email = invitation.Email,
                        role = invitation.Role,
                        expires_at = invitation.ExpiresAt
                    });
                }
                if (!invitation.Email.Equals(caller.Email, StringComparison.OrdinalIgnoreCase)) return Error(HttpStatusCode.Forbidden, "email_mismatch", "Sign in with the invited email.");
                if (invitation.ExpiresAt <= DateTimeOffset.UtcNow) return Error(HttpStatusCode.Conflict, "invitation_expired", "Invitation expired.");
                invitation.Status = action == "accept" ? "accepted" : "declined";
                if (action == "decline") return new(HttpStatusCode.NoContent);
                if (invitation.ProjectId is { } projectId) AddCollaborator(org.Id, caller.UserId, projectId, invitation.Role);
                else org.Members[caller.UserId] = new(caller.Email, invitation.Role, DateTimeOffset.UtcNow);
                return InvitationJson(action, new { organization_id = org.Id, project_id = invitation.ProjectId, role = invitation.Role });
            }
            Require(parts.Length >= 2 && parts[0] == "organizations", "Unknown platform route.");
            var organization = Find(parts[1]);
            var self = organization?.Members.GetValueOrDefault(caller.UserId);
            var granted = organization?.Collaborators.GetValueOrDefault(caller.UserId);
            if (organization is null || self is null && (granted is null || !organization.Projects.Any(project => granted.ContainsKey(project.Id))))
                return Error(HttpStatusCode.NotFound, "not_found", "Organization not found.");
            if (organization.RequireMfa && !enrolled.ContainsKey(caller.UserId))
                return Error(HttpStatusCode.Forbidden, "mfa_required", "This organization requires two-factor authentication.");
            if (self is null)
            {
                if (method == "GET" && parts[2..] is ["projects"])
                    return Page(organization.Projects.Where(project => granted!.ContainsKey(project.Id)).Select(project => Wire(organization, project, caller.UserId)), query);
                if (method == "GET" && parts[2..] is ["projects", var requested])
                {
                    var found = organization.Projects.SingleOrDefault(project => project.Id == requested && granted!.ContainsKey(project.Id));
                    return found is null ? Error(HttpStatusCode.NotFound, "not_found", "Project not found.") : Json(Wire(organization, found, caller.UserId));
                }
                if (method == "GET" && parts.Length == 2) return Json(Wire(organization, caller.UserId));
                return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs organization membership.");
            }
            var admin = self.Role is "owner" or "admin";
            switch (parts[2..], method)
            {
                case ([], "GET"):
                    return Json(Wire(organization, caller.UserId));
                case (["mfa-policy"], "PUT"):
                    if (self.Role != "owner") return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs owner.");
                    var requireMfa = json!.RootElement.GetProperty("require_mfa").GetBoolean();
                    if (requireMfa && !enrolled.ContainsKey(caller.UserId)) return Error(HttpStatusCode.Conflict, "mfa_not_enrolled", "Turn on two-factor first.");
                    organization.RequireMfa = requireMfa;
                    return Json(Wire(organization, caller.UserId));
                case ([], "DELETE"):
                    if (self.Role != "owner") return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs owner.");
                    if (organization.Projects.Count > 0) return Error(HttpStatusCode.Conflict, "not_empty", "Delete the organization's projects first.");
                    organizations.Remove(organization);
                    return new(HttpStatusCode.NoContent);
                case (["projects", var deletedProject], "DELETE"):
                    if (!admin) return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs admin.");
                    return organization.Projects.RemoveAll(project => project.Id == deletedProject) == 1 ? new(HttpStatusCode.NoContent) : Error(HttpStatusCode.NotFound, "not_found", "Project not found.");
                case ([], "PATCH"):
                    if (!admin) return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs admin.");
                    organization.Name = Field("name");
                    return Json(Wire(organization, caller.UserId));
                case (["members"], "GET"):
                    return Page(organization.Members.Select(item => (object)new { user_id = item.Key, email = item.Value.Email, role = item.Value.Role, joined_at = item.Value.JoinedAt }), query);
                case (["members", var user, "role"], "PUT"):
                    if (!admin) return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs admin.");
                    var target = organization.Members[user];
                    Require(Field("role") is "viewer" or "member" or "admin" or "owner", "Role is an organization role.");
                    organization.Members[user] = target with { Role = Field("role") };
                    return Json(new { user_id = user, email = target.Email, role = Field("role"), joined_at = target.JoinedAt });
                case (["projects"], "GET"):
                    return Page(organization.Projects.Select(project => Wire(organization, project, caller.UserId)), query);
                case (["projects"], "POST"):
                    if (self.Role == "viewer") return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs member.");
                    if (organization.Projects.Any(project => project.Name == Field("name"))) return Error(HttpStatusCode.Conflict, "name_taken", "Name taken.");
                    var created = new Project(ids.NewId(), Field("name"), caller.UserId, DateTimeOffset.UtcNow);
                    organization.Projects.Add(created);
                    AddCollaborator(organization.Id, caller.UserId, created.Id, "admin");
                    return Json(Wire(organization, created, caller.UserId), HttpStatusCode.Created);
                case (["invitations"], "POST"):
                    if (!admin) return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs admin.");
                    var projectId = Field("project_id");
                    var token = AddInvitation(organization.Id, Field("email"), Field("role"), caller.UserId, projectId.Length == 0 ? null : projectId);
                    var createdInvitation = organization.Invitations.Single(invitation => invitation.Token == token);
                    return Json(new
                    {
                        invitation = new
                        {
                            id = createdInvitation.Id,
                            email = createdInvitation.Email,
                            role = createdInvitation.Role,
                            project_id = createdInvitation.ProjectId,
                            invited_by = createdInvitation.InvitedBy,
                            created_at = createdInvitation.CreatedAt,
                            expires_at = createdInvitation.ExpiresAt,
                            status = createdInvitation.Status
                        },
                        token
                    }, HttpStatusCode.Created);
                case (["invitations", var invitationId], "DELETE"):
                    if (!admin) return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs admin.");
                    var revokedInvitation = organization.Invitations.SingleOrDefault(invitation => invitation.Id == invitationId);
                    if (revokedInvitation is null) return Error(HttpStatusCode.NotFound, "not_found", "Invitation unavailable.");
                    revokedInvitation.Status = "revoked";
                    return new(HttpStatusCode.NoContent);
                case (["invitations"], "GET"):
                    if (!admin) return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs admin.");
                    return Page(organization.Invitations.Select(invite => (object)new
                    { id = invite.Id, email = invite.Email, role = invite.Role, project_id = invite.ProjectId, invited_by = invite.InvitedBy, created_at = invite.CreatedAt, expires_at = invite.ExpiresAt, status = invite.Status }), query);
                case (["ownership-transfer"], "GET"):
                    return organization.Transfer is { } transfer
                        ? Json(new { from_user_id = transfer.From, to_user_id = transfer.To, created_at = transfer.CreatedAt, expires_at = transfer.ExpiresAt })
                        : Error(HttpStatusCode.NotFound, "not_found", "No pending transfer.");
                case (["audit-events"], "GET"):
                    if (!admin) return Error(HttpStatusCode.Forbidden, "forbidden_role", "Needs admin.");
                    return Page(organization.Audit, query);
                default:
                    throw new InvalidOperationException("Unexpected platform call " + method + " " + string.Join('/', parts));
            }
        }

        private HttpResponseMessage Page(IEnumerable<object> source, string query)
        {
            var parameters = System.Web.HttpUtility.ParseQueryString(query);
            Require(int.TryParse(parameters["limit"], out var limit) && limit is >= 1 and <= 100, "Pages ask for a limit of 1 to 100.");
            var items = source.ToArray();
            var start = parameters["after"] is { } after ? int.Parse(after, System.Globalization.CultureInfo.InvariantCulture) : 0;
            if (start > 0) Interlocked.Increment(ref pagedRequests);
            var page = items.Skip(start).Take(Math.Min(limit, PageSize)).ToArray();
            var next = start + page.Length < items.Length ? (start + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Json(new { items = page, next });
        }

        private static object Wire(Organization org, string userId) => new
        {
            id = org.Id,
            name = org.Name,
            created_by = org.CreatedBy,
            created_at = org.CreatedAt,
            role = org.Members.GetValueOrDefault(userId)?.Role,
            require_mfa = org.RequireMfa
        };
        // Current organization membership and explicit grants alone authorize (owner implies project admin).
        private static object Wire(Organization org, Project project, string userId) => new
        {
            id = project.Id,
            organization_id = org.Id,
            name = project.Name,
            issuer = "https://auth.terrascale.tech/" + project.Id,
            created_by = project.CreatedBy,
            created_at = project.CreatedAt,
            role = EffectiveRole(org, project, userId)
        };
        private static string? EffectiveRole(Organization org, Project project, string user)
        {
            var implied = org.Members.GetValueOrDefault(user)?.Role == "owner"
                ? "admin" : org.Members.GetValueOrDefault(user)?.Role;
            var granted = org.Collaborators.GetValueOrDefault(user)?.GetValueOrDefault(project.Id);
            static int Rank(string? role) => role switch { "admin" => 3, "member" => 2, "viewer" => 1, _ => 0 };
            return Rank(implied) >= Rank(granted) ? implied : granted;
        }
        private Organization? Find(string id) => organizations.FirstOrDefault(org => org.Id == id);
        private HttpResponseMessage InvitationJson(string action, object body) => Json(InvitationReplies.GetValueOrDefault(action, body));
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };
        private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) => Json(new { error = new { code, message } }, status);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private sealed class Organization(string id, string createdBy, DateTimeOffset createdAt)
        {
            public string Id { get; } = id;
            public string Name { get; set; } = "";
            public bool RequireMfa { get; set; }
            public string CreatedBy { get; } = createdBy;
            public DateTimeOffset CreatedAt { get; } = createdAt;
            public Dictionary<string, Member> Members { get; } = [];
            public Dictionary<string, Dictionary<string, string>> Collaborators { get; } = [];
            public List<Project> Projects { get; } = [];
            public List<Invitation> Invitations { get; } = [];
            public List<object> Audit { get; } = [];
            public (string From, string To, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)? Transfer { get; set; }
        }
        private sealed record Member(string Email, string Role, DateTimeOffset JoinedAt);
        private sealed record Project(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt);
        private sealed class Invitation(string id, string token, string email, string role, string invitedBy, DateTimeOffset createdAt, DateTimeOffset expiresAt, string? projectId = null)
        {
            public string? ProjectId { get; } = projectId;
            public string Id { get; } = id;
            public string Token { get; } = token;
            public string Email { get; } = email;
            public string Role { get; } = role;
            public string InvitedBy { get; } = invitedBy;
            public DateTimeOffset CreatedAt { get; } = createdAt;
            public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
            public string Status { get; set; } = "pending";
        }
    }
}
