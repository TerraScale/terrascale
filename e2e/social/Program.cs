using System.Net;
using System.Security.Claims;
using System.Reflection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Contracts;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Authentication.Features.Email;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;

namespace Journey.Social;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args is ["platform-sessions"])
        {
            await PlatformSessions.RunAsync();
            return;
        }

        var options = new AuthOptions
        {
            Listen = new IPEndPoint(IPAddress.Loopback, 8090),
            MetricsListen = new IPEndPoint(IPAddress.Loopback, 9092),
            PublicOrigin = new Uri("https://auth.example.invalid/"),
            MasterKey = RandomNumberGenerator.GetBytes(32),
        };
        foreach (var name in new[] { "microsoft", "gitlab", "linkedin", "slack", "discord", "facebook", "x", "oauth2" })
        {
            foreach (var verified in new[] { true, false })
            {
                using var provider = new Provider(name, verified);
                var configuration = name == "oauth2"
                    ? new OAuth2Configuration("https://example.invalid/authorize", "https://example.invalid/token", "https://example.invalid/userinfo",
                        "profile email", "account.id", "account.email", "account.verified")
                    : null;
                var record = SocialProviders.Seal(options, "project", name, null, "client", "secret", null, null, null,
                    name == "microsoft" ? "common" : null, configuration);
                var social = new SocialSignIn(provider, options, TimeProvider.System);
                var (location, cookie) = await social.StartAsync("project", record, null, CancellationToken.None);
                var query = QueryHelpers.ParseQuery(location.Query);
                Require(query["state"].ToString().Length > 0, "state");
                Require(query["code_challenge_method"] == "S256", "PKCE");
                var state = social.Resume("project", name, cookie, query["state"]);
                Require(state is not null, "browser state");
                provider.Nonce = state!.Nonce;
                var identity = await social.CompleteAsync("project", record, state, "authorization-code", CancellationToken.None) ?? throw new InvalidOperationException(name + " identity missing");
                Require(identity.Subject == "123" && identity.Email == "user@example.invalid", name + " identity");
                Require(identity.EmailVerified == (verified && name is not ("facebook" or "x")), name + " verified email");
                Require(social.Resume("other-project", name, cookie, query["state"]) is null, "tenant state");
                var changed = record with { ClientId = "other-client" };
                Require(await social.CompleteAsync("project", changed, state, "authorization-code", CancellationToken.None) is null, "configuration binding");
            }
        }

        var magic = typeof(SocialSignIn).Assembly.GetType("TerraScale.Authentication.Features.SignIn.MagicLinks", throwOnError: true)!;
        var create = magic.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!;
        var open = magic.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)!;
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(TimeProvider.System.GetUtcNow().ToUnixTimeSeconds());
        var issued = new IssuedPasscode("user@example.invalid", "123456", issuedAt.AddMinutes(10))
        {
            Tenant = "project",
            IssueId = "issuance",
        };
        var link = (Uri)create.Invoke(null, [options, issued, "browser-hash", null])!;
        var token = QueryHelpers.ParseQuery(link.Query)["token"].ToString();
        Require(open.Invoke(null, [options, "project", token, new FixedClock(issuedAt.AddMinutes(10).AddSeconds(-1))]) is not null, "magic link before expiry");
        Require(open.Invoke(null, [options, "project", token, new FixedClock(issuedAt.AddMinutes(10))]) is null, "magic link at ten-minute expiry");
        Require(open.Invoke(null, [options, "other-project", token, new FixedClock(issuedAt)]) is null, "magic link tenant binding");
        var pendingType = typeof(SocialSignIn).Assembly.GetType("TerraScale.Authentication.Features.SignIn.PendingSocialLink", throwOnError: true)!;
        var pendingLinks = typeof(SocialSignIn).Assembly.GetType("TerraScale.Authentication.Features.SignIn.PendingSocialLinks", throwOnError: true)!;
        var pendingSeal = pendingLinks.GetMethod("Seal", BindingFlags.Static | BindingFlags.NonPublic)!;
        var pendingOpen = pendingLinks.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)!;
        var pendingProvider = SocialProviders.Seal(options, "project", SocialProviders.Facebook, null, "client", "secret", null, null, null);
        var pending = pendingType.GetConstructors().Single().Invoke(["pending-id", "user@example.invalid",
            new LinkedProvider("facebook", "https://www.facebook.com", "client", "123"), typeof(SocialSignIn).GetMethod("ConfigurationHash", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [pendingProvider])!, issuedAt.AddMinutes(10).ToUnixTimeSeconds()]);
        var pendingToken = (string)pendingSeal.Invoke(null, [options, "project", pending])!;
        Require(pendingOpen.Invoke(null, [options, "project", pendingToken, new FixedClock(issuedAt.AddMinutes(10).AddSeconds(-1))]) is not null, "pending link before expiry");
        Require(pendingOpen.Invoke(null, [options, "project", pendingToken, new FixedClock(issuedAt.AddMinutes(10))]) is null, "pending link ten-minute expiry");
        Require(pendingOpen.Invoke(null, [options, "other-project", pendingToken, new FixedClock(issuedAt)]) is null, "pending link tenant binding");
        var pendingWrite = pendingLinks.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!;
        var atLimit = new DefaultHttpContext();
        Require(pendingWrite.Invoke(null, [atLimit, "project", new string('a', 16384)]) is true, "pending cookie at size limit");
        var overLimit = new DefaultHttpContext();
        Require(pendingWrite.Invoke(null, [overLimit, "project", new string('a', 16385)]) is false, "pending cookie over size limit");
        Require(overLimit.Response.Headers.SetCookie.Count == 0, "oversized pending cookie is not written");
        var emails = new AuthEmails(options);
        foreach (var (locale, body, warning) in new[]
        {
            (AuthEmails.English, "Enter this code to connect Facebook to your TerraScale account.", "If you didn't try to sign in with Facebook, ignore this email."),
            (AuthEmails.Portuguese, "Digite este código para conectar Facebook à sua conta em TerraScale.", "Se você não tentou entrar com Facebook, ignore este e-mail."),
        })
        {
            var message = await emails.RenderAsync(AuthEmailKind.SocialLinkCode, locale,
                new AuthEmailValues("user@example.invalid") { Code = "123456", Provider = "Facebook", ExpiresIn = TimeSpan.FromMinutes(10) });
            Require(message.Text.Contains(body, StringComparison.Ordinal), "social link localized body");
            Require(message.Text.Contains(warning, StringComparison.Ordinal), "social link localized warning");
        }

        foreach (var reserved in new[] { "admin", "administrator", "root", "support", "security", "help", "terrascale", "system", "owner", "staff" })
        {
            Require(Users.NormalizeUsername(reserved.ToUpperInvariant()) is null, "reserved username");
        }

        Console.WriteLine("Pending social links: expiry, tenant binding, cookie size cap and en/pt-BR provider copy passed; reserved usernames refused.");
        Console.WriteLine("Magic links: ten-minute expiry and tenant binding passed.");
        Console.WriteLine("8 providers: token/userinfo, verification, PKCE, state and configuration binding passed.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(label);
        }
    }

    private sealed class FixedClock(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
    }

    private sealed class Provider(string name, bool verified) : HttpMessageHandler, IHttpClientFactory
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string Nonce { get; set; } = string.Empty;

        public HttpClient CreateClient(string clientName) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var issuer = name switch
            {
                "microsoft" => "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
                "gitlab" => "https://gitlab.com",
                "linkedin" => "https://www.linkedin.com/oauth",
                "slack" => "https://slack.com",
                _ => "https://example.invalid",
            };
            var oidc = name is "microsoft" or "gitlab" or "linkedin" or "slack";
            JsonObject body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                body = new JsonObject
                {
                    ["issuer"] = name == "microsoft" ? "https://login.microsoftonline.com/{tenantid}/v2.0" : issuer,
                    ["authorization_endpoint"] = issuer + "/authorize",
                    ["token_endpoint"] = issuer + "/token",
                    ["jwks_uri"] = issuer + "/keys",
                };
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/keys", StringComparison.Ordinal))
            {
                var publicKey = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(this.key) { KeyId = "key" });
                body = new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = publicKey.Kty, ["crv"] = publicKey.Crv, ["x"] = publicKey.X, ["y"] = publicKey.Y, ["kid"] = "key" }) };
            }
            else if (request.Method == HttpMethod.Post)
            {
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                Require(form["code"] == "authorization-code", name + " code");
                Require(form["code_verifier"].ToString().Length > 0, name + " verifier");
                Require(name != "x" || request.Headers.Authorization?.Scheme == "Basic", "X client authentication");
                var claims = new List<Claim>
                {
                    new("sub", "123"),
                    new("email", "user@example.invalid"),
                    new("email_verified", name == "microsoft" || verified ? "true" : "false", ClaimValueTypes.Boolean),
                    new("xms_edov", verified ? "true" : "false", ClaimValueTypes.Boolean),
                    new("nonce", this.Nonce),
                    new("tid", "11111111-1111-1111-1111-111111111111"),
                };
                var now = TimeProvider.System.GetUtcNow().UtcDateTime;
                var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = issuer,
                    Audience = "client",
                    Subject = new ClaimsIdentity(claims),
                    IssuedAt = now,
                    Expires = now.AddMinutes(5),
                    SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(this.key) { KeyId = "key" }, SecurityAlgorithms.EcdsaSha256),
                });
                body = oidc ? new JsonObject { ["id_token"] = token } : new JsonObject { ["access_token"] = "access-token" };
            }
            else
            {
                Require(request.Headers.Authorization?.Parameter == "access-token", name + " userinfo token");
                var user = new JsonObject { ["id"] = "123", ["email"] = "user@example.invalid", ["verified"] = verified, ["confirmed_email"] = "user@example.invalid" };
                body = name switch
                {
                    "oauth2" => new JsonObject { ["account"] = user },
                    "x" => new JsonObject { ["data"] = user },
                    _ => user,
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.key.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
