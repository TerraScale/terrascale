using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Common.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Mcp
{
    // MCP clients (Claude Code, claude.ai connectors, Cursor) sign in with the MCP authorization flow
    // instead of a pasted personal access token. The dashboard is the authorization server for its own
    // MCP resource: protected-resource metadata (RFC 9728), dynamic registration of public clients
    // (RFC 7591), authorization code with PKCE S256 and resource indicators (RFC 8707). The user signs in
    // on ts-auth's hosted pages, and the access token is a personal access token holding its own fresh
    // ts-auth grant, so it is listed and revoked on Profile → Access tokens like any other.
    public static partial class McpOAuth
    {
        public const string ClientCollection = "dashboard_mcp_oauth_clients";
        public const string CodeCollection = "dashboard_mcp_oauth_codes";
        public const string McpHost = "mcp.terrascale.tech";
        private const int TokenDays = 90;
        private const int MaxRegistrationBytes = 16 * 1024;
        private const string Scope = "mcp";
        private static readonly string[] BlockedSchemes = ["javascript", "data", "file", "vbscript", "about", "blob", "ftp", "ws", "wss"];
        private static readonly string[] BearerMethods = ["header"];
        private static readonly string[] ResponseTypes = ["code"];
        private static readonly string[] GrantTypes = ["authorization_code"];
        private static readonly string[] CodeChallengeMethods = ["S256"];
        private static readonly string[] TokenEndpointAuthMethods = ["none"];

        public static void MapMcpOAuth(this WebApplication app)
        {
            app.MapGet("/.well-known/oauth-protected-resource/{**resource}", ProtectedResource);
            app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServer);
            app.MapPost("/oauth/register", RegisterAsync);
            app.MapGet("/oauth/authorize", AuthorizeAsync);
            app.MapPost("/oauth/authorize", ConsentAsync);
            app.MapPost("/oauth/token", TokenAsync);
        }

        // mcp.terrascale.tech serves only MCP; its sign-in and authorization pages live on the dashboard host.
        public static bool IsMcpHost(HttpContext context)
        {
            return context.Request.Host.Host.Equals(McpHost, StringComparison.OrdinalIgnoreCase);
        }

        private static string Origin(HttpContext context)
        {
            return $"{context.Request.Scheme}://{context.Request.Host}";
        }

        private static string Issuer(HttpContext context)
        {
            return IsMcpHost(context) ? "https://dash.terrascale.tech" : Origin(context);
        }

        private static string Resource(HttpContext context)
        {
            return IsMcpHost(context) ? $"https://{McpHost}/" : Origin(context) + "/mcp";
        }

        public static string ResourceMetadataUrl(HttpContext context)
        {
            return Origin(context) + "/.well-known/oauth-protected-resource" + (IsMcpHost(context) ? "" : "/mcp");
        }

        private static IResult ProtectedResource(HttpContext context)
        {
            return Results.Json(new Dictionary<string, object>
            {
                ["resource"] = Resource(context),
                ["authorization_servers"] = new[] { Issuer(context) },
                ["scopes_supported"] = new[] { Scope },
                ["bearer_methods_supported"] = BearerMethods,
                ["resource_name"] = "TerraScale",
                ["resource_documentation"] = "https://docs.terrascale.tech/guides/mcp"
            });
        }

        private static IResult AuthorizationServer(HttpContext context)
        {
            var issuer = Issuer(context);
            return Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = issuer + "/oauth/authorize",
                ["token_endpoint"] = issuer + "/oauth/token",
                ["registration_endpoint"] = issuer + "/oauth/register",
                ["scopes_supported"] = new[] { Scope },
                ["response_types_supported"] = ResponseTypes,
                ["grant_types_supported"] = GrantTypes,
                ["code_challenge_methods_supported"] = CodeChallengeMethods,
                ["token_endpoint_auth_methods_supported"] = TokenEndpointAuthMethods,
                ["authorization_response_iss_parameter_supported"] = true,
                ["service_documentation"] = "https://docs.terrascale.tech/guides/mcp"
            });
        }

        // Anyone may register: a client is only a name and its redirect URIs, and every authorization
        // still needs the user's consent on the dashboard and a sign-in on ts-auth.
        private static async Task<IResult> RegisterAsync(HttpContext context, IDashboardState state, TimeProvider time)
        {
            static IResult TooLarge()
            {
                return OAuthError("invalid_client_metadata", "Client metadata exceeds 16 KiB.", StatusCodes.Status413PayloadTooLarge);
            }

            if (context.Request.ContentLength > MaxRegistrationBytes)
            {
                return TooLarge();
            }

            if (!context.Request.HasJsonContentType() ||
                !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
                contentType.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            {
                return OAuthError("invalid_client_metadata", "Send client metadata as UTF-8 application/json.");
            }
            // Like PlatformJson, read a fixed byte envelope plus one overflow byte before parsing.
            // Content-Length is optional; neither chunked input nor JSON whitespace can bypass this bound.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var bytes = new byte[MaxRegistrationBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await context.Request.Body.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (count == 0)
                {
                    break;
                }

                length += count;
            }
            if (length > MaxRegistrationBytes)
            {
                return TooLarge();
            }

            JsonElement metadata;
            try
            {
                // JsonElement decodes strings lazily: validate even ignored fields and property names before persistence.
                var reader = new Utf8JsonReader(bytes.AsSpan(0, length));
                while (reader.Read())
                {
                    if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                    {
                        _ = reader.GetString();
                    }
                }

                metadata = JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan(0, length));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            { return OAuthError("invalid_client_metadata", "Client metadata must be a JSON object."); }
            if (metadata.ValueKind != JsonValueKind.Object)
            {
                return OAuthError("invalid_client_metadata", "Client metadata must be a JSON object.");
            }

            if (!metadata.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array || uris.GetArrayLength() is 0 or > 10 ||
                uris.EnumerateArray().Any(uri => uri.ValueKind != JsonValueKind.String || !ValidRedirect(uri.GetString()!)))
            {
                return OAuthError("invalid_redirect_uri", "Register 1–10 redirect URIs: https, http on a loopback host, or a private app scheme, without fragments.");
            }

            if (metadata.TryGetProperty("grant_types", out var grants) &&
                (grants.ValueKind != JsonValueKind.Array || !grants.EnumerateArray().Any(grant => grant.ValueKind == JsonValueKind.String && grant.GetString() == "authorization_code")))
            {
                return OAuthError("invalid_client_metadata", "Clients use the authorization_code grant.");
            }

            var name = metadata.TryGetProperty("client_name", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString()!.Trim() : "";
            if (name.EnumerateRunes().Count() > 64 || name.Any(char.IsControl))
            {
                return OAuthError("invalid_client_metadata", "client_name uses at most 64 printable characters.");
            }

            var id = AccountService.NewId();
            var client = McpRegistrations.Create(id, name.Length == 0 ? "MCP client" : name, [.. uris.EnumerateArray().Select(uri => uri.GetString()!)], time.GetUtcNow());
            await state.PutAsync(ClientCollection, id, JsonSerializer.SerializeToElement(client), TerraBasePrecondition.Absent, context.RequestAborted);
            return Results.Json(new Dictionary<string, object>
            {
                ["client_id"] = id,
                ["client_id_issued_at"] = client.CreatedAt.ToUnixTimeSeconds(),
                ["client_name"] = client.Name,
                ["redirect_uris"] = client.RedirectUris,
                ["grant_types"] = GrantTypes,
                ["response_types"] = ResponseTypes,
                ["token_endpoint_auth_method"] = "none"
            }, statusCode: StatusCodes.Status201Created);
        }

        private static async Task<IResult> AuthorizeAsync(HttpContext context, IDashboardState state)
        {
            var request = await ReadRequestAsync(context, state, context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));
            if (request.Page is not null)
            {
                return request.Page;
            }

            if (request.Error is not null)
            {
                return request.Error;
            }

            var authorization = request.Authorization!;
            // The consent form's redirect chain can end at the client's redirect URI, so the page's form-action allows it.
            context.Items[FormActionKey] = FormActionSource(authorization.RedirectUri);
            return McpConsentView.Create(new(new("Connect an AI client", DocumentModel.NonceFor(context)), authorization.ClientName,
                Display(authorization.RedirectUri), Fields(authorization), Csrf.Token(context), null));
        }

        public const string FormActionKey = "TerraScale.Dashboard/form-action";

        private static async Task<IResult> ConsentAsync(HttpContext context, IDashboardState state)
        {
            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest();
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Csrf.Validate(context, form))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var request = await ReadRequestAsync(context, state, form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));
            if (request.Page is not null)
            {
                return request.Page;
            }

            if (request.Error is not null)
            {
                return request.Error;
            }

            if (form["decision"] != "allow")
            {
                return Denied(request.Authorization!);
            }

            var account = await AccountRequest.RequireAsync(context, context.RequestServices.GetRequiredService<DashboardSessions>(), context.RequestServices.GetRequiredService<AccountService>());
            if (account is null)
            {
                return AccountRequest.LoginRedirect(context);
            }

            return await StepUpFlow.StartAsync(context, account, "create_pat", "ts-dashboard", mcp: request.Authorization);
        }

        // The ts-auth callback for an MCP authorization: the fresh grant waits in its own credential
        // until the client redeems the single-use code with its PKCE verifier.
        public static async Task<IResult> GrantAsync(HttpContext context, McpAuthorization authorization, SessionRecord fresh, StepUpIntent? proof)
        {
            var sessions = context.RequestServices.GetRequiredService<DashboardSessions>();
            var state = context.RequestServices.GetRequiredService<IDashboardState>();
            var time = context.RequestServices.GetRequiredService<TimeProvider>();
            var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var tokenExpiresAt = time.GetUtcNow().AddDays(TokenDays);
            string? credential = null;
            try
            {
                var client = await McpRegistrations.ReadActiveAsync(state, time, authorization.ClientId, context.RequestAborted);
                if (client is null || !client.RedirectUris.Any(uri => RedirectMatches(uri, authorization.RedirectUri)))
                {
                    throw new AccountAccessException("This AI client registration expired or changed. Add the server again.", 400);
                }

                var browser = await sessions.ReadAsync(context);
                if (proof is null || proof.Mcp != authorization || proof.Audience != "ts-dashboard" || browser?.UserId != proof.UserId ||
                    fresh.UserId != proof.UserId || StepUpFlow.Browser(context) != proof.Browser)
                {
                    throw StepUpFlow.Required();
                }

                context.Request.QueryString = new(proof.Query);
                var account = await AccountRequest.RequireAsync(context, sessions, context.RequestServices.GetRequiredService<AccountService>()) ?? throw StepUpFlow.Required();
                StepUpFlow.Validate(context, account, proof, "create_pat");
                await StepUpFlow.ConsumeAsync(context, account, proof);
                client = await McpRegistrations.CompleteSignInAsync(state, time, authorization.ClientId, context.RequestAborted);
                if (client is null || !client.RedirectUris.Any(uri => RedirectMatches(uri, authorization.RedirectUri)))
                {
                    throw new AccountAccessException("This AI client registration expired or changed. Add the server again.", 400);
                }

                credential = await sessions.CreateCredentialAsync(fresh, tokenExpiresAt, context.RequestAborted);
                var grant = new McpCode(authorization.ClientId, authorization.RedirectUri, authorization.CodeChallenge, authorization.Resource, fresh.UserId, credential,
                    client.Name, time.GetUtcNow().AddMinutes(5), tokenExpiresAt);
                await state.PutAsync(CodeCollection, Hash(code), JsonSerializer.SerializeToElement(grant), TerraBasePrecondition.Absent, context.RequestAborted);
            }
            catch (Exception error)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    if (credential is not null)
                    {
                        await sessions.DeleteCredentialAsync(credential, cleanup.Token);
                    }
                }
                finally { await context.RequestServices.GetRequiredService<IAuthGateway>().RevokeAsync(fresh, cleanup.Token); }
                if (error is AccountAccessException access)
                {
                    return ErrorPage(context, error.Message, access.StatusCode);
                }

                throw;
            }
            return Results.Redirect(QueryHelpers.AddQueryString(authorization.RedirectUri, new Dictionary<string, string?>
            { ["code"] = code, ["state"] = authorization.State, ["iss"] = authorization.Issuer }.Where(pair => pair.Value is not null)));
        }

        public static IResult Denied(McpAuthorization authorization)
        {
            return Results.Redirect(QueryHelpers.AddQueryString(authorization.RedirectUri, new Dictionary<string, string?>
            { ["error"] = "access_denied", ["state"] = authorization.State, ["iss"] = authorization.Issuer }.Where(pair => pair.Value is not null)));
        }

        private static async Task<IResult> TokenAsync(HttpContext context, IDashboardState state, AccessTokens tokens, TimeProvider time)
        {
            if (!context.Request.HasFormContentType)
            {
                return OAuthError("invalid_request", "Send the token request as a form.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form["grant_type"] != "authorization_code")
            {
                return OAuthError("unsupported_grant_type", "Only the authorization_code grant is supported.");
            }

            var code = form["code"].ToString();
            if (code is not { Length: 64 } || !code.All(char.IsAsciiHexDigitLower))
            {
                return OAuthError("invalid_grant", "The authorization code is invalid.");
            }

            var document = await state.GetAsync(CodeCollection, Hash(code), context.RequestAborted);
            if (document is null || document.Id != Hash(code))
            {
                return OAuthError("invalid_grant", "The authorization code is invalid or was already used.");
            }
            // Deleting first makes the code single use across replicas: only one redemption wins the version.
            try { await state.DeleteAsync(CodeCollection, document.Id, document.Version, context.RequestAborted); }
            catch (Exception error) when (error is TerraBasePreconditionFailedException or
                                          HttpRequestException { StatusCode: System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.NotFound })
            { return OAuthError("invalid_grant", "The authorization code is invalid or was already used."); }
            var grant = document.Document.Deserialize<McpCode>() ?? throw new JsonException("Invalid authorization code.");
            var verifier = form["code_verifier"].ToString();
            var failure =
                grant.ExpiresAt <= time.GetUtcNow() ? "The authorization code expired." :
                form["client_id"] != grant.ClientId ? "The authorization code belongs to another client." :
                form["redirect_uri"] != grant.RedirectUri ? "redirect_uri does not match the authorization request." :
                !VerifierMatches(verifier, grant.CodeChallenge) ? "code_verifier does not match the code challenge." : null;
            // RFC 8707: the token request names the same resource the user consented to; the token serves only that MCP server.
            var resource = form["resource"];
            var target = failure is null && resource.Count != 1 ? "Send the resource the authorization request named." :
                failure is null && (CanonicalResource(context, resource.ToString()) is not { } named || named != grant.Resource) ? "resource does not match the authorization request." : null;
            // The code is spent: every path that does not issue the token forgets its grant, even when the client disconnects.
            async Task ForgetAsync()
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await tokens.ForgetGrantAsync(grant.CredentialId, cleanup.Token);
            }
            try
            {
                if (failure is null && target is null)
                {
                    var client = await McpRegistrations.ReadActiveAsync(state, time, grant.ClientId, context.RequestAborted);
                    if (client is not { FirstSignInCompletedAt: not null } || !client.RedirectUris.Any(uri => RedirectMatches(uri, grant.RedirectUri)))
                    {
                        failure = "This AI client registration expired or changed. Add the server again.";
                    }
                }
            }
            catch { await ForgetAsync(); throw; }
            if (failure is not null || target is not null)
            {
                await ForgetAsync();
                return failure is not null ? OAuthError("invalid_grant", failure) : OAuthError("invalid_target", target!);
            }
            try
            {
                var created = await tokens.IssueGrantedAsync(grant.UserId, AccessTokenName(grant.ClientName), grant.TokenExpiresAt, grant.CredentialId, grant.ClientId, context.RequestAborted);
                return Results.Json(new Dictionary<string, object>
                {
                    ["access_token"] = created.Secret,
                    ["token_type"] = "Bearer",
                    ["expires_in"] = (long)(grant.TokenExpiresAt - time.GetUtcNow()).TotalSeconds,
                    ["scope"] = Scope
                });
            }
            catch (AccountAccessException error) { return OAuthError("invalid_grant", error.Message); }
        }

        private static async Task<AuthorizationRequest> ReadRequestAsync(HttpContext context, IDashboardState state, Dictionary<string, string> parameters)
        {
            var clientId = parameters.GetValueOrDefault("client_id", "");
            var client = await McpRegistrations.ReadActiveAsync(state, context.RequestServices.GetRequiredService<TimeProvider>(), clientId, context.RequestAborted);
            if (client is null)
            {
                return new(Page: ErrorPage(context, "This AI client is not registered with TerraScale. Add the server to your client again."));
            }

            var requested = parameters.GetValueOrDefault("redirect_uri", "");
            var redirect = requested.Length == 0 && client.RedirectUris.Length == 1 ? client.RedirectUris[0] : client.RedirectUris.Any(uri => RedirectMatches(uri, requested)) ? requested : null;
            if (redirect is null)
            {
                return new(Page: ErrorPage(context, "The AI client sent a redirect address it did not register. Add the server to your client again."));
            }

            var stateValue = parameters.GetValueOrDefault("state") is { Length: > 0 } given ? given : null;
            var resource = CanonicalResource(context, parameters.GetValueOrDefault("resource", ""));
            var authorization = new McpAuthorization(clientId, client.Name, redirect, parameters.GetValueOrDefault("code_challenge", ""), stateValue, Issuer(context), resource ?? "");
            string? Fail(string error, string description)
            {
                return QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> { ["error"] = error, ["error_description"] = description, ["state"] = stateValue, ["iss"] = authorization.Issuer }.Where(pair => pair.Value is not null));
            }

            var error =
                parameters.GetValueOrDefault("response_type") != "code" ? Fail("unsupported_response_type", "Use response_type=code.") :
                parameters.GetValueOrDefault("code_challenge_method") != "S256" || !ChallengePattern().IsMatch(authorization.CodeChallenge) ? Fail("invalid_request", "PKCE with code_challenge_method=S256 is required.") :
                stateValue is { Length: > 1024 } ? Fail("invalid_request", "state is too long.") :
                resource is null ? Fail("invalid_target", "Name a TerraScale MCP server as the resource.") :
                !SupportedScope(parameters.GetValueOrDefault("scope")) ? Fail("invalid_scope", $"Request scope={Scope} or omit it.") : null;
            return error is null ? new(Authorization: authorization) : new(Error: Results.Redirect(error));
        }

        // Both MCP addresses name the same server, and the access token works on both; a code stays bound to the
        // canonical form of the address its authorization named, and only that address redeems it.
        private static string? CanonicalResource(HttpContext context, string resource)
        {
            if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri) || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            {
                return null;
            }

            if (uri.Scheme == "https" && uri.IdnHost.Equals(McpHost, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && uri.AbsolutePath == "/")
            {
                return $"https://{McpHost}/";
            }

            var authority = uri.GetLeftPart(UriPartial.Authority);
            return authority.Equals(Issuer(context), StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.TrimEnd('/') == "/mcp" ? authority + "/mcp" : null;
        }

        // The only scope is the whole MCP server; anything else would promise a narrower or wider grant than the token carries.
        private static bool SupportedScope(string? scope)
        {
            return scope is null || scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(item => item == Scope);
        }

        private static bool ValidRedirect(string value)
        {
            if (value.Length > 2000 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            {
                return false;
            }

            return uri.Scheme switch
            {
                "https" => true,
                "http" => uri.IsLoopback,
                var scheme => !BlockedSchemes.Contains(scheme) && SchemePattern().IsMatch(scheme)
            };
        }

        // RFC 8252: native clients listen on an ephemeral loopback port, so the port may differ from registration.
        private static bool RedirectMatches(string registered, string requested)
        {
            if (registered == requested)
            {
                return true;
            }

            return Uri.TryCreate(registered, UriKind.Absolute, out var known) && Uri.TryCreate(requested, UriKind.Absolute, out var asked) &&
                known.Scheme == "http" && asked.Scheme == "http" && known.IsLoopback && known.Host == asked.Host &&
                known.PathAndQuery == asked.PathAndQuery && asked.UserInfo.Length == 0 && asked.Fragment.Length == 0;
        }

        private static bool VerifierMatches(string verifier, string challenge)
        {
            return verifier.Length is >= 43 and <= 128 && VerifierPattern().IsMatch(verifier) &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))), Encoding.ASCII.GetBytes(challenge));
        }

        private static string FormActionSource(string redirect)
        {
            var uri = new Uri(redirect);
            return uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : uri.Scheme + ":";
        }

        private static string Display(string redirect)
        {
            var uri = new Uri(redirect);
            return uri.Scheme is "http" or "https" ? uri.Authority : uri.Scheme + ":";
        }

        private static string AccessTokenName(string clientName)
        {
            var name = "MCP: " + clientName;
            return name.EnumerateRunes().Count() <= 64 ? name : string.Concat(name.EnumerateRunes().Take(64).Select(rune => rune.ToString()));
        }

        private static IReadOnlyList<KeyValuePair<string, string>> Fields(McpAuthorization authorization)
        {
            return [.. new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = authorization.ClientId,
                ["redirect_uri"] = authorization.RedirectUri,
                ["code_challenge"] = authorization.CodeChallenge,
                ["code_challenge_method"] = "S256",
                ["state"] = authorization.State,
                ["resource"] = authorization.Resource,
                ["scope"] = Scope
            }.Where(pair => pair.Value is not null).Select(pair => KeyValuePair.Create(pair.Key, pair.Value!))];
        }

        private static RazorSlices.RazorSlice<McpConsentPage> ErrorPage(HttpContext context, string message, int statusCode = 400)
        {
            var view = McpConsentView.Create(new(new("Connect an AI client", DocumentModel.NonceFor(context)), "", "", [], "", message));
            view.StatusCode = statusCode;
            return view;
        }

        private static IResult OAuthError(string error, string description, int statusCode = StatusCodes.Status400BadRequest)
        {
            return Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, statusCode: statusCode);
        }

        private static string Hash(string value)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        [GeneratedRegex("^[A-Za-z0-9_-]{43}$")] private static partial Regex ChallengePattern();
        [GeneratedRegex("^[A-Za-z0-9._~-]+$")] private static partial Regex VerifierPattern();
        [GeneratedRegex("^[a-z][a-z0-9+.-]*$")] private static partial Regex SchemePattern();

        private sealed record AuthorizationRequest(McpAuthorization? Authorization = null, IResult? Error = null, IResult? Page = null);
        private sealed record McpCode(string ClientId, string RedirectUri, string CodeChallenge, string Resource, string UserId, string CredentialId, string ClientName,
            DateTimeOffset ExpiresAt, DateTimeOffset TokenExpiresAt);
    }

    public sealed record McpAuthorization(string ClientId, string ClientName, string RedirectUri, string CodeChallenge, string? State, string Issuer, string Resource);

    public sealed record McpConsentPage(DocumentModel Document, string ClientName, string RedirectHost, IReadOnlyList<KeyValuePair<string, string>> Fields, string Csrf, string? Error);
}
