using Microsoft.AspNetCore;
using OpenIddict.Server;
using TerraScale.Authentication.Features.ProjectTenants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

public sealed class ProjectRedirectPolicy(ProjectAuthSettings settings) :
    IOpenIddictServerHandler<ApplyAuthorizationResponseContext>, IOpenIddictServerHandler<ApplyEndSessionResponseContext>
{
    public async ValueTask HandleAsync(ApplyAuthorizationResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await this.RefuseAsync(context, context.RedirectUri))
        {
            context.HandleRequest();
        }
    }

    public async ValueTask HandleAsync(ApplyEndSessionResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await this.RefuseAsync(context, context.PostLogoutRedirectUri))
        {
            context.HandleRequest();
        }
    }

    private async Task<bool> RefuseAsync(BaseRequestContext context, string? uri)
    {
        if (context.Transaction.GetHttpRequest()?.HttpContext is not { } http
            || await OidcResults.AllowsRedirectAsync(settings, Tenancy.Current(http), uri, context.CancellationToken))
        {
            return false;
        }

        http.Response.Headers.CacheControl = "no-store";
        await OidcResults.DisallowedRedirect().ExecuteAsync(http);
        return true;
    }
}
