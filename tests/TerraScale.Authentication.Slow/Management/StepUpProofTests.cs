using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Management;

/// <summary>Real HTTP and TerraBase proof issuance, hosted verification and atomic consumption.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class StepUpProofTests(TerraBaseFixture fixture)
{
    private const string Root = "/v1/terrascale/step-up";
    private const string Callback = "https://dash.example.test/auth/step-up/callback";
    private static readonly string[] ReadScopes = ["documents:read"];
    private static readonly string[] ClientRedirects = ["https://client.example.test/callback"];

    [Test]
    public async Task Passwordless_proof_requires_fresh_email_once_and_rejects_csrf_cross_account_purpose_and_replay()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (alice, session, tokens) = await oidc.SignInAndRedeemAsync(new Faker().Internet.Email());
        var token = tokens.GetProperty("access_token").GetString()!;
        var (_, otherSession, otherTokens) = await oidc.SignInAndRedeemAsync(new Faker().Internet.Email());
        // A poisoned ordinary sign-in ancestry must never trigger epoch recovery during step-up.
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var ordinary = (await store.GetAsync(AuthStore.PasscodeCollection, alice.InstanceId, alice.Id))!;
        var ordinaryCode = ordinary.Document.Deserialize(IdentityJson.Default.PasscodeRecord)!;
        var poisoned = ordinaryCode with { ResetsAuthentication = true };
        await store.ReplaceAsync(AuthStore.PasscodeCollection, alice.InstanceId, alice.Id,
            JsonSerializer.SerializeToElement(poisoned, IdentityJson.Default.PasscodeRecord), ordinary.Version);
        var proof = await BeginAsync(oidc, token);
        var path = proof.GetProperty("step_up_url").GetString()!.Replace(OidcHarness.PublicOrigin, "", StringComparison.Ordinal);
        using (var unproved = await ConsumeAsync(oidc, token, proof))
        {
            await Assert.That(unproved.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        using (var foreign = await HostedAsync(oidc, path, otherSession))
        {
            await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        using (var shown = await HostedAsync(oidc, path, session))
        {
            await Assert.That(shown.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await shown.Content.ReadAsStringAsync()).DoesNotContain(proof.GetProperty("proof").GetString()!);
        }
        await Assert.That(oidc.Email.HasPending).IsFalse();
        clock.UtcNow += TimeSpan.FromSeconds(61);
        using (var csrf = await HostedAsync(oidc, path, session, new() { ["action"] = "send" }, "https://evil.example"))
        {
            await Assert.That(csrf.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        using (var send = await HostedAsync(oidc, path, session, new() { ["action"] = "send" }))
        {
            await Assert.That(send.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        var code = await oidc.Email.NextCodeAsync(alice.Email);
        using (var repeated = await HostedAsync(oidc, path, session, new() { ["action"] = "send" }))
        {
            await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        await Assert.That(oidc.Email.HasPending).IsFalse();
        using (var login = await oidc.PostFormAsync("/ui/terrascale/sign-in/verify", new() { ["email"] = alice.Email, ["code"] = code }))
        {
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        var otherProof = await BeginAsync(oidc, token);
        var otherPath = otherProof.GetProperty("step_up_url").GetString()!.Replace(OidcHarness.PublicOrigin, "", StringComparison.Ordinal);
        using (var anotherAttempt = await HostedAsync(oidc, otherPath, session, new() { ["code"] = code }))
        {
            await Assert.That(anotherAttempt.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        using (var verify = await HostedAsync(oidc, path, session, new() { ["code"] = code }))
        {
            await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        var after = (await oidc.Server.App.Services.GetRequiredService<Users>().GetAsync(alice.InstanceId, alice.Id))!;
        await Assert.That(after.AuthEpoch).IsEqualTo(alice.AuthEpoch);
        await Assert.That(after.AuthStamp).IsEqualTo(alice.AuthStamp);
        using (var wrongPurpose = await ConsumeAsync(oidc, token, proof, "delete_account", Receipt()))
        {
            await Assert.That(wrongPurpose.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        using (var wrongUser = await ConsumeAsync(oidc, otherTokens.GetProperty("access_token").GetString()!, proof))
        {
            await Assert.That(wrongUser.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ConsumeAsync(oidc, token, proof)));
        try
        {
            await Assert.That(attempts.Count(result => result.StatusCode == HttpStatusCode.NoContent)).IsEqualTo(1);
            await Assert.That(attempts.Count(result => result.StatusCode == HttpStatusCode.Forbidden)).IsEqualTo(5);
        }
        finally
        {
            foreach (var response in attempts)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task Expiry_refuses_approved_proofs_and_all_sensitive_auth_mutations_require_proof()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, token) = await oidc.SignInUserAsync(new Faker().Internet.Email());
        var proof = await oidc.ApprovedProofAsync(token, "create_pat", "ts-dashboard");
        clock.UtcNow += TimeSpan.FromMinutes(5);
        using (var expired = await RawAsync(oidc, Root + "/consume", token, new { proof, purpose = "create_pat", audience = "ts-dashboard" }))
        {
            await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, "/v1/terrascale/account"))
        {
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var result = await oidc.Client.SendAsync(delete);
            await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        var organization = await oidc.CreateOrganizationAsync(token);
        using (var key = await RawAsync(oidc, $"{PlatformCalls.Organizations}/{organization}/api-keys", token,
            new { name = "unproved", scopes = ReadScopes }))
        {
            await Assert.That(key.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        var keys = await oidc.OkAsync(HttpMethod.Get, $"{PlatformCalls.Organizations}/{organization}/api-keys", token);
        await Assert.That(keys.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        var created = await oidc.OkAsync(HttpMethod.Post, $"{PlatformCalls.Organizations}/{organization}/api-keys", token,
            new { name = "proved", scopes = ReadScopes }, HttpStatusCode.Created);
        var keyId = created.GetProperty("api_key").GetProperty("id").GetString();
        using (var rotation = await RawAsync(oidc, $"{PlatformCalls.Organizations}/{organization}/api-keys/{keyId}/rotate", token, new { }))
        {
            await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        var project = await oidc.CreateProjectAsync(token, organization, "Step-up");
        using var client = await RawAsync(oidc, $"/v1/{project}/clients", token,
            new { name = "unproved", type = "confidential", redirect_uris = ClientRedirects });
        await Assert.That(client.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Deletion_consumption_binds_only_the_exact_receipt_and_replay_cannot_authorize_a_second_operation()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync(new Faker().Internet.Email());
        var proof = await oidc.ApprovedProofAsync(token, "delete_account", "ts-dashboard");
        var receipt = Receipt();
        string authorization;
        using (var consume = await RawAsync(oidc, Root + "/consume", token, new { proof, purpose = "delete_account", audience = "ts-dashboard", operation_receipt = receipt }))
        {
            await Assert.That(consume.StatusCode).IsEqualTo(HttpStatusCode.OK);
            authorization = (await OidcHarness.JsonAsync(consume)).GetProperty("operation_grant").GetString()!;
        }
        using (var replay = await RawAsync(oidc, Root + "/consume", token, new { proof, purpose = "delete_account", audience = "ts-dashboard", operation_receipt = Receipt() }))
        {
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/v1/terrascale/account");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        delete.Headers.Add("X-TerraScale-Deletion-Receipt", Receipt());
        using var wrongReceipt = await oidc.Client.SendAsync(delete);
        await Assert.That(wrongReceipt.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var allowed = new HttpRequestMessage(HttpMethod.Delete, "/v1/terrascale/account");
        allowed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        allowed.Headers.Add("X-TerraScale-Deletion-Receipt", receipt);
        allowed.Headers.Add("X-TerraScale-Deletion-Authorization", authorization);
        using var result = await oidc.Client.SendAsync(allowed);
        await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    private static string Receipt() => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static async Task<JsonElement> BeginAsync(OidcHarness oidc, string token)
    {
        using var response = await RawAsync(oidc, Root, token, new { purpose = "create_pat", audience = "ts-dashboard", return_uri = Callback });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        return await OidcHarness.JsonAsync(response);
    }

    private static Task<HttpResponseMessage> ConsumeAsync(OidcHarness oidc, string token, JsonElement proof, string purpose = "create_pat", string? receipt = null) =>
        RawAsync(oidc, Root + "/consume", token, new { proof = proof.GetProperty("proof").GetString(), purpose, audience = "ts-dashboard", operation_receipt = receipt });

    private static Task<HttpResponseMessage> RawAsync(OidcHarness oidc, string path, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return oidc.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> HostedAsync(OidcHarness oidc, string path, string session, Dictionary<string, string>? form = null, string origin = OidcHarness.PublicOrigin)
    {
        var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add("Cookie", "tsauth_session=" + session);
        if (form is not null) { request.Content = new FormUrlEncodedContent(form); request.Headers.Add("Origin", origin); }
        return oidc.Client.SendAsync(request);
    }
}
