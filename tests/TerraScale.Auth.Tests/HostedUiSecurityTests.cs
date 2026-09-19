namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;

/// <summary>
/// Hosted-UI XSS and CSRF posture: every attacker-influenced value that a page interpolates
/// must be escaped at interpolation, the session cookie must carry SameSite=Lax (which is the
/// CSRF defense for every session-gated POST), state-changing routes must not accept GET, and
/// the slug charset that all raw slug interpolation rests on must hold at creation.
/// </summary>
[NotInParallel]
public sealed class HostedUiSecurityTests
{
    private static async Task<AuthServiceFixture> ServiceAsync() => await AuthServiceFixture.GetAsync();

    private static async Task<(string InstanceId, string Slug)> InstanceAsync()
    {
        var service = await ServiceAsync();
        var (id, slug, _) = await service.CreateInstanceAsync("secure-" + Guid.NewGuid().ToString("N")[..8]);
        return (id, slug);
    }

    private static string Email() => $"s{Guid.NewGuid().ToString("N")[..10]}@example.com";

    private static HttpRequestMessage Post(string path, Dictionary<string, string> fields, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage Get(string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static async Task<(string Cookie, string SessionId)> SignUpAsync(AuthServiceFixture service, string slug, string email, string? userAgent = null)
    {
        var request = Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
            ["name"] = "Test User",
        });
        if (userAgent is not null)
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using var response = await service.Http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var header = response.Headers.GetValues("Set-Cookie").First();
        var cookie = header.Split(';')[0];
        var session = await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(cookie["tsauth_session=".Length..]))
            ?? throw new InvalidOperationException("session missing from store");
        return (cookie, session.Id);
    }

    // ---- Escaping -------------------------------------------------------------------------

    [Test]
    public async Task EscEncodesHtmlAndAttributeMetacharacters()
    {
        // Attribute-safe: double quote must be encoded too (every attribute interpolation in the
        // UI is double-quoted, so a bare " is the only way out of an attribute context).
        var escaped = Ui.Esc("""<script>"&'x</script>""");
        await Assert.That(escaped).DoesNotContain("<");
        await Assert.That(escaped).DoesNotContain("\"");
        await Assert.That(escaped).DoesNotContain("&\"");
        await Assert.That(escaped).Contains("&lt;script&gt;");
    }

    [Test]
    public async Task SignUpValidationErrorEchoesNameAndEmailEscaped()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var name = """<script>alert("x")</script>""";
        var email = $"a'b@example.com";

        // Short password re-renders the form with the submitted name and email.
        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "short",
            ["name"] = name,
        }));
        var html = await BodyAsync(response);
        await Assert.That(html.Contains(System.Net.WebUtility.HtmlEncode(name))).IsTrue();
        await Assert.That(html.Contains(name)).IsFalse();
        await Assert.That(html).Contains(System.Net.WebUtility.HtmlEncode(email));
    }

    [Test]
    public async Task SignInPageEscapesHostileNextParameter()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var hostile = "\"><script>alert(1)</script>";

        using var response = await service.Http.SendAsync(
            Get($"/ui/{slug}/sign-in?next={Uri.EscapeDataString(hostile)}"));
        var html = await BodyAsync(response);
        await Assert.That(html.Contains(System.Net.WebUtility.HtmlEncode(hostile))).IsTrue();
        await Assert.That(html.Contains(hostile)).IsFalse();
    }

    [Test]
    public async Task SessionsPageEscapesAttackerChosenUserAgent()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var hostile = """UA <script>alert(1)</script>""";
        var (cookie, _) = await SignUpAsync(service, slug, Email(), userAgent: hostile);

        using var response = await service.Http.SendAsync(Get($"/ui/{slug}/account/sessions", cookie));
        var html = await BodyAsync(response);
        await Assert.That(html.Contains(System.Net.WebUtility.HtmlEncode(hostile))).IsTrue();
        await Assert.That(html.Contains(hostile)).IsFalse();
    }

    [Test]
    public async Task InstanceCreationRejectsSlugsThatCouldBreakOutOfHtml()
    {
        // Every page interpolates {slug} raw (URLs, and the passkey/passcode pages splice it into
        // inline <script>), so the creation-time charset check is the single load-bearing guard.
        var service = await ServiceAsync();
        foreach (var slug in new[] { "\"><script>alert(1)</script>", "a b", "a/b", "a\\b", "a'b" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/instances")
            {
                Content = JsonContent.Create(new { slug, name = "x" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "operator-test-key");
            using var response = await service.Http.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    // ---- CSRF -----------------------------------------------------------------------------

    [Test]
    public async Task SessionCookieIsLaxHttpOnlyAndSecure()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = Email(),
            ["password"] = "password123",
        }));
        var header = response.Headers.GetValues("Set-Cookie").First().ToLowerInvariant();
        await Assert.That(header.StartsWith("tsauth_session=")).IsTrue();
        // Lax is the CSRF defense documented on SessionCookies; losing it would expose every
        // session-gated POST route to cross-site forms.
        await Assert.That(header).Contains("samesite=lax");
        await Assert.That(header).Contains("httponly");
        await Assert.That(header).Contains("secure");
    }

    [Test]
    public async Task CrossSiteFormPostWithoutCookieCannotRevokeSessions()
    {
        // SameSite=Lax means a cross-site form POST arrives without the session cookie; those
        // requests must not be able to revoke (or sign out) a real session.
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var (cookie, sessionId) = await SignUpAsync(service, slug, Email());

        using var revokeAll = await service.Http.SendAsync(Post($"/ui/{slug}/account/sessions/revoke-all", []));
        await Assert.That(revokeAll.StatusCode).IsEqualTo(HttpStatusCode.Found);
        using var signOut = await service.Http.SendAsync(Post($"/ui/{slug}/sign-out", []));
        await Assert.That(signOut.StatusCode).IsEqualTo(HttpStatusCode.Found);

        var session = await service.Store.GetSessionAsync(sessionId);
        await Assert.That(session?.Status).IsEqualTo("active");

        // And the real browser (cookie attached) still has its session.
        using var account = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie));
        await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task StateChangingUiRoutesRejectGet()
    {
        // One representative mutating route per module: none may accept GET (a GET that mutates
        // on the session cookie would be reachable from an <img>/link despite SameSite=Lax,
        // which does send the cookie on top-level GET navigations).
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        foreach (var path in new[]
        {
            $"/ui/{slug}/sign-out",                                // Users
            $"/ui/{slug}/account/password",                        // Users
            $"/ui/{slug}/account/sessions/revoke-all",             // Users
            $"/ui/{slug}/mfa/enroll/start",                        // MFA
            $"/ui/{slug}/passcode/request",                        // Passcode
            $"/ui/{slug}/passkeys/register",                       // Passkeys
            $"/ui/{slug}/passkeys/manage/pk_x/delete",             // Passkeys
            $"/ui/{slug}/admin/connections",                       // Admin
        })
        {
            using var response = await service.Http.SendAsync(Get(path));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        }
    }
}
