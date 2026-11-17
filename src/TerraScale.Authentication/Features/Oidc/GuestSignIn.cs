using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Http.Metadata;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Hosting;
using TerraScale.DotNet.TerraBase;
using TerraScale.Endpoints;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

public static class GuestSignIn
{
    public const string GrantType = "urn:terrascale:params:oauth:grant-type:anonymous";

    public const string Claim = "is_anonymous";

    public const string Route = "oauth/anonymous";

    public const string Permission = Permissions.Prefixes.GrantType + GrantType;

    public const int ClientMinuteBudget = 30;

    public const int AddressMinuteBudget = 10;

    internal static bool Allows(OidcApplication client) => client.Permissions.Contains(Permission, StringComparer.Ordinal);

    internal static bool IsGuestPath(PathString path) =>
        (path.Value ?? string.Empty).TrimEnd('/').EndsWith("/" + Route, StringComparison.OrdinalIgnoreCase);
}

internal sealed class RouteGuestGrant : IOpenIddictServerHandler<ExtractTokenRequestContext>
{
    public ValueTask HandleAsync(ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request is not { } request)
        {
            return ValueTask.CompletedTask;
        }

        var guestPath = context.Transaction.GetHttpRequest() is { } http && GuestSignIn.IsGuestPath(http.Path);
        if (!guestPath)
        {
            if (request.GrantType == GuestSignIn.GrantType)
            {
                context.Reject(Errors.UnsupportedGrantType, "Guests sign in at oauth/anonymous.");
            }

            return ValueTask.CompletedTask;
        }

        if (!Tenancy.IsProject(Tenancy.Current(context.Transaction.GetHttpRequest()?.HttpContext)))
        {
            context.Reject(Errors.UnsupportedGrantType, "Only a project issuer admits guests.");
            return ValueTask.CompletedTask;
        }

        if (request.GrantType is not null || request.Scope is not null)
        {
            context.Reject(Errors.InvalidRequest, "A guest sign-in takes only client_id and the client's authentication.");
            return ValueTask.CompletedTask;
        }

        request.GrantType = GuestSignIn.GrantType;
        request.Scope = Scopes.OpenId + " " + Scopes.OfflineAccess;
        return ValueTask.CompletedTask;
    }
}

public sealed class GuestSignInEndpoint(Users users, AttemptLimiter limiter, ProjectAuthSettings settings, AuthOptions options, TimeProvider clock)
    : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/{slug}/" + GuestSignIn.Route);
        Name("oauth_anonymous");
        Summary("Guest sign-in");
        Description("application/x-www-form-urlencoded client_id, for a project client with allow_anonymous; a confidential client also authenticates with client_secret_basic or client_secret_post, and no other parameter is accepted. Creates a guest user, which has no email or password credential and counts as a user of the project, and answers the token endpoint's access, refresh and ID tokens with is_anonymous: true and the scopes openid and offline_access; the access token names the project data audiences like any user sign-in. Only a project whose sign_up_mode is public admits guests (403 access_denied otherwise). Each client admits 30 guests a minute and each address 10 a minute per project (429 rate_limited with Retry-After), on top of the token endpoint's per-peer budget. A client without allow_anonymous answers 400 unauthorized_client, and the platform issuer answers 400 unsupported_grant_type.");
        Tags("OAuth");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<TokenBody>();
        Produces<OAuthErrorBody>(StatusCodes.Status400BadRequest);
        Produces<OAuthErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<OAuthErrorBody>(StatusCodes.Status403Forbidden);
        Produces<OAuthErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<OAuthErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic, PublicClientAlternative: true));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        this.HttpContext.Response.Headers.Pragma = "no-cache";
        try
        {
            return await this.AdmitAsync(cancellationToken);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            return Results.Json(new OAuthErrorBody(Errors.TemporarilyUnavailable, error.Message), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> AdmitAsync(CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetOpenIddictServerRequest() is not { GrantType: GuestSignIn.GrantType, ClientId: { } clientId })
        {
            return Results.NotFound();
        }

        var tenant = Tenancy.Current(this.HttpContext);
        if (!Tenancy.IsProject(tenant))
        {
            return OidcResults.Forbid(Errors.UnsupportedGrantType, "Only a project issuer admits guests.");
        }

        if ((await settings.GetAsync(tenant, cancellationToken)).SignUpMode != "public")
        {
            return Results.Json(new OAuthErrorBody(Errors.AccessDenied, "This project does not admit guests."), OidcJson.Default.OAuthErrorBody, statusCode: StatusCodes.Status403Forbidden);
        }

        var address = HostedAuth.Subject(this.HttpContext.Connection.RemoteIpAddress?.ToString()) ?? "unknown";
        if ((await limiter.AcquireAsync(tenant, "guest-client", clientId, GuestSignIn.ClientMinuteBudget, TimeSpan.FromMinutes(1), cancellationToken)
            ?? await limiter.AcquireAsync(tenant, "guest-ip", limiter.Fold(address), GuestSignIn.AddressMinuteBudget, TimeSpan.FromMinutes(1), cancellationToken)) is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        var guest = await users.CreateGuestAsync(tenant, cancellationToken);
        return OidcResults.SignIn(options, guest, clock.GetUtcNow(), [Scopes.OpenId, Scopes.OfflineAccess], OidcResults.Audiences(tenant, options, clientId));
    }
}
