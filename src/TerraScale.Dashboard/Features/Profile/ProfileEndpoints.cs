using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed record ProfilePage(ShellModel Shell, string Tab = "profile", string Error = "", string Status = "", IReadOnlyList<ProfileToken>? Tokens = null, CreatedProfileToken? CreatedToken = null, IReadOnlyList<DashboardSessionInfo>? Sessions = null, AccountMfaStatus? Mfa = null)
    {
        public string OrganizationRoleLabel => Shell.Context.Role is { Length: > 0 } role ? role : "No organization membership";
        public string? SelectedProjectRole => Shell.Context.Project is not { } project ? null
            : PlatformDatabases.IsPlatform(Shell.Context.Organization?.Id) ? "viewer"
            : project.Role is "viewer" or "member" or "admin" ? project.Role : null;
        public int ScopeRoleCount => (Shell.Context.Role.Length > 0 ? 1 : 0) + (SelectedProjectRole is not null ? 1 : 0);
        public bool EmailCodeEntry { get; init; }
        public string? PendingEmail { get; init; }
        public string? DraftName { get; init; }
        public string? DraftPhone { get; init; }
        public string? InvalidField { get; init; }
        public IReadOnlyList<string>? Providers { get; init; }
        public string? ConfirmProvider { get; init; }
        public int? CodeWait { get; init; }
        public string[] Permissions
        {
            get
            {
                var result = new List<string>();
                if (Shell.Context.Project is not null)
                {
                    result.Add("Access selected project");
                    if (Shell.Context.CanWriteProjectData)
                    {
                        result.Add("Write selected project data");
                    }
                }
                if (Shell.Context.CanManage)
                {
                    result.Add("Manage organization resources");
                }

                if (Shell.Context.Organization?.CanProvision(Shell.Context.UserId) == true)
                {
                    result.Add("Provision organization resources");
                }

                return [.. result];
            }
        }
    }

    public static class ProfileEndpoints
    {
        // ts-auth ends every sign-in of the account, its hosted cookie included. Remove every local
        // session of the user without trying to revoke the now-invalid bearer again.
        internal static async Task<IResult> SignOutEverywhereAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthPlatform platform)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/profile?tab=sessions", Results.BadRequest("Invalid form token."));
            }

            await platform.SignOutEverywhereAsync(context.Session, http.RequestAborted);
            await sessions.RevokeUserAsync(context.UserId, http.RequestAborted);
            await sessions.EndAsync(http);
            return HttpRedirects.SeeOther("/login?returnTo=%2Fprofile");
        }

        internal static async Task<IResult> SaveAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, bool language)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/profile", Results.BadRequest("Invalid form token."));
            }

            try
            {
                if (language)
                {
                    var locale = form["locale"].ToString();
                    if (locale is not ("en" or "pt-BR"))
                    {
                        throw new AccountAccessException("Choose an available language.", 422, "invalid_profile_locale");
                    }

                    await accounts.UpdateAccountAsync(context.Session, account => account.Locale = locale, http.RequestAborted);
                }
                else
                {
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
}
