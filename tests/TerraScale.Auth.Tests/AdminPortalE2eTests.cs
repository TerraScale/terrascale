namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TerraScale.Auth.Crypto;

/// <summary>Hosted admin portal: org-admin authorization on every route, connection
/// create validation, HTML escaping of echoed names, and enable/disable effects.</summary>
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
        var (_, memberId) = await SignUpAsync(service, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, memberId, "member");
        var (nonMemberCookie, _) = await SignUpAsync(service, slug, instanceId);
        var (bannedCookie, bannedId) = await SignUpAsync(service, slug, instanceId);
        using var ban = await service.Http.SendAsync(Api(HttpMethod.Patch, apiKey, $"/v1/{slug}/users/{bannedId}", new { banned = true }));
        await Assert.That(ban.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var (_, pem) = NewIdpCertificate();
        var form = ConnectionForm(pem);

        // Anonymous: redirected to sign-in on all three routes.
        using (var page = await service.Http.SendAsync(Get($"/ui/{slug}/admin")))
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using (var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", form)))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using (var toggled = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/con_x/disable", form)))
            await Assert.That(toggled.StatusCode).IsEqualTo(HttpStatusCode.Redirect);

        // Plain member of the org: 403 on all three routes.
        var memberCookie = await SignInCookieAsync(service, slug, memberId);
        using (var page = await service.Http.SendAsync(Get($"/ui/{slug}/admin", memberCookie)))
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var before = await ConnectionCountAsync(service, instanceId);
        using (var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", form, memberCookie)))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ConnectionCountAsync(service, instanceId)).IsEqualTo(before);
        using (var toggled = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/con_x/disable", form, memberCookie)))
            await Assert.That(toggled.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // Signed-in non-member: 403 on all three routes.
        using (var page = await service.Http.SendAsync(Get($"/ui/{slug}/admin", nonMemberCookie)))
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using (var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", form, nonMemberCookie)))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await ConnectionCountAsync(service, instanceId)).IsEqualTo(before);
        }

        // Banned user: session no longer resolves, so redirect.
        using (var page = await service.Http.SendAsync(Get($"/ui/{slug}/admin", bannedCookie)))
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        using (var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", form, bannedCookie)))
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
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

        var (adminCookie, adminId) = await SignUpAsync(service, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, adminId, "admin");

        var (_, pem) = NewIdpCertificate();
        using var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", ConnectionForm(pem), adminCookie));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);

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

        using var page = await service.Http.SendAsync(Get($"/ui/{slug}/admin", adminCookie));
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html.Contains("Corp IdP")).IsTrue();
        await Assert.That(html.Contains("Globex SSO")).IsFalse();
        await Assert.That(html.Contains("Instance SSO")).IsFalse();
    }

    [Test]
    public async Task ConnectionCreateValidatesPemAndUrlAndNeverStoresInvalidInput()
    {
        var service = await ServiceAsync();
        var (instanceId, slug, apiKey) = await InstanceAsync();
        var orgId = await CreateOrganizationAsync(service, slug, apiKey, "Acme");
        var (adminCookie, adminId) = await SignUpAsync(service, slug, instanceId);
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

        foreach (var badPem in badPems)
        {
            using var response = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", ConnectionForm(badPem), adminCookie));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
        foreach (var badUrl in badUrls)
        {
            using var response = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", ConnectionForm(pem, badUrl), adminCookie));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
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
        var (adminCookie, adminId) = await SignUpAsync(service, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, adminId, "admin");

        var (_, pem) = NewIdpCertificate();
        using var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections",
            ConnectionForm(pem, name: "\"><img src=x onerror=alert(2)><script>alert(1)</script>"), adminCookie));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);

        using var page = await service.Http.SendAsync(Get($"/ui/{slug}/admin", adminCookie));
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html.Contains("<script>alert(1)</script>")).IsFalse();
        await Assert.That(html.Contains("<img src=x")).IsFalse();
        await Assert.That(html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;")).IsTrue();
        await Assert.That(html.Contains("&quot;&gt;&lt;img src=x onerror=alert(2)&gt;")).IsTrue();

        // The public sign-in connection list escapes the same name.
        using var list = await service.Http.SendAsync(Get($"/ui/{slug}/connections/list"));
        var listHtml = await list.Content.ReadAsStringAsync();
        await Assert.That(listHtml.Contains("<script>alert(1)</script>")).IsFalse();
        await Assert.That(listHtml.Contains("&lt;script&gt;alert(1)&lt;/script&gt;")).IsTrue();

        // Validation errors render as a page without echoing raw input.
        using var bad = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections",
            ConnectionForm("not a pem", name: "<script>alert(9)</script>"), adminCookie));
        var badHtml = await bad.Content.ReadAsStringAsync();
        await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(badHtml.Contains("<script>alert(9)</script>")).IsFalse();
    }

    [Test]
    public async Task EnableDisableIsOrgAdminGatedAndDisabledConnectionsLeaveSignIn()
    {
        var service = await ServiceAsync();
        var (instanceId, slug, apiKey) = await InstanceAsync();
        var orgId = await CreateOrganizationAsync(service, slug, apiKey, "Acme");
        var otherOrgId = await CreateOrganizationAsync(service, slug, apiKey, "Globex");

        var (adminCookie, adminId) = await SignUpAsync(service, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, adminId, "admin");
        var (_, memberId) = await SignUpAsync(service, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, orgId, memberId, "member");
        var (otherAdminCookie, otherAdminId) = await SignUpAsync(service, slug, instanceId);
        await AddMembershipAsync(service, slug, apiKey, otherOrgId, otherAdminId, "admin");

        var (_, pem) = NewIdpCertificate();
        using var created = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections", ConnectionForm(pem), adminCookie));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var connection = (await service.Store.ListConnectionsAsync(instanceId)).Single(c => c.OrgId == orgId);

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

        // Org admin disables: effective immediately at sign-in.
        using (var disabled = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/{connection.Id}/disable", [], adminCookie)))
            await Assert.That(disabled.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That((await service.Store.GetConnectionAsync(connection.Id))!.Enabled).IsFalse();
        using (var offered = await service.Http.SendAsync(Get($"/ui/{slug}/connections")))
        {
            var body = await offered.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.EnumerateArray().Any(c => c.GetProperty("id").GetString() == connection.Id)).IsFalse();
        }
        using (var listPage = await service.Http.SendAsync(Get($"/ui/{slug}/connections/list")))
            await Assert.That((await listPage.Content.ReadAsStringAsync()).Contains("Corp IdP")).IsFalse();

        // And the org admin can bring it back.
        using (var enabled = await service.Http.SendAsync(Post($"/ui/{slug}/admin/connections/{connection.Id}/enable", [], adminCookie)))
            await Assert.That(enabled.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
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
