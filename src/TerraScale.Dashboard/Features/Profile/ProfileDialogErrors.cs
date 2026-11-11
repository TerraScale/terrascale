using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Features.Profile;

public static class ProfileDialogErrors
{
    public static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts,
        string dialog, string tab, string message, int status, string email = "")
    {
        if (RequestError.WantsJson(http))
        {
            return Results.Problem(message, statusCode: status);
        }

        var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
        if (context is null)
        {
            return Results.Unauthorized();
        }

        var tokens = tab == "access-tokens" ? await http.RequestServices.GetRequiredService<IProfileTokens>().ListAsync(context, http.RequestAborted) : null;
        var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), tab, Error: message,
            Tokens: tokens, Sessions: await sessions.ListAsync(http, context.Session))
        { DialogId = dialog, DraftEmail = email });
        view.StatusCode = status;
        return view;
    }
}
