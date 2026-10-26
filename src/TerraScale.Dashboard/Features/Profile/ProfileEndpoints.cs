using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Profile;

public sealed record ProfilePage(ShellModel Shell, string Tab = "profile", string Error = "", string Status = "", IReadOnlyList<ProfileToken>? Tokens = null, CreatedProfileToken? CreatedToken = null, IReadOnlyList<DashboardSessionInfo>? Sessions = null, AccountMfaStatus? Mfa = null)
{
    public string OrganizationRoleLabel => Shell.Context.Role is { Length: > 0 } role ? role : "No organization membership";
    public string? SelectedProjectRole => Shell.Context.Project is not { } project ? null
        : PlatformDatabases.IsPlatform(Shell.Context.Organization?.Id) ? "viewer"
        : project.Role is "viewer" or "member" or "admin" ? project.Role : null;
    public int ScopeRoleCount => (Shell.Context.Role.Length > 0 ? 1 : 0) + (SelectedProjectRole is not null ? 1 : 0);
    public string? DraftName { get; init; }
    public string? DraftPhone { get; init; }
    public string? InvalidField { get; init; }
    public IReadOnlyList<string>? Providers { get; init; }
    public string? ConfirmProvider { get; init; }
    public string[] Permissions
    {
        get
        {
            var result = new List<string>();
            if (Shell.Context.Project is not null)
            {
                result.Add("Access selected project");
                if (Shell.Context.CanWriteProjectData) result.Add("Write selected project data");
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
        endpoints.MapGet("/account-deleted", IResult (HttpContext http) =>
            AccountDeletedView.Create(new("Account deleted", DocumentModel.NonceFor(http))));
        endpoints.MapGet("/profile", async Task<IResult> (HttpContext http, DashboardSessions sessions, AccountService accounts, IProfileTokens tokens, ITsAuthMfa mfa, ITsAuthProviders providers) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null) return AccountRequest.LoginRedirect(http);
            var tab = http.Request.Query["tab"].ToString();
            if (tab is not ("security" or "sessions" or "access-tokens" or "danger")) tab = "profile";
            var list = tab is "access-tokens" or "danger" ? await tokens.ListAsync(context, http.RequestAborted) : null;
            var active = await sessions.ListAsync(http, context.Session);
            var status = tab == "security" ? await mfa.GetAccountMfaAsync(context.Session, http.RequestAborted) : null;
            var linked = tab == "profile" ? await providers.ListProvidersAsync(context.Session, http.RequestAborted) : null;
            var notice = tab == "profile" ? http.Request.Query["status"].ToString() switch
            {
                "profile-saved" => "Profile saved.",
                "language-saved" => "Language preference saved.",
                _ => ""
            } : "";
            return ProfileView.Create(new(ShellModel.For(http, context, "Profile"), tab, Status: notice, Tokens: list, Sessions: active, Mfa: status)
            {
                Providers = linked,
                ConfirmProvider = http.Request.Query["unlink"].ToString() is var selected && TsAuthGateway.IsSignInProvider(selected) ? selected : null
            });
        });
        endpoints.MapPost("/api/v1/dashboard/profile", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            await SaveAsync(http, sessions, accounts, false));
        endpoints.MapPost("/api/v1/dashboard/profile/language", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            await SaveAsync(http, sessions, accounts, true));
        endpoints.MapPost("/api/v1/dashboard/profile/delete", AccountDeletion.DeleteAsync);
        endpoints.MapGet("/account-deletion", AccountDeletion.ProgressAsync);
        endpoints.MapPost("/api/v1/dashboard/profile/delete/status", AccountDeletion.StatusAsync);
        endpoints.MapPost("/api/v1/dashboard/profile/delete/continue", (HttpContext http, DashboardSessions sessions, ITsAuthPlatform platform,
            IDashboardState state, AccessTokens tokens, IDashboardIdentity identity) =>
            AccountDeletion.ContinueAsync(http, sessions, platform, state, tokens, identity));
        endpoints.MapPost("/api/v1/dashboard/profile/providers/{provider}/unlink", SignInMethods.UnlinkAsync);
        return endpoints;
    }

    private static async Task<IResult> SaveAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, bool language)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
        if (context is null) return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
        try
        {
            if (language)
            {
                var locale = form["locale"].ToString();
                if (locale is not ("en" or "pt-BR")) throw new AccountAccessException("Choose an available language.", 422, "invalid_profile_locale");
                await accounts.UpdateAccountAsync(context.Session, account => account.Locale = locale, http.RequestAborted);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(form["name"])) throw new AccountAccessException("Enter a display name.", 422, "invalid_profile_name");
                await ProfileChanges.SaveAsync(accounts, context.Session, form["name"].ToString(), form["phoneNumber"].ToString(), http.RequestAborted);
            }
            return HttpRedirects.SeeOther("/profile?tab=profile&status=" + (language ? "language-saved" : "profile-saved"));
        }
        catch (AccountAccessException error)
        {
            var slice = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), Error: error.Message, Sessions: await sessions.ListAsync(http, context.Session))
            {
                Providers = await http.RequestServices.GetRequiredService<ITsAuthProviders>().ListProvidersAsync(context.Session, http.RequestAborted),
                InvalidField = error.Code switch
                {
                    "invalid_profile_name" => "displayName",
                    "invalid_profile_phone" => "phoneNumber",
                    "invalid_profile_locale" => "interfaceLanguage",
                    _ => null
                },
                DraftName = language ? null : form["name"].ToString(),
                DraftPhone = language ? null : form["phoneNumber"].ToString()
            });
            slice.StatusCode = error.StatusCode;
            return slice;
        }
    }
}
