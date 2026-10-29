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

namespace TerraScale.Authentication.Fast.Management;

/// <summary>Real HTTP and TerraBase proof issuance, hosted verification and atomic consumption.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class StepUpProofTests(TerraBaseFixture fixture)
{
    private const string Root = "/v1/terrascale/step-up";
    private const string Callback = "https://dash.example.test/auth/step-up/callback";

    [Test]
    public async Task Password_and_an_existing_MFA_session_cannot_replace_a_fresh_second_factor()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var services = oidc.Server.App.Services;
        var password = new Faker().Internet.Password(24);
        var hash = await services.GetRequiredService<PasswordHasher>().HashAsync(password, CancellationToken.None);
        var user = (await services.GetRequiredService<Users>().CreateAsync("terrascale", new Faker().Internet.Email(), hash, CancellationToken.None))!;
        var mfa = services.GetRequiredService<TotpCredentials>();
        var enrollment = (await mfa.BeginAsync(user))!;
        var totp = new Totp(Base32Encoding.ToBytes(enrollment.Secret));
        var activated = (await mfa.ConfirmAsync(user, totp.ComputeTotp(clock.UtcNow.UtcDateTime)))!;
        var session = await services.GetRequiredService<Sessions>().CreateAsync(activated.User with { MultiFactor = true });
        var (verifier, challenge) = OidcHarness.Pkce();
        using var exchange = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session.Token, challenge), verifier);
        var token = (await OidcHarness.JsonAsync(exchange)).GetProperty("access_token").GetString()!;
        var proof = await BeginAsync(oidc, token);
        var path = proof.GetProperty("step_up_url").GetString()!.Replace(OidcHarness.PublicOrigin, "", StringComparison.Ordinal);
        using (var primary = await HostedAsync(oidc, path, session.Token, new() { ["password"] = password }))
        {
            await Assert.That(primary.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await primary.Content.ReadAsStringAsync()).Contains("Authenticator or recovery code");
        }
        using (var insufficient = await ConsumeAsync(oidc, token, proof))
        {
            await Assert.That(insufficient.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        clock.UtcNow += TimeSpan.FromSeconds(31);
        using (var second = await HostedAsync(oidc, path, session.Token, new() { ["code"] = totp.ComputeTotp(clock.UtcNow.UtcDateTime) }))
        {
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        using var approved = await ConsumeAsync(oidc, token, proof);
        await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    public async Task Passwordless_step_up_requires_a_fresh_email_code_and_consumes_its_proof()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, session, tokens) = await oidc.SignInAndRedeemAsync(new Faker().Internet.Email());
        var token = tokens.GetProperty("access_token").GetString()!;
        var proof = await BeginAsync(oidc, token);
        var path = proof.GetProperty("step_up_url").GetString()!.Replace(OidcHarness.PublicOrigin, "", StringComparison.Ordinal);
        using (var unproved = await ConsumeAsync(oidc, token, proof))
        {
            await Assert.That(unproved.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        clock.UtcNow += TimeSpan.FromSeconds(61);
        using (var send = await HostedAsync(oidc, path, session, new() { ["action"] = "send" }))
        {
            await Assert.That(send.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        var code = await oidc.Email.NextCodeAsync(user.Email);
        using (var verify = await HostedAsync(oidc, path, session, new() { ["code"] = code }))
        {
            await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        using var consumed = await ConsumeAsync(oidc, token, proof);
        await Assert.That(consumed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var replay = await ConsumeAsync(oidc, token, proof);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
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
