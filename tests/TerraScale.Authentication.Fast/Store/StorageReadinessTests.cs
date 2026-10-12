using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Store;

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
            .IsEquivalentTo(["api_keys", "audit_events", "email_config_versions", "email_configs", "invitations", "memberships", "mfa_credentials", "oidc_applications", "oidc_authorizations", "oidc_tokens", "organizations", "passcodes", "project_members", "projects", "rate_limits", "sessions", "signing_keys", "storage_health", "uniques", "users", "verification_tokens"]);
    }

    [Test]
    public async Task Startup_stores_the_issuer_key_in_terrabase_before_reporting_ready()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var auth = await StartAuthAsync(tenant);

        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(10))).IsTrue();
        var stored = (await database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Single().Document;
        var jwks = await auth.Client.GetFromJsonAsync<JsonElement>($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        await Assert.That(stored.GetProperty("instanceId").GetString()).IsEqualTo(OidcIssuer.InstanceId);
        await Assert.That(jwks.GetProperty("keys").EnumerateArray().Single().GetProperty("kid").GetString()).IsEqualTo(stored.GetProperty("id").GetString());
    }

    [Test]
    public async Task Malformed_public_signing_metadata_after_startup_reports_not_ready_without_repairing_the_key()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var auth = await StartAuthAsync(tenant);
        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(10))).IsTrue();
        var stored = (await database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Single();
        var key = JsonNode.Parse(stored.Document.GetRawText())!;
        key["x"] = "not-a-coordinate";
        var written = await database.PutDocumentAsync(AuthStore.SigningKeyCollection, stored.Id, JsonSerializer.SerializeToElement(key), TerraBasePrecondition.Matches(stored.Version));

        // It was ready, so the next pass that reads the broken key flips it.
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", TimeSpan.FromSeconds(15))).IsTrue();
        using var response = await auth.Client.GetAsync("/readyz");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var after = (await database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Single();
        await Assert.That(after.Version).IsEqualTo(written.Version);
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
    [Arguments("signing_keys")]
    [Arguments("oidc_applications")]
    [Arguments("oidc_authorizations")]
    [Arguments("oidc_tokens")]
    [Arguments("rate_limits")]
    [Arguments("email_configs")]
    [Arguments("email_config_versions")]
    [Arguments("mfa_credentials")]
    public async Task Collection_lost_after_startup_fails_closed_without_reprovisioning(string collection)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var auth = await StartAuthAsync(tenant);
        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(10))).IsTrue();

        // Only the startup bootstrap creates collections; the readiness probe never repairs storage.
        await database.DropCollectionAsync(collection);
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", TimeSpan.FromSeconds(15))).IsTrue();
        await Assert.That((await database.ListCollectionsAsync()).Items.Any(item => item.Name == collection)).IsFalse();
    }

    [Test]
    public async Task Startup_creates_every_collection_in_an_empty_database()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync(provision: false);
        await using var auth = await StartAuthAsync(tenant);

        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(15))).IsTrue();
        await Assert.That((await database.ListCollectionsAsync(limit: 200)).Items.Select(item => item.Name)).IsEquivalentTo(AuthStore.RequiredCollections);
    }

    private static Task<bool> ReadyWithinAsync(AuthTestServer auth, TimeSpan limit) => ReadinessWait.ReadyWithinAsync(auth.Client, limit);

    private Task<AuthTestServer> StartAuthAsync(TerraBaseTestTenant tenant, string databaseName = "auth", string? apiKey = null, string? masterKey = null, TimeSpan? refresh = null) =>
        AuthTestServer.StartAsync(settings: values =>
        {
            if (masterKey is not null)
            {
                values["master-key"] = masterKey;
            }
        }, services: services =>
        {
            fixture.UseStorage(services, tenant, databaseName, apiKey);
            if (refresh is { } interval)
            {
                services.AddSingleton(new KeyRingRefresh(interval));
            }
        });
}
