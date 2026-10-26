using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Microsoft.AspNetCore;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// The hosted OpenID Connect issuers: one per tenant, <c>{TS_AUTH_PUBLIC_ORIGIN}/{tenant}</c>, with every protocol route
/// under <c>/{tenant}/</c>. The <c>terrascale</c> tenant serves the platform; each project is a tenant whose discovery
/// users sign in to the customer's own applications with the authorization code flow, under its own key ring.
/// </summary>
public static class OidcIssuer
{
    /// <summary>The issuer's path segment.</summary>
    public const string Slug = "terrascale";

    /// <summary>The instance that owns the issuer's users, sessions, keys, clients and tokens.</summary>
    public const string InstanceId = "terrascale";

    /// <summary>The client id of the ts-dashboard client registered from <c>TS_AUTH_DASHBOARD_*</c>.</summary>
    public const string DashboardClientId = "01M4EQ2FRJ8S8B29E8TQVWNW4J";

    /// <summary>The public authorization-code client for the status administration UI.</summary>
    public const string StatusClientId = "00000000000000000000000006";

    /// <summary>The status client's only registered authorization callback.</summary>
    public const string StatusRedirectUri = "https://status.terrascale.tech/admin/callback";

    /// <summary>
    /// The public device-flow (RFC 8628) client of the ts CLI. Client ids are TerraScale ids, so the contract's
    /// <c>ts-cli</c> is this reserved id with that display name.
    /// </summary>
    public const string CliClientId = "00000000000000000000000007";

    /// <summary>
    /// The user's authentication epoch, in codes, refresh tokens and sign-in access tokens: a grant or a management call
    /// whose epoch is not the user's current one (after sign-out everywhere or a password reset) is refused.
    /// </summary>
    public const string EpochClaim = "ts_auth_epoch";

    /// <summary>The winning authentication-state identity, independent of concurrent equal numeric epochs.</summary>
    public const string StateClaim = "ts_auth_state";

    /// <summary>
    /// Present (value <c>totp</c>) in codes, refresh tokens and sign-in access tokens whose sign-in proved the user's second
    /// factor. An account with an active factor accepts only these; activating or removing a factor advances the epoch.
    /// </summary>
    public const string MfaClaim = "ts_auth_mfa";

    /// <summary>The discovery document's path under a tenant's issuer.</summary>
    public const string ConfigurationPath = ".well-known/openid-configuration";

    /// <summary>The JWKS path under a tenant's issuer.</summary>
    public const string JwksPath = ".well-known/jwks.json";

    /// <summary>Lifetime of access and identity tokens; within the key ring's <c>Jwt.MaxLifetime</c>.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The platform issuer, derived only from the configured public origin.</summary>
    public static Uri Issuer(AuthOptions options) => Issuer(options, Slug);

    /// <summary>A tenant's issuer, derived only from the configured public origin.</summary>
    public static Uri Issuer(AuthOptions options, string tenant)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(tenant);
        return new Uri(options.PublicOrigin, tenant);
    }

    /// <summary>
    /// The audiences of a user's sign-in access token: the issuer, so the platform management API (TS-598) accepts it,
    /// and every <c>TS_AUTH_ACCESS_TOKEN_AUDIENCES</c> value.
    /// </summary>
    public static IReadOnlyList<string> SignInAudiences(AuthOptions options) => [Issuer(options).AbsoluteUri, .. options.AccessTokenAudiences];

    /// <summary>
    /// Registers OpenIddict on the TerraBase stores: authorization code with S256 PKCE and rotating refresh tokens,
    /// discovery, JWKS, userinfo and end-session. Access and identity tokens are ES256 JWTs signed by the instance's key
    /// ring. Authorization codes and refresh tokens are reference tokens, signed and encrypted under keys derived from
    /// the master key, so a key-ring rotation never invalidates them.
    /// </summary>
    public static IServiceCollection AddOidc(this IServiceCollection services, AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<ClientRegistration>();
        services.AddHttpContextAccessor();
        services.AddOpenIddict()
            .AddCore(core =>
            {
                // Replicated payload and revocation winners must be read from the current regional store.
                core.DisableEntityCaching();
                core.SetDefaultApplicationEntity<OidcApplication>()
                    .SetDefaultAuthorizationEntity<OidcAuthorization>()
                    .SetDefaultTokenEntity<OidcToken>()
                    .ReplaceApplicationManager<OidcApplication, ApiKeyApplicationManager>()
                    .ReplaceApplicationStore<OidcApplication, OidcApplicationStore>(ServiceLifetime.Singleton)
                    .ReplaceAuthorizationStore<OidcAuthorization, OidcAuthorizationStore>(ServiceLifetime.Singleton)
                    .ReplaceTokenStore<OidcToken, OidcTokenStore>(ServiceLifetime.Singleton);
            })
            .AddServer(server =>
            {
                // No fixed issuer: TrustPublicOrigin sets each request's base URI to its tenant's issuer, which OpenIddict
                // then uses as the issuer, and these relative endpoints resolve under it.
                server.SetConfigurationEndpointUris(ConfigurationPath)
                    .SetJsonWebKeySetEndpointUris(JwksPath)
                    .SetAuthorizationEndpointUris("oauth/authorize")
                    .SetTokenEndpointUris("oauth/token")
                    .SetUserInfoEndpointUris("oauth/userinfo")
                    .SetEndSessionEndpointUris("oauth/logout")
                    .SetRevocationEndpointUris("oauth/revoke")
                    // RFC 8628 for the ts CLI: the user approves on ts-auth's own hosted page, outside the issuer path.
                    .SetDeviceAuthorizationEndpointUris("oauth/device")
                    .SetEndUserVerificationEndpointUris(new Uri(options.PublicOrigin, DeviceFlow.PagePath.TrimStart('/')))
                    .AllowDeviceAuthorizationFlow()
                    .AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .AllowClientCredentialsFlow()
                    // Organization and project tokens for signed-in users (TS-598).
                    .AllowTokenExchangeFlow()
                    // The bound GitHub Actions workload only (GitHubActionsBinding creates its client).
                    .AllowCustomFlow(GitHubActionsGrant.GrantType)
                    .RequireProofKeyForCodeExchange()
                    .RegisterScopes(Scopes.Email, Scopes.Profile, Scopes.OfflineAccess)
                    // Each client's scope permissions still bound what it may request; there is no scope store.
                    .DisableScopeValidation()
                    .UseReferenceRefreshTokens()
                    .DisableAccessTokenEncryption()
                    // A refresh token family expires 14 days after its code is redeemed; rotation never extends it.
                    .DisableSlidingRefreshTokenExpiration()
                    .SetRefreshTokenReuseLeeway(null)
                    .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(1))
                    .SetAccessTokenLifetime(TokenLifetime)
                    .SetIdentityTokenLifetime(TokenLifetime)
                    .SetRefreshTokenLifetime(Sessions.Lifetime)
                    .AddSigningKey(new SymmetricSecurityKey(Derive(options, "ts-auth oidc token signing")) { KeyId = "ts-auth-internal" })
                    .AddSigningCredentials(new SigningCredentials(KeyRingPlaceholder(), SecurityAlgorithms.EcdsaSha256))
                    .AddEncryptionKey(new SymmetricSecurityKey(Derive(options, "ts-auth oidc token encryption")) { KeyId = "ts-auth-internal" })
                    .Configure(server => server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain))
                    .RemoveEventHandler(OpenIddictServerHandlers.RedeemTokenEntry.Descriptor)
                    .RemoveEventHandler(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor)
                    .RemoveEventHandler(OpenIddictServerHandlers.Discovery.AttachSigningKeys.Descriptor)
                    .AddEventHandler<ProcessRequestContext>(handler => handler.UseSingletonHandler<TrustPublicOrigin>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ResolveRequestUri.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseSingletonHandler<SkipShortLivedTokenEntries>()
                        .SetOrder(OpenIddictServerHandlers.Protection.CreateTokenEntry.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseScopedHandler<BindIssuedCredential>()
                        .SetOrder(OpenIddictServerHandlers.Protection.CreateTokenEntry.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ValidateTokenContext>(handler => handler.UseScopedHandler<ValidateCredentialLineage>()
                        .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseSingletonHandler<SignWithKeyRing>()
                        .SetOrder(OpenIddictServerHandlers.Protection.AttachSecurityCredentials.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ValidateTokenContext>(handler => handler.UseSingletonHandler<ValidateWithKeyRing>()
                        .SetOrder(OpenIddictServerHandlers.Protection.ResolveTokenValidationParameters.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<HandleJsonWebKeySetRequestContext>(handler => handler.UseSingletonHandler<PublishKeyRing>()
                        .SetOrder(OpenIddictServerHandlers.Discovery.AttachSigningKeys.Descriptor.Order)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ProcessSignInContext>(handler => handler.UseScopedHandler<RedeemExactlyOnce>()
                        .SetOrder(OpenIddictServerHandlers.RedeemTokenEntry.Descriptor.Order)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ProcessSignInContext>(handler => handler.UseScopedHandler<ProjectSessionPolicy>()
                        .SetOrder(OpenIddictServerHandlers.PrepareRefreshTokenPrincipal.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<HandleRevocationRequestContext>(handler => handler.UseScopedHandler<RevokeTheSignIn>()
                        .SetOrder(OpenIddictServerHandlers.Revocation.RevokeToken.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyTokenResponseContext>(handler => handler.UseSingletonHandler<CountTokenResponses>()
                        .SetOrder(int.MinValue)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyTokenResponseContext>(handler => handler.UseSingletonHandler<ApiKeyFailureNotice>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.AttachWwwAuthenticateHeader<ApplyTokenResponseContext>.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyAuthorizationResponseContext>(handler => handler.UseSingletonHandler<RenderLocalErrorPage<ApplyAuthorizationResponseContext>>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ProcessLocalErrorResponse<ApplyAuthorizationResponseContext>.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyAuthorizationResponseContext>(handler => handler.UseScopedHandler<ProjectRedirectPolicy>()
                        .SetOrder(OpenIddictServerHandlers.Authentication.AttachRedirectUri.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyEndSessionResponseContext>(handler => handler.UseScopedHandler<ProjectRedirectPolicy>()
                        .SetOrder(OpenIddictServerHandlers.Session.AttachPostLogoutRedirectUri.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyEndSessionResponseContext>(handler => handler.UseSingletonHandler<RenderLocalErrorPage<ApplyEndSessionResponseContext>>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ProcessLocalErrorResponse<ApplyEndSessionResponseContext>.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ValidateEndUserVerificationRequestContext>(handler => handler.UseScopedHandler<DeviceGate>()
                        .SetOrder(OpenIddictServerHandlers.Device.ValidateVerificationAuthentication.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyEndUserVerificationResponseContext>(handler => handler.UseSingletonHandler<RenderDeviceDecision>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ProcessHostRedirectionResponse.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyDeviceAuthorizationResponseContext>(handler => handler.UseSingletonHandler<AnnouncePollInterval>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ProcessJsonResponse<ApplyDeviceAuthorizationResponseContext>.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ProcessAuthenticationContext>(handler => handler.UseScopedHandler<PollNoFasterThanInterval>()
                        .SetOrder(OpenIddictServerHandlers.ValidateDeviceCode.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom));
                server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    .EnableEndUserVerificationEndpointPassthrough()
                    // TLS terminates in front of the service; AuthOptions requires an HTTPS public origin in production.
                    .DisableTransportSecurityRequirement();
            });
        return services;
    }

    private static byte[] Derive(AuthOptions options, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: System.Text.Encoding.UTF8.GetBytes(purpose));

    // OpenIddict requires an asymmetric signing key at startup and derives the id_token hash algorithm from it. The real
    // keys come from the key ring per request (SignWithKeyRing). This one has no private part, so if it were ever
    // chosen to sign, signing would fail rather than produce a token that no published key verifies.
    private static ECDsaSecurityKey KeyRingPlaceholder()
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new ECDsaSecurityKey(ECDsa.Create(generated.ExportParameters(includePrivateParameters: false))) { KeyId = "ts-auth-key-ring" };
    }
}

/// <summary>
/// Resolves OpenIddict's base and request URIs against the configured public origin instead of the request's Host
/// header and scheme, so discovery, issuer checks and endpoint matching never trust request headers. The base URI is the
/// tenant's issuer, as <see cref="Tenancy"/> resolved it: <c>terrascale</c> for every endpoint, an existing project for
/// its discovery document, JWKS and authorization-code endpoints. Any other request gets the bare origin, under which no endpoint matches, so it
/// falls through to routing and answers 404.
/// </summary>
public sealed class TrustPublicOrigin(AuthOptions options) : IOpenIddictServerHandler<ProcessRequestContext>
{
    /// <summary>The transaction property holding the request's tenant.</summary>
    public const string TenantProperty = "ts-auth:tenant";

    /// <inheritdoc />
    public ValueTask HandleAsync(ProcessRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Transaction.GetHttpRequest() ?? throw new InvalidOperationException("OpenIddict ran outside an HTTP request.");
        var host = new HostString(options.PublicOrigin.Authority);
        var tenant = TenantOf(request);
        var basePath = tenant is null ? request.PathBase : request.PathBase.Add(new PathString("/" + tenant));
        context.BaseUri = Uri.TryCreate(UriHelper.BuildAbsolute(options.PublicOrigin.Scheme, host, basePath), UriKind.Absolute, out var baseUri) ? baseUri : null;
        context.RequestUri = Uri.TryCreate(UriHelper.BuildAbsolute(options.PublicOrigin.Scheme, host, request.PathBase, request.Path, request.QueryString), UriKind.Absolute, out var requestUri) ? requestUri : null;
        context.Transaction.SetProperty(TenantProperty, tenant);
        return ValueTask.CompletedTask;
    }

    private static string? TenantOf(HttpRequest request)
    {
        // The hosted device page is the platform issuer's end-user verification endpoint: the device codes it approves
        // carry the issuer the token endpoint validates. No other hosted page is a protocol endpoint.
        if (request.Path.StartsWithSegments("/ui", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(request.Path.Value?.TrimEnd('/'), DeviceFlow.PagePath, StringComparison.OrdinalIgnoreCase) ? OidcIssuer.Slug : null;
        }

        return Tenancy.Resolved(request.HttpContext);
    }
}

/// <summary>Signs access and identity tokens with the instance's current key-ring key.</summary>
public sealed class SignWithKeyRing(KeyRing ring) : IOpenIddictServerHandler<GenerateTokenContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TokenType is TokenTypeIdentifiers.AccessToken or TokenTypeIdentifiers.IdentityToken)
        {
            var keys = await KeyRingKeys.LoadAsync(ring, context.Transaction, publicOnly: false, context.CancellationToken).ConfigureAwait(false);
            context.SigningCredentials = new SigningCredentials(keys[0].SecurityKey, SecurityAlgorithms.EcdsaSha256);
        }
    }
}

/// <summary>
/// Validates access and identity tokens against every key in the instance's ring, read on each request so every
/// replica sees replicated rotations. The startup placeholder never validates anything.
/// </summary>
public sealed class ValidateWithKeyRing(KeyRing ring) : IOpenIddictServerHandler<ValidateTokenContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var internalKeys = context.Options.SigningCredentials.Select(credentials => credentials.Key).OfType<SymmetricSecurityKey>();
        if (context.ValidTokenTypes.Count > 0 && !context.ValidTokenTypes.Overlaps([TokenTypeIdentifiers.AccessToken, TokenTypeIdentifiers.IdentityToken]))
        {
            context.TokenValidationParameters.IssuerSigningKeys = [.. internalKeys];
            return;
        }

        var keys = await KeyRingKeys.LoadAsync(ring, context.Transaction, publicOnly: true, context.CancellationToken, Jwt.KeyId(context.Token)).ConfigureAwait(false);
        context.TokenValidationParameters.IssuerSigningKeys = [.. internalKeys, .. keys.Select(key => key.SecurityKey)];
    }
}

/// <summary>Publishes the public half of every key in the instance's ring as the JWKS.</summary>
public sealed class PublishKeyRing(KeyRing ring) : IOpenIddictServerHandler<HandleJsonWebKeySetRequestContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(HandleJsonWebKeySetRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenant = context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) ?? throw new InvalidOperationException("The request has no tenant.");
        // This region's copy, read apart from the keys signature checks use (KeyRing.GetPublishedAsync).
        foreach (var key in await ring.GetPublishedAsync(tenant, context.CancellationToken).ConfigureAwait(false))
        {
            using (key)
            {
                context.Keys.Add(key.PublicJwk());
            }
        }
    }
}

/// <summary>
/// Replaces OpenIddict's redemption step, which ignores a lost redemption for refresh tokens. Here a code, device code,
/// device user code or refresh token is redeemed by a conditional write on the version this request validated, so of any number of concurrent
/// redemptions one succeeds locally. Replicated winner claims reject unused losing descendants without ending the
/// winning grant. Authenticated replay of a consumed refresh token or authorization code ends that grant,
/// including when its ancestry lost or a concurrent redemption just won.
/// </summary>
public sealed class RedeemExactlyOnce(IOpenIddictTokenStore<OidcToken> tokens, IOpenIddictAuthorizationStore<OidcAuthorization> authorizations, AuthStore store, TimeProvider clock) : IOpenIddictServerHandler<ProcessSignInContext>
{
    internal const string DeviceAuthorizationClaim = "ts_auth_device_authorization";

    /// <inheritdoc />
    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Transaction.SetProperty(CredentialLineage.BindingsProperty, CredentialLineage.Read(context.Principal!));
        if (context.EndpointType is not (OpenIddictServerEndpointType.Token or OpenIddictServerEndpointType.EndUserVerification))
        {
            return;
        }

        var authentication = context.Transaction.GetProperty<ProcessAuthenticationContext>(typeof(ProcessAuthenticationContext).FullName!)
            ?? throw new InvalidOperationException("The authentication context is missing.");
        var principal = context.EndpointType is OpenIddictServerEndpointType.EndUserVerification ? authentication.UserCodePrincipal
            : context.Request.IsAuthorizationCodeGrantType() ? authentication.AuthorizationCodePrincipal
            : context.Request.IsDeviceCodeGrantType() ? authentication.DeviceCodePrincipal
            : context.Request.IsRefreshTokenGrantType() ? authentication.RefreshTokenPrincipal
            : null;
        if (principal?.GetTokenId() is not { Length: > 0 } identifier)
        {
            return;
        }

        var token = await tokens.FindByIdAsync(identifier, context.CancellationToken).ConfigureAwait(false);
        if (token is not { Status: Statuses.Valid, Version: not null })
        {
            await this.RejectAsync(context, principal, token).ConfigureAwait(false);
            return;
        }

        var tenant = context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) ?? throw new InvalidOperationException("The request has no tenant.");
        var inherited = token.Bindings.Concat(CredentialLineage.Read(context.Principal!)).Distinct().ToArray();
        var addsBinding = context.EndpointType is not OpenIddictServerEndpointType.EndUserVerification;
        var binding = AuthStore.NewCredentialBinding("oidc", identifier);
        List<StoreOperation> operations =
        [
            AuthStore.CredentialClaim(tenant, binding),
            AuthStore.Replace(AuthStore.OidcTokenCollection, tenant, identifier,
                Redeemed(token, clock.GetUtcNow()), token.Version),
        ];
        string? approvedAuthorizationId = null;
        if (!addsBinding)
        {
            if (principal.GetAuthorizationId() is not { Length: > 0 } parentId
                || await authorizations.FindByIdAsync(parentId, context.CancellationToken).ConfigureAwait(false) is not { Status: Statuses.Valid } parent
                || context.Principal!.GetClaim(Claims.Subject) is not { Length: > 0 } subject)
            {
                context.Reject(Errors.InvalidGrant, CredentialLineage.ConflictNotice);
                return;
            }

            inherited = [.. inherited.Append(AuthStore.AuthorizationBinding(parentId)).Distinct()];
            var approval = new OidcAuthorization
            {
                Id = AuthStore.NewId(),
                InstanceId = tenant,
                ApplicationId = parent.ApplicationId,
                CreationDate = clock.GetUtcNow(),
                Properties = parent.Properties,
                Scopes = [.. context.Principal!.GetScopes()],
                Status = Statuses.Valid,
                Subject = subject,
                Type = AuthorizationTypes.AdHoc,
            };
            approvedAuthorizationId = approval.Id;
            using var valid = JsonDocument.Parse("""{"eq":["/status","valid"]}""");
            operations.Add(AuthStore.Require(AuthStore.OidcAuthorizationCollection, tenant, parentId, valid.RootElement.Clone()));
            operations.Add(AuthStore.Create(AuthStore.OidcAuthorizationCollection, tenant, approval.Id,
                JsonSerializer.SerializeToElement(approval, OidcJson.Default.OidcAuthorization)));
        }
        else if (context.Request.IsDeviceCodeGrantType())
        {
            if (principal.GetClaim(DeviceAuthorizationClaim) is not { Length: > 0 } approvalId
                || await authorizations.FindByIdAsync(approvalId, context.CancellationToken).ConfigureAwait(false) is not { Status: Statuses.Valid } approval
                || approval.Subject != context.Principal!.GetClaim(Claims.Subject) || approval.ApplicationId != token.ApplicationId)
            {
                context.Reject(Errors.InvalidGrant, CredentialLineage.ConflictNotice);
                return;
            }

            approvedAuthorizationId = approvalId;
        }

        if (inherited.Length + (addsBinding ? 1 : 0) > AuthStore.MaxCredentialBindings)
        {
            context.Reject(Errors.InvalidGrant, "This sign-in reached its rotation limit. Sign in again to continue.");
            return;
        }

        if (await store.AreCredentialsCurrentAsync(tenant, inherited, context.CancellationToken).ConfigureAwait(false)
            && await store.TransactAsync(operations, context.CancellationToken).ConfigureAwait(false) is null)
        {
            // Approval rewrites the existing device-code record; that record's LWW payload selects the approver.
            // Only redemption that creates distinct successor credentials needs the additional branch binding.
            var bindings = addsBinding ? [.. inherited, binding] : inherited;
            CredentialLineage.Attach(context.Principal!, bindings);
            context.Transaction.SetProperty(CredentialLineage.BindingsProperty, (IReadOnlyList<CredentialBinding>)bindings);
            if (!addsBinding)
            {
                // The encrypted payload selects its own subject-bound authorization; the shared parent stays unchanged.
                context.Principal!.SetClaim(DeviceAuthorizationClaim, approvedAuthorizationId);
            }
            else if (approvedAuthorizationId is not null)
            {
                context.Principal!.SetAuthorizationId(approvedAuthorizationId);
            }

            return;
        }

        // Redeemed() mutates our local object before the CAS. Only a fresh stored read can prove another use won.
        await this.RejectAsync(context, principal, await tokens.FindByIdAsync(identifier, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async ValueTask RejectAsync(ProcessSignInContext context, ClaimsPrincipal principal, OidcToken? token)
    {
        var replay = context.EndpointType is OpenIddictServerEndpointType.Token && context.Request.IsRefreshTokenGrantType()
            && await CredentialLineage.RevokeRefreshReplayAsync(tokens, token, principal, context.Request.ClientId,
                context.CancellationToken).ConfigureAwait(false);
        var codeReplay = !replay && await AuthorizationCodeReplay.EndGrantAsync(context, principal, store, tokens).ConfigureAwait(false);
        context.Reject(Errors.InvalidGrant, replay ? CredentialLineage.ReplayNotice
            : codeReplay ? AuthorizationCodeReplay.Notice : CredentialLineage.ConflictNotice);
    }

    private static JsonElement Redeemed(OidcToken token, DateTimeOffset now)
    {
        token.Status = Statuses.Redeemed;
        token.RedemptionDate = now;
        return JsonSerializer.SerializeToElement(token, OidcJson.Default.OidcToken);
    }
}

/// <summary>
/// Makes revocation (RFC 7009) end the whole sign-in, as §2.1 recommends: revoking a refresh or access token revokes
/// its authorization, so every refresh token of that sign-in is refused and ts-auth stops accepting its access tokens.
/// Access tokens validated offline, as TerraBase does, stay valid until they expire (at most 5 minutes).
/// </summary>
public sealed class RevokeTheSignIn(IOpenIddictTokenManager tokens) : IOpenIddictServerHandler<HandleRevocationRequestContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(HandleRevocationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GenericTokenPrincipal?.GetAuthorizationId() is { Length: > 0 } authorizationId)
        {
            await tokens.RevokeByAuthorizationIdAsync(authorizationId, context.CancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Registers or updates the public ts-status and ts-cli clients, the ts-dashboard client (<c>TS_AUTH_DASHBOARD_*</c>) and the service clients
/// (<c>TS_AUTH_SERVICE_CLIENTS</c>). The storage bootstrap (<see cref="SigningKeys.SigningKeyRotation"/>) runs it and retries
/// until storage answers, so ts-auth starts without TerraBase and stays not ready until they are registered. Concurrent
/// replicas converge through conditional writes. Clients removed from configuration stay registered.
/// </summary>
public sealed class ClientRegistration(IServiceScopeFactory scopes, AuthOptions options)
{
    /// <summary>Registers the status client and every configured client; storage failures are retried by bootstrap.</summary>
    public async Task RegisterAllAsync(CancellationToken cancellationToken)
    {
        var descriptors = options.ServiceClients.Select(Service).ToList();
        descriptors.Add(Status());
        descriptors.Add(Cli());
        if (options.DashboardClient is { } dashboard)
        {
            descriptors.Add(Dashboard(dashboard));
        }

        foreach (var descriptor in descriptors)
        {
            await this.RegisterAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
    }

    private static OpenIddictApplicationDescriptor Status() => new()
    {
        ClientId = OidcIssuer.StatusClientId,
        ClientType = ClientTypes.Public,
        ApplicationType = ApplicationTypes.Web,
        ConsentType = ConsentTypes.Implicit,
        DisplayName = "TerraScale Status",
        RedirectUris = { new Uri(OidcIssuer.StatusRedirectUri) },
        Permissions =
        {
            Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode, Permissions.ResponseTypes.Code,
            Permissions.Scopes.Email, Permissions.Scopes.Profile,
        },
        Requirements = { Requirements.Features.ProofKeyForCodeExchange },
    };

    // The ts CLI: a public device-flow client. Its sign-in tokens are exchanged for organization and project tokens (TS-598).
    private static OpenIddictApplicationDescriptor Cli()
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = OidcIssuer.CliClientId,
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Native,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = "ts-cli",
            Permissions =
            {
                Permissions.Endpoints.DeviceAuthorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.DeviceCode, Permissions.GrantTypes.RefreshToken, Permissions.GrantTypes.TokenExchange,
                Permissions.Scopes.Email, Permissions.Scopes.Profile,
            },
        };
        descriptor.Permissions.UnionWith(TerraBaseScopes.All.Select(scope => Permissions.Prefixes.Scope + scope));
        return descriptor;
    }

    private static OpenIddictApplicationDescriptor Dashboard(DashboardClientOptions dashboard)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = OidcIssuer.DashboardClientId,
            ClientSecret = dashboard.Secret,
            ClientType = ClientTypes.Confidential,
            ApplicationType = ApplicationTypes.Web,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "TerraScale Dashboard",
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                // Organization and project tokens for its signed-in users (TS-598).
                Permissions.GrantTypes.TokenExchange,
                Permissions.Scopes.Email, Permissions.Scopes.Profile,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        descriptor.Permissions.UnionWith(TerraBaseScopes.All.Select(scope => Permissions.Prefixes.Scope + scope));
        descriptor.RedirectUris.UnionWith(dashboard.RedirectUris);
        descriptor.PostLogoutRedirectUris.UnionWith(dashboard.PostLogoutRedirectUris);
        return descriptor;
    }

    // Only the token endpoint and the client-credentials grant: no scopes, so no identity or refresh tokens.
    private static OpenIddictApplicationDescriptor Service(ServiceClientOptions client) => new()
    {
        ClientId = client.ClientId,
        ClientSecret = client.Secret,
        ClientType = ClientTypes.Confidential,
        ApplicationType = ApplicationTypes.Web,
        ConsentType = ConsentTypes.Implicit,
        DisplayName = client.ClientId,
        Permissions = { Permissions.Endpoints.Token, Permissions.GrantTypes.ClientCredentials },
    };

    private async Task RegisterAsync(OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // A fresh scope per attempt: the manager's scoped cache would otherwise keep answering the first lookup.
            await using var scope = scopes.CreateAsyncScope();
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            try
            {
                if (await applications.FindByClientIdAsync(descriptor.ClientId!, cancellationToken).ConfigureAwait(false) is { } existing)
                {
                    await applications.UpdateAsync(existing, descriptor, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await applications.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (Exception error) when (error is OpenIddictExceptions.ConcurrencyException
                || error is TerraBasePreconditionFailedException { ErrorCode: "precondition_failed" }
                // OpenIddict's own duplicate-client check fires when another replica created the client in between;
                // a descriptor that is invalid for any other reason fails again on the last attempt and propagates.
                || error is OpenIddictExceptions.ValidationException && attempt < 2)
            {
                // Another replica registered or updated the client first: reread it and apply this configuration.
            }
        }

        throw ApiException.Unavailable();
    }
}

/// <summary>Loads public and signing material separately per request; keys are disposed with the response.</summary>
internal static class KeyRingKeys
{
    public static async Task<IReadOnlyList<Es256Key>> LoadAsync(KeyRing ring, OpenIddictServerTransaction transaction, bool publicOnly, CancellationToken cancellationToken, string? keyId = null)
    {
        var property = publicOnly ? "ts-auth:public-key-ring" : "ts-auth:signing-key-ring";
        if (transaction.GetProperty<IReadOnlyList<Es256Key>>(property) is { } loaded)
        {
            return loaded;
        }

        var tenant = transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) ?? throw new InvalidOperationException("The request has no tenant.");
        var keys = publicOnly
            ? await ring.GetPublicAsync(tenant, keyId, cancellationToken).ConfigureAwait(false)
            : await ring.GetActiveAsync(tenant, cancellationToken).ConfigureAwait(false);
        var response = transaction.GetHttpRequest()?.HttpContext.Response ?? throw new InvalidOperationException("OpenIddict ran outside an HTTP request.");
        foreach (var key in keys)
        {
            response.RegisterForDispose(key);
        }

        transaction.SetProperty(property, keys);
        return keys;
    }
}
