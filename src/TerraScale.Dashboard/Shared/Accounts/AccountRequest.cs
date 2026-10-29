namespace TerraScale.Dashboard.Shared.Accounts;

public static class AccountRequest
{
    // organizationScoped is false only for account pages (profile), which stay open while the selected
    // organization requires two-factor the caller has not set up; every other page is blocked.
    public static async Task<AccountContext?> RequireAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, bool organizationScoped = true)
    {
        // The account context loads while ts-auth confirms the session; it is returned only for an active session.
        var read = await sessions.ReadWithAsync(http, async session =>
            await accounts.GetContextAsync(session, http.RequestAborted, Scope(http, "organizationId"), Scope(http, "projectId")));
        if (organizationScoped && read?.Value is { Organization: { MfaRequired: true } blocked } context)
            throw new TerraScale.Dashboard.Features.Auth.OrganizationMfaRequiredException(blocked.Name,
                [.. context.Organizations.Where(org => !org.MfaRequired).Select(org => (org.Id, org.Name))]);
        return read?.Value;
    }

    private static string? Scope(HttpContext http, string name)
    {
        if (!http.Request.Query.TryGetValue(name, out var values)) return null;
        if (values.Count != 1 || !TerraScale.DotNet.TerraScaleIds.IsValid(values[0]))
            throw new AccountAccessException("Workspace identity is invalid.", 400);
        return values[0];
    }

    // ts-auth revokes the session once it accepts an account deletion; a browser still holding that deletion's
    // recovery cookie follows it to completion (/account-deleted) instead of being asked to sign in again.
    public static IResult LoginRedirect(HttpContext http) =>
        http.Request.Cookies[TerraScale.Dashboard.Features.Profile.AccountDeletion.Cookie] is { Length: 64 } deletion && deletion.All(char.IsAsciiHexDigitLower)
            ? Results.Redirect("/account-deletion")
            : Results.Redirect("/login?returnTo=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString));
}
