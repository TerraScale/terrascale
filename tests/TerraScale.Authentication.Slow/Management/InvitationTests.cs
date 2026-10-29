using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Management;

/// <summary>Invitations, ownership transfer and audit events of the platform (TS-598), over real HTTP on the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class InvitationTests(TerraBaseFixture fixture)
{
    // The dashboard's accept page, on the origin of the harness's first dashboard redirect URI.

    [Test]
    public async Task An_organization_sends_at_most_a_hundred_invitations_an_hour()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await StartAsync(clock);
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        var invitations = $"{Organizations}/{id}/invitations";
        var existing = (await oidc.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "first@example.test", role = "viewer" }, HttpStatusCode.Created);
        var bucket = (await oidc.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single(item => !existing.Contains(item.Id));
        var state = JsonNode.Parse(bucket.Document.GetRawText())!.AsObject();
        // Exercise the last admitted request and the first refusal without repeating 98 identical invitations.
        state["used"] = TerraScale.Authentication.Features.Platform.Invitations.PerOrganizationPerHour - 1;
        await oidc.Database.PutDocumentAsync(AttemptLimiter.Collection, bucket.Id, JsonSerializer.SerializeToElement(state), TerraBasePrecondition.Matches(bucket.Version));
        await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "last@example.test", role = "viewer" }, HttpStatusCode.Created);
        await Assert.That((await oidc.Database.GetDocumentAsync(AttemptLimiter.Collection, bucket.Id))!.Document.GetProperty("used").GetInt32())
            .IsEqualTo(TerraScale.Authentication.Features.Platform.Invitations.PerOrganizationPerHour);

        await Assert.That(await oidc.FailAsync(HttpMethod.Post, invitations, aliceToken, new { email = "one-more@example.test", role = "viewer" })).IsEqualTo((429, "rate_limited"));

        // The budget is the organization's own.
        var other = await oidc.CreateOrganizationAsync(aliceToken);
        await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{other}/invitations", aliceToken, new { email = "one-more@example.test", role = "viewer" }, HttpStatusCode.Created);
    }

    [Test]
    public async Task An_expired_invitation_is_refused_and_can_be_replaced()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await StartAsync(clock);
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (_, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        var invitations = $"{Organizations}/{id}/invitations";
        var token = (await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "bob@example.test", role = "member" }, HttpStatusCode.Created)).GetProperty("token").GetString()!;

        clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, invitations, aliceToken)).GetProperty("items")[0].GetProperty("status").GetString()).IsEqualTo("expired");
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", bobToken, new { token })).IsEqualTo((409, "invitation_expired"));
        var replacement = (await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "bob@example.test", role = "member" }, HttpStatusCode.Created)).GetProperty("token").GetString()!;
        await oidc.OkAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", bobToken, new { token = replacement });
    }

    [Test]
    public async Task Deleting_a_project_or_organization_releases_its_invitations_claims()
    {
        await using var oidc = await StartAsync();
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        var project = await oidc.CreateProjectAsync(aliceToken, id, "Production");
        var invitations = $"{Organizations}/{id}/invitations";
        var toProject = (await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "bob@example.test", role = "viewer", project_id = project }, HttpStatusCode.Created)).GetProperty("token").GetString()!;
        var toOrganization = (await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "bob@example.test", role = "viewer" }, HttpStatusCode.Created)).GetProperty("token").GetString()!;
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();

        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{project}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.FindUniqueAsync("terrascale", "invitation-token", TerraScale.Authentication.Security.Tokens.Sha256Hex(toProject))).IsNull();
        await Assert.That(await store.FindUniqueAsync("terrascale", "invitation-pending", $"{id}/{project}/bob@example.test")).IsNull();
        await Assert.That(await store.FindUniqueAsync("terrascale", "invitation-token", TerraScale.Authentication.Security.Tokens.Sha256Hex(toOrganization))).IsNotNull();

        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await store.FindUniqueAsync("terrascale", "invitation-token", TerraScale.Authentication.Security.Tokens.Sha256Hex(toOrganization))).IsNull();
        await Assert.That(await store.FindUniqueAsync("terrascale", "invitation-pending", $"{id}/-/bob@example.test")).IsNull();
    }

    [Test]
    public async Task An_invitation_accepted_on_two_replicas_at_once_works_once()
    {
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(new HttpClient(pause)));
        await using var replica = await oidc.StartReplicaAsync();
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (_, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        var token = (await oidc.OkAsync(HttpMethod.Post, $"{Organizations}/{id}/invitations", aliceToken, new { email = "bob@example.test", role = "admin" }, HttpStatusCode.Created)).GetProperty("token").GetString()!;

        var held = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", bobToken, new { token }));
        using (var first = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", bobToken, new { token }, replica.Client))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(await pause.ReleaseAsync(held)).IsEqualTo((409, "not_pending"));
        var joined = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/audit-events", aliceToken)).GetProperty("items").EnumerateArray().Count(item => item.GetProperty("action").GetString() == "member.joined");
        await Assert.That(joined).IsEqualTo(1);
    }

    [Test]
    public async Task An_invitation_paused_while_its_organization_is_deleted_is_refused()
    {
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(new HttpClient(pause)));
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();

        // So does a project's, for an invitation to it.
        var project = await oidc.CreateProjectAsync(aliceToken, id, "Production");
        var toProject = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{id}/invitations", aliceToken, new { email = "bob@example.test", role = "viewer", project_id = project }));
        var deleted = (await store.GetAsync(AuthStore.ProjectCollection, "terrascale", project))!;
        await Assert.That(await store.TransactAsync([AuthStore.Delete(AuthStore.ProjectCollection, "terrascale", project, deleted.Version)])).IsNull();
        await Assert.That(await pause.ReleaseAsync(toProject)).IsEqualTo((404, "not_found"));

        // The deletion commits after the invitation's reads and before its memberships are cleaned up.
        var held = await pause.HoldNextAsync(() => oidc.FailAsync(HttpMethod.Post, $"{Organizations}/{id}/invitations", aliceToken, new { email = "bob@example.test", role = "viewer" }));
        var organization = (await store.GetAsync(AuthStore.OrganizationCollection, "terrascale", id))!;
        await Assert.That(await store.TransactAsync([AuthStore.Delete(AuthStore.OrganizationCollection, "terrascale", id, organization.Version)])).IsNull();
        await Assert.That(await pause.ReleaseAsync(held)).IsEqualTo((404, "not_found"));
    }

    private Task<OidcHarness> StartAsync(TimeProvider? clock = null) =>
        OidcHarness.StartAsync(fixture, clock: clock);

}
