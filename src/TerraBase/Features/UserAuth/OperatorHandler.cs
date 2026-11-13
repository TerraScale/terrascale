using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using ZLogger;
using TerraBase.Features.Callers;
using TerraBase.Features.Platform;

namespace TerraBase.Features.UserAuth;

internal sealed class OperatorHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IProjectCatalog catalog) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = UserAuthExtensions.OperatorScheme;

    public const string UserHeader = "X-TerraBase-User-Id";

    public const string KeyId = "operator";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Listeners.ArrivedOn(Context) != TerraBaseListener.Internal
            || Request.Headers.Authorization.Count > 0
                && (Context.GetEndpoint()?.Metadata.GetMetadata<OperatorAuthorityProof>() is null
                    || !Request.Headers.ContainsKey(UserHeader) || OperatorAuthorityProof.Read(Request) is null))
        {
            return AuthenticateResult.NoResult();
        }

        if (Context.Features.Get<CallerIdentity>() is { } caller && !Request.Headers.ContainsKey(UserHeader))
        {
            return await CallerAsync(caller).ConfigureAwait(false);
        }

        if (!Request.Headers.TryGetValue(UserHeader, out var values))
        {
            return await DataAsync().ConfigureAwait(false);
        }

        var user = values.Count == 1 ? values[0] : null;
        if (!EntityIds.IsValid(user))
        {
            return AuthenticateResult.Fail(UserHeader + " must be one canonical uppercase ULID");
        }

        if (Context.Features.Get<CallerIdentity>() is null)
        {
            Context.RequestServices.GetRequiredService<CallerAdmission>().Legacy("act_as");
        }
        Logger.ZLogInformation($"The platform operator acted as user {user:@UserId}: {Request.Method:@Method} {Request.Path.Value:@Path}");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private async Task<AuthenticateResult> CallerAsync(CallerIdentity caller)
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<Requires>()?.Grants.Contains("catalog.read") == true)
        {
            Claim[] catalogClaims = [new(ClaimTypes.NameIdentifier, caller.Key.Caller), new(PlatformExplore.RoleClaim, PlatformExplore.AdminRole)];
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(catalogClaims, SchemeName)), SchemeName));
        }
        if (Request.RouteValues["project"] is not string project
            || await catalog.ProjectOrganizationAsync(project, Context.RequestAborted).ConfigureAwait(false) is not string organization)
        {
            return AuthenticateResult.NoResult();
        }
        IReadOnlyList<string> scopes = caller.Grants.Contains("data:" + project) || caller.Grants.Contains("platform.write")
            ? [ApiKeyScopes.SqlRead, ApiKeyScopes.SqlWrite] : [ApiKeyScopes.SqlRead];
        var key = new ApiKey(caller.Key.Kid, caller.Key.Caller, scopes, organization, project, ExpiresAt: caller.Until);
        Context.Features.Set(key);
        Claim[] claims = [new(ClaimTypes.NameIdentifier, key.Id), .. scopes.Select(scope => new Claim(ApiKeyAuthenticationHandler.ScopeClaim, scope))];
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }

    private async Task<AuthenticateResult> DataAsync()
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>() is not { } served || ReferenceEquals(served, ListenerMetadata.Management)
            || !served.Listeners.Contains(TerraBaseListener.Client)
            || !Request.RouteValues.TryGetValue("project", out var value) || value is not string project || !EntityIds.IsValid(project)
            || await catalog.ProjectOrganizationAsync(project, Context.RequestAborted).ConfigureAwait(false) is not string organization)
        {
            return AuthenticateResult.NoResult();
        }

        Context.RequestServices.GetRequiredService<CallerAdmission>().Legacy("data");
        var key = new ApiKey(KeyId, "platform operator", [.. ApiKeyScopes.All, .. ApiKeyScopes.ProjectTokenScopes], organization, project);
        Context.Features.Set(key);
        Claim[] claims = [new(ClaimTypes.NameIdentifier, key.Id), .. key.Scopes.Select(scope => new Claim(ApiKeyAuthenticationHandler.ScopeClaim, scope))];
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
}
