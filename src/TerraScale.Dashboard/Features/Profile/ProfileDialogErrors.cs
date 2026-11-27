using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Features.Profile;

public static class ProfileDialogErrors
{
    public static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts,
        string dialog, string tab, string message, int status)
    {
        if (RequestError.WantsJson(http))
        {
            return TypedResults.Problem(message, statusCode: status);
        }

        var required = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
        if (!AccountRequest.TryResolveApi(http, required, out var context, out var rejection))
        {
            return rejection;
        }

        var tokens = tab == "access-tokens" ? await http.RequestServices.GetRequiredService<IProfileTokens>().ListAsync(context, http.RequestAborted) : null;
        var agents = tab == "agents" ? await http.RequestServices.GetRequiredService<ITsAuthPlatform>().ListAccountAgentsAsync(context.Session, http.RequestAborted) : null;
        var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), tab, Error: message,
            Tokens: tokens, Sessions: await sessions.ListAsync(http, context.Session))
        { DialogId = dialog, Agents = agents });
        view.StatusCode = status;
        return view;
    }
}
