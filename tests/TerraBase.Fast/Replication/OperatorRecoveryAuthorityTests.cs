using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed partial class OperatorRecoveryTests
{
    [Test]
    [Arguments("owner", 409)]
    [Arguments("admin", 409)]
    [Arguments("member", 404)]
    [Arguments("collaborator", 404)]
    [Arguments("wrong_user", 404)]
    [Arguments("wrong_organization", 404)]
    [Arguments("wrong_project", 404)]
    [Arguments("project_reader", 404)]
    [Arguments("missing_project", 404)]
    [Arguments("revoked", 404)]
    [Arguments("expired", 404)]
    [Arguments("malformed", 404)]
    [Arguments("redirect", 404)]
    [Arguments("unavailable", 503)]
    [Arguments("rate_limited", 503)]
    public async Task Enrolled_recovery_requires_live_subject_organization_and_exact_project_authority(string scenario, int expected)
    {
        var probe = new RebuildProbe();
        await using var api = await StartAsync(probe, new Freshness());
        var enrolled = await EnrollRecoveryAsync(api);
        var token = api.Issuer.Mint(api.User);
        api.Issuer.GrantRecovery(token, new TestIssuer.RecoveryAuthority(
            scenario == "wrong_user" ? EntityIds.New().ToString() : api.User,
            scenario == "wrong_organization" ? EntityIds.New().ToString() : enrolled.Tenant.OrganizationId,
            Role: scenario is "owner" or "admin" or "member" ? scenario : scenario == "collaborator" ? null : "owner",
            Status: scenario switch
            {
                "revoked" or "expired" => HttpStatusCode.Unauthorized,
                "redirect" => HttpStatusCode.Found,
                "rate_limited" => HttpStatusCode.TooManyRequests,
                _ => HttpStatusCode.OK,
            }, Redirect: scenario == "redirect" ? new Uri("https://untrusted.test/") : null,
            Available: scenario != "unavailable", Malformed: scenario == "malformed"));
        if (scenario != "missing_project")
        {
            api.Issuer.GrantProject(token, enrolled.Tenant.OrganizationId, enrolled.Tenant.ProjectId!,
                new TestIssuer.ProjectAuthority(scenario == "wrong_project" ? EntityIds.New().ToString() : enrolled.Tenant.ProjectId!,
                    enrolled.Tenant.OrganizationId, Role: scenario == "project_reader" ? "viewer" : "admin"));
        }

        using var owner = api.Service(api.User);
        // Reproduce the production404: the genuine ts-auth owner has no duplicated legacy storage membership.
        using var before = await owner.PostAsJsonAsync(enrolled.Path, enrolled.Request);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var after = await owner.PostAsJsonAsync(enrolled.Path, enrolled.Request);
        await Assert.That((int)after.StatusCode).IsEqualTo(expected);
        await Assert.That(probe.Calls).IsEqualTo(expected == 409 ? 1 : 0);
        if (expected == 409)
        {
            await Assert.That(probe.Database).IsEqualTo(enrolled.Tenant);
            await Assert.That(probe.Incarnation.ToString()).IsEqualTo(enrolled.Request.FaultedIncarnation);
        }
    }

    [Test]
    public async Task Recovery_proof_cannot_replace_peer_actor_or_internal_listener_or_authorize_other_routes()
    {
        var probe = new RebuildProbe();
        await using var api = await StartAsync(probe, new Freshness());
        var enrolled = await EnrollRecoveryAsync(api);
        var token = GrantRecovery(api, enrolled.Tenant);
        using var publicClient = api.WithToken(token);
        publicClient.DefaultRequestHeaders.Add("X-TerraBase-User-Id", api.User);
        using var publicResult = await publicClient.PostAsJsonAsync(enrolled.Path, enrolled.Request);
        using var noPeer = new HttpClient
        {
            BaseAddress = api.Server.BaseAddress,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        noPeer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        noPeer.DefaultRequestHeaders.Add("X-TerraBase-User-Id", api.User);
        using var peerResult = await noPeer.PostAsJsonAsync(enrolled.Path, enrolled.Request);
        using var noActor = PeerTls.CreateClient(api.Server.BaseAddress);
        noActor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var actorResult = await noActor.PostAsJsonAsync(enrolled.Path, enrolled.Request);
        using var service = api.Service(api.User);
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var otherRoute = await service.GetAsync(ManagementApi.Uri("/v1/management/organizations"));
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "invalid");
        using var malformed = await service.PostAsJsonAsync(enrolled.Path, enrolled.Request);
        await Assert.That(publicResult.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(peerResult.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(actorResult.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(otherRoute.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(probe.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Live_recovery_rechecks_catalog_target_and_freshness_after_authority(bool delete)
    {
        var probe = new RebuildProbe();
        var freshness = new Freshness();
        await using var api = await StartAsync(probe, freshness);
        var enrolled = await EnrollRecoveryAsync(api);
        var token = GrantRecovery(api, enrolled.Tenant);
        api.Issuer.GrantRecovery(token, new TestIssuer.RecoveryAuthority(api.User, enrolled.Tenant.OrganizationId,
            BeforeOrganization: async () =>
            {
                if (delete)
                {
                    using var project = api.WithToken(api.Issuer.Mint(api.User, claims =>
                    {
                        claims.Claims["org_id"] = enrolled.Tenant.OrganizationId;
                        claims.Claims["project_id"] = enrolled.Tenant.ProjectId!;
                        claims.Claims["scope"] = "databases:write";
                    }));
                    using var removed = await project.DeleteAsync(ManagementApi.Uri($"/v1/projects/{enrolled.Tenant.ProjectId}/databases/recovery"));
                    removed.EnsureSuccessStatusCode();
                }
                else
                {
                    freshness.Passes = 0;
                }
            }));
        using var service = api.Service(api.User);
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var result = await service.PostAsJsonAsync(enrolled.Path, enrolled.Request);
        await Assert.That(result.StatusCode).IsEqualTo(delete ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable);
        await Assert.That(probe.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Ts_auth_proof_cannot_take_over_legacy_recovery_storage()
    {
        var probe = new RebuildProbe();
        await using var api = await StartAsync(probe, new Freshness());
        var legacy = await api.CreateManagedDatabaseAsync();
        var actor = EntityIds.New().ToString();
        var token = api.Issuer.Mint(actor);
        api.Issuer.GrantRecovery(token, new TestIssuer.RecoveryAuthority(actor, legacy.Organization));
        api.Issuer.GrantProject(token, legacy.Organization, legacy.Project);
        using var service = api.Service(actor);
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var denied = await service.PostAsJsonAsync(Path(legacy), new { faultedIncarnation = EntityIds.New().ToString(), reason = "retry" });
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(probe.Calls).IsEqualTo(0);
    }

    private static string GrantRecovery(ManagementApi api, TenantContext tenant)
    {
        var token = api.Issuer.Mint(api.User);
        api.Issuer.GrantRecovery(token, new TestIssuer.RecoveryAuthority(api.User, tenant.OrganizationId));
        api.Issuer.GrantProject(token, tenant.OrganizationId, tenant.ProjectId!);
        return token;
    }

    private static async Task<EnrolledRecovery> EnrollRecoveryAsync(ManagementApi api)
    {
        var organization = EntityIds.New().ToString();
        var project = EntityIds.New().ToString();
        using var client = api.WithToken(api.Issuer.Mint(api.User, claims =>
        {
            claims.Claims["org_id"] = organization;
            claims.Claims["project_id"] = project;
            claims.Claims["scope"] = "databases:read databases:write";
        }));
        using var created = await client.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "recovery" });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var tenant = new TenantContext(organization, PlanTier.Hobby, project, body.GetProperty("id").GetString()!);
        var cancellationToken = TestContext.Current!.Execution.CancellationToken;
        var storage = api.Server.Services.GetRequiredService<IRebuildStorage>();
        var incarnation = await storage.IncarnationAsync(tenant, cancellationToken) ?? throw new InvalidOperationException("missing enrolled file");
        await api.Server.Services.GetRequiredService<IRebuildLedger>().SaveAsync(
            new RebuildRecord(tenant, incarnation, storage.SidePath(tenant, incarnation), RebuildPhase.Pending, null), cancellationToken);
        return new EnrolledRecovery(tenant, ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases/{tenant.DatabaseId}/recovery/retry"),
            new ReplicaRecoveryRequest(incarnation.ToString(), "Approved enrolled tenant retry"));
    }

    private sealed record EnrolledRecovery(TenantContext Tenant, Uri Path, ReplicaRecoveryRequest Request);
}
