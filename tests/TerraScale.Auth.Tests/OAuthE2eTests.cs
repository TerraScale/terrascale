namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>End-to-end OAuth 2.1 / OIDC provider flows over the real service.</summary>
[NotInParallel]
public sealed class OAuthE2eTests
{
    private const string RedirectUri = "https://app.test/callback";

    private sealed record Arranged(
        AuthServiceFixture Service,
        string Slug,
        string InstanceId,
        string ApiKey,
        User User,
        Session Session,
        string SessionToken,
        string ClientId,
        string? ClientSecret,
        string ClientRecordId,
        string RedirectUri);

    private static async Task<Arranged> ArrangeAsync(bool publicClient = false)
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("oauth-" + Guid.NewGuid().ToString("N")[..10]);

        var email = $"u{Guid.NewGuid().ToString("N")[..10]}@example.com";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var user = await service.Store.CreateUserAsync(new User
        {
            Id = "usr_" + Guid.NewGuid().ToString("N")[..12],
            InstanceId = instanceId,
            Email = email,
            EmailKey = $"{instanceId}:{email}",
            EmailVerified = true,
            Name = "Test User",
            PasswordHash = PasswordHasher.Hash("correct horse battery staple"),
            CreatedAt = now,
            UpdatedAt = now,
        });

        var sessions = new SessionService(service.Store);
        var (session, token) = await sessions.CreateSessionAsync(instanceId, user.Id, "e2e", "127.0.0.1");

        using var create = new HttpRequestMessage(HttpMethod.Post, $"/v1/{slug}/oauth-clients")
        {
            Content = JsonContent.Create(new { name = "Test App", redirect_uris = new[] { RedirectUri }, @public = publicClient }),
        };
        create.Headers.Authorization = new("Bearer", apiKey);
        var created = await service.Http.SendAsync(create);
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return new Arranged(
            service, slug, instanceId, apiKey, user, session, token,
            body.GetProperty("client").GetProperty("clientId").GetString()!,
            body.GetProperty("client_secret").GetString(),
            body.GetProperty("client").GetProperty("id").GetString()!,
            RedirectUri);
    }

    // ---- helpers -----------------------------------------------------------------

    private static HttpRequestMessage AuthorizeRequest(Arranged arranged, string? state = null, string? challenge = null,
        string scope = "openid profile email", string? prompt = null, string? nonce = null, string? redirectUri = null,
        bool withSessionCookie = true)
    {
        var query = $"response_type=code&client_id={arranged.ClientId}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri ?? arranged.RedirectUri)}&scope={Uri.EscapeDataString(scope)}";
        if (state is not null) query += $"&state={state}";
        if (challenge is not null) query += $"&code_challenge={challenge}&code_challenge_method=S256";
        if (nonce is not null) query += $"&nonce={nonce}";
        if (prompt is not null) query += $"&prompt={prompt}";
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{arranged.Slug}/oauth/authorize?{query}");
        if (withSessionCookie)
            request.Headers.Add("Cookie", $"tsauth_session={arranged.SessionToken}");
        return request;
    }

    private static HttpRequestMessage AuthenticatedForm(Arranged arranged, string path, Dictionary<string, string> form, bool useBasic = true)
    {
        // client_id rides in the form so public clients (no secret) authenticate too.
        form["client_id"] = arranged.ClientId;
        var request = new HttpRequestMessage(HttpMethod.Post, $"/{arranged.Slug}/{path}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (useBasic && arranged.ClientSecret is not null)
            request.Headers.Authorization = new("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{arranged.ClientId}:{arranged.ClientSecret}")));
        return request;
    }

    private static HttpRequestMessage TokenRequest(Arranged arranged, Dictionary<string, string> form, bool useBasic = true) =>
        AuthenticatedForm(arranged, "oauth/token", form, useBasic);

    private static async Task<JsonElement> TokenResponseAsync(Arranged arranged, string code, string verifier, bool useBasic = true)
    {
        var response = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = arranged.RedirectUri,
            ["code_verifier"] = verifier,
        }, useBasic));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Runs authorize + token exchange with fresh PKCE and returns the token body.</summary>
    private static async Task<(JsonElement Tokens, string Verifier, string Code)> FullFlowAsync(
        Arranged arranged, string? nonce = null, string scope = "openid profile email", bool useBasic = true)
    {
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var challenge = Encodings.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        using var authorize = AuthorizeRequest(arranged, state: "st123", challenge: challenge, nonce: nonce, scope: scope);
        var authorization = await arranged.Service.Http.SendAsync(authorize);
        await Assert.That(authorization.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var location = authorization.Headers.Location!.ToString();
        await Assert.That(location.StartsWith(RedirectUri + "?code=")).IsTrue();
        await Assert.That(location).Contains("state=st123");
        var code = QueryValue(location, "code");

        var tokens = await TokenResponseAsync(arranged, code, verifier, useBasic);
        return (tokens, verifier, code);
    }

    private static string QueryValue(string url, string name)
    {
        var separator = url.IndexOf('?');
        if (separator < 0)
            throw new InvalidOperationException($"no query in '{url}'");
        foreach (var pair in url[(separator + 1)..].Split('&'))
            if (pair.Split('=', 2) is [var key, var value] && key == name)
                return Uri.UnescapeDataString(value);
        throw new InvalidOperationException($"'{name}' not in '{url}'");
    }

    private static JsonElement JwtPayload(string jwt) =>
        JsonSerializer.Deserialize<JsonElement>(Encodings.Base64UrlDecode(jwt.Split('.')[1]));

    private static string? Kid(string jwt)
    {
        var header = JsonSerializer.Deserialize<JsonElement>(Encodings.Base64UrlDecode(jwt.Split('.')[0]));
        return header.TryGetProperty("kid", out var kid) ? kid.GetString() : null;
    }

    private static async Task<ECDsa> JwksKeyAsync(AuthServiceFixture service, string slug, string kid)
    {
        var jwks = await service.Http.GetFromJsonAsync<JsonElement>($"/{slug}/.well-known/jwks.json");
        foreach (var key in jwks.GetProperty("keys").EnumerateArray())
            if (key.GetProperty("kid").GetString() == kid)
                return ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint
                    {
                        X = Encodings.Base64UrlDecode(key.GetProperty("x").GetString()!),
                        Y = Encodings.Base64UrlDecode(key.GetProperty("y").GetString()!),
                    },
                });
        throw new InvalidOperationException($"kid '{kid}' not published in jwks");
    }

    private static bool JwtSignatureValid(ECDsa key, string jwt)
    {
        var parts = jwt.Split('.');
        return key.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Encodings.Base64UrlDecode(parts[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    // ---- discovery + jwks ------------------------------------------------------------

    [Test]
    public async Task DiscoveryAndJwksExposeStandardShapes()
    {
        var arranged = await ArrangeAsync();
        var discovery = await arranged.Service.Http.GetFromJsonAsync<JsonElement>($"/{arranged.Slug}/.well-known/openid-configuration");
        await Assert.That(discovery.GetProperty("issuer").GetString()).IsEqualTo($"https://auth.test/{arranged.Slug}");
        await Assert.That(discovery.GetProperty("authorization_endpoint").GetString()).IsEqualTo($"https://auth.test/{arranged.Slug}/oauth/authorize");
        await Assert.That(discovery.GetProperty("token_endpoint").GetString()).IsEqualTo($"https://auth.test/{arranged.Slug}/oauth/token");
        await Assert.That(discovery.GetProperty("userinfo_endpoint").GetString()).IsEqualTo($"https://auth.test/{arranged.Slug}/oauth/userinfo");
        await Assert.That(discovery.GetProperty("jwks_uri").GetString()).IsEqualTo($"https://auth.test/{arranged.Slug}/.well-known/jwks.json");
        await Assert.That(discovery.GetProperty("end_session_endpoint").GetString()).IsEqualTo($"https://auth.test/{arranged.Slug}/oauth/logout");
        await Assert.That(string.Join(",", discovery.GetProperty("response_types_supported").EnumerateArray().Select(v => v.GetString()))).IsEqualTo("code");
        await Assert.That(string.Join(",", discovery.GetProperty("grant_types_supported").EnumerateArray().Select(v => v.GetString()))).IsEqualTo("authorization_code,refresh_token,client_credentials");
        await Assert.That(string.Join(",", discovery.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(v => v.GetString()))).IsEqualTo("S256");
        await Assert.That(string.Join(",", discovery.GetProperty("id_token_signing_alg_values_supported").EnumerateArray().Select(v => v.GetString()))).IsEqualTo("ES256");

        var jwks = await arranged.Service.Http.GetFromJsonAsync<JsonElement>($"/{arranged.Slug}/.well-known/jwks.json");
        var key = jwks.GetProperty("keys").EnumerateArray().First();
        await Assert.That(key.GetProperty("kty").GetString()).IsEqualTo("EC");
        await Assert.That(key.GetProperty("crv").GetString()).IsEqualTo("P-256");
        await Assert.That(key.GetProperty("alg").GetString()).IsEqualTo("ES256");
        await Assert.That(key.GetProperty("use").GetString()).IsEqualTo("sig");
        await Assert.That(key.GetProperty("kid").GetString()!.Length > 0).IsTrue();
        await Assert.That(key.GetProperty("x").GetString()!.Length).IsEqualTo(43);
        await Assert.That(key.GetProperty("y").GetString()!.Length).IsEqualTo(43);
    }

    // ---- authorization code + PKCE + JWT contents ------------------------------------

    [Test]
    public async Task AuthorizationCodeFlowWithPkceIssuesVerifiableTokens()
    {
        var arranged = await ArrangeAsync();
        var (tokens, _, _) = await FullFlowAsync(arranged, nonce: "n-42");

        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var idToken = tokens.GetProperty("id_token").GetString()!;
        await Assert.That(tokens.GetProperty("token_type").GetString()).IsEqualTo("Bearer");
        await Assert.That(tokens.GetProperty("expires_in").GetInt32()).IsEqualTo(3600);
        await Assert.That(tokens.GetProperty("scope").GetString()).IsEqualTo("openid profile email");
        await Assert.That(tokens.GetProperty("refresh_token").GetString()!.Length > 0).IsTrue();

        var access = JwtPayload(accessToken);
        await Assert.That(access.GetProperty("iss").GetString()).IsEqualTo($"https://auth.test/{arranged.Slug}");
        await Assert.That(access.GetProperty("sub").GetString()).IsEqualTo(arranged.User.Id);
        await Assert.That(access.GetProperty("aud").GetString()).IsEqualTo(arranged.ClientId);
        await Assert.That(access.GetProperty("scope").GetString()).IsEqualTo("openid profile email");
        await Assert.That(access.GetProperty("sid").GetString()).IsEqualTo(arranged.Session.Id);

        var id = JwtPayload(idToken);
        await Assert.That(id.GetProperty("iss").GetString()).IsEqualTo($"https://auth.test/{arranged.Slug}");
        await Assert.That(id.GetProperty("sub").GetString()).IsEqualTo(arranged.User.Id);
        await Assert.That(id.GetProperty("aud").GetString()).IsEqualTo(arranged.ClientId);
        await Assert.That(id.GetProperty("nonce").GetString()).IsEqualTo("n-42");
        await Assert.That(id.GetProperty("auth_time").GetInt64()).IsEqualTo(arranged.Session.CreatedAt);
        await Assert.That(id.GetProperty("email").GetString()).IsEqualTo(arranged.User.Email);
        await Assert.That(id.GetProperty("email_verified").GetBoolean()).IsTrue();
        var expectedAtHash = Encodings.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]);
        await Assert.That(id.GetProperty("at_hash").GetString()).IsEqualTo(expectedAtHash);

        // The access token signature must verify against the published JWKS key.
        var key = await JwksKeyAsync(arranged.Service, arranged.Slug, Kid(accessToken)!);
        await Assert.That(JwtSignatureValid(key, accessToken)).IsTrue();
        await Assert.That(JwtSignatureValid(key, idToken)).IsTrue();

        // userinfo returns claims per granted scopes.
        using var userinfo = new HttpRequestMessage(HttpMethod.Get, $"/{arranged.Slug}/oauth/userinfo");
        userinfo.Headers.Authorization = new("Bearer", accessToken);
        var userinfoResponse = await arranged.Service.Http.SendAsync(userinfo);
        await Assert.That(userinfoResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var claims = await userinfoResponse.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(claims.GetProperty("sub").GetString()).IsEqualTo(arranged.User.Id);
        await Assert.That(claims.GetProperty("email").GetString()).IsEqualTo(arranged.User.Email);
        await Assert.That(claims.GetProperty("name").GetString()).IsEqualTo("Test User");
    }

    [Test]
    public async Task UserinfoHonorsGrantedScopesAndRejectsBadTokens()
    {
        var arranged = await ArrangeAsync();
        var (tokens, _, _) = await FullFlowAsync(arranged, scope: "openid"); // no email/profile

        using var userinfo = new HttpRequestMessage(HttpMethod.Get, $"/{arranged.Slug}/oauth/userinfo");
        userinfo.Headers.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());
        var response = await arranged.Service.Http.SendAsync(userinfo);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var claims = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(claims.TryGetProperty("email", out _)).IsFalse();
        await Assert.That(claims.TryGetProperty("name", out _)).IsFalse();

        using var noHeader = new HttpRequestMessage(HttpMethod.Get, $"/{arranged.Slug}/oauth/userinfo");
        await Assert.That((await arranged.Service.Http.SendAsync(noHeader)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var garbage = new HttpRequestMessage(HttpMethod.Get, $"/{arranged.Slug}/oauth/userinfo");
        garbage.Headers.Authorization = new("Bearer", "not.a.jwt");
        await Assert.That((await arranged.Service.Http.SendAsync(garbage)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    // ---- authorize rejections ----------------------------------------------------------

    [Test]
    public async Task AuthorizeRejectsBadRequestsByRedirectingErrors()
    {
        var arranged = await ArrangeAsync();

        using var noSession = new HttpRequestMessage(HttpMethod.Get,
            $"/{arranged.Slug}/oauth/authorize?response_type=code&client_id={arranged.ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}");
        var signIn = await arranged.Service.Http.SendAsync(noSession);
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        var signInLocation = signIn.Headers.Location!.ToString();
        await Assert.That(signInLocation.StartsWith($"/ui/{arranged.Slug}/sign-in?next=")).IsTrue();
        await Assert.That(signInLocation).Contains(Uri.EscapeDataString($"/{arranged.Slug}/oauth/authorize"));

        using var promptNone = AuthorizeRequest(arranged, prompt: "none", withSessionCookie: false);
        var loginRequired = await arranged.Service.Http.SendAsync(promptNone);
        await Assert.That(loginRequired.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(loginRequired.Headers.Location!.ToString()).Contains("error=login_required");

        using var badResponseType = AuthorizeRequest(arranged);
        badResponseType.RequestUri = new Uri(badResponseType.RequestUri!.ToString().Replace("response_type=code", "response_type=token"), UriKind.Relative);
        var unsupported = await arranged.Service.Http.SendAsync(badResponseType);
        await Assert.That(unsupported.Headers.Location!.ToString()).Contains("error=unsupported_response_type");

        using var badScope = AuthorizeRequest(arranged, scope: "openid bogus");
        var invalidScope = await arranged.Service.Http.SendAsync(badScope);
        await Assert.That(invalidScope.Headers.Location!.ToString()).Contains("error=invalid_scope");

        using var badRedirect = AuthorizeRequest(arranged, redirectUri: "https://evil.test/callback");
        var mismatch = await arranged.Service.Http.SendAsync(badRedirect);
        await Assert.That(mismatch.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var publicArranged = await ArrangeAsync(publicClient: true);
        using var noPkce = AuthorizeRequest(publicArranged);
        var pkceRequired = await publicArranged.Service.Http.SendAsync(noPkce);
        await Assert.That(pkceRequired.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(pkceRequired.Headers.Location!.ToString()).Contains("error=invalid_request");

        using var plain = AuthorizeRequest(arranged);
        plain.RequestUri = new Uri(plain.RequestUri!.ToString() + "&code_challenge=abc&code_challenge_method=plain", UriKind.Relative);
        var plainRejected = await arranged.Service.Http.SendAsync(plain);
        await Assert.That(plainRejected.Headers.Location!.ToString()).Contains("error=invalid_request");
    }

    // ---- token grant rejections ----------------------------------------------------------

    [Test]
    public async Task TokenEndpointRejectsVerifierReplayAndMismatch()
    {
        var arranged = await ArrangeAsync();

        // A completed exchange consumes its code...
        var (_, verifier, code) = await FullFlowAsync(arranged);

        var fresh = await AuthorizeWithPkceAsync(arranged);

        var wrongVerifier = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = fresh.Code,
            ["redirect_uri"] = arranged.RedirectUri,
            ["code_verifier"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
        }));
        await Assert.That(wrongVerifier.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await wrongVerifier.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("invalid_grant");

        var redirectMismatch = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = fresh.Code,
            ["redirect_uri"] = "https://app.test/other",
            ["code_verifier"] = fresh.Verifier,
        }));
        await Assert.That(redirectMismatch.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await redirectMismatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("invalid_grant");

        // ...so replaying the first code with its (correct) verifier fails too.
        var replay = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = arranged.RedirectUri,
            ["code_verifier"] = verifier,
        }));
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("invalid_grant");

        var unknownGrant = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["code"] = "x",
        }));
        await Assert.That(unknownGrant.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await unknownGrant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("unsupported_grant_type");

        var badSecret = new HttpRequestMessage(HttpMethod.Post, $"/{arranged.Slug}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = arranged.ClientId,
                ["client_secret"] = "wrong-secret",
            }),
        };
        await Assert.That((await arranged.Service.Http.SendAsync(badSecret)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private sealed record PkceCode(string Code, string Verifier);

    private static async Task<PkceCode> AuthorizeWithPkceAsync(Arranged arranged)
    {
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var challenge = Encodings.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var authorize = AuthorizeRequest(arranged, challenge: challenge);
        var response = await arranged.Service.Http.SendAsync(authorize);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        return new PkceCode(QueryValue(response.Headers.Location!.ToString(), "code"), verifier);
    }

    // ---- refresh rotation + reuse detection ----------------------------------------------

    [Test]
    public async Task RefreshTokensRotateAndReuseRevokesTheFamily()
    {
        var arranged = await ArrangeAsync();
        var (tokens, _, _) = await FullFlowAsync(arranged);

        var first = tokens.GetProperty("refresh_token").GetString()!;
        var refreshed = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = first,
        }));
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var secondBody = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        var second = secondBody.GetProperty("refresh_token").GetString()!;
        await Assert.That(second == first).IsFalse();
        await Assert.That(secondBody.GetProperty("access_token").GetString()!.Length > 0).IsTrue();
        await Assert.That(secondBody.TryGetProperty("id_token", out _)).IsFalse();

        // Replaying the rotated token trips reuse detection and kills the family.
        var reuse = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = first,
        }));
        await Assert.That(reuse.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await reuse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("invalid_grant");

        var deadSecond = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = second,
        }));
        await Assert.That(deadSecond.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var stored = await arranged.Service.Store.FindRefreshTokenAsync(SecureTokens.Sha256Hex(first));
        await Assert.That(stored!.Status).IsEqualTo("rotated");
        var storedSecond = await arranged.Service.Store.FindRefreshTokenAsync(SecureTokens.Sha256Hex(second));
        await Assert.That(storedSecond!.Status).IsEqualTo("revoked");

        var audit = await arranged.Service.Store.ListAuditEventsAsync(arranged.InstanceId, 100);
        await Assert.That(audit.Any(e => e.Type == "oauth.token_refreshed")).IsTrue();
        await Assert.That(audit.Any(e => e.Type == "oauth.code_issued")).IsTrue();
    }

    // ---- public clients + client_credentials ------------------------------------------------

    [Test]
    public async Task PublicClientFlowUsesPkceWithoutSecret()
    {
        var arranged = await ArrangeAsync(publicClient: true);
        await Assert.That(arranged.ClientSecret).IsNull();

        // PKCE + client_id in the form, no secret anywhere.
        var (tokens, _, _) = await FullFlowAsync(arranged, useBasic: false);
        await Assert.That(tokens.GetProperty("access_token").GetString()!.Length > 0).IsTrue();

        // client_credentials is for confidential clients only.
        var clientCredentials = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = arranged.ClientId,
        }, useBasic: false));
        await Assert.That(clientCredentials.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await clientCredentials.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()).IsEqualTo("unauthorized_client");
    }

    [Test]
    public async Task ClientCredentialsIssuesMachineTokens()
    {
        var arranged = await ArrangeAsync();
        var response = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "openid",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.TryGetProperty("refresh_token", out _)).IsFalse();
        var claims = JwtPayload(body.GetProperty("access_token").GetString()!);
        await Assert.That(claims.GetProperty("sub").GetString()).IsEqualTo(arranged.ClientId);
        await Assert.That(claims.GetProperty("aud").GetString()).IsEqualTo(arranged.ClientId);

        // client_secret_post works too.
        var posted = await arranged.Service.Http.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/{arranged.Slug}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = arranged.ClientId,
                ["client_secret"] = arranged.ClientSecret!,
            }),
        });
        await Assert.That(posted.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // ---- introspection + revocation -----------------------------------------------------------

    [Test]
    public async Task IntrospectionAndRevocationFollowTheRfcs()
    {
        var arranged = await ArrangeAsync();
        var (tokens, _, _) = await FullFlowAsync(arranged);
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var activeAccess = await IntrospectAsync(arranged, accessToken);
        await Assert.That(activeAccess.GetProperty("active").GetBoolean()).IsTrue();
        await Assert.That(activeAccess.GetProperty("sub").GetString()).IsEqualTo(arranged.User.Id);
        await Assert.That(activeAccess.GetProperty("client_id").GetString()).IsEqualTo(arranged.ClientId);

        var activeRefresh = await IntrospectAsync(arranged, refreshToken);
        await Assert.That(activeRefresh.GetProperty("active").GetBoolean()).IsTrue();
        await Assert.That(activeRefresh.GetProperty("token_type").GetString()).IsEqualTo("refresh_token");

        // RFC 7009: always 200, even for junk; revoking a stateless access token is a no-op.
        await Assert.That((await RevokeAsync(arranged, "garbage")).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await RevokeAsync(arranged, accessToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);

        await Assert.That((await RevokeAsync(arranged, refreshToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await IntrospectAsync(arranged, refreshToken)).GetProperty("active").GetBoolean()).IsFalse();
        // ...and the revoked refresh token no longer rotates.
        var refresh = await arranged.Service.Http.SendAsync(TokenRequest(arranged, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }));
        await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        await Assert.That((await IntrospectAsync(arranged, "not-a-token")).GetProperty("active").GetBoolean()).IsFalse();

        using var anonymous = new HttpRequestMessage(HttpMethod.Post, $"/{arranged.Slug}/oauth/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = accessToken }),
        };
        await Assert.That((await arranged.Service.Http.SendAsync(anonymous)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private static async Task<JsonElement> IntrospectAsync(Arranged arranged, string token)
    {
        var response = await arranged.Service.Http.SendAsync(AuthenticatedForm(arranged, "oauth/introspect", new Dictionary<string, string>
        {
            ["token"] = token,
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> RevokeAsync(Arranged arranged, string token) =>
        arranged.Service.Http.SendAsync(AuthenticatedForm(arranged, "oauth/revoke", new Dictionary<string, string> { ["token"] = token }));

    // ---- RP-initiated logout ------------------------------------------------------------------

    [Test]
    public async Task LogoutKillsSessionAndHonorsPostLogoutRedirect()
    {
        var arranged = await ArrangeAsync();

        using var redirect = new HttpRequestMessage(HttpMethod.Get,
            $"/{arranged.Slug}/oauth/logout?client_id={arranged.ClientId}&post_logout_redirect_uri={Uri.EscapeDataString("https://app.test/bye")}&state=so-long");
        redirect.Headers.Add("Cookie", $"tsauth_session={arranged.SessionToken}");
        var redirected = await arranged.Service.Http.SendAsync(redirect);
        await Assert.That(redirected.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(redirected.Headers.Location!.ToString()).IsEqualTo("https://app.test/bye?state=so-long");

        // The session is revoked server-side: the same cookie can no longer authorize.
        using var after = AuthorizeRequest(arranged, prompt: "none");
        var loginRequired = await arranged.Service.Http.SendAsync(after);
        await Assert.That(loginRequired.Headers.Location!.ToString()).Contains("error=login_required");

        // Unregistered post-logout origins are refused; the signed-out page shows instead.
        using var evil = new HttpRequestMessage(HttpMethod.Get,
            $"/{arranged.Slug}/oauth/logout?client_id={arranged.ClientId}&post_logout_redirect_uri={Uri.EscapeDataString("https://evil.test/bye")}");
        var page = await arranged.Service.Http.SendAsync(evil);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("Signed out");
    }

    // ---- client management ---------------------------------------------------------------------

    [Test]
    public async Task OAuthClientManagementLifecycle()
    {
        var arranged = await ArrangeAsync();

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, $"/v1/{arranged.Slug}/oauth-clients");
        await Assert.That((await arranged.Service.Http.SendAsync(anonymous)).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var list = new HttpRequestMessage(HttpMethod.Get, $"/v1/{arranged.Slug}/oauth-clients");
        list.Headers.Authorization = new("Bearer", arranged.ApiKey);
        var listResponse = await arranged.Service.Http.SendAsync(list);
        await Assert.That(listResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var listed = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var clients = listed.GetProperty("clients");
        await Assert.That(clients.GetArrayLength()).IsEqualTo(1);
        await Assert.That(clients[0].GetProperty("clientId").GetString()).IsEqualTo(arranged.ClientId);
        await Assert.That(clients[0].TryGetProperty("clientSecretHash", out _)).IsFalse();

        // A client from another instance's key is invisible.
        var (otherInstanceId, otherSlug, otherKey) = await arranged.Service.CreateInstanceAsync("oauth-" + Guid.NewGuid().ToString("N")[..10]);
        _ = otherInstanceId;
        using var crossList = new HttpRequestMessage(HttpMethod.Get, $"/v1/{otherSlug}/oauth-clients");
        crossList.Headers.Authorization = new("Bearer", arranged.ApiKey);
        await Assert.That((await arranged.Service.Http.SendAsync(crossList)).StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/v1/{arranged.Slug}/oauth-clients/{arranged.ClientRecordId}");
        delete.Headers.Authorization = new("Bearer", arranged.ApiKey);
        await Assert.That((await arranged.Service.Http.SendAsync(delete)).StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var deleteAgain = new HttpRequestMessage(HttpMethod.Delete, $"/v1/{arranged.Slug}/oauth-clients/{arranged.ClientRecordId}");
        deleteAgain.Headers.Authorization = new("Bearer", arranged.ApiKey);
        await Assert.That((await arranged.Service.Http.SendAsync(deleteAgain)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The deleted client can no longer start an authorization.
        using var authorize = AuthorizeRequest(arranged);
        await Assert.That((await arranged.Service.Http.SendAsync(authorize)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
