using Microsoft.AspNetCore;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Features.Ui;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// Answers an authorization or end-session error that cannot be returned to the client (an unknown client, an
/// unregistered redirect URI, a malformed request) with the hosted error page instead of OpenIddict's plain text.
/// Ordered just before <see cref="OpenIddictServerAspNetCoreHandlers.ProcessLocalErrorResponse{TContext}"/>, so it only
/// sees errors no earlier handler redirected; the status code OpenIddict chose is kept.
/// </summary>
/// <typeparam name="TContext">The authorization or end-session response context.</typeparam>
public sealed class RenderLocalErrorPage<TContext> : IOpenIddictServerHandler<TContext>
    where TContext : OpenIddictServerEvents.BaseRequestContext
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(TContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Transaction.Response is not { Error: { } error } response || context.Transaction.GetHttpRequest()?.HttpContext is not { } http)
        {
            return;
        }

        var page = new SignInPageModel(SignInStep.Rejected, Tenancy.Current(http), Error: response.ErrorDescription, Detail: error);
        await SignInPageModel.Render(http, page, http.Response.StatusCode).ExecuteAsync(http).ConfigureAwait(false);
        context.HandleRequest();
    }
}
