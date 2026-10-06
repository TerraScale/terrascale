using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.Testing;

namespace TerraScale.Auth.Fast.Store;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class StorageReadinessTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Keys_scoped_to_another_organization_or_project_never_report_ready(bool anotherOrganization)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        string key;
        if (anotherOrganization)
        {
            var foreign = await fixture.Server.CreateTenantAsync();
            key = foreign.ApiKey;
        }
        else
        {
            var project = await fixture.Server.Management().CreateProjectAsync(tenant.UserId, tenant.Organization.Id, "Other project");
            key = await fixture.Server.CreateApiKeyAsync(tenant.Organization.Id, projectId: project.Id);
        }

        await using var auth = await StartAuthAsync(tenant, apiKey: key);
        using var response = await auth.Client.GetAsync("/readyz");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await database.ListDocumentsAsync("storage_health")).Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Default_host_is_ready_only_for_the_configured_provisioned_database()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var auth = await StartAuthAsync(tenant);
        using var ready = await auth.Client.GetAsync("/readyz");
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("ready");

        await using var missing = await StartAuthAsync(tenant, databaseName: "missing");
        using var unavailable = await missing.Client.GetAsync("/readyz");
        await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await database.ListCollectionsAsync()).Items.Select(collection => collection.Name))
            .IsEquivalentTo(["oidc_applications", "oidc_authorizations", "oidc_tokens", "passcodes", "rate_limits", "sessions", "signing_key_rings", "storage_health", "uniques", "users", "verification_tokens"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Invalid_or_read_only_keys_never_report_ready(bool readOnly)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var key = readOnly
            ? await fixture.Server.CreateApiKeyAsync(tenant.Organization.Id, ["documents:read"], tenant.Project.Id)
            : "tb_invalid_synthetic_key";
        await using var auth = await StartAuthAsync(tenant, apiKey: key);

        using var response = await auth.Client.GetAsync("/readyz");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await database.ListDocumentsAsync("storage_health")).Items.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("uniques")]
    [Arguments("verification_tokens")]
    [Arguments("storage_health")]
    [Arguments("users")]
    [Arguments("sessions")]
    [Arguments("passcodes")]
    [Arguments("signing_key_rings")]
    [Arguments("oidc_applications")]
    [Arguments("oidc_authorizations")]
    [Arguments("oidc_tokens")]
    public async Task Missing_required_collection_fails_closed_without_reprovisioning(string collection)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await database.DropCollectionAsync(collection);
        await using var auth = await StartAuthAsync(tenant);

        using var response = await auth.Client.GetAsync("/readyz");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await database.ListCollectionsAsync()).Items.Any(item => item.Name == collection)).IsFalse();
    }

    [Test]
    public async Task Private_members_fail_over_to_the_next_ready_member_trusting_only_the_configured_root()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        // The first member refuses connections; storage moves on to the real one.
        await using var auth = await StartMembersAsync(tenant, $"https://localhost:9;{fixture.Server.ClientAddress}", fixture.Server.Certificate.ExportCertificatePem(), failover: true);

        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(10))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Private_members_are_never_ready_over_an_untrusted_or_misnamed_certificate(bool misnamed)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // The member certificate names localhost only, not 127.0.0.1.
        var endpoint = misnamed ? $"https://127.0.0.1:{fixture.Server.ClientAddress.Port}" : fixture.Server.ClientAddress.ToString();
        var root = misnamed ? fixture.Server.Certificate.ExportCertificatePem() : other.ExportCertificatePem();
        await using var auth = await StartMembersAsync(tenant, endpoint, root);

        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(3))).IsFalse();
        await Assert.That((await database.ListDocumentsAsync("storage_health")).Items.Count).IsEqualTo(0);
    }

    private static async Task<bool> ReadyWithinAsync(AuthTestServer auth, TimeSpan limit)
    {
        var deadline = DateTimeOffset.UtcNow + limit;
        do
        {
            using var response = await auth.Client.GetAsync("/readyz");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return true;
            }

            // Readiness is cached for one second.
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
        }
        while (DateTimeOffset.UtcNow < deadline);
        return false;
    }

    // Uses the product's own storage client: no test HttpClient replaces its trust.
    private static Task<AuthTestServer> StartMembersAsync(TerraBaseTestTenant tenant, string endpoints, string root, bool failover = false) =>
        AuthTestServer.StartAsync(settings: values =>
        {
            values["terrascale-api-endpoints"] = endpoints;
            values["terrascale-api-ca"] = root;
            values["terrascale-api-failover"] = failover ? "true" : "false";
            values["terrascale-api-key"] = tenant.ApiKey;
            values["database"] = $"{tenant.Project.Id}/auth";
        });

    private Task<AuthTestServer> StartAuthAsync(TerraBaseTestTenant tenant, string databaseName = "auth", string? apiKey = null) =>
        AuthTestServer.StartAsync(settings: values =>
        {
            values["terrascale-api-url"] = fixture.Server.ClientAddress.GetLeftPart(UriPartial.Authority);
            values["terrascale-api-key"] = apiKey ?? tenant.ApiKey;
            values["database"] = $"{tenant.Project.Id}/{databaseName}";
        }, services: services => services.AddSingleton(fixture.Server.Http));
}
