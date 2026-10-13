using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Profile;

public sealed record ProfilePage(ShellModel Shell, string Tab = "profile", string Error = "", string Status = "", IReadOnlyList<ProfileToken>? Tokens = null, CreatedProfileToken? CreatedToken = null, IReadOnlyList<DashboardSessionInfo>? Sessions = null)
{
    public string? DraftName { get; init; }
    public string? DraftPhone { get; init; }
    public string[] Permissions
    {
        get
        {
            var result = new List<string>();
            if (Shell.Context.Project is not null)
            {
                result.Add("Access selected project");
                if (Shell.Context.CanWrite) result.Add("Write selected project data");
            }
            if (Shell.Context.CanManage) result.Add("Manage organization resources");
            if (Shell.Context.Organization?.CanProvision(Shell.Context.UserId) == true) result.Add("Provision organization resources");
            return result.ToArray();
        }
    }
}

public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfile(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/profile", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IProfileTokens tokens) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountEndpoints.LoginRedirect(http);
            var tab = http.Request.Query["tab"].ToString();
            if (tab is not ("security" or "sessions" or "access-tokens")) tab = "profile";
            var list = tab == "access-tokens" ? await tokens.ListAsync(context, http.RequestAborted) : null;
            var active = await sessions.ListAsync(http, context.Session);
            return ProfileView.Create(new(ShellModel.For(http, context, "Profile"), tab, Error: PasswordError(http.Request.Query["error"].ToString()),
                Status: http.Request.Query["status"] == "code-sent" ? "We emailed a code to your address. Enter it with your new password." : "", Tokens: list, Sessions: active));
        });
        endpoints.MapPost("/api/v1/dashboard/profile", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            await SaveAsync(http, sessions, accounts, false));
        endpoints.MapPost("/api/v1/dashboard/profile/language", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            await SaveAsync(http, sessions, accounts, true));
        return endpoints;
    }

    private static string PasswordError(string value)
    {
        if (value is "Current password is incorrect." or "Use at least 12 characters." or
            "Use a password with 1024 bytes or fewer." or "Passwords do not match." or "Choose a different password." or
            "The code is incorrect or has expired.") return value;
        const string prefix = "Too many sign-in attempts. Try again in ";
        if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith(" seconds.", StringComparison.Ordinal) &&
            int.TryParse(value.AsSpan(prefix.Length, value.Length - prefix.Length - 9), out var seconds) && seconds is >= 1 and <= 300 &&
            value == prefix + seconds + " seconds.") return value;
        if (value == prefix + "1 second.") return value;
        return "";
    }

    private static async Task<IResult> SaveAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, bool language)
    {
        var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
        if (context is null) return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
        try
        {
            if (language)
            {
                var locale = form["locale"].ToString();
                if (locale is not ("en" or "pt-BR")) throw new AccountAccessException("Choose an available language.", 422);
                await accounts.UpdateAccountAsync(context.Session, account => account.Locale = locale, http.RequestAborted);
            }
            else
            {
                await ProfileChanges.SaveAsync(accounts, context.Session, form["name"].ToString(), form["phoneNumber"].ToString(), http.RequestAborted);
            }
            return AuthEndpoints.SeeOther("/profile?tab=profile");
        }
        catch (AccountAccessException error)
        {
            var slice = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), Error: error.Message, Sessions: await sessions.ListAsync(http, context.Session))
            {
                DraftName = language ? null : form["name"].ToString(),
                DraftPhone = language ? null : form["phoneNumber"].ToString()
            });
            slice.StatusCode = error.StatusCode;
            return slice;
        }
    }
}
