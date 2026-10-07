using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Microsoft.AspNetCore;
using OpenIddict.Server.AspNetCore;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Platform;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Security;
using TerraScale.DotNet.TerraBase;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Auth.Features.Oidc;

/// <summary>
/// The single hosted OpenID Connect issuer. There is no instance registry yet, so one instance serves the platform:
/// issuer <c>{TS_AUTH_PUBLIC_ORIGIN}/terrascale</c>, with every protocol route under <c>/terrascale/</c>.
/// </summary>
public static class OidcIssuer
{
    /// <summary>The issuer's path segment.</summary>
    public const string Slug = "terrascale";

    /// <summary>The instance that owns the issuer's users, sessions, keys, clients and tokens.</summary>
    public const string InstanceId = "terrascale";

    /// <summary>The client id of the ts-dashboard client registered from <c>TS_AUTH_DASHBOARD_*</c>.</summary>
    public const string DashboardClientId = "ts-dashboard";

    /// <summary>
    /// The user's authentication epoch, in codes, refresh tokens and sign-in access tokens: a grant or a management call
    /// whose epoch is not the user's current one (after sign-out everywhere or a password reset) is refused.
    /// </summary>
    public const string EpochClaim = "ts_auth_epoch";

    /// <summary>Lifetime of access and identity tokens; within the key ring's <c>Jwt.MaxLifetime</c>.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    /// <summary>The issuer, derived only from the configured public origin.</summary>
    public static Uri Issuer(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Uri(options.PublicOrigin, Slug);
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
        // OpenIddict writes raw tokens only at Trace and Debug; never let a verbose log level enable those.
        services.Configure<LoggerFilterOptions>(filters => filters.AddFilter("OpenIddict", LogLevel.Information));
        services.AddHostedService<ClientRegistration>();
        services.AddOpenIddict()
            .AddCore(core =>
            {
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
                server.SetIssuer(Issuer(options))
                    .SetConfigurationEndpointUris($"{Slug}/.well-known/openid-configuration")
                    .SetJsonWebKeySetEndpointUris($"{Slug}/.well-known/jwks.json")
                    .SetAuthorizationEndpointUris($"{Slug}/oauth/authorize")
                    .SetTokenEndpointUris($"{Slug}/oauth/token")
                    .SetUserInfoEndpointUris($"{Slug}/oauth/userinfo")
                    .SetEndSessionEndpointUris($"{Slug}/oauth/logout")
                    .SetRevocationEndpointUris($"{Slug}/oauth/revoke")
                    .AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .AllowClientCredentialsFlow()
                    // Organization and project tokens for signed-in users (TS-598).
                    .AllowTokenExchangeFlow()
                    // The headless grants (TS-582) are permitted only to ts-dashboard; see ClientRegistration.
                    .AllowCustomFlow(HeadlessGrants.Password)
                    .AllowCustomFlow(HeadlessGrants.EmailCode)
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
                    .RemoveEventHandler(OpenIddictServerHandlers.Discovery.AttachSigningKeys.Descriptor)
                    .AddEventHandler<ProcessRequestContext>(handler => handler.UseSingletonHandler<TrustPublicOrigin>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ResolveRequestUri.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseSingletonHandler<SkipShortLivedTokenEntries>()
                        .SetOrder(OpenIddictServerHandlers.Protection.CreateTokenEntry.Descriptor.Order - 1)
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
                    .AddEventHandler<HandleRevocationRequestContext>(handler => handler.UseScopedHandler<RevokeTheSignIn>()
                        .SetOrder(OpenIddictServerHandlers.Revocation.RevokeToken.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom));
                server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
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
/// header and scheme, so discovery, issuer checks and endpoint matching never trust request headers.
/// </summary>
public sealed class TrustPublicOrigin(AuthOptions options) : IOpenIddictServerHandler<ProcessRequestContext>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(ProcessRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Transaction.GetHttpRequest() ?? throw new InvalidOperationException("OpenIddict ran outside an HTTP request.");
        var host = new HostString(options.PublicOrigin.Authority);
        context.BaseUri = Uri.TryCreate(UriHelper.BuildAbsolute(options.PublicOrigin.Scheme, host, request.PathBase), UriKind.Absolute, out var baseUri) ? baseUri : null;
        context.RequestUri = Uri.TryCreate(UriHelper.BuildAbsolute(options.PublicOrigin.Scheme, host, request.PathBase, request.Path, request.QueryString), UriKind.Absolute, out var requestUri) ? requestUri : null;
        return ValueTask.CompletedTask;
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
            var keys = await KeyRingKeys.LoadAsync(ring, context.Transaction, context.CancellationToken).ConfigureAwait(false);
            context.SigningCredentials = new SigningCredentials(keys[0].SecurityKey, SecurityAlgorithms.EcdsaSha256);
        }
    }
}

/// <summary>
/// Validates access and identity tokens against every key in the instance's ring, read on each request so every
/// replica sees a rotation at once. The startup placeholder never validates anything.
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

        var keys = await KeyRingKeys.LoadAsync(ring, context.Transaction, context.CancellationToken).ConfigureAwait(false);
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
        foreach (var key in await KeyRingKeys.LoadAsync(ring, context.Transaction, context.CancellationToken).ConfigureAwait(false))
        {
            context.Keys.Add(key.PublicJwk());
        }
    }
}

/// <summary>
/// Replaces OpenIddict's redemption step, which ignores a lost redemption for refresh tokens. Here a code or refresh
/// token is redeemed by a conditional write on the version this request validated, so of any number of concurrent
/// redemptions exactly one succeeds. A loser is treated as reuse: the authorization is revoked, which rejects every
/// token issued under it, the winner's new ones included, wherever ts-auth validates them.
/// </summary>
public sealed class RedeemExactlyOnce(IOpenIddictTokenManager tokens) : IOpenIddictServerHandler<ProcessSignInContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EndpointType is not OpenIddictServerEndpointType.Token)
        {
            return;
        }

        var authentication = context.Transaction.GetProperty<ProcessAuthenticationContext>(typeof(ProcessAuthenticationContext).FullName!)
            ?? throw new InvalidOperationException("The authentication context is missing.");
        var principal = context.Request.IsAuthorizationCodeGrantType() ? authentication.AuthorizationCodePrincipal
            : context.Request.IsRefreshTokenGrantType() ? authentication.RefreshTokenPrincipal
            : null;
        if (principal?.GetTokenId() is not { Length: > 0 } identifier)
        {
            return;
        }

        // The request-scoped entity cache returns the copy validation read, so the write fails if anything changed it.
        var token = await tokens.FindByIdAsync(identifier, context.CancellationToken).ConfigureAwait(false);
        if (token is not null && await tokens.HasStatusAsync(token, Statuses.Valid, context.CancellationToken).ConfigureAwait(false)
            && await tokens.TryRedeemAsync(token, context.CancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (principal.GetAuthorizationId() is { Length: > 0 } authorizationId)
        {
            await tokens.RevokeByAuthorizationIdAsync(authorizationId, context.CancellationToken).ConfigureAwait(false);
        }

        context.Reject(Errors.InvalidGrant, context.Request.IsAuthorizationCodeGrantType()
            ? "The authorization code has already been redeemed."
            : "The refresh token has already been redeemed.");
    }
}

/// <summary>
/// Makes revocation (RFC 7009) end the whole sign-in, as §2.1 recommends: revoking a refresh or access token revokes
/// its authorization, so every refresh token of that sign-in is refused and ts-auth stops accepting its access tokens.
/// Access tokens validated offline, as TerraBase does, stay valid until they expire (at most 1 hour).
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
/// Registers or updates the ts-dashboard client (<c>TS_AUTH_DASHBOARD_*</c>) and the service clients
/// (<c>TS_AUTH_SERVICE_CLIENTS</c>) at startup. Concurrent replicas converge through conditional writes; a storage
/// failure stops startup instead of serving without them. Clients removed from configuration stay registered.
/// </summary>
public sealed class ClientRegistration(IServiceScopeFactory scopes, AuthOptions options) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var descriptors = options.ServiceClients.Select(Service).ToList();
        if (options.DashboardClient is { } dashboard)
        {
            descriptors.Add(Dashboard(dashboard));
        }

        if (descriptors.Count == 0)
        {
            return;
        }

        foreach (var descriptor in descriptors)
        {
            await this.RegisterAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
                // The first-party headless API (TS-582); the email-code grant also gates its JSON endpoints.
                Permissions.Prefixes.GrantType + HeadlessGrants.Password, Permissions.Prefixes.GrantType + HeadlessGrants.EmailCode,
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

/// <summary>Loads the instance's ring once per request; the keys are disposed with the response.</summary>
internal static class KeyRingKeys
{
    public static async Task<IReadOnlyList<Es256Key>> LoadAsync(KeyRing ring, OpenIddictServerTransaction transaction, CancellationToken cancellationToken)
    {
        const string property = "ts-auth:key-ring";
        if (transaction.GetProperty<IReadOnlyList<Es256Key>>(property) is { } loaded)
        {
            return loaded;
        }

        var keys = await ring.GetActiveAsync(OidcIssuer.InstanceId, cancellationToken).ConfigureAwait(false);
        var response = transaction.GetHttpRequest()?.HttpContext.Response ?? throw new InvalidOperationException("OpenIddict ran outside an HTTP request.");
        foreach (var key in keys)
        {
            response.RegisterForDispose(key);
        }

        transaction.SetProperty(property, keys);
        return keys;
    }
}
