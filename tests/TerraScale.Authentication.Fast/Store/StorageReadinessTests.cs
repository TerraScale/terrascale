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
    public async Task Storage_readiness_overlaps_reads_but_never_writes_before_catalog_validation(bool missingCollection)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        if (missingCollection)
        {
            await database.DropCollectionAsync(AuthStore.ChallengeCollection);
        }

        using var requests = new OverlappingReads { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(requests);
        var probedDatabase = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = fixture.Server.InternalAddress,
        }).Database(tenant.Project.Id, "auth");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new TerraBaseStorageHealth(probedDatabase).CheckAsync(deadline.Token);

        await Assert.That(result.Healthy).IsEqualTo(!missingCollection);
        await Assert.That(requests.MutatedBeforeCatalog).IsFalse();
        await Assert.That(requests.Mutations).IsEqualTo(missingCollection ? 0 : 2);
        await Assert.That((await database.ListDocumentsAsync(AuthStore.HealthCollection)).Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Storage_readiness_checks_required_collections_beyond_the_first_catalog_page()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        for (var index = 0; index < 200; index++)
        {
            await database.CreateCollectionAsync($"extra_{index:D3}");
        }

        using var requests = new CatalogRequests { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(requests);
        var probedDatabase = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = fixture.Server.InternalAddress,
        }).Database(tenant.Project.Id, "auth");
        var health = new TerraBaseStorageHealth(probedDatabase);
        await Assert.That((await health.CheckAsync(CancellationToken.None)).Healthy).IsTrue();
        await Assert.That(requests.Count).IsEqualTo(2);
        await Assert.That((await database.ListDocumentsAsync(AuthStore.HealthCollection)).Items.Count).IsEqualTo(0);

        await database.DropCollectionAsync(AuthStore.ChallengeCollection);
        await Assert.That((await health.CheckAsync(CancellationToken.None)).Healthy).IsFalse();
        await Assert.That((await database.ListDocumentsAsync(AuthStore.HealthCollection)).Items.Count).IsEqualTo(0);
    }

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

        await using var auth = await StartAuthAsync(tenant, apiKey: key, provision: false);
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

        await using var missing = await StartAuthAsync(tenant, databaseName: "missing", provision: false);
        // Let one pass run against the missing database before asking.
        await Task.Delay(ReadinessWait.NextPass);
        using var unavailable = await missing.Client.GetAsync("/readyz");
        await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await database.ListCollectionsAsync()).Items.Select(collection => collection.Name))
            .IsEquivalentTo(["account_deletion_receipts", "api_key_usage", "api_keys", "audit_events", "auth_mail_outbox", "closure_cancellations", "closure_commits", "closure_jobs", "dashboard_deletion_cursors", "deletion_jobs", "email_admission", "email_alert_deliveries", "email_allowance_deliveries", "email_changes", "email_config_versions", "email_configs", "email_support_deliveries", "history_erasure_marks", "invitations", "memberships", "mfa_credentials", "oidc_applications", "oidc_authorizations", "oidc_tokens", "organization_sso", "organizations", "passcodes", "project_auth_settings", "project_members", "projects", "rate_limits", "sessions", "signing_keys", "step_up_deletion_grants", "step_up_passcodes", "step_up_proofs", "storage_health", "uniques", "users", "verification_tokens"]);
    }

    [Test]
    public async Task The_provisioned_issuer_key_is_stored_in_terrabase_and_published()
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

        // It was ready, so the third consecutive pass that reads the broken key flips it.
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", TimeSpan.FromSeconds(45))).IsTrue();
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
        await using var auth = await StartAuthAsync(tenant, apiKey: key, provision: false);

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
    [Arguments("deletion_jobs")]
    [Arguments("closure_jobs")]
    [Arguments("closure_cancellations")]
    [Arguments("closure_commits")]
    public async Task Collection_lost_after_startup_fails_closed_without_reprovisioning(string collection)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var auth = await StartAuthAsync(tenant);
        await auth.App.Services.GetRequiredService<SigningKeyRotation>().Bootstrapped.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(10))).IsTrue();

        // Only the startup bootstrap creates collections; the readiness probe never repairs storage.
        await database.DropCollectionAsync(collection);
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", TimeSpan.FromSeconds(15))).IsTrue();
        await Assert.That((await database.ListCollectionsAsync()).Items.Any(item => item.Name == collection)).IsFalse();
    }

    [Test]
    public async Task Startup_creates_every_collection_and_the_first_signing_key_in_an_empty_database()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync(provision: false);
        await using var auth = await StartAuthAsync(tenant, provision: false);

        await Assert.That(await ReadinessWait.UntilAsync(async () => (await database.ListCollectionsAsync(limit: 200)).Items.Count == AuthStore.RequiredCollections.Count, TimeSpan.FromSeconds(15))).IsTrue();
        await Assert.That((await database.ListCollectionsAsync(limit: 200)).Items.Select(item => item.Name)).IsEquivalentTo(AuthStore.RequiredCollections);
        await Assert.That(await ReadyWithinAsync(auth, TimeSpan.FromSeconds(15))).IsTrue();
        using var response = await auth.Client.GetAsync("/readyz");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Count).IsEqualTo(1);
    }

    private static Task<bool> ReadyWithinAsync(AuthTestServer auth, TimeSpan limit) => ReadinessWait.ReadyWithinAsync(auth.Client, limit);

    private sealed class OverlappingReads : DelegatingHandler
    {
        private readonly TaskCompletionSource catalogStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource documentStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool catalogCompleted;

        public bool MutatedBeforeCatalog { get; private set; }

        public int Mutations { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var catalog = request.RequestUri!.AbsolutePath.EndsWith("/collections", StringComparison.Ordinal);
            if (catalog)
            {
                this.catalogStarted.TrySetResult();
                await this.documentStarted.Task.WaitAsync(cancellationToken);
            }
            else if (request.Method == HttpMethod.Get)
            {
                this.documentStarted.TrySetResult();
                await this.catalogStarted.Task.WaitAsync(cancellationToken);
            }
            else
            {
                this.Mutations++;
                this.MutatedBeforeCatalog |= !Volatile.Read(ref this.catalogCompleted);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (catalog)
            {
                Volatile.Write(ref this.catalogCompleted, true);
            }

            return response;
        }
    }

    private sealed class CatalogRequests : DelegatingHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/collections", StringComparison.Ordinal))
            {
                this.Count++;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    // provision: the operator's signing-key provisioning runs before the server starts, as on a provisioned deployment.
    private Task<AuthTestServer> StartAuthAsync(TerraBaseTestTenant tenant, string databaseName = "auth", string? apiKey = null, string? masterKey = null, TimeSpan? refresh = null, bool provision = true) =>
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
        }, beforeStart: provision ? services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None) : null);
}
