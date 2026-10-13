using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

// Two dashboard replicas call ts-auth's /v1/terrascale platform API through the
// production TsAuthGateway transport. The producer fake follows terrascale
// e30ffdc2 PlatformEndpoints/InvitationEndpoints; it must be rechecked against
// the corrected exported ts-dotnet auth fixtures before this counts as parity.
public sealed class TsAuthPlatformJourneyTests
{
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
        for (var index = 0; index < 3; index++) platform.Producer.AddOrganization("Unrelated " + index, platform.Principal("platform-unrelated-" + index).Subject);
        var context = await Context(second);
        var organizations = context.GetProperty("organizations").EnumerateArray().ToArray();
        var personal = organizations.Single(org => org.GetProperty("id").GetString() == organizationId);
        await Assert.That(personal.GetProperty("name").GetString()).IsEqualTo("Renamed at ts-auth");
        await Assert.That(personal.GetProperty("kind").GetString()).IsEqualTo("personal");
        await Assert.That(personal.GetProperty("projects").EnumerateObject().Select(item => item.Name).Order().ToArray())
            .IsEquivalentTo(new[] { projectId, outside }.Order().ToArray());
        var shared = organizations.Single(org => org.GetProperty("id").GetString() == business);
        await Assert.That(shared.GetProperty("members").GetProperty(user.Subject).GetProperty("role").GetString()).IsEqualTo("admin");
        await Assert.That(organizations.Any(org => org.GetProperty("id").GetString() == collaboration)).IsFalse()
            .Because("a project collaborator has no organization role and no organization view");

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
        using (var accepted = await HttpJourney.PostFormAsync(inviteeClient, "/invitations/" + token + "/respond", new() { ["_csrf"] = csrf, ["action"] = "accept" }))
        {
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(accepted.Headers.Location?.OriginalString).IsEqualTo("/organization?tab=members&invitation=accepted");
        }
        await Assert.That(platform.Producer.Role(business, invitee.Subject)).IsEqualTo("member");
        await Assert.That((await Me(inviteeClient)).GetProperty("organizationId").GetString()).IsEqualTo(business);

        // A ts-auth refusal reaches the page with its status and the dashboard's message for its code; nothing is stored.
        using var personalClient = platform.Client();
        using (var login = await HttpJourney.LoginAsync(personalClient, member.Email, member.Password)) await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var personal = (await Me(personalClient)).GetProperty("organizationId").GetString()!;
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
        // Review of #13 at 2a04b403: a project made at ts-auth showed in context but saving its cap answered 404.
        var outside = platform.Producer.AddProject(personal, "Made outside", owner.Subject);
        var csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using (var cap = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/usage/spending-controls", new() { ["_csrf"] = csrf, ["projCap_" + outside] = "12.50" }))
        {
            await Assert.That(cap.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(cap.Headers.Location?.OriginalString).IsEqualTo("/usage?tab=settings&saved=ok");
        }
        var project = (await Context(client)).GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == personal)
            .GetProperty("projects").GetProperty(outside);
        await Assert.That(project.GetProperty("spendCapCents").GetInt64()).IsEqualTo(1250);
        await Assert.That(project.GetProperty("name").GetString()).IsEqualTo("Made outside");

        // Review of #13 at 2a04b403: confirming deletion with the name ts-auth now holds answered 422.
        var business = platform.Producer.AddOrganization("Named before", owner.Subject, owner.Email);
        csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using (var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-organization", new()
        { ["_csrf"] = csrf, ["organizationId"] = business, ["returnUrl"] = "/organization" }))
            await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        platform.Producer.Rename(business, "Renamed at ts-auth");
        csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using (var deletion = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/deletion", new()
        { ["_csrf"] = csrf, ["confirmation"] = "Renamed at ts-auth" }))
            await Assert.That(deletion.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var organization = (await Context(client)).GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == business);
        await Assert.That(organization.GetProperty("deletionRequested").GetBoolean()).IsTrue();
        await Assert.That(organization.GetProperty("name").GetString()).IsEqualTo("Renamed at ts-auth");
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

    private static async Task<JsonElement> Me(HttpClient client) => JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me")).RootElement.Clone();
    private static async Task<JsonElement> Context(HttpClient client) => JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/context")).RootElement.Clone();

    private sealed record Principal(string Subject, string Email, string Password);

    private sealed class PlatformScenario : IAsyncDisposable
    {
        private readonly DemoEnvironment environment = new();
        private readonly List<WebApplication> hosts = [];
        private readonly ConcurrentDictionary<string, Principal> principals = new();
        private TestAuth? auth;
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
            var app = environment.CreateHost(Root, 0, publicOrigin, builder =>
            {
                auth ??= (TestAuth)builder.Services.Last(service => service.ServiceType == typeof(IAuthGateway)).ImplementationInstance!;
                Producer ??= new(auth.ResolveAccessToken);
                var fallback = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new Factory(Producer, fallback)));
                // The production transport, not the in-memory platform used by other journeys.
                builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthPlatform, TsAuthGateway>());
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri is { Host: "auth.terrascale.tech" } uri && uri.AbsolutePath.StartsWith("/v1/terrascale/", StringComparison.Ordinal)
                ? producer.HandleAsync(request, cancellationToken)
                : fallback.SendAsync(request, cancellationToken);
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
        private readonly ConcurrentQueue<(string Method, string Path, string User, string Body)> calls = new();
        private readonly ConcurrentQueue<string> failures = new();
        private (string Method, string Path, HttpStatusCode Status, string Code, string Message)? refusal;
        private int pagedRequests;
        public IReadOnlyList<string> Failures => failures.ToArray();
        public int PagedRequests => pagedRequests;

        public int Count(string method, string path, string user) => calls.Count(call => call.Method == method && call.Path == path && call.User == user);
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
        public string AddProject(string organizationId, string name, string creator)
        { lock (gate) { var project = new Project(ids.NewId(), name, creator, DateTimeOffset.UtcNow); Find(organizationId)!.Projects.Add(project); return project.Id; } }
        public void AddMember(string organizationId, string userId, string email, string role) { lock (gate) Find(organizationId)!.Members[userId] = new(email, role, DateTimeOffset.UtcNow); }
        public void AddCollaborator(string organizationId, string userId) { lock (gate) Find(organizationId)!.Collaborators.Add(userId); }
        public void RemoveMember(string organizationId, string userId) { lock (gate) Find(organizationId)!.Members.Remove(userId); }
        public string? Role(string organizationId, string userId) { lock (gate) return Find(organizationId)!.Members.GetValueOrDefault(userId)?.Role; }
        public string InvitationId(string organizationId, string token) { lock (gate) return Find(organizationId)!.Invitations.Single(invitation => invitation.Token == token).Id; }
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
                if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } bearer } || resolve(bearer) is not { } caller)
                    return Error(HttpStatusCode.Unauthorized, "unauthorized", "Sign in again.");
                calls.Enqueue((request.Method.Method, path, caller.UserId, body));
                lock (gate)
                {
                    if (refusal is { } refused && refused.Method == request.Method.Method && refused.Path == path)
                    {
                        refusal = null;
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
            using var json = body.Length == 0 ? null : JsonDocument.Parse(body);
            string Field(string name) => json?.RootElement.TryGetProperty(name, out var value) == true ? value.GetString() ?? "" : "";
            if (parts is ["organizations"] && method == "GET")
                return Page(organizations.Where(org => org.Members.ContainsKey(caller.UserId) || org.Collaborators.Contains(caller.UserId)).Select(org => Wire(org, caller.UserId)), query);
            if (parts is ["organizations"] && method == "POST")
            {
                var created = new Organization(ids.NewId(), caller.UserId, DateTimeOffset.UtcNow) { Name = Field("name") };
                created.Members[caller.UserId] = new(caller.Email, "owner", DateTimeOffset.UtcNow);
                organizations.Add(created);
                return Json(Wire(created, caller.UserId), HttpStatusCode.Created);
            }
            if (parts is ["invitations", var action] && method == "POST")
            {
                var (org, invitation) = organizations.SelectMany(item => item.Invitations.Select(invite => (item, invite)))
                    .FirstOrDefault(item => item.invite.Token == Field("token") && item.invite.Status == "pending");
                if (invitation is null) return Error(HttpStatusCode.NotFound, "not_found", "Invitation not found.");
                if (action == "lookup")
                    return Json(new { organization = new { id = org.Id, name = org.Name }, project = (object?)null, email = invitation.Email, role = invitation.Role, expires_at = invitation.ExpiresAt });
                if (!invitation.Email.Equals(caller.Email, StringComparison.OrdinalIgnoreCase)) return Error(HttpStatusCode.Forbidden, "email_mismatch", "Sign in with the invited email.");
                invitation.Status = action == "accept" ? "accepted" : "declined";
                if (action == "decline") return new(HttpStatusCode.NoContent);
                org.Members[caller.UserId] = new(caller.Email, invitation.Role, DateTimeOffset.UtcNow);
                return Json(new { organization_id = org.Id, project_id = (string?)null, role = invitation.Role });
            }
            Require(parts.Length >= 2 && parts[0] == "organizations", "Unknown platform route.");
            var organization = Find(parts[1]);
            if (organization is null || !organization.Members.TryGetValue(caller.UserId, out var self))
                return Error(HttpStatusCode.NotFound, "not_found", "Organization not found.");
            var admin = self.Role is "owner" or "admin";
            switch (parts[2..], method)
            {
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
            role = org.Members.GetValueOrDefault(userId)?.Role
        };
        // The creator holds project admin; otherwise the organization role implies the project role (owner → admin).
        private static object Wire(Organization org, Project project, string userId) => new
        {
            id = project.Id,
            organization_id = org.Id,
            name = project.Name,
            issuer = "https://auth.terrascale.tech/" + project.Id,
            created_by = project.CreatedBy,
            created_at = project.CreatedAt,
            role = project.CreatedBy == userId || org.Members.GetValueOrDefault(userId)?.Role is "owner" or "admin" ? "admin" : org.Members.GetValueOrDefault(userId)?.Role
        };
        private Organization? Find(string id) => organizations.FirstOrDefault(org => org.Id == id);
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };
        private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) => Json(new { error = new { code, message } }, status);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private sealed class Organization(string id, string createdBy, DateTimeOffset createdAt)
        {
            public string Id { get; } = id;
            public string Name { get; set; } = "";
            public string CreatedBy { get; } = createdBy;
            public DateTimeOffset CreatedAt { get; } = createdAt;
            public Dictionary<string, Member> Members { get; } = [];
            public HashSet<string> Collaborators { get; } = [];
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
            public DateTimeOffset ExpiresAt { get; } = expiresAt;
            public string Status { get; set; } = "pending";
        }
    }
}
