using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.Datastar;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed record ProfilePage(ShellModel Shell, string Tab = "profile", string Error = "", string Status = "", IReadOnlyList<ProfileToken>? Tokens = null, CreatedProfileToken? CreatedToken = null, IReadOnlyList<DashboardSessionInfo>? Sessions = null, AccountMfaStatus? Mfa = null)
    {
        public string OrganizationRoleLabel => Shell.Context.Role is { Length: > 0 } role ? role : "No organization membership";
        public string? SelectedProjectRole => Shell.Context.Project is not { } project ? null
            : PlatformDatabases.IsPlatform(Shell.Context.Organization?.Id) ? "viewer"
            : project.Role is "viewer" or "member" or "admin" ? project.Role : null;
        public int ScopeRoleCount => (Shell.Context.Role.Length > 0 ? 1 : 0) + (SelectedProjectRole is not null ? 1 : 0);
        public string DialogId { get; init; } = "";
        public IReadOnlyList<TsAuthAgent>? Agents { get; init; }
        public string DraftEmail { get; init; } = "";
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

            try { await platform.SignOutEverywhereAsync(context.Session, http.RequestAborted); }
            catch (AccountAccessException error)
            { return await ProfileDialogErrors.PageAsync(http, sessions, accounts, "sign-out-everywhere", "sessions", error.Message, error.StatusCode); }
            catch (HttpRequestException)
            { return await ProfileDialogErrors.PageAsync(http, sessions, accounts, "sign-out-everywhere", "sessions", "Sign-out could not be confirmed. Try again.", 503); }
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

            ProfileSaveResult saved;
            if (language)
            {
                var locale = form["locale"].ToString();
                if (locale is not ("en" or "pt-BR"))
                {
                    return await InvalidAsync(http, sessions, context, "Choose an available language.", "invalid_profile_locale", 422, language, form);
                }

                await accounts.UpdateAccountAsync(context.Session, account => account.Locale = locale, http.RequestAborted);
                saved = new Done();
            }
            else
            {
                saved = await ProfileChanges.SaveAsync(accounts, context.Session, form["name"].ToString(), form["phoneNumber"].ToString(), http.RequestAborted);
            }
            switch (saved)
            {
                case Validation invalid:
                    return await InvalidAsync(http, sessions, context, invalid.Message, invalid.Code, 422, language, form);
                case Done:
                    break;
            }
            if (http.Request.IsDatastarRequest)
            {
                context = await accounts.GetContextAsync(context.Session, http.RequestAborted);
                await DatastarEvents.PatchElementsAsync(http, ProfilePatch.Create(new(ShellModel.For(http, context, "Profile"), "profile", Status: language ? "Language preference saved." : "Profile saved.", Sessions: await sessions.ListAsync(http, context.Session))));
                return Results.Empty;
            }
            return HttpRedirects.SeeOther("/profile?tab=profile&status=" + (language ? "language-saved" : "profile-saved"));
        }

        internal static string? InvalidField(string code)
        {
            return code switch
            {
                "invalid_profile_name" => "displayName",
                "invalid_profile_phone" => "phoneNumber",
                "invalid_profile_locale" => "interfaceLanguage",
                _ => null
            };
        }

        private static async Task<IResult> InvalidAsync(HttpContext http, DashboardSessions sessions, AccountContext context, string message, string code, int statusCode, bool language, IFormCollection form)
        {
            if (http.Request.IsDatastarRequest)
            {
                await DatastarEvents.PatchElementsAsync(http, ProfilePatch.Create(new(ShellModel.For(http, context, "Profile"), "profile", Error: message, Sessions: await sessions.ListAsync(http, context.Session))
                {
                    Providers = await http.RequestServices.GetRequiredService<ITsAuthProviders>().ListProvidersAsync(context.Session, http.RequestAborted),
                    InvalidField = InvalidField(code),
                    DraftName = language ? null : form["name"].ToString(),
                    DraftPhone = language ? null : form["phoneNumber"].ToString()
                }));
                return Results.Empty;
            }
            var slice = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), Error: message, Sessions: await sessions.ListAsync(http, context.Session))
            {
                Providers = await http.RequestServices.GetRequiredService<ITsAuthProviders>().ListProvidersAsync(context.Session, http.RequestAborted),
                InvalidField = InvalidField(code),
                DraftName = language ? null : form["name"].ToString(),
                DraftPhone = language ? null : form["phoneNumber"].ToString()
            });
            slice.StatusCode = statusCode;
            return slice;
        }
    }
}
