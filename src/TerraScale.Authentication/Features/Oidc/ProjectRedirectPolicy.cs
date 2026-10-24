using Microsoft.AspNetCore;
using OpenIddict.Server;
using TerraScale.Authentication.Features.ProjectTenants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>Prevents protocol errors, including errors before endpoint passthrough, from reaching a disallowed URI.</summary>
public sealed class ProjectRedirectPolicy(ProjectAuthSettings settings) :
    IOpenIddictServerHandler<ApplyAuthorizationResponseContext>, IOpenIddictServerHandler<ApplyEndSessionResponseContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(ApplyAuthorizationResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await this.RefuseAsync(context, context.RedirectUri).ConfigureAwait(false))
        {
            context.HandleRequest();
        }
    }

    /// <inheritdoc />
    public async ValueTask HandleAsync(ApplyEndSessionResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await this.RefuseAsync(context, context.PostLogoutRedirectUri).ConfigureAwait(false))
        {
            context.HandleRequest();
        }
    }

    private async Task<bool> RefuseAsync(BaseRequestContext context, string? uri)
    {
        if (context.Transaction.GetHttpRequest()?.HttpContext is not { } http
            || await OidcResults.AllowsRedirectAsync(settings, Tenancy.Current(http), uri, context.CancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        http.Response.Headers.CacheControl = "no-store";
        await OidcResults.DisallowedRedirect().ExecuteAsync(http).ConfigureAwait(false);
        return true;
    }
}
