using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Management;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class StepUpReviewRegressionTests(TerraBaseFixture fixture)
{
    private static readonly string[] ReadScopes = ["documents:read"];
    private static readonly string[] ClientRedirects = ["https://client.example.test/callback"];

    [Test]
    [Arguments("fra")]
    [Arguments("sjc")]
    [Arguments("")]
    public async Task Production_cannot_start_a_second_authentication_authority_even_without_mail(string region)
    {
        var rejected = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(
            settings: values =>
            {
                values.Remove("environment");
                values["terrabase-url"] = "http://127.0.0.1:8081";
            }, configure: builder => builder.Configuration["TS_REGION"] = region));
        await Assert.That(rejected!.Message).Contains("GRU authority");
    }

    [Test]
    public async Task A_consume_that_commits_after_expiry_returns_no_usable_deletion_authorization()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        using var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        using var transport = new HttpClient(pause);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(transport));
        var (_, token) = await oidc.SignInUserAsync(new Faker().Internet.Email());
        var proof = await oidc.ApprovedProofAsync(token, "delete_account", "ts-dashboard");
        var receipt = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var held = await pause.HoldNextAsync(() => oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/step-up/consume", token,
            new { proof, purpose = "delete_account", audience = "ts-dashboard", operation_receipt = receipt }));
        clock.UtcNow += TimeSpan.FromMinutes(5);
        using var response = await pause.ReleaseAsync(held);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain("operation_grant");
        using var delete = Deletion(token, receipt, null);
        using var refused = await oidc.Client.SendAsync(delete);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Losing_fresh_primary_lineage_revokes_a_deletion_grant_and_an_issued_API_key()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (user, token) = await oidc.SignInUserAsync(new Faker().Internet.Email());
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var binding = AuthStore.NewCredentialBinding("step-up-test", AuthStore.NewId());
        await store.TransactAsync([AuthStore.CredentialClaim(user.InstanceId, binding)]);
        CredentialBinding[] lineage = [.. user.AuthenticationBindings, binding];
        var proof = await oidc.ApprovedProofAsync(token, "delete_account", "ts-dashboard", lineage);
        var receipt = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        string authorization;
        using (var consumed = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/step-up/consume", token,
            new { proof, purpose = "delete_account", audience = "ts-dashboard", operation_receipt = receipt }))
        {
            await Assert.That(consumed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            authorization = (await OidcHarness.JsonAsync(consumed)).GetProperty("operation_grant").GetString()!;
        }
        var organization = await oidc.CreateOrganizationAsync(token);
        var keyProof = await oidc.ApprovedProofAsync(token, "create_api_key", bindings: lineage);
        using var create = new HttpRequestMessage(HttpMethod.Post, $"{PlatformCalls.Organizations}/{organization}/api-keys")
        { Content = JsonContent.Create(new { name = "lineage", scopes = ReadScopes }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        create.Headers.Add(StepUpProofs.Header, keyProof);
        using var created = await oidc.Client.SendAsync(create);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var key = (await OidcHarness.JsonAsync(created)).GetProperty("key").GetString()!;
        using (var initialExchange = await oidc.ApiKeyTokenAsync(key))
        {
            await Assert.That(initialExchange.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        var project = await oidc.CreateProjectAsync(token, organization, "Lineage");
        var clientProof = await oidc.ApprovedProofAsync(token, "create_oauth_client", bindings: lineage);
        using var createClient = new HttpRequestMessage(HttpMethod.Post, $"/v1/{project}/clients")
        { Content = JsonContent.Create(new { name = "lineage", type = "confidential", redirect_uris = ClientRedirects }) };
        createClient.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        createClient.Headers.Add(StepUpProofs.Header, clientProof);
        using var registered = await oidc.Client.SendAsync(createClient);
        await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var client = await OidcHarness.JsonAsync(registered);
        var clientId = client.GetProperty("client_id").GetString()!;
        var secret = client.GetProperty("client_secret").GetString()!;
        using (var admitted = await oidc.PostTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = "invalid" }, clientId, secret, tenant: project))
        {
            await Assert.That((await OidcHarness.JsonAsync(admitted)).GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        }
        // Reconcile this slot to a different winner while the management sign-in remains valid.
        var claim = (await store.FindUniqueAsync(user.InstanceId, binding.Kind, binding.Key))!.Value;
        await store.TransactAsync([AuthStore.Reclaim(user.InstanceId, binding.Kind, binding.Key, AuthStore.NewId(), claim.Version)]);
        using var delete = Deletion(token, receipt, authorization);
        using var refused = await oidc.Client.SendAsync(delete);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var exchange = await oidc.ApiKeyTokenAsync(key);
        await Assert.That(exchange.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await OidcHarness.JsonAsync(exchange)).GetProperty("error").GetString()).IsEqualTo("invalid_client");
        using var rejectedClient = await oidc.PostTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = "invalid" }, clientId, secret, tenant: project);
        await Assert.That((await OidcHarness.JsonAsync(rejectedClient)).GetProperty("error").GetString()).IsEqualTo("invalid_client");
        await oidc.OkAsync(HttpMethod.Get, PlatformCalls.Organizations, token);
    }

    [Test]
    public async Task A_key_secret_issued_before_fresh_proof_lineage_keeps_exchanging()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (user, token) = await oidc.SignInUserAsync(new Faker().Internet.Email());
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var binding = AuthStore.NewCredentialBinding("step-up-test", AuthStore.NewId());
        await store.TransactAsync([AuthStore.CredentialClaim(user.InstanceId, binding)]);
        CredentialBinding[] lineage = [.. user.AuthenticationBindings, binding];
        var organization = await oidc.CreateOrganizationAsync(token);
        var keyProof = await oidc.ApprovedProofAsync(token, "create_api_key", bindings: lineage);
        using var create = new HttpRequestMessage(HttpMethod.Post, $"{PlatformCalls.Organizations}/{organization}/api-keys")
        { Content = JsonContent.Create(new { name = "before lineage", scopes = ReadScopes }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        create.Headers.Add(StepUpProofs.Header, keyProof);
        using var created = await oidc.Client.SendAsync(create);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var key = (await OidcHarness.JsonAsync(created)).GetProperty("key").GetString()!;

        // Stored as every key issued before fresh-proof lineage existed: no lineage at all.
        var stored = (await store.GetAsync(AuthStore.ApiKeyCollection, PlatformDirectory.Instance, key[..26]))!;
        var record = stored.Document.Deserialize(PlatformJson.Default.ApiKeyRecord)! with { SecretBindings = [] };
        await Assert.That(await store.TransactAsync([AuthStore.Replace(AuthStore.ApiKeyCollection, PlatformDirectory.Instance, key[..26],
            JsonSerializer.SerializeToElement(record, PlatformJson.Default.ApiKeyRecord), stored.Version)])).IsNull();

        // The creator losing a lineage the secret never had leaves it working.
        var claim = (await store.FindUniqueAsync(user.InstanceId, binding.Kind, binding.Key))!.Value;
        await store.TransactAsync([AuthStore.Reclaim(user.InstanceId, binding.Kind, binding.Key, AuthStore.NewId(), claim.Version)]);
        using var exchange = await oidc.ApiKeyTokenAsync(key);
        await Assert.That(exchange.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Request_creation_is_budgeted_and_expired_proof_records_are_pruned()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, token) = await oidc.SignInUserAsync(new Faker().Internet.Email());
        for (var index = 0; index < StepUpProofs.MaxRequests; index++)
        {
            using var admitted = await BeginAsync(oidc, token);
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }
        using (var limited = await BeginAsync(oidc, token))
        {
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        }
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        await Assert.That(await store.QueryAsync(AuthStore.StepUpProofCollection, user.InstanceId, "/userId", user.Id).CountAsync()).IsEqualTo(StepUpProofs.MaxRequests);
        clock.UtcNow += TimeSpan.FromMinutes(6);
        await oidc.Server.App.Services.GetRequiredService<StepUpProofs>().PruneAsync(500);
        await Assert.That(await store.QueryAsync(AuthStore.StepUpProofCollection, user.InstanceId, "/userId", user.Id).CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task A_new_signin_after_global_revocation_cannot_consume_an_old_epoch_proof()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, token) = await oidc.SignInUserAsync(new Faker().Internet.Email());
        var proof = await oidc.ApprovedProofAsync(token, "create_pat", "ts-dashboard");
        await oidc.Server.App.Services.GetRequiredService<Sessions>().RevokeAllAsync(user.InstanceId, user.Id);
        clock.UtcNow += TimeSpan.FromSeconds(61);
        var session = await oidc.SignInAsync(user.Email);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var exchange = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        var fresh = (await OidcHarness.JsonAsync(exchange)).GetProperty("access_token").GetString()!;
        using var refused = await oidc.CallAsync(HttpMethod.Post, "/v1/terrascale/step-up/consume", fresh,
            new { proof, purpose = "create_pat", audience = "ts-dashboard" });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    private static Task<HttpResponseMessage> BeginAsync(OidcHarness oidc, string token) => oidc.CallAsync(HttpMethod.Post,
        "/v1/terrascale/step-up", token, new { purpose = "create_pat", audience = "ts-dashboard", return_uri = "https://dash.example.test/auth/step-up/callback" });

    private static HttpRequestMessage Deletion(string token, string receipt, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/terrascale/account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-TerraScale-Deletion-Receipt", receipt);
        if (authorization is not null)
        {
            request.Headers.Add("X-TerraScale-Deletion-Authorization", authorization);
        }
        return request;
    }
}
