using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Server;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Impersonation;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;
using TerraScale.DotNet;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

public static class OidcIssuer
{
    public const string Slug = "terrascale";

    public const string InstanceId = "terrascale";

    public const string DashboardClientId = "01M4EQ2FRJ8S8B29E8TQVWNW4J";

    public const string StatusClientId = "00000000000000000000000006";

    public const string StatusRedirectUri = "https://status.terrascale.tech/admin/callback";

    public const string CliClientId = "00000000000000000000000007";

    public const string EpochClaim = "ts_auth_epoch";

    public const string StateClaim = "ts_auth_state";

    public const string MfaClaim = "ts_auth_mfa";

    public const string OrganizationPickerClaim = "ts_org_picker";

    public const string ConfigurationPath = ".well-known/openid-configuration";

    public const string JwksPath = ".well-known/jwks.json";

    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    public static Uri Issuer(AuthOptions options) => Issuer(options, Slug);

    public static Uri Issuer(AuthOptions options, string tenant)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(tenant);
        return new Uri(options.PublicOrigin, tenant);
    }

    public static IReadOnlyList<string> SignInAudiences(AuthOptions options) => [Issuer(options).AbsoluteUri, .. options.AccessTokenAudiences];

    public static IServiceCollection AddOidc(this IServiceCollection services, AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<ClientRegistration>();
        services.AddHttpContextAccessor();
        services.AddOpenIddict()
            .AddCore(core =>
            {
                core.DisableEntityCaching();
                core.SetDefaultApplicationEntity<OidcApplication>()
                    .SetDefaultAuthorizationEntity<OidcAuthorization>()
                    .SetDefaultTokenEntity<OidcToken>()
                    .ReplaceApplicationManager<OidcApplication, ApiKeyApplicationManager>()
                    .ReplaceApplicationStore<OidcApplication, OidcApplicationStore>(ServiceLifetime.Singleton)
                    .ReplaceAuthorizationStore<OidcAuthorization, OidcAuthorizationStore>(ServiceLifetime.Singleton)
                    .ReplaceTokenStore<OidcToken, OidcTokenStore>(ServiceLifetime.Singleton);
                services.AddSingleton<OidcAuthorizationStore>();
                services.AddSingleton<OidcTokenStore>();
                services.Replace(ServiceDescriptor.Singleton<IOpenIddictAuthorizationStore<OidcAuthorization>>(static sp => sp.GetRequiredService<OidcAuthorizationStore>()));
                services.Replace(ServiceDescriptor.Singleton<IOpenIddictTokenStore<OidcToken>>(static sp => sp.GetRequiredService<OidcTokenStore>()));
            })
            .AddServer(server =>
            {
                server.SetConfigurationEndpointUris(ConfigurationPath)
                    .SetJsonWebKeySetEndpointUris(JwksPath)
                    .SetAuthorizationEndpointUris("oauth/authorize")
                    .SetTokenEndpointUris("oauth/token", GuestSignIn.Route)
                    .SetUserInfoEndpointUris("oauth/userinfo")
                    .SetEndSessionEndpointUris("oauth/logout")
                    .SetRevocationEndpointUris("oauth/revoke")
                    .SetDeviceAuthorizationEndpointUris("oauth/device")
                    .SetEndUserVerificationEndpointUris(new Uri(options.PublicOrigin, DeviceFlow.PagePath.TrimStart('/')))
                    .AllowDeviceAuthorizationFlow()
                    .AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .AllowClientCredentialsFlow()
                    .AllowTokenExchangeFlow()
                    .AllowCustomFlow(GitHubActionsGrant.GrantType)
                    .AllowCustomFlow(GuestSignIn.GrantType)
                    .AllowCustomFlow(AgentExchange.GrantType)
                    .RequireProofKeyForCodeExchange()
                    .RegisterScopes(Scopes.Email, Scopes.Profile, Scopes.OfflineAccess)
                    .DisableScopeValidation()
                    .RegisterClaims(Claims.Email, Claims.EmailVerified, Claims.AuthenticationTime)
                    .UseReferenceRefreshTokens()
                    .DisableAccessTokenEncryption()
                    .DisableSlidingRefreshTokenExpiration()
                    .SetRefreshTokenReuseLeeway(null)
                    .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(1))
                    .SetDeviceCodeLifetime(DeviceFlow.Lifetime)
                    .SetUserCodeLifetime(DeviceFlow.Lifetime)
                    .SetAccessTokenLifetime(TokenLifetime)
                    .SetIdentityTokenLifetime(TokenLifetime)
                    .SetRefreshTokenLifetime(Sessions.Lifetime)
                    .AddSigningKey(new SymmetricSecurityKey(Derive(options, "ts-auth oidc token signing")) { KeyId = "ts-auth-internal" })
                    .AddSigningCredentials(new SigningCredentials(KeyRingPlaceholder(), SecurityAlgorithms.EcdsaSha256))
                    .AddEncryptionKey(new SymmetricSecurityKey(Derive(options, "ts-auth oidc token encryption")) { KeyId = "ts-auth-internal" })
                    .Configure(server =>
                    {
                        server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                        server.PromptValues.Remove(PromptValues.Consent);
                        server.PromptValues.Remove(PromptValues.SelectAccount);
                    })
                    .RemoveEventHandler(OpenIddictServerHandlers.Exchange.ValidateResources.Descriptor)
                    .AddEventHandler<ValidateTokenRequestContext>(handler => handler.UseScopedHandler<MachineTokenResources>()
                        .SetOrder(OpenIddictServerHandlers.Exchange.ValidateResources.Descriptor.Order)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .RemoveEventHandler(OpenIddictServerHandlers.RedeemTokenEntry.Descriptor)
                    .RemoveEventHandler(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor)
                    .RemoveEventHandler(OpenIddictServerHandlers.Discovery.AttachSigningKeys.Descriptor)
                    .AddEventHandler<ProcessRequestContext>(handler => handler.UseSingletonHandler<TrustPublicOrigin>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ResolveRequestUri.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseSingletonHandler<SkipShortLivedTokenEntries>()
                        .SetOrder(OpenIddictServerHandlers.Protection.CreateTokenEntry.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ValidateTokenContext>(handler => handler.UseScopedHandler<ValidateCredentialLineage>()
                        .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ValidateTokenContext>(handler => handler.UseScopedHandler<ValidateImpersonationToken>()
                        .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseScopedHandler<LimitImpersonationToken>()
                        .SetOrder(OpenIddictServerHandlers.Protection.CreateTokenEntry.Descriptor.Order - 2)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ValidateTokenContext>(handler => handler.UseSingletonHandler<ValidateAgentOwnerToken>()
                        .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order + 2)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseSingletonHandler<LimitAgentOwnerToken>()
                        .SetOrder(OpenIddictServerHandlers.Protection.CreateTokenEntry.Descriptor.Order - 3)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseSingletonHandler<RefuseUnfencedAgentToken>()
                        .SetOrder(OpenIddictServerHandlers.Protection.CreateTokenEntry.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ExtractTokenRequestContext>(handler => handler.UseSingletonHandler<RouteGuestGrant>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ExtractBasicAuthenticationCredentials<ExtractTokenRequestContext>.Descriptor.Order + 1_000)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ExtractTokenRequestContext>(handler => handler.UseSingletonHandler<RouteAgentGrant>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ExtractBasicAuthenticationCredentials<ExtractTokenRequestContext>.Descriptor.Order + 1_001)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ValidateTokenRequestContext>(handler => handler.UseSingletonHandler<RefuseScopeExpansion>()
                        .SetOrder(OpenIddictServerHandlers.Exchange.ValidateScopePermissions.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseSingletonHandler<SignWithKeyRing>()
                        .SetOrder(OpenIddictServerHandlers.Protection.AttachSecurityCredentials.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseSingletonHandler<EncryptAgentIdAccessTokens>()
                        .SetOrder(OpenIddictServerHandlers.Protection.AttachSecurityCredentials.Descriptor.Order + 2)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<GenerateTokenContext>(handler => handler.UseSingletonHandler<IdentifyAgentIdIdentityTokens>()
                        .SetOrder(OpenIddictServerHandlers.Protection.GenerateIdentityModelToken.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ValidateTokenContext>(handler => handler.UseSingletonHandler<ValidateWithKeyRing>()
                        .SetOrder(OpenIddictServerHandlers.Protection.ResolveTokenValidationParameters.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<HandleJsonWebKeySetRequestContext>(handler => handler.UseSingletonHandler<PublishKeyRing>()
                        .SetOrder(OpenIddictServerHandlers.Discovery.AttachSigningKeys.Descriptor.Order)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<HandleConfigurationRequestContext>(handler => handler.UseSingletonHandler<ProjectDiscoveryMetadata>()
                        .SetOrder(OpenIddictServerHandlers.Discovery.AttachAdditionalMetadata.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ProcessSignInContext>(handler => handler.UseScopedHandler<RedeemExactlyOnce>()
                        .SetOrder(OpenIddictServerHandlers.RedeemTokenEntry.Descriptor.Order)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ProcessSignInContext>(handler => handler.UseScopedHandler<ProjectSessionPolicy>()
                        .SetOrder(OpenIddictServerHandlers.PrepareRefreshTokenPrincipal.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ProcessSignInContext>(handler => handler.UseSingletonHandler<KeepFixedRefreshToken>()
                        .SetOrder(OpenIddictServerHandlers.EvaluateGeneratedTokens.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ProcessSignInContext>(handler => handler.UseSingletonHandler<ExpireFixedRefreshToken>()
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
                    .AddEventHandler<ApplyTokenResponseContext>(handler => handler.UseSingletonHandler<ReportCodeReplayNotice>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.AttachWwwAuthenticateHeader<ApplyTokenResponseContext>.Descriptor.Order - 2)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyAuthorizationResponseContext>(handler => handler.UseSingletonHandler<RenderLocalErrorPage<ApplyAuthorizationResponseContext>>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.ProcessLocalErrorResponse<ApplyAuthorizationResponseContext>.Descriptor.Order - 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyAuthorizationResponseContext>(handler => handler.UseScopedHandler<ProjectRedirectPolicy>()
                        .SetOrder(OpenIddictServerHandlers.Authentication.AttachRedirectUri.Descriptor.Order + 1)
                        .SetType(OpenIddictServerHandlerType.Custom))
                    .AddEventHandler<ApplyAuthorizationResponseContext>(handler => handler.UseSingletonHandler<FormPostSecurityPolicy>()
                        .SetOrder(OpenIddictServerAspNetCoreHandlers.Authentication.ProcessFormPostResponse.Descriptor.Order - 1)
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
                    .DisableTransportSecurityRequirement();
            });
        return services;
    }

    internal static byte[] Derive(AuthOptions options, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, 32, info: System.Text.Encoding.UTF8.GetBytes(purpose));

    private static ECDsaSecurityKey KeyRingPlaceholder()
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new ECDsaSecurityKey(ECDsa.Create(generated.ExportParameters(includePrivateParameters: false))) { KeyId = "ts-auth-key-ring" };
    }
}

public sealed class TrustPublicOrigin(AuthOptions options) : IOpenIddictServerHandler<ProcessRequestContext>
{
    public const string TenantProperty = "ts-auth:tenant";

    public ValueTask HandleAsync(ProcessRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Transaction.GetHttpRequest() ?? throw new InvalidOperationException("OpenIddict ran outside an HTTP request.");
        var host = new HostString(options.PublicOrigin.Authority);
        var tenant = TenantOf(request);
        if (tenant is null)
        {
            context.SkipRequest();
            return ValueTask.CompletedTask;
        }

        var basePath = request.PathBase.Add(new PathString("/" + tenant));
        context.BaseUri = Uri.TryCreate(UriHelper.BuildAbsolute(options.PublicOrigin.Scheme, host, basePath), UriKind.Absolute, out var baseUri) ? baseUri : null;
        context.RequestUri = Uri.TryCreate(UriHelper.BuildAbsolute(options.PublicOrigin.Scheme, host, request.PathBase, request.Path, request.QueryString), UriKind.Absolute, out var requestUri) ? requestUri : null;
        context.Transaction.SetProperty(TenantProperty, tenant);
        return ValueTask.CompletedTask;
    }

    private static string? TenantOf(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/ui", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(request.Path.Value?.TrimEnd('/'), DeviceFlow.PagePath, StringComparison.OrdinalIgnoreCase) ? OidcIssuer.Slug : null;
        }

        return Tenancy.Resolved(request.HttpContext);
    }
}

public sealed class RefuseScopeExpansion : IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.IsRefreshTokenGrantType() || string.IsNullOrEmpty(context.Request.Scope))
        {
            return ValueTask.CompletedTask;
        }

        var granted = context.RefreshTokenPrincipal is { } refresh ? refresh.GetScopes().ToHashSet(StringComparer.Ordinal) : [];
        if (granted.Count is 0 || granted.IsSupersetOf(context.Request.GetScopes()))
        {
            return ValueTask.CompletedTask;
        }

        context.Reject(OidcErrors.InvalidScope, "The requested scope exceeds the original authorization.");
        return ValueTask.CompletedTask;
    }
}

public sealed class SignWithKeyRing(KeyRing ring) : IOpenIddictServerHandler<GenerateTokenContext>
{
    public async ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TokenType is TokenTypeIdentifiers.AccessToken or TokenTypeIdentifiers.IdentityToken)
        {
            var keys = await KeyRingKeys.LoadAsync(ring, context.Transaction, publicOnly: false, context.CancellationToken);
            context.SigningCredentials = new SigningCredentials(keys[0].SecurityKey, SecurityAlgorithms.EcdsaSha256);
        }
    }
}

public sealed class ValidateWithKeyRing(KeyRing ring) : IOpenIddictServerHandler<ValidateTokenContext>
{
    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var internalKeys = context.Options.SigningCredentials.Select(credentials => credentials.Key).OfType<SymmetricSecurityKey>();
        if (context.ValidTokenTypes.Count > 0 && !context.ValidTokenTypes.Overlaps([TokenTypeIdentifiers.AccessToken, TokenTypeIdentifiers.IdentityToken]))
        {
            context.TokenValidationParameters.IssuerSigningKeys = [.. internalKeys];
            return;
        }

        var keys = await KeyRingKeys.LoadAsync(ring, context.Transaction, publicOnly: true, context.CancellationToken, Jwt.KeyId(context.Token));
        context.TokenValidationParameters.IssuerSigningKeys = [.. internalKeys, .. keys.Select(key => key.SecurityKey)];
    }
}

public sealed class PublishKeyRing(KeyRing ring) : IOpenIddictServerHandler<HandleJsonWebKeySetRequestContext>
{
    public async ValueTask HandleAsync(HandleJsonWebKeySetRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenant = context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) ?? throw new InvalidOperationException("The request has no tenant.");
        foreach (var key in await ring.GetPublishedAsync(tenant, context.CancellationToken))
        {
            using (key)
            {
                context.Keys.Add(key.PublicJwk());
            }
        }
    }
}

public sealed class RedeemExactlyOnce(IOpenIddictTokenStore<OidcToken> tokens, IOpenIddictAuthorizationStore<OidcAuthorization> authorizations, AuthData data, Credentials credentials, TimeProvider clock) : IOpenIddictServerHandler<ProcessSignInContext>
{
    internal const string DeviceAuthorizationClaim = "ts_auth_device_authorization";

    internal const string DeviceApprovalKind = "oidc-device-approval";

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

        var token = await tokens.FindByIdAsync(identifier, context.CancellationToken);
        if (token is not { Status: Statuses.Valid, Version: not null })
        {
            await this.RejectAsync(context, principal, token);
            return;
        }

        var tenant = context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) ?? throw new InvalidOperationException("The request has no tenant.");
        var inherited = token.Bindings.Concat(CredentialLineage.Read(context.Principal!)).Distinct().ToArray();
        if (FixedRefreshTokens.Presented(context))
        {
            if (!await credentials.AreCurrentAsync(tenant, inherited, context.CancellationToken))
            {
                context.Reject(OidcErrors.InvalidGrant, CredentialLineage.ConflictNotice);
            }

            return;
        }

        var addsBinding = context.EndpointType is not OpenIddictServerEndpointType.EndUserVerification;
        var binding = Credentials.New("oidc", identifier);
        List<Change> operations =
        [
            Credentials.Claim(tenant, binding),
            Change.Update(Redeemed(token, clock.GetUtcNow())),
        ];
        string? approvedAuthorizationId = null;
        if (!addsBinding)
        {
            if (principal.GetAuthorizationId() is not { Length: > 0 } parentId
                || await authorizations.FindByIdAsync(parentId, context.CancellationToken) is not { Status: Statuses.Valid } parent
                || context.Principal!.GetClaim(Claims.Subject) is not { Length: > 0 } subject)
            {
                context.Reject(OidcErrors.InvalidGrant, CredentialLineage.ConflictNotice);
                return;
            }

            inherited = [.. inherited.Append(Credentials.Authorization(parentId)).Distinct()];
            var approval = new OidcAuthorization
            {
                Id = AuthIds.New(),
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
            operations.Add(Change.Assert(parent));
            operations.Add(Change.Insert(approval));
            operations.Add(Credentials.Claim(tenant, DeviceApprovalKind, approval.Id, parentId));
        }
        else if (context.Request.IsDeviceCodeGrantType())
        {
            if (principal.GetClaim(DeviceAuthorizationClaim) is not { Length: > 0 } approvalId
                || await authorizations.FindByIdAsync(approvalId, context.CancellationToken) is not { Status: Statuses.Valid } approval
                || approval.Subject != context.Principal!.GetClaim(Claims.Subject) || approval.ApplicationId != token.ApplicationId)
            {
                context.Reject(OidcErrors.InvalidGrant, CredentialLineage.ConflictNotice);
                return;
            }

            approvedAuthorizationId = approvalId;
        }

        if (tenant == AgentIdIssuer.Slug)
        {
            if (context.Transaction.GetHttpRequest()?.HttpContext.Items[AgentIdFence.Item] is not AgentIdFence fence)
            {
                context.Reject(OidcErrors.InvalidGrant, "The agent can no longer sign in.");
                return;
            }

            operations.AddRange(fence.Asserts);
        }

        if (inherited.Length + (addsBinding ? 1 : 0) > Credentials.MaxLineage)
        {
            context.Reject(OidcErrors.InvalidGrant, "This sign-in reached its rotation limit. Sign in again to continue.");
            return;
        }

        if (await credentials.AreCurrentAsync(tenant, inherited, context.CancellationToken)
            && await data.SaveAsync(operations, context.CancellationToken))
        {
            var bindings = addsBinding ? [.. inherited, binding] : inherited;
            CredentialLineage.Attach(context.Principal!, bindings);
            context.Transaction.SetProperty(CredentialLineage.BindingsProperty, (IReadOnlyList<CredentialBinding>)bindings);
            if (!addsBinding)
            {
                context.Principal!.SetClaim(DeviceAuthorizationClaim, approvedAuthorizationId);
            }
            else if (approvedAuthorizationId is not null)
            {
                context.Principal!.SetAuthorizationId(approvedAuthorizationId);
            }

            return;
        }

        await this.RejectAsync(context, principal, await tokens.FindByIdAsync(identifier, context.CancellationToken));
    }

    private async ValueTask RejectAsync(ProcessSignInContext context, ClaimsPrincipal principal, OidcToken? token)
    {
        var replay = context.EndpointType is OpenIddictServerEndpointType.Token
            && (context.Request.IsRefreshTokenGrantType() || context.Request.IsAuthorizationCodeGrantType())
            && await CredentialLineage.RevokeReplayAsync(tokens, token, principal, context.Request.ClientId,
                context.CancellationToken);
        context.Reject(OidcErrors.InvalidGrant, replay ? CredentialLineage.ReplayNotice : CredentialLineage.ConflictNotice);
    }

    private static OidcToken Redeemed(OidcToken token, DateTimeOffset now)
    {
        token.Status = Statuses.Redeemed;
        token.RedemptionDate = now;
        return token;
    }
}

public sealed class RevokeTheSignIn(IOpenIddictTokenManager tokens) : IOpenIddictServerHandler<HandleRevocationRequestContext>
{
    public async ValueTask HandleAsync(HandleRevocationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GenericTokenPrincipal?.GetAuthorizationId() is { Length: > 0 } authorizationId)
        {
            await tokens.RevokeByAuthorizationIdAsync(authorizationId, context.CancellationToken);
        }
    }
}

public sealed class ClientRegistration(IServiceScopeFactory scopes, AuthOptions options)
{
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
            await this.RegisterAsync(descriptor, cancellationToken);
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
            await using var scope = scopes.CreateAsyncScope();
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            try
            {
                if (await applications.FindByClientIdAsync(descriptor.ClientId!, cancellationToken) is { } existing)
                {
                    await applications.UpdateAsync(existing, descriptor, cancellationToken);
                }
                else
                {
                    await applications.CreateAsync(descriptor, cancellationToken);
                }

                return;
            }
            catch (Exception error) when (error is OpenIddictExceptions.ConcurrencyException
                || error is OpenIddictExceptions.ValidationException && attempt < 2)
            {
            }
        }

        throw ApiException.Unavailable();
    }
}

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
            ? await ring.GetPublicAsync(tenant, keyId, cancellationToken)
            : await ring.GetActiveAsync(tenant, cancellationToken);
        var response = transaction.GetHttpRequest()?.HttpContext.Response ?? throw new InvalidOperationException("OpenIddict ran outside an HTTP request.");
        foreach (var key in keys)
        {
            response.RegisterForDispose(key);
        }

        transaction.SetProperty(property, keys);
        return keys;
    }
}
