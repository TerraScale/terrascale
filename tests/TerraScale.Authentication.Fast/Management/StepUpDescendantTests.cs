using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Management;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class StepUpDescendantTests(TerraBaseFixture fixture)
{
    private static readonly string[] ReadScopes = ["documents:read"];
    private static readonly string[] ClientRedirects = ["https://client.example.test/callback"];

    [Test]
    public async Task A_real_hosted_email_ancestor_loss_revokes_its_issued_API_key()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, session, tokens) = await oidc.SignInAndRedeemAsync(new Faker().Internet.Email());
        var token = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Hosted lineage");
        var (proof, binding) = await ApproveAsync(oidc, clock, user, session, token, "create_api_key");
        using var created = await PostAsync(oidc, $"{PlatformCalls.Organizations}/{organization}/projects/{project}/api-keys", token,
            new { name = "hosted", scopes = ReadScopes }, proof);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var key = (await OidcHarness.JsonAsync(created)).GetProperty("key").GetString()!;
        using (var admitted = await oidc.ApiKeyTokenAsync(key))
        {
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await LoseAsync(oidc, user, binding);
        using var refused = await oidc.ApiKeyTokenAsync(key);
        await InvalidClientAsync(refused);
        await oidc.OkAsync(HttpMethod.Get, PlatformCalls.Organizations, token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Rotated_secrets_keep_their_own_real_hosted_email_lineage_during_grace(bool loseCurrent)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, session, tokens) = await oidc.SignInAndRedeemAsync(new Faker().Internet.Email());
        var token = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Selective lineage");
        var keys = $"{PlatformCalls.Organizations}/{organization}/projects/{project}/api-keys";
        var (createProof, previousBinding) = await ApproveAsync(oidc, clock, user, session, token, "create_api_key");
        using var created = await PostAsync(oidc, keys, token, new { name = "rotate", scopes = ReadScopes }, createProof);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var previous = (await OidcHarness.JsonAsync(created)).GetProperty("key").GetString()!;
        var (rotateProof, currentBinding) = await ApproveAsync(oidc, clock, user, session, token, "rotate_api_key");
        await Assert.That(previousBinding.Key == currentBinding.Key).IsFalse();
        using var rotated = await PostAsync(oidc, $"{keys}/{previous[..26]}/rotate", token, new { previous_secret_ttl = 3600 }, rotateProof);
        await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var current = (await OidcHarness.JsonAsync(rotated)).GetProperty("key").GetString()!;
        using (var oldAdmitted = await oidc.ApiKeyTokenAsync(previous))
        using (var newAdmitted = await oidc.ApiKeyTokenAsync(current))
        {
            await Assert.That(oldAdmitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(newAdmitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await LoseAsync(oidc, user, loseCurrent ? currentBinding : previousBinding);
        using var refused = await oidc.ApiKeyTokenAsync(loseCurrent ? current : previous);
        await InvalidClientAsync(refused);
        using var surviving = await oidc.ApiKeyTokenAsync(loseCurrent ? previous : current);
        await Assert.That(surviving.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_confidential_client_retains_its_real_hosted_email_ancestor()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, session, tokens) = await oidc.SignInAndRedeemAsync(new Faker().Internet.Email());
        var token = tokens.GetProperty("access_token").GetString()!;
        var organization = await oidc.CreateOrganizationAsync(token);
        var project = await oidc.CreateProjectAsync(token, organization, "Confidential lineage");
        var (proof, binding) = await ApproveAsync(oidc, clock, user, session, token, "create_oauth_client");
        using var created = await PostAsync(oidc, $"/v1/{project}/clients", token,
            new { name = "hosted", type = "confidential", redirect_uris = ClientRedirects }, proof);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var client = await OidcHarness.JsonAsync(created);
        var id = client.GetProperty("client_id").GetString()!;
        var secret = client.GetProperty("client_secret").GetString()!;
        using (var admitted = await oidc.PostTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = "invalid" }, id, secret, tenant: project))
        {
            await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That((await OidcHarness.JsonAsync(admitted)).GetProperty("error").GetString()).IsEqualTo("invalid_grant");
        }
        await LoseAsync(oidc, user, binding);
        using var refused = await oidc.PostTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = "invalid" }, id, secret, tenant: project);
        await InvalidClientAsync(refused);
    }

    private static async Task<(string Proof, CredentialBinding Binding)> ApproveAsync(
        OidcHarness oidc, StoreTestClock clock, UserRecord user, string session, string token, string purpose)
    {
        clock.UtcNow += TimeSpan.FromSeconds(61); // Shared email budget, without a wall-clock delay.
        using var started = await PostAsync(oidc, "/v1/terrascale/step-up", token,
            new { purpose, audience = "ts-auth", return_uri = "https://dash.example.test/auth/step-up/callback" });
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var body = await OidcHarness.JsonAsync(started);
        var proof = body.GetProperty("proof").GetString()!;
        var path = body.GetProperty("step_up_url").GetString()!.Replace(OidcHarness.PublicOrigin, "", StringComparison.Ordinal);
        using (var shown = await HostedAsync(oidc, path, session))
        {
            await Assert.That(shown.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That(oidc.Email.HasPending).IsFalse();
        using (var sent = await HostedAsync(oidc, path, session, new() { ["action"] = "send" }))
        {
            await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        var code = await oidc.Email.NextCodeAsync(user.Email);
        using (var verified = await HostedAsync(oidc, path, session, new() { ["code"] = code }))
        {
            await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var document = (await store.GetAsync(AuthStore.StepUpProofCollection, user.InstanceId, proof[..26]))!;
        var record = document.Document.Deserialize(StepUpJson.Default.StepUpRecord)!;
        await Assert.That(record.Approved).IsTrue();
        return (proof, record.Bindings!.Single(binding => binding.Kind == "credential:passcode"));
    }

    private static async Task LoseAsync(OidcHarness oidc, UserRecord user, CredentialBinding binding)
    {
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var claim = (await store.FindUniqueAsync(user.InstanceId, binding.Kind, binding.Key))!.Value;
        // Reconcile exactly this fresh primary winner; preserve the user's epoch and browser sign-in.
        var failure = await store.TransactAsync([
            AuthStore.Reclaim(user.InstanceId, binding.Kind, binding.Key, AuthStore.NewId(), claim.Version)
        ]);
        await Assert.That(failure).IsNull();
    }

    private static async Task InvalidClientAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var body = await OidcHarness.JsonAsync(response);
        await Assert.That(body.GetProperty("error").GetString()).IsEqualTo("invalid_client");
        await Assert.That(body.TryGetProperty("access_token", out _)).IsFalse();
    }

    private static async Task<HttpResponseMessage> PostAsync(OidcHarness oidc, string path, string token, object body, string? proof = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (proof is not null)
        {
            request.Headers.Add(StepUpProofs.Header, proof);
        }
        return await oidc.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> HostedAsync(OidcHarness oidc, string path, string session, Dictionary<string, string>? form = null)
    {
        using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("Cookie", "tsauth_session=" + session);
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
            request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        }
        return await oidc.Client.SendAsync(request);
    }
}
