namespace TerraScale.Dashboard.Shared.Accounts;

public static class AccountRequest
{
    public static async Task<AccountContext?> RequireAsync(HttpContext http, DashboardSessions sessions, AccountService accounts)
    {
        var session = await sessions.ReadAsync(http);
        if (session is null) return null;
        return await accounts.GetContextAsync(session, http.RequestAborted);
    }

    public static IResult LoginRedirect(HttpContext http) => Results.Redirect("/login?returnTo=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString));
}
