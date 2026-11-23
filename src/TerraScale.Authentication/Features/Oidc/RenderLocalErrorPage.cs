using Microsoft.AspNetCore;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Features.Ui;

namespace TerraScale.Authentication.Features.Oidc;

public sealed class RenderLocalErrorPage<TContext> : IOpenIddictServerHandler<TContext>
    where TContext : OpenIddictServerEvents.BaseRequestContext
{
    public async ValueTask HandleAsync(TContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Transaction.Response is not { Error: { } error } response || context.Transaction.GetHttpRequest()?.HttpContext is not { } http)
        {
            return;
        }

        var page = new SignInPageModel(SignInStep.Rejected, Tenancy.Current(http), Error: response.ErrorDescription, Detail: error);
        await SignInPageModel.Render(http, page, http.Response.StatusCode).ExecuteAsync(http);
        context.HandleRequest();
    }
}
