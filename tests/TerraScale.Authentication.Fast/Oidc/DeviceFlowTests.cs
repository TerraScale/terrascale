using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>RFC 8628 for the ts CLI: codes from /oauth/device, approval on ts-auth's hosted device page, polling at /oauth/token.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DeviceFlowTests(TerraBaseFixture fixture)
{
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string Page = $"/ui/{OidcIssuer.Slug}/device";
    private static readonly string[] DeviceScopes = ["openid", "email", "offline_access"];

    private static ManualClock Clock() => new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    [Test]
    public async Task Resource_scopes_are_never_offered_as_approvable_sign_in_permissions()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        using var requested = await oidc.Client.PostAsync($"/{OidcIssuer.Slug}/oauth/device", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = OidcIssuer.CliClientId, ["scope"] = "openid email documents:read" }));
        await Assert.That(requested.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var device = await OidcHarness.JsonAsync(requested);
        var code = device.GetProperty("user_code").GetString()!;
        using var shown = await oidc.AuthorizeAsync($"{Page}?user_code={Uri.EscapeDataString(code)}", session);
        await Assert.That(shown.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var html = await shown.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("Device request unavailable");
        await Assert.That(html).DoesNotContain("Requested permissions");
        await Assert.That(html).DoesNotContain("name=\"decision\"");
        using var approved = await DecideAsync(oidc, session, code, "approve");
        await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await PollAsync(oidc, device.GetProperty("device_code").GetString()!)).IsEqualTo("authorization_pending");
    }

    [Test]
    public async Task A_device_is_signed_in_only_after_an_explicit_approval_and_redeems_once()
    {
        var clock = Clock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await oidc.CreateUserAsync();
        var device = await AuthorizeDeviceAsync(oidc);

        await Assert.That(device.GetProperty("verification_uri").GetString()).IsEqualTo(OidcHarness.PublicOrigin + Page);
        var userCode = device.GetProperty("user_code").GetString()!;
        await Assert.That(device.GetProperty("verification_uri_complete").GetString()).IsEqualTo($"{OidcHarness.PublicOrigin}{Page}?user_code={userCode}");
        await Assert.That(device.GetProperty("interval").GetInt64()).IsEqualTo(5);
        await Assert.That(device.GetProperty("expires_in").GetInt64()).IsBetween(590, 600);
        var deviceCode = device.GetProperty("device_code").GetString()!;

        // Pending until approved; polling again within the interval is told to slow down.
        await Assert.That(await PollAsync(oidc, deviceCode)).IsEqualTo("authorization_pending");
        await Assert.That(await PollAsync(oidc, deviceCode)).IsEqualTo("slow_down");

        // Without a session, the link sends the browser to sign in and back with the code; nothing is looked up.
        var link = $"{Page}?user_code={Uri.EscapeDataString(userCode)}";
        using var anonymous = await oidc.AuthorizeAsync(link, session: null);
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(anonymous.Headers.Location!.OriginalString).IsEqualTo($"/ui/{OidcIssuer.Slug}/sign-in?next={Uri.EscapeDataString(link)}");

        // Signing in with that next continues to the device page.
        var session = await oidc.SignInAsync("alice@example.test");
        using var signIn = await oidc.AuthorizeAsync($"/ui/{OidcIssuer.Slug}/sign-in?next={Uri.EscapeDataString(link)}", session: null);
        await Assert.That(await signIn.Content.ReadAsStringAsync()).Contains($"name=\"next\" value=\"{link}\"");

        // Opening the link shows the code and the account, and approves nothing.
        using var shown = await oidc.AuthorizeAsync(link, session);
        var confirm = await shown.Content.ReadAsStringAsync();
        await Assert.That(shown.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(confirm).Contains("<h1>Approve this device?</h1>");
        await Assert.That(confirm).Contains(userCode);
        await Assert.That(confirm).Contains("alice@example.test");
        await Assert.That(confirm).Contains("ts-cli");
        await Assert.That(confirm).Contains("Read your email address (email)");
        await Assert.That(confirm).Contains("Sign you in and identify your account (openid)");
        await Assert.That(confirm).Contains("Keep access through refresh tokens until access expires or is revoked (offline_access)");
        await Assert.That(confirm).DoesNotContain("Read your profile information");
        await Assert.That(confirm).Contains("name=\"decision\" value=\"approve\"");
        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.That(await PollAsync(oidc, deviceCode)).IsEqualTo("authorization_pending");

        // A cross-origin approval is refused.
        using var forged = await DecideAsync(oidc, session, userCode, "approve", origin: "https://evil.example");
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.That(await PollAsync(oidc, deviceCode)).IsEqualTo("authorization_pending");

        using var approved = await DecideAsync(oidc, session, userCode, "approve");
        await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await approved.Content.ReadAsStringAsync()).Contains("<h1>Device connected</h1>");

        clock.Advance(TimeSpan.FromSeconds(6));
        using var tokens = await PollResponseAsync(oidc, deviceCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await tokens.Content.ReadAsStringAsync());
        await Assert.That(tokens.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("access_token").GetString()).IsNotNull();
        await Assert.That(body.GetProperty("refresh_token").GetString()).IsNotNull();
        await Assert.That(body.GetProperty("id_token").GetString()).IsNotNull();
        await Assert.That(body.GetProperty("scope").GetString()!.Split(' ').Order()).IsEquivalentTo(DeviceScopes.Order());

        // The device's access token calls the platform API as the approving user.
        var (status, _) = await oidc.FailAsync(HttpMethod.Get, PlatformCalls.Organizations, body.GetProperty("access_token").GetString());
        await Assert.That(status).IsEqualTo((int)HttpStatusCode.OK);

        // A device code is redeemed once, and its user code decides once.
        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.That(await PollAsync(oidc, deviceCode)).IsEqualTo("invalid_grant");
        using var again = await DecideAsync(oidc, session, userCode, "approve");
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await again.Content.ReadAsStringAsync()).Contains("That code is wrong or expired");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Repeated_consent_GETs_leave_codes_unchanged_and_the_rendered_form_can_be_approved(bool compactCode)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var device = await AuthorizeDeviceAsync(oidc);
        var complete = new Uri(device.GetProperty("verification_uri_complete").GetString()!).PathAndQuery;
        var entered = device.GetProperty("user_code").GetString()!;
        if (compactCode)
        {
            entered = entered.Replace("-", string.Empty, StringComparison.Ordinal);
            complete = $"{Page}?user_code={Uri.EscapeDataString(entered)}";
        }

        var before = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items
            .Select(document => (document.Id, document.Version)).ToArray();
        string submitted = string.Empty;
        for (var visit = 0; visit < 2; visit++)
        {
            using var shown = await oidc.AuthorizeAsync(complete, session);
            await Assert.That(shown.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await shown.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("<h1>Approve this device?</h1>");
            // Submit the actual native form value, rather than reconstructing it from the issuance response.
            var hidden = Regex.Match(html, "name=\"user_code\" value=\"([^\"]+)\"");
            await Assert.That(hidden.Success).IsTrue();
            submitted = WebUtility.HtmlDecode(hidden.Groups[1].Value);
            await Assert.That(submitted).IsEqualTo(entered);
        }

        var after = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items
            .Select(document => (document.Id, document.Version)).ToArray();
        await Assert.That(after).IsEquivalentTo(before);
        using var approved = await DecideAsync(oidc, session, submitted, "approve");
        await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await approved.Content.ReadAsStringAsync()).Contains("<h1>Device connected</h1>");
        using var exchanged = await PollResponseAsync(oidc, device.GetProperty("device_code").GetString()!);
        await Assert.That(exchanged.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_denied_device_gets_access_denied_and_an_old_code_expires()
    {
        var clock = Clock();
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");

        var denied = await AuthorizeDeviceAsync(oidc);
        using var deny = await DecideAsync(oidc, session, denied.GetProperty("user_code").GetString()!, "deny");
        await Assert.That(deny.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await deny.Content.ReadAsStringAsync()).Contains("<h1>Request denied</h1>");
        await Assert.That(await PollAsync(oidc, denied.GetProperty("device_code").GetString()!)).IsEqualTo("access_denied");

        var expired = await AuthorizeDeviceAsync(oidc);
        clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.That(await PollAsync(oidc, expired.GetProperty("device_code").GetString()!)).IsEqualTo("expired_token");
        using var late = await DecideAsync(oidc, session, expired.GetProperty("user_code").GetString()!, "approve");
        await Assert.That(late.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // An unknown client gets no codes.
        using var unknown = await oidc.Client.PostAsync($"/{OidcIssuer.Slug}/oauth/device", new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = "00000000000000000000000099" }));
        await Assert.That(unknown.StatusCode).IsNotEqualTo(HttpStatusCode.OK);

        using var unsupported = await oidc.Client.PostAsync($"/{OidcIssuer.Slug}/oauth/device", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = OidcIssuer.CliClientId, ["scope"] = "openid invented_permission" }));
        await Assert.That(unsupported.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Consent_uses_the_current_registered_name_and_refuses_a_revoked_client()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var session = await oidc.SignInAsync("alice@example.test");
        var device = await AuthorizeDeviceAsync(oidc);
        var applications = oidc.Server.App.Services.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await applications.FindByClientIdAsync(OidcIssuer.CliClientId))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application);
        descriptor.DisplayName = "Research <device> & client";
        await applications.UpdateAsync(application, descriptor);
        var link = $"{Page}?user_code={Uri.EscapeDataString(device.GetProperty("user_code").GetString()!)}";
        using var consent = await oidc.AuthorizeAsync(link, session);
        await Assert.That(await consent.Content.ReadAsStringAsync()).Contains("Research &lt;device&gt; &amp; client");
        await applications.DeleteAsync(application);
        using var revoked = await DecideAsync(oidc, session, device.GetProperty("user_code").GetString()!, "approve");
        await Assert.That(revoked.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> AuthorizeDeviceAsync(OidcHarness oidc)
    {
        using var response = await oidc.Client.PostAsync($"/{OidcIssuer.Slug}/oauth/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = OidcIssuer.CliClientId,
            ["scope"] = "openid email offline_access",
        }));
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK
            ? JsonSerializer.Deserialize<JsonElement>(body)
            : throw new InvalidOperationException($"The device endpoint answered {response.StatusCode}: {body}");
    }

    private static Task<HttpResponseMessage> PollResponseAsync(OidcHarness oidc, string deviceCode) =>
        oidc.Client.PostAsync($"/{OidcIssuer.Slug}/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = DeviceGrant,
            ["device_code"] = deviceCode,
            ["client_id"] = OidcIssuer.CliClientId,
        }));

    private static async Task<string?> PollAsync(OidcHarness oidc, string deviceCode)
    {
        using var response = await PollResponseAsync(oidc, deviceCode);
        return await OidcHarness.ErrorAsync(response);
    }

    private static Task<HttpResponseMessage> DecideAsync(OidcHarness oidc, string session, string userCode, string decision, string origin = OidcHarness.PublicOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Page)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["user_code"] = userCode, ["decision"] = decision }),
        };
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Cookie", $"tsauth_session={session}");
        return oidc.Client.SendAsync(request);
    }
}
