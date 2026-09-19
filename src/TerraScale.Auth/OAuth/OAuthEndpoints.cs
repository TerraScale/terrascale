namespace TerraScale.Auth.OAuth;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;

/// <summary>
/// The complete OAuth 2.1 / OIDC provider: discovery, JWKS, authorization-code
/// (+ PKCE) issuance, the token endpoint (authorization_code, refresh_token
/// with rotation + reuse detection, client_credentials), userinfo, revocation,
/// introspection, RP-initiated logout, and OAuth client management.
/// </summary>
public static class OAuthEndpoints
{
    private static readonly string[] SupportedScopes = ["openid", "profile", "email"];
    private const int AccessTokenLifetimeSeconds = 3600;
    private const int AuthorizationCodeLifetimeSeconds = 60;
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maps OAuth endpoints.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/{slug}/.well-known/openid-configuration", async (string slug, AuthStore store, AuthOptions options, CancellationToken ct) =>
        {
            if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
                return ApiResults.NotFound();
            var issuer = Issuer(options, instance.Slug);
            return Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = $"{issuer}/oauth/authorize",
                ["token_endpoint"] = $"{issuer}/oauth/token",
                ["userinfo_endpoint"] = $"{issuer}/oauth/userinfo",
                ["jwks_uri"] = $"{issuer}/.well-known/jwks.json",
                ["revocation_endpoint"] = $"{issuer}/oauth/revoke",
                ["introspection_endpoint"] = $"{issuer}/oauth/introspect",
                ["end_session_endpoint"] = $"{issuer}/oauth/logout",
                ["response_types_supported"] = new[] { "code" },
                ["grant_types_supported"] = new[] { "authorization_code", "refresh_token", "client_credentials" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "ES256" },
                ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "none" },
                ["scopes_supported"] = SupportedScopes,
            });
        });

        app.MapGet("/{slug}/.well-known/jwks.json", async (string slug, AuthStore store, KeyRing keys, CancellationToken ct) =>
        {
            if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
                return ApiResults.NotFound();
            var active = await keys.GetActiveAsync(instance.Id, ct);
            return Results.Json(new { keys = active.Select(key => key.ToPublicJwk()).ToList() });
        });

        app.MapGet("/{slug}/oauth/authorize", async (string slug, HttpRequest request, AuthStore store, AuthOptions options,
            Users.SessionService sessions, CancellationToken ct) =>
        {
            if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
                return ApiResults.NotFound();

            var query = request.Query;
            var clientId = query["client_id"].ToString();
            var redirectUri = query["redirect_uri"].ToString();
            var state = query["state"].ToString();

            // RFC 6749 4.1.2.1: only redirect once client_id and redirect_uri are validated.
            if (clientId.Length == 0)
                return ApiResults.BadRequest("invalid_request", "client_id is required");
            if (await store.FindOAuthClientAsync(instance.Id, clientId, ct) is not { } client)
                return ApiResults.BadRequest("invalid_request", "unknown client_id");
            if (redirectUri.Length == 0 || !client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
                return ApiResults.BadRequest("invalid_request", "redirect_uri does not exactly match a registered redirect uri");

            if (query["response_type"].ToString() != "code")
                return RedirectError(redirectUri, state, "unsupported_response_type", "only response_type=code is supported");

            var scopeParameter = query["scope"].ToString();
            var scopes = scopeParameter.Length == 0
                ? ["openid"]
                : scopeParameter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (scopes.Any(scope => !SupportedScopes.Contains(scope)))
                return RedirectError(redirectUri, state, "invalid_scope", "supported scopes: openid profile email");

            var challenge = query["code_challenge"].ToString();
            var challengeMethod = query["code_challenge_method"].ToString();
            if (challenge.Length == 0 && challengeMethod.Length > 0)
                return RedirectError(redirectUri, state, "invalid_request", "code_challenge is required when code_challenge_method is present");
            if (challenge.Length > 0 && challengeMethod != "S256")
                return RedirectError(redirectUri, state, "invalid_request", "only code_challenge_method=S256 is supported");
            if (client.Public && challenge.Length == 0)
                return RedirectError(redirectUri, state, "invalid_request", "PKCE with S256 is required for public clients");

            var cookie = request.Cookies[SessionCookies.Name];
            var resolved = cookie is null ? null : await sessions.ResolveAsync(instance.Id, cookie, ct);
            if (resolved is null)
            {
                if (query["prompt"].ToString() == "none")
                    return RedirectError(redirectUri, state, "login_required", "no active session");
                var authorizeUrl = Issuer(options, instance.Slug) + "/oauth/authorize" + request.QueryString.Value;
                return Results.Redirect($"/ui/{instance.Slug}/sign-in?next={Uri.EscapeDataString(authorizeUrl)}");
            }

            // DECISION: no consent page in v1 — first-party clients get the code
            // immediately once the user has a session. Third-party consent UI is a
            // later feature; the authorize POST path it would need is not mapped yet.
            var (session, user) = resolved.Value;
            var nonce = query["nonce"].ToString();
            var code = SecureTokens.RandomToken(48);
            await store.CreateAuthorizationCodeAsync(new AuthorizationCode
            {
                // The session id rides in the record id so the token exchange can
                // issue sid / auth_time claims (the shared model has no session field).
                Id = "oac:" + session.Id + ":" + SecureTokens.RandomToken(12),
                InstanceId = instance.Id,
                ClientId = client.ClientId,
                UserId = user.Id,
                CodeKey = SecureTokens.Sha256Hex(code),
                RedirectUri = redirectUri,
                Scopes = scopes,
                CodeChallenge = challenge.Length > 0 ? challenge : null,
                CodeChallengeMethod = challenge.Length > 0 ? "S256" : null,
                Nonce = nonce.Length > 0 ? nonce : null,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(AuthorizationCodeLifetimeSeconds).ToUnixTimeSeconds(),
                Consumed = false,
            }, ct);
            await AuditAsync(store, instance.Id, "oauth.code_issued", user.Id, new Dictionary<string, object>
            {
                ["client_id"] = client.ClientId,
                ["scope"] = string.Join(' ', scopes),
            }, ct);

            var callback = AppendQuery(redirectUri, "code=" + code);
            return Results.Redirect(state.Length > 0 ? AppendQuery(callback, "state=" + Uri.EscapeDataString(state)) : callback);
        });

        app.MapPost("/{slug}/oauth/token", async (string slug, HttpRequest request, AuthStore store, KeyRing keys,
            AuthOptions options, CancellationToken ct) =>
        {
            if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
                return ApiResults.NotFound();
            var form = await request.ReadFormAsync(ct);

            var authentication = await AuthenticateClientAsync(request, form, store, instance.Id, ct);
            if (authentication.Client is null)
                return TokenError("invalid_client", authentication.Description ?? "client authentication failed", authentication.Status);

            var issuer = Issuer(options, instance.Slug);
            var requestedGrant = form["grant_type"].ToString();
            if (requestedGrant.Length == 0)
                return TokenError("invalid_request", "grant_type is required");
            return requestedGrant switch
            {
                "authorization_code" => await AuthorizationCodeGrantAsync(store, keys, issuer, instance, authentication.Client, form, ct),
                "refresh_token" => await RefreshTokenGrantAsync(store, keys, issuer, instance, authentication.Client, form, ct),
                "client_credentials" => await ClientCredentialsGrantAsync(keys, issuer, instance, authentication.Client, form, ct),
                var grantType => TokenError("unsupported_grant_type", $"grant_type '{grantType}' is not supported"),
            };
        });

        app.MapGet("/{slug}/oauth/userinfo", async (string slug, HttpRequest request, AuthStore store, KeyRing keys,
            AuthOptions options, CancellationToken ct) =>
        {
            if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
                return ApiResults.NotFound();

            if (BearerToken(request) is not { } token)
                return UnauthorizedBearer("missing bearer token");

            var claims = await VerifyAccessTokenAsync(keys, store, options, instance, token, ct);
            if (claims is null || ClaimString(claims, "sub") is not { } sub || await store.GetUserAsync(sub, ct) is not { } user)
                return UnauthorizedBearer("invalid access token");

            var granted = (ClaimString(claims, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var body = new Dictionary<string, object> { ["sub"] = user.Id };
            if (granted.Contains("email") && user.Email is not null)
            {
                body["email"] = user.Email;
                body["email_verified"] = user.EmailVerified;
            }
            if (granted.Contains("profile") && user.Name is not null)
                body["name"] = user.Name;
            return Results.Json(body);
        });

        app.MapPost("/{slug}/oauth/revoke", async (string slug, HttpRequest request, AuthStore store, CancellationToken ct) =>
        {
            if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
                return ApiResults.NotFound();
            var form = await request.ReadFormAsync(ct);
            var authentication = await AuthenticateClientAsync(request, form, store, instance.Id, ct);
            if (authentication.Client is null)
                return TokenError("invalid_client", authentication.Description ?? "client authentication failed", 401);

            // RFC 7009: unknown or already-invalid tokens still return 200; access
            // tokens are stateless JWTs, so only refresh tokens need store work.
            var tokenValue = form["token"].ToString();
            if (tokenValue.Length == 0)
                return TokenError("invalid_request", "token is required");
            if (await store.FindRefreshTokenAsync(SecureTokens.Sha256Hex(tokenValue), ct) is { } refresh
                && refresh.InstanceId == instance.Id
                && refresh.ClientId == authentication.Client.ClientId
                && refresh.Status == "active")
            {
                await store.UpsertRefreshTokenAsync(refresh with { Status = "revoked" }, ct);
            }
            return Results.Ok();
        });

        app.MapPost("/{slug}/oauth/introspect", async (string slug, HttpRequest request, AuthStore store, KeyRing keys,
            AuthOptions options, CancellationToken ct) =>
        {
            if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
                return ApiResults.NotFound();
            var form = await request.ReadFormAsync(ct);
            var authentication = await AuthenticateClientAsync(request, form, store, instance.Id, ct);
            if (authentication.Client is null)
                return TokenError("invalid_client", authentication.Description ?? "client authentication failed", 401);

            var tokenValue = form["token"].ToString();
            if (tokenValue.Length == 0)
                return TokenError("invalid_request", "token is required");
            if (tokenValue.Contains('.'))
            {
                var claims = await VerifyAccessTokenAsync(keys, store, options, instance, tokenValue, ct);
                if (claims is not null)
                    return Results.Json(new Dictionary<string, object>
                    {
                        ["active"] = true,
                        ["token_type"] = "Bearer",
                        ["iss"] = ClaimString(claims, "iss") ?? "",
                        ["sub"] = ClaimString(claims, "sub") ?? "",
                        ["aud"] = ClaimString(claims, "aud") ?? "",
                        ["client_id"] = ClaimString(claims, "aud") ?? "",
                        ["scope"] = ClaimString(claims, "scope") ?? "",
                        ["exp"] = ClaimLong(claims, "exp") ?? 0,
                        ["iat"] = ClaimLong(claims, "iat") ?? 0,
                    });
            }
            // Truthful only if the token endpoint would actually honor it: the
            // refresh grant refuses banned/deleted users, so introspection must
            // not report those tokens as active.
            else if (tokenValue.Length > 0
                && await store.FindRefreshTokenAsync(SecureTokens.Sha256Hex(tokenValue), ct) is { } refresh
                && refresh.InstanceId == instance.Id
                && refresh.ClientId == authentication.Client.ClientId
                && refresh.Status == "active"
                && refresh.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && await store.GetUserAsync(refresh.UserId, ct) is { Banned: false, Deleted: false })
            {
                return Results.Json(new Dictionary<string, object>
                {
                    ["active"] = true,
                    ["token_type"] = "refresh_token",
                    ["sub"] = refresh.UserId,
                    ["client_id"] = refresh.ClientId,
                    ["scope"] = string.Join(' ', refresh.Scopes),
                    ["exp"] = refresh.ExpiresAt,
                    ["iat"] = refresh.CreatedAt,
                });
            }
            return Results.Json(new { active = false });
        });

        app.MapGet("/{slug}/oauth/logout", async (string slug, HttpRequest request, AuthStore store,
            Users.SessionService sessions, CancellationToken ct) =>
        {
            if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
                return ApiResults.NotFound();

            // Kill the hosted-UI session behind the cookie, then clear the cookie.
            // id_token_hint is accepted but not load-bearing in v1 (the session
            // cookie is the authority on who is signed in).
            var cookie = request.Cookies[SessionCookies.Name];
            if (cookie is not null && await sessions.ResolveAsync(instance.Id, cookie, ct) is { } resolved)
                await sessions.RevokeAsync(instance.Id, resolved.Session.Id, ct);
            request.HttpContext.Response.Cookies.Delete(SessionCookies.Name, SessionCookies.Options);

            var query = request.Query;
            var clientId = query["client_id"].ToString();
            var postLogoutUri = query["post_logout_redirect_uri"].ToString();
            var state = query["state"].ToString();
            if (postLogoutUri.Length > 0 && clientId.Length > 0
                && await store.FindOAuthClientAsync(instance.Id, clientId, ct) is { } client
                && client.RedirectUris.Any(registered => SharesOrigin(registered, postLogoutUri)))
            {
                var target = state.Length > 0 ? AppendQuery(postLogoutUri, "state=" + Uri.EscapeDataString(state)) : postLogoutUri;
                return Results.Redirect(target);
            }
            return Ui.Page("Signed out", """
                <h1>Signed out</h1>
                <p class="muted">You have been signed out.</p>
                """);
        });

        app.MapPost("/v1/{slug}/oauth-clients", async (string slug, HttpRequest request, InstanceResolver resolver,
            AuthStore store, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context)
                return ApiResults.Unauthorized();
            if (context.Instance.Slug != slug)
                return ApiResults.Forbidden("this API key does not belong to the instance");

            var body = await request.ReadFromJsonAsync<CreateOAuthClientRequest>(ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Name) || body.RedirectUris is not { Length: > 0 })
                return ApiResults.BadRequest("invalid_request", "name and redirect_uris are required");
            foreach (var uri in body.RedirectUris)
                if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
                    return ApiResults.BadRequest("invalid_request", "redirect_uris must be absolute http(s) urls");

            var authMethod = body.TokenEndpointAuthMethod is { Length: > 0 } ? body.TokenEndpointAuthMethod : (body.Public ? "none" : "client_secret_basic");
            if (authMethod is not ("client_secret_basic" or "client_secret_post" or "none"))
                return ApiResults.BadRequest("invalid_request", "token_endpoint_auth_method must be client_secret_basic, client_secret_post, or none");

            // The secret is returned exactly once; only its SHA-256 is stored.
            string? secret = body.Public || authMethod == "none" ? null : SecureTokens.RandomToken(40);
            var clientId = SecureTokens.RandomToken(24);
            var client = new OAuthClient
            {
                Id = "clt_" + SecureTokens.RandomToken(20),
                InstanceId = context.Instance.Id,
                ClientId = clientId,
                ClientKey = $"{context.Instance.Id}:{clientId}",
                Name = body.Name.Trim(),
                ClientSecretHash = secret is null ? null : SecureTokens.Sha256Hex(secret),
                RedirectUris = body.RedirectUris,
                TokenEndpointAuthMethod = authMethod,
                Public = body.Public || authMethod == "none",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            await store.CreateOAuthClientAsync(client, ct);
            return Results.Ok(new { client = ClientView(client), client_secret = secret });
        });

        app.MapGet("/v1/{slug}/oauth-clients", async (string slug, HttpRequest request, InstanceResolver resolver,
            AuthStore store, TerraScaleClient sdk, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context)
                return ApiResults.Unauthorized();
            if (context.Instance.Slug != slug)
                return ApiResults.Forbidden("this API key does not belong to the instance");

            var page = await sdk.Documents.ListAsync(store.Database, "oauth_clients", 200, null, ct);
            var clients = page.Items
                .Select(item => item.Value.Deserialize<OAuthClient>(Json))
                .OfType<OAuthClient>()
                .Where(client => client.InstanceId == context.Instance.Id)
                .Select(ClientView)
                .ToList();
            return Results.Ok(new { clients });
        });

        app.MapDelete("/v1/{slug}/oauth-clients/{id}", async (string slug, string id, HttpRequest request,
            InstanceResolver resolver, AuthStore store, TerraScaleClient sdk, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context)
                return ApiResults.Unauthorized();
            if (context.Instance.Slug != slug)
                return ApiResults.Forbidden("this API key does not belong to the instance");

            if (await store.GetOAuthClientAsync(id, ct) is not { } client || client.InstanceId != context.Instance.Id)
                return ApiResults.NotFound("not_found", "no such oauth client");
            await store.DeleteOAuthClientAsync(id, ct);
            return Results.NoContent();
        });
    }

    /// <summary>Management request body for OAuth client creation.</summary>
    public sealed record CreateOAuthClientRequest(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris,
        [property: JsonPropertyName("public")] bool Public,
        [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

    // ---- Token grants ---------------------------------------------------------------

    private static async Task<IResult> AuthorizationCodeGrantAsync(AuthStore store, KeyRing keys, string issuer,
        Instance instance, OAuthClient client, IFormCollection form, CancellationToken ct)
    {
        var code = form["code"].ToString();
        if (code.Length == 0)
            return TokenError("invalid_request", "code is required");

        var record = await store.FindAuthorizationCodeAsync(SecureTokens.Sha256Hex(code), ct);
        if (record is null || record.InstanceId != instance.Id || record.ClientId != client.ClientId)
            return TokenError("invalid_grant", "invalid authorization code");
        if (record.Consumed)
        {
            // RFC 6749 4.1.2: replayed code means theft — revoke the tokens it
            // produced (the refresh family for this user+client).
            await RevokeRefreshFamilyAsync(store, instance.Id, client.ClientId, record.UserId, ct);
            return TokenError("invalid_grant", "authorization code already used");
        }
        if (record.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return TokenError("invalid_grant", "authorization code expired");
        if (form["redirect_uri"].ToString() != record.RedirectUri)
            return TokenError("invalid_grant", "redirect_uri does not match the authorization request");

        if (record.CodeChallenge is null)
        {
            if (client.Public)
                return TokenError("invalid_grant", "PKCE is required for public clients");
        }
        else
        {
            var verifier = form["code_verifier"].ToString();
            if (verifier.Length == 0)
                return TokenError("invalid_grant", "code_verifier is required");
            var derived = Encodings.Base64UrlEncode(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            if (!SecureTokens.FixedTimeEquals(derived, record.CodeChallenge))
                return TokenError("invalid_grant", "code_verifier does not match the code challenge");
        }

        // Consume before issuing so a concurrent replay cannot win (single-use codes).
        await store.UpsertAuthorizationCodeAsync(record with { Consumed = true }, ct);

        if (await store.GetUserAsync(record.UserId, ct) is not { } user)
            return TokenError("invalid_grant", "user not found");
        var sessionId = record.Id.Split(':')[1];
        var session = await store.GetSessionAsync(sessionId, ct);
        var scope = string.Join(' ', record.Scopes);

        var accessToken = await SignAccessTokenAsync(keys, instance.Id, issuer, user.Id, client.ClientId, scope, session?.Id, ct);
        var (refreshToken, refreshId) = await CreateRefreshTokenAsync(store, instance.Id, client.ClientId, user.Id, record.Scopes, ct);

        var response = new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = AccessTokenLifetimeSeconds,
            ["scope"] = scope,
            ["refresh_token"] = refreshToken,
        };
        if (record.Scopes.Contains("openid"))
        {
            var idTokenClaims = new Dictionary<string, object>
            {
                ["iss"] = issuer,
                ["sub"] = user.Id,
                ["aud"] = client.ClientId,
                ["at_hash"] = Encodings.Base64UrlEncode(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]),
            };
            if (session is not null)
                idTokenClaims["auth_time"] = session.CreatedAt;
            if (record.Nonce is not null)
                idTokenClaims["nonce"] = record.Nonce;
            if (record.Scopes.Contains("email") && user.Email is not null)
            {
                idTokenClaims["email"] = user.Email;
                idTokenClaims["email_verified"] = user.EmailVerified;
            }
            var key = await keys.GetCurrentAsync(instance.Id, ct);
            response["id_token"] = JsonWebToken.Sign(idTokenClaims, DateTimeOffset.UtcNow.AddSeconds(AccessTokenLifetimeSeconds), key);
        }
        return Results.Json(response);
    }

    private static async Task<IResult> RefreshTokenGrantAsync(AuthStore store, KeyRing keys, string issuer,
        Instance instance, OAuthClient client, IFormCollection form, CancellationToken ct)
    {
        var tokenValue = form["refresh_token"].ToString();
        if (tokenValue.Length == 0)
            return TokenError("invalid_request", "refresh_token is required");

        var token = await store.FindRefreshTokenAsync(SecureTokens.Sha256Hex(tokenValue), ct);
        if (token is null || token.InstanceId != instance.Id)
            return TokenError("invalid_grant", "unknown refresh token");
        if (token.ClientId != client.ClientId)
            return TokenError("invalid_grant", "refresh token was issued to another client");
        if (token.Status != "active" || token.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            // Reuse of a rotated/revoked token: assume theft and kill the family.
            await RevokeRefreshFamilyAsync(store, instance.Id, client.ClientId, token.UserId, ct);
            return TokenError("invalid_grant", "refresh token is no longer active");
        }
        if (await store.GetUserAsync(token.UserId, ct) is not { Banned: false, Deleted: false })
        {
            // A banned/deleted user must not keep minting tokens; kill the family.
            await RevokeRefreshFamilyAsync(store, instance.Id, client.ClientId, token.UserId, ct);
            return TokenError("invalid_grant", "user is no longer active");
        }

        var (refreshToken, refreshId) = await CreateRefreshTokenAsync(store, instance.Id, client.ClientId, token.UserId, token.Scopes, ct);
        await store.UpsertRefreshTokenAsync(token with { Status = "rotated", RotatedTo = refreshId }, ct);
        await AuditAsync(store, instance.Id, "oauth.token_refreshed", token.UserId, new Dictionary<string, object> { ["client_id"] = client.ClientId }, ct);

        var scope = string.Join(' ', token.Scopes);
        var accessToken = await SignAccessTokenAsync(keys, instance.Id, issuer, token.UserId, client.ClientId, scope, null, ct);
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = AccessTokenLifetimeSeconds,
            ["scope"] = scope,
            ["refresh_token"] = refreshToken,
        });
    }

    private static async Task<IResult> ClientCredentialsGrantAsync(KeyRing keys, string issuer,
        Instance instance, OAuthClient client, IFormCollection form, CancellationToken ct)
    {
        if (client.Public || client.ClientSecretHash is null)
            return TokenError("unauthorized_client", "client_credentials requires an authenticated confidential client");

        var scope = form["scope"].ToString();
        var accessToken = await SignAccessTokenAsync(keys, instance.Id, issuer, client.ClientId, client.ClientId, scope, null, ct);
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = AccessTokenLifetimeSeconds,
            ["scope"] = scope,
        });
    }

    // ---- Shared plumbing ----------------------------------------------------------------

    private sealed record ClientAuthentication(OAuthClient? Client, string? Description, int Status = 401);

    /// <summary>Client authentication per RFC 6749 2.3.1: Basic (client_secret_basic) or form (client_secret_post); public clients send client_id alone.</summary>
    private static async Task<ClientAuthentication> AuthenticateClientAsync(HttpRequest request, IFormCollection form,
        AuthStore store, string instanceId, CancellationToken ct)
    {
        string clientId;
        string? secret = null;
        var header = request.Headers.Authorization.ToString();
        var usedBasic = header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);

        // RFC 6749 5.2: a 401 for a client that tried the Authorization header
        // must carry WWW-Authenticate naming that scheme.
        ClientAuthentication Fail(string description)
        {
            if (usedBasic)
                request.HttpContext.Response.Headers.WWWAuthenticate = "Basic";
            return new(null, description);
        }

        if (usedBasic)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var separator = decoded.IndexOf(':');
                if (separator < 0)
                    return Fail("malformed Basic authorization header");
                clientId = Uri.UnescapeDataString(decoded[..separator]);
                secret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
            }
            catch (FormatException)
            {
                return Fail("malformed Basic authorization header");
            }
        }
        else
        {
            clientId = form["client_id"].ToString();
            var formSecret = form["client_secret"].ToString();
            secret = formSecret.Length == 0 ? null : formSecret;
        }

        if (clientId.Length == 0)
            return Fail("client_id is required");
        if (await store.FindOAuthClientAsync(instanceId, clientId, ct) is not { } client)
            return Fail("unknown client");
        if (client.ClientSecretHash is null)
            return new(client, null); // public client: nothing to verify
        if (secret is null)
            return Fail("client secret required");
        return SecureTokens.FixedTimeEquals(SecureTokens.Sha256Hex(secret), client.ClientSecretHash)
            ? new(client, null)
            : Fail("invalid client secret");
    }

    /// <summary>Revokes every active refresh token for a user+client pair (the token "family").</summary>
    private static async Task RevokeRefreshFamilyAsync(AuthStore store, string instanceId, string clientId, string userId, CancellationToken ct)
    {
        foreach (var sibling in await store.ListRefreshTokensByUserAsync(userId, ct))
            if (sibling.InstanceId == instanceId && sibling.ClientId == clientId && sibling.Status == "active")
                await store.UpsertRefreshTokenAsync(sibling with { Status = "revoked" }, ct);
    }

    private static async Task<string> SignAccessTokenAsync(KeyRing keys, string instanceId, string issuer,
        string subject, string audience, string scope, string? sessionId, CancellationToken ct)
    {
        var claims = new Dictionary<string, object>
        {
            ["iss"] = issuer,
            ["sub"] = subject,
            ["aud"] = audience,
            ["scope"] = scope,
        };
        if (sessionId is not null)
            claims["sid"] = sessionId;
        var key = await keys.GetCurrentAsync(instanceId, ct);
        return JsonWebToken.Sign(claims, DateTimeOffset.UtcNow.AddSeconds(AccessTokenLifetimeSeconds), key);
    }

    private static async Task<(string Token, string Id)> CreateRefreshTokenAsync(AuthStore store, string instanceId,
        string clientId, string userId, string[] scopes, CancellationToken ct)
    {
        var token = SecureTokens.RandomToken(48);
        var now = DateTimeOffset.UtcNow;
        var id = "rtk_" + SecureTokens.RandomToken(20);
        await store.CreateRefreshTokenAsync(new RefreshToken
        {
            Id = id,
            InstanceId = instanceId,
            ClientId = clientId,
            UserId = userId,
            TokenKey = SecureTokens.Sha256Hex(token),
            Status = "active",
            Scopes = scopes,
            CreatedAt = now.ToUnixTimeSeconds(),
            ExpiresAt = now.Add(RefreshTokenLifetime).ToUnixTimeSeconds(),
        }, ct);
        return (token, id);
    }

    private static async Task<Dictionary<string, object>?> VerifyAccessTokenAsync(KeyRing keys, AuthStore store,
        AuthOptions options, Instance instance, string token, CancellationToken ct)
    {
        var activeKeys = await keys.GetActiveAsync(instance.Id, ct);
        var claims = JsonWebToken.Verify(token, kid => (SigningKey?)activeKeys.FirstOrDefault(key => key.Id == kid));
        if (claims is null)
            return null;
        if (ClaimString(claims, "iss") != Issuer(options, instance.Slug))
            return null;
        // The audience must be a client of this instance (exp is checked by Verify).
        return ClaimString(claims, "aud") is { } audience
            && await store.FindOAuthClientAsync(instance.Id, audience, ct) is not null
            ? claims
            : null;
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    private static IResult UnauthorizedBearer(string description) =>
        Results.Json(new { error = "invalid_token", error_description = description }, statusCode: 401);

    /// <summary>The safe client shape returned by the management API (never the secret hash).</summary>
    private static object ClientView(OAuthClient client) => new
    {
        client.Id,
        client.ClientId,
        client.Name,
        client.RedirectUris,
        client.TokenEndpointAuthMethod,
        client.Public,
        client.CreatedAt,
    };

    private static bool SharesOrigin(string registered, string candidate) =>
        Uri.TryCreate(registered, UriKind.Absolute, out var a)
        && Uri.TryCreate(candidate, UriKind.Absolute, out var b)
        && a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;

    private static string? ClaimString(Dictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value)
            && value is JsonElement { ValueKind: JsonValueKind.String } element
                ? element.GetString()
                : null;

    private static long? ClaimLong(Dictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value)
            && value is JsonElement { ValueKind: JsonValueKind.Number } element
            && element.TryGetInt64(out var parsed)
                ? parsed
                : null;

    private static string Issuer(AuthOptions options, string slug) =>
        options.PublicOrigin.ToString().TrimEnd('/') + "/" + slug;

    private static string AppendQuery(string uri, string query) =>
        uri.Contains('?') ? $"{uri}&{query}" : $"{uri}?{query}";

    private static IResult RedirectError(string redirectUri, string state, string error, string description)
    {
        var query = $"error={Uri.EscapeDataString(error)}&error_description={Uri.EscapeDataString(description)}";
        if (state.Length > 0)
            query += "&state=" + Uri.EscapeDataString(state);
        return Results.Redirect(AppendQuery(redirectUri, query));
    }

    private static IResult TokenError(string error, string description, int status = 400) =>
        Results.Json(new { error, error_description = description }, statusCode: status);

    private static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
}
