namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Playwright;
using TerraScale.Auth.Ui;

/// <summary>Hosted admin portal, driven through a real headless Chromium (Playwright):
/// org-admin authorization on every route, connection create validation, HTML escaping
/// of echoed names, and enable/disable effects. The JSON management API and the
/// POST-only authorization probes stay on the wire.</summary>
[NotInParallel]
public sealed class AdminPortalE2eTests
{
    private static async Task<AuthServiceFixture> ServiceAsync() => await AuthServiceFixture.GetAsync();

    private static async Task<(string InstanceId, string Slug, string ApiKey)> InstanceAsync()
    {
        var service = await ServiceAsync();
        return await service.CreateInstanceAsync("admin-" + Guid.NewGuid().ToString("N")[..8]);
    }

    private static string Email() => $"admin{Guid.NewGuid().ToString("N")[..10]}@example.com";

    private static string Url(AuthServiceFixture service, string path) => new Uri(service.Origin, path).ToString();

    private static HttpRequestMessage Get(string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage Post(string path, Dictionary<string, string> fields, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage Api(HttpMethod method, string apiKey, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    /// <summary>Signs a user up over the hosted UI and returns (cookie, user id).</summary>
    private static async Task<(string Cookie, string UserId)> SignUpAsync(AuthServiceFixture service, string slug, string instanceId)
    {
        var email = Email();
        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var header = response.Headers.GetValues("Set-Cookie").First();
        var user = await service.Store.FindUserByEmailAsync(instanceId, email)
            ?? throw new InvalidOperationException("user missing from store");
        return (header.Split(';')[0], user.Id);
    }

    /// <summary>Signs up through the real sign-up page; the live session stays in the
    /// page's browser context and the created user's id is returned.</summary>
    private static async Task<string> SignUpInBrowserAsync(AuthServiceFixture service, IPage page, string slug, string instanceId)
    {
        var email = Email();
        await page.GotoAsync(Url(service, $"/ui/{slug}/sign-up"));
        await page.FillAsync("#su-email", email);
        await page.FillAsync("#su-password", "password123");
        // Register navigation before submitting, then assert the authenticated
        // page itself. Authorization probes do not depend on the window load event.
        await Task.WhenAll(
            page.WaitForURLAsync(Url(service, $"/ui/{slug}/account"),
                new() { WaitUntil = WaitUntilState.DOMContentLoaded }),
            page.ClickAsync("button:has-text('Create account')"));
        await page.GetByRole(AriaRole.Heading, new() { Name = "Your account", Exact = true }).WaitForAsync();
        var user = await service.Store.FindUserByEmailAsync(instanceId, email)
            ?? throw new InvalidOperationException("user missing from store");
        return user.Id;
    }

    /// <summary>The browser context's live session cookie (name=value), for wire-level
    /// probes against the same session the page is signed in with.</summary>
    private static async Task<string> SessionCookieAsync(AuthServiceFixture service, IPage page)
    {
        var cookie = (await page.Context.CookiesAsync(service.Origin.ToString()))
            .Single(c => c.Name == SessionCookies.Name);
        return $"{SessionCookies.Name}={cookie.Value}";
    }

    /// <summary>Fails the test if any script dialog (alert/confirm/prompt) ever fires on the page.</summary>
    private static void FailOnDialog(IPage page)
    {
        page.Dialog += (_, dialog) => throw new InvalidOperationException(
            $"unexpected script dialog ({dialog.Type}): {dialog.Message}");
    }

    private static async Task<string> CreateOrganizationAsync(AuthServiceFixture service, string slug, string apiKey, string name)
    {
        using var response = await service.Http.SendAsync(Api(HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    private static async Task AddMembershipAsync(AuthServiceFixture service, string slug, string apiKey, string orgId, string userId, string role)
    {
        using var response = await service.Http.SendAsync(Api(HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = userId, role }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static (X509Certificate2 Certificate, string Pem) NewIdpCertificate()
    {
        var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test SAML IdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return (certificate, certificate.ExportCertificatePem());
    }

    private static Dictionary<string, string> ConnectionForm(string pem, string ssoUrl = "https://idp.example/sso", string name = "Corp IdP") => new()
    {
        ["name"] = name,
        ["idp_entity_id"] = "https://idp.example/metadata",
        ["sso_redirect_url"] = ssoUrl,
        ["x509_certificate_pem"] = pem,
    };

    /// <summary>Fills and submits the portal's add-connection form through the real
    /// browser; returns the POST response (302 on success, 400 on validation error).
    /// The certificate field is a single-line input, so the browser strips the PEM's
    /// newlines — the server's parser is newline-agnostic (it strips the PEM markers
    /// and base64-decodes the rest), so this is exactly how a real user pastes one.</summary>
    private static async Task<IResponse> AddConnectionAsync(
        AuthServiceFixture service, IPage page, string slug, string name, string pem, string ssoUrl = "https://idp.example/sso")
    {
        await page.FillAsync("input[name=name]", name);
        await page.FillAsync("input[name=idp_entity_id]", "https://idp.example/metadata");
        await page.FillAsync("input[name=sso_redirect_url]", ssoUrl);
        await page.FillAsync("input[name=x509_certificate_pem]", pem);
        return (await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button:has-text('Add SAML connection')"),
            Url(service, $"/ui/{slug}/admin/connections")))!;
    }

    private static async Task<int> ConnectionCountAsync(AuthServiceFixture service, string instanceId)
        => (await service.Store.ListConnectionsAsync(instanceId)).Count;

    [Test]
    public async Task EveryRouteRejectsAnonymousNonMemberMemberAndBannedUsers()
    {
        var service = await ServiceAsync();
        var (instanceId, slug, apiKey) = await InstanceAsync();
        var orgId = await CreateOrganizationAsync(service, slug, apiKey, "Acme");

        var (_, adminId) = await SignUpAsync(service, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, adminId, "admin");
        await using var memberPage = await BrowserFixture.NewPageAsync();
        var memberId = await SignUpInBrowserAsync(service, memberPage, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, memberId, "member");
        await using var nonMemberPage = await BrowserFixture.NewPageAsync();
        await SignUpInBrowserAsync(service, nonMemberPage, slug, instanceId);
        await using var bannedPage = await BrowserFixture.NewPageAsync();
        var bannedId = await SignUpInBrowserAsync(service, bannedPage, slug, instanceId);
        using var ban = await service.Http.SendAsync(Api(HttpMethod.Patch, apiKey, $"/v1/{slug}/users/{bannedId}", new { banned = true }));
        await Assert.That(ban.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var (_, pem) = NewIdpCertificate();
        var form = ConnectionForm(pem);

        // Anonymous: the browser is bounced to sign-in on every route.
        await using var anonPage = await BrowserFixture.NewPageAsync();
        await anonPage.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        await Assert.That(anonPage.Url).Contains("/sign-in");
        using (var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", form)))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using (var toggled = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/con_x/disable", form)))
            await Assert.That(toggled.StatusCode).IsEqualTo(HttpStatusCode.Redirect);

        // Plain member of the org: the portal renders its 403 page; the POST-only routes
        // reject over the wire with the same session the browser holds.
        var denied = await memberPage.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        await Assert.That(denied!.Status).IsEqualTo(403);
        await Assert.That(await memberPage.Locator("h1").TextContentAsync()).IsEqualTo("Not authorized");
        var memberCookie = await SessionCookieAsync(service, memberPage);
        var before = await ConnectionCountAsync(service, instanceId);
        using (var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", form, memberCookie)))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ConnectionCountAsync(service, instanceId)).IsEqualTo(before);
        using (var toggled = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/con_x/disable", form, memberCookie)))
            await Assert.That(toggled.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // Signed-in non-member: 403 on all three routes.
        denied = await nonMemberPage.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        await Assert.That(denied!.Status).IsEqualTo(403);
        var nonMemberCookie = await SessionCookieAsync(service, nonMemberPage);
        using (var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", form, nonMemberCookie)))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await ConnectionCountAsync(service, instanceId)).IsEqualTo(before);
        }

        // Banned user: session no longer resolves, so the browser is bounced to sign-in.
        await bannedPage.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        await Assert.That(bannedPage.Url).Contains("/sign-in");
        var bannedCookie = await SessionCookieAsync(service, bannedPage);
        using (var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", form, bannedCookie)))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using (var toggled = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/con_x/disable", form, bannedCookie)))
            await Assert.That(toggled.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
    }

    private static async Task<string> SignInCookieAsync(AuthServiceFixture service, string slug, string userId)
    {
        var user = await service.Store.GetUserAsync(userId) ?? throw new InvalidOperationException("user missing");
        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = user.Email!,
            ["password"] = "password123",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        return response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
    }

    [Test]
    public async Task OrgAdminCreatesSamlConnectionScopedToTheirOrgOnly()
    {
        var service = await ServiceAsync();
        var (instanceId, slug, apiKey) = await InstanceAsync();
        var orgId = await CreateOrganizationAsync(service, slug, apiKey, "Acme");
        var otherOrgId = await CreateOrganizationAsync(service, slug, apiKey, "Globex");

        await using var page = await BrowserFixture.NewPageAsync();
        var adminId = await SignUpInBrowserAsync(service, page, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, adminId, "admin");

        // The org admin adds the connection through the real portal form; the browser
        // follows the redirect back to the portal.
        var (_, pem) = NewIdpCertificate();
        await page.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        var created = await AddConnectionAsync(service, page, slug, "Corp IdP", pem);
        await Assert.That(created.Status).IsEqualTo(302);
        await page.WaitForURLAsync("**/admin");

        var connections = await service.Store.ListConnectionsAsync(instanceId);
        var connection = connections.SingleOrDefault(c => c.Name == "Corp IdP")
            ?? throw new InvalidOperationException("connection not created");
        await Assert.That(connection.OrgId).IsEqualTo(orgId);
        await Assert.That(connection.Type).IsEqualTo("saml");
        await Assert.That(connection.Enabled).IsTrue();

        // A connection owned by another org, and an instance-level one, must not leak onto this admin's page.
        using var foreign = await service.Http.SendAsync(Api(HttpMethod.Post, apiKey, $"/v1/{slug}/connections", new
        {
            type = "saml",
            name = "Globex SSO",
            org_id = otherOrgId,
            idp_entity_id = "https://globex.example/metadata",
            sso_redirect_url = "https://globex.example/sso",
            x509_certificate_pem = pem,
        }));
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var instanceLevel = await service.Http.SendAsync(Api(HttpMethod.Post, apiKey, $"/v1/{slug}/connections", new
        {
            type = "saml",
            name = "Instance SSO",
            idp_entity_id = "https://inst.example/metadata",
            sso_redirect_url = "https://inst.example/sso",
            x509_certificate_pem = pem,
        }));
        await Assert.That(instanceLevel.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // The rendered portal lists only the connections of the orgs this admin runs.
        var portal = await page.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        await Assert.That(portal!.Status).IsEqualTo(200);
        var rows = string.Join("\n", await page.Locator(".row").AllTextContentsAsync());
        await Assert.That(rows).Contains("Corp IdP");
        await Assert.That(rows).DoesNotContain("Globex SSO");
        await Assert.That(rows).DoesNotContain("Instance SSO");
    }

    [Test]
    public async Task ConnectionCreateValidatesPemAndUrlAndNeverStoresInvalidInput()
    {
        var service = await ServiceAsync();
        var (instanceId, slug, apiKey) = await InstanceAsync();
        var orgId = await CreateOrganizationAsync(service, slug, apiKey, "Acme");
        await using var page = await BrowserFixture.NewPageAsync();
        var adminId = await SignUpInBrowserAsync(service, page, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, adminId, "admin");

        var (_, pem) = NewIdpCertificate();
        var badPems = new[]
        {
            "not a pem",
            "-----BEGIN FOO-----\nAAAA\n-----END FOO-----",
            "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----",   // truncated base64 -> not a cert
            "-----BEGIN CERTIFICATE-----\n" + pem.Replace("-----BEGIN CERTIFICATE-----", "").Replace("-----END CERTIFICATE-----", "").Trim()[..20] + "\n-----END CERTIFICATE-----",
        };
        var badUrls = new[] { "/relative/sso", "javascript:alert(1)", "ftp://idp.example/sso", "file:///etc/passwd", "idp.example/sso" };

        await page.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        foreach (var badPem in badPems)
        {
            // Each rejected submit re-renders the form with the validation error.
            var response = await AddConnectionAsync(service, page, slug, "Corp IdP", badPem);
            await Assert.That(response.Status).IsEqualTo(400);
            await page.Locator(".error").WaitForAsync();
        }
        foreach (var badUrl in badUrls)
        {
            var response = await AddConnectionAsync(service, page, slug, "Corp IdP", pem, badUrl);
            await Assert.That(response.Status).IsEqualTo(400);
            await page.Locator(".error").WaitForAsync();
        }
        await Assert.That(await ConnectionCountAsync(service, instanceId)).IsEqualTo(0);
    }

    [Test]
    public async Task ConnectionAndOrgNamesAreHtmlEscapedEverywhereTheyRender()
    {
        var service = await ServiceAsync();
        var (instanceId, slug, apiKey) = await InstanceAsync();
        var orgId = await CreateOrganizationAsync(service, slug, apiKey, "<script>alert(3)</script>");
        await Assert.That(orgId.StartsWith("org_")).IsTrue();
        await using var page = await BrowserFixture.NewPageAsync();
        FailOnDialog(page);
        var adminId = await SignUpInBrowserAsync(service, page, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, adminId, "admin");

        var hostileName = "\"><img src=x onerror=alert(2)><script>alert(1)</script>";
        var (_, pem) = NewIdpCertificate();
        await page.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        var created = await AddConnectionAsync(service, page, slug, hostileName, pem);
        await Assert.That(created.Status).IsEqualTo(302);
        await page.WaitForURLAsync("**/admin");

        // The portal renders the hostile name as TEXT (the DOM decodes the entities)
        // and never as markup: no script or attacker img elements, no script dialog.
        var rows = string.Join("\n", await page.Locator(".row").AllTextContentsAsync());
        await Assert.That(rows).Contains(hostileName);
        await Assert.That(await page.Locator("script").CountAsync()).IsEqualTo(0);
        await Assert.That(await page.Locator("img[src=x]").CountAsync()).IsEqualTo(0);

        // The public sign-in connection list escapes the same name.
        await page.GotoAsync(Url(service, $"/ui/{slug}/connections/list"));
        await Assert.That(await page.Locator("body").TextContentAsync()).Contains(hostileName);
        await Assert.That(await page.Locator("script").CountAsync()).IsEqualTo(0);
        await Assert.That(await page.Locator("img[src=x]").CountAsync()).IsEqualTo(0);

        // Validation errors render a page without echoing raw input.
        await page.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        var bad = await AddConnectionAsync(service, page, slug, "<script>alert(9)</script>", "not a pem");
        await Assert.That(bad.Status).IsEqualTo(400);
        await page.Locator(".error").WaitForAsync();
        await Assert.That(await page.Locator("script").CountAsync()).IsEqualTo(0);
        await Assert.That(await page.Locator("img[src=x]").CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task EnableDisableIsOrgAdminGatedAndDisabledConnectionsLeaveSignIn()
    {
        var service = await ServiceAsync();
        var (instanceId, slug, apiKey) = await InstanceAsync();
        var orgId = await CreateOrganizationAsync(service, slug, apiKey, "Acme");
        var otherOrgId = await CreateOrganizationAsync(service, slug, apiKey, "Globex");

        await using var page = await BrowserFixture.NewPageAsync();
        var adminId = await SignUpInBrowserAsync(service, page, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, adminId, "admin");
        var (_, memberId) = await SignUpAsync(service, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, memberId, "member");
        var (otherAdminCookie, otherAdminId) = await SignUpAsync(service, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, otherOrgId, otherAdminId, "admin");

        var (_, pem) = NewIdpCertificate();
        await page.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        var created = await AddConnectionAsync(service, page, slug, "Corp IdP", pem);
        await Assert.That(created.Status).IsEqualTo(302);
        await page.WaitForURLAsync("**/admin");
        var connection = (await service.Store.ListConnectionsAsync(instanceId)).Single(c => c.OrgId == orgId);
        var adminCookie = await SessionCookieAsync(service, page);

        // Admin of a different org: 403, connection untouched.
        using (var denied = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/{connection.Id}/disable", [], otherAdminCookie)))
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        // Plain member: 403, connection untouched.
        var memberCookie = await SignInCookieAsync(service, slug, memberId);
        using (var denied = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/{connection.Id}/disable", [], memberCookie)))
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await service.Store.GetConnectionAsync(connection.Id))!.Enabled).IsTrue();

        // Still offered at sign-in while enabled.
        using (var offered = await service.Http.SendAsync(Get($"/ui/{slug}/connections")))
        {
            var body = await offered.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.EnumerateArray().Any(c => c.GetProperty("id").GetString() == connection.Id)).IsTrue();
        }

        // Org admin disables through the portal: effective immediately at sign-in.
        var disabled = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button:has-text('Disable')"),
            Url(service, $"/ui/{slug}/admin/connections/{connection.Id}/disable"));
        await Assert.That(disabled!.Status).IsEqualTo(302);
        await page.WaitForURLAsync("**/admin");
        await Assert.That((await service.Store.GetConnectionAsync(connection.Id))!.Enabled).IsFalse();
        using (var offered = await service.Http.SendAsync(Get($"/ui/{slug}/connections")))
        {
            var body = await offered.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.EnumerateArray().Any(c => c.GetProperty("id").GetString() == connection.Id)).IsFalse();
        }
        await page.GotoAsync(Url(service, $"/ui/{slug}/connections/list"));
        await Assert.That(await page.Locator("body").TextContentAsync()).DoesNotContain("Corp IdP");

        // And the org admin can bring it back (the portal row now offers Enable).
        await page.GotoAsync(Url(service, $"/ui/{slug}/admin"));
        var enabled = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button:has-text('Enable')"),
            Url(service, $"/ui/{slug}/admin/connections/{connection.Id}/enable"));
        await Assert.That(enabled!.Status).IsEqualTo(302);
        await page.WaitForURLAsync("**/admin");
        await Assert.That((await service.Store.GetConnectionAsync(connection.Id))!.Enabled).IsTrue();

        // Instance-level connections are managed by the API, not the portal: org admin cannot toggle.
        using var instanceLevel = await service.Http.SendAsync(Api(HttpMethod.Post, apiKey, $"/v1/{slug}/connections", new
        {
            type = "saml",
            name = "Instance SSO",
            idp_entity_id = "https://inst.example/metadata",
            sso_redirect_url = "https://inst.example/sso",
            x509_certificate_pem = pem,
        }));
        var instanceLevelId = (await instanceLevel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using (var denied = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/{instanceLevelId}/disable", [], adminCookie)))
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await service.Store.GetConnectionAsync(instanceLevelId))!.Enabled).IsTrue();
    }
}
