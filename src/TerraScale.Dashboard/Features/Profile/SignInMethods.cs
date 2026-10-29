using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Profile;

public static class SignInMethods
{
    public static async Task<IResult> UnlinkAsync(HttpContext http, string provider, DashboardSessions sessions, AccountService accounts, ITsAuthProviders providers)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
        if (context is null) return Results.Unauthorized();
        if (!http.Request.HasFormContentType) return Results.BadRequest();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
        if (!TsAuthGateway.IsSignInProvider(provider)) return Results.NotFound();
        try
        {
            await providers.UnlinkProviderAsync(context.Session, provider, form["code"].ToString().Trim(), http.RequestAborted);
        }
        catch (AccountAccessException error)
        {
            if (error.StatusCode == 429) http.Response.Headers.RetryAfter = error.Code;
            return await FailureAsync(error.Message, error.StatusCode);
        }
        catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            return await FailureAsync("Sign-in methods are unavailable right now. Refresh the page before trying again.", 503);
        }
        // ts-auth has already invalidated every previous grant, including its hosted cookie.
        // Remove local sessions without trying to revoke the now-invalid bearer again.
        await sessions.RevokeUserAsync(context.UserId, http.RequestAborted);
        await sessions.EndAsync(http);
        return HttpRedirects.SeeOther("/login?returnTo=%2Fprofile");

        async Task<IResult> FailureAsync(string message, int status)
        {
            var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), Error: message,
                Sessions: await sessions.ListAsync(http, context.Session))
            {
                Providers = await providers.ListProvidersAsync(context.Session, http.RequestAborted),
                ConfirmProvider = provider
            });
            view.StatusCode = status;
            return view;
        }
    }
}
