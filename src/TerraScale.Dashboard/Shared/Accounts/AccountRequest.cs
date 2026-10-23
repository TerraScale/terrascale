namespace TerraScale.Dashboard.Shared.Accounts;

public static class AccountRequest
{
    public static async Task<AccountContext?> RequireAsync(HttpContext http, DashboardSessions sessions, AccountService accounts)
    {
        // The account context loads while ts-auth confirms the session; it is returned only for an active session.
        var read = await sessions.ReadWithAsync(http, async session =>
            await accounts.GetContextAsync(session, http.RequestAborted, Scope(http, "organizationId"), Scope(http, "projectId")));
        return read?.Value;
    }

    private static string? Scope(HttpContext http, string name)
    {
        if (!http.Request.Query.TryGetValue(name, out var values)) return null;
        if (values.Count != 1 || !TerraScale.DotNet.TerraScaleIds.IsValid(values[0]))
            throw new AccountAccessException("Workspace identity is invalid.", 400);
        return values[0];
    }

    public static IResult LoginRedirect(HttpContext http) => Results.Redirect("/login?returnTo=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString));
}
