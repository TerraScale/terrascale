using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.SigningKeys;
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
        await auth.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(10))).IsTrue();
        using var ready = await auth.Client.GetAsync("/readyz");
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("ready");

        await using var missing = await StartAuthAsync(tenant, databaseName: "missing");
        // Let one pass run against the missing database before asking.
        await Task.Delay(ReadinessWait.NextPass);
        using var unavailable = await missing.Client.GetAsync("/readyz");
        await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await database.ListCollectionsAsync()).Items.Select(collection => collection.Name))
            .IsEquivalentTo(["api_keys", "audit_events", "email_config_versions", "email_configs", "invitations", "memberships", "mfa_credentials", "oidc_applications", "oidc_authorizations", "oidc_tokens", "organizations", "passcodes", "project_members", "projects", "rate_limits", "sessions", "signing_key_rings", "storage_health", "uniques", "users", "verification_tokens"]);
    }

    [Test]
    public async Task Startup_replaces_a_legacy_signing_ring_before_reporting_ready()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var vault = FakeOciVault.For(tenant.Project.Id);
        var id = OidcIssuer.InstanceId;
        // One master key: only the earlier format, not an unopenable box, triggers the replacement.
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using (var previous = await StartAuthAsync(tenant, masterKey: masterKey))
        {
            await previous.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped.WaitAsync(TimeSpan.FromSeconds(30));
        }

        // The ring format before public metadata, as an earlier release stored it.
        var stored = vault.Current(id);
        var ring = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
        ring.Remove("instanceId");
        foreach (var key in ring["keys"]!.AsArray())
        {
            foreach (var field in new[] { "activatesAt", "x", "y", "publicMetadataMac" })
            {
                key!.AsObject().Remove(field);
            }
        }

        vault.Put(id, JsonSerializer.SerializeToElement(ring));
        var legacyKid = ring["keys"]![0]!["id"]!.GetValue<string>();

        await using var auth = await StartAuthAsync(tenant, masterKey: masterKey);
        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(10))).IsTrue();
        var replaced = vault.Current(id).Document;
        await Assert.That(replaced.GetProperty("instanceId").GetString()).IsEqualTo(OidcIssuer.InstanceId);
        await Assert.That(replaced.GetProperty("keys").GetArrayLength()).IsEqualTo(1);
        var jwks = await auth.Client.GetFromJsonAsync<JsonElement>($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        var kid = jwks.GetProperty("keys").EnumerateArray().Single().GetProperty("kid").GetString();
        await Assert.That(kid).IsEqualTo(replaced.GetProperty("keys")[0].GetProperty("id").GetString());
        await Assert.That(kid).IsNotEqualTo(legacyKid);
        // Key material lives only in the vault.
        await Assert.That((await database.ListDocumentsAsync("signing_key_rings")).Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_ring_emptied_after_startup_reports_not_ready_without_recreating_it()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var vault = FakeOciVault.For(tenant.Project.Id);
        await using var auth = await StartAuthAsync(tenant);
        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(10))).IsTrue();
        vault.Put(OidcIssuer.InstanceId, JsonDocument.Parse("{}").RootElement);
        var emptied = vault.Current(OidcIssuer.InstanceId).Version;

        // The verified vault read stays cached for five minutes, so the emptied ring is not re-read this soon.
        await Task.Delay(ReadinessWait.NextPass);
        await Assert.That(vault.Current(OidcIssuer.InstanceId).Version).IsEqualTo(emptied);
    }

    [Test]
    public async Task Healthy_vault_metadata_is_read_once_across_passes_within_five_minutes()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var vault = FakeOciVault.For(tenant.Project.Id);
        await using var auth = await StartAuthAsync(tenant, refresh: KeyRingRefresh.Max);
        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(10))).IsTrue();
        var reads = vault.CurrentReads(OidcIssuer.InstanceId);

        // Two more passes run inside the cache window and must not read the vault.
        await Task.Delay(TimeSpan.FromSeconds(11));
        await Assert.That(vault.CurrentReads(OidcIssuer.InstanceId)).IsEqualTo(reads);
        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(1))).IsTrue();
    }

    [Test]
    public async Task Malformed_public_signing_metadata_after_startup_reports_not_ready_without_repairing_the_ring()
    {
        var (tenant, _) = await fixture.CreateDatabaseAsync();
        var vault = FakeOciVault.For(tenant.Project.Id);
        await using var auth = await StartAuthAsync(tenant);
        await auth.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped.WaitAsync(TimeSpan.FromSeconds(30));
        var id = OidcIssuer.InstanceId;
        var stored = vault.Current(id);
        var ring = JsonNode.Parse(stored.Document.GetRawText())!;
        ring["keys"]![0]!["x"] = "not-a-coordinate";
        vault.Put(id, JsonSerializer.SerializeToElement(ring));
        var before = vault.Current(id);

        // A pass after the write: a cached verified read never sees this malformed ring.
        await Task.Delay(ReadinessWait.NextPass);
        var after = vault.Current(id);
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetRawText()).IsEqualTo(before.Document.GetRawText());
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
    [Arguments("rate_limits")]
    [Arguments("email_configs")]
    [Arguments("email_config_versions")]
    [Arguments("mfa_credentials")]
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

    private static Task<bool> ReadyWithinAsync(AuthTestServer auth, TimeSpan limit) => ReadinessWait.ReadyWithinAsync(auth.Client, limit);

    // Uses the product's own storage client: no test HttpClient replaces its trust.
    private static Task<AuthTestServer> StartMembersAsync(TerraBaseTestTenant tenant, string endpoints, string root, bool failover = false) =>
        AuthTestServer.StartAsync(settings: values =>
        {
            values["terrascale-api-endpoints"] = endpoints;
            values["terrascale-api-ca"] = root;
            values["terrascale-api-failover"] = failover ? "true" : "false";
            values["terrascale-api-key"] = tenant.ApiKey;
            values["database"] = $"{tenant.Project.Id}/auth";
            FakeOciVault.For(tenant.Project.Id).Bind(values);
        });

    private Task<AuthTestServer> StartAuthAsync(TerraBaseTestTenant tenant, string databaseName = "auth", string? apiKey = null, string? masterKey = null, TimeSpan? refresh = null) =>
        AuthTestServer.StartAsync(settings: values =>
        {
            values["terrascale-api-url"] = fixture.Server.ClientAddress.GetLeftPart(UriPartial.Authority);
            values["terrascale-api-key"] = apiKey ?? tenant.ApiKey;
            values["database"] = $"{tenant.Project.Id}/{databaseName}";
            FakeOciVault.For(tenant.Project.Id).Bind(values);
            if (masterKey is not null)
            {
                values["master-key"] = masterKey;
            }
        }, services: services =>
        {
            services.AddSingleton(fixture.Server.Http);
            if (refresh is { } interval)
            {
                services.AddSingleton(new KeyRingRefresh(interval));
            }
        });
}
