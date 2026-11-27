using System.Diagnostics.CodeAnalysis;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Billing;

namespace TerraScale.Dashboard.Common.Accounts
{
    public sealed record AccountUnauthenticated;

    public readonly union AccountRequestOutcome(
        AccountContext,
        AccountUnauthenticated,
        SessionUnavailable);

    public static class AccountRequest
    {
        public static async Task<AccountRequestOutcome> RequireAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, bool organizationScoped = true)
        {
            var read = await sessions.ReadWithAsync(http, async session =>
                await accounts.GetContextAsync(session, http.RequestAborted, Scope(http, "organizationId"), Scope(http, "projectId")));
            return read switch
            {
                SessionRead<AccountContext> loaded => await FinalizeAsync(http, accounts, loaded.Value, organizationScoped),
                SessionAbsent => new AccountUnauthenticated(),
                SessionUnavailable unavailable => unavailable,
            };
        }

        private static async Task<AccountRequestOutcome> FinalizeAsync(HttpContext http, AccountService accounts, AccountContext account, bool organizationScoped)
        {
            if (OrganizationCheckoutNavigation.IsNavigation(http))
            {
                var completed = await http.RequestServices.GetRequiredService<StripeBilling>().ResumeOrganizationAsync(account.Session, http);
                if (completed.OrganizationId is not null)
                {
                    account = await accounts.GetContextAsync(account.Session, http.RequestAborted, Scope(http, "organizationId"), Scope(http, "projectId"));
                }
            }

            if (organizationScoped && account is { Organization: { MfaRequired: true } blocked })
            {
                throw new TerraScale.Dashboard.Features.Auth.OrganizationMfaRequiredException(blocked.Name,
                    [.. account.Organizations.Where(org => !org.MfaRequired).Select(org => (org.Id, org.Name))]);
            }

            return account;
        }

        public static IResult ServiceUnavailable(HttpContext http)
        {
            if (RequestError.WantsJson(http))
            {
                return TypedResults.Problem(
                    "A TerraScale service is temporarily unavailable. Try again shortly.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var view = Common.UI.Slices.ServiceUnavailable.Create(new DocumentModel("Service unavailable", DocumentModel.NonceFor(http)));
            view.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return view;
        }

        public static bool TryResolvePage(
            HttpContext http,
            AccountRequestOutcome outcome,
            [NotNullWhen(true)] out AccountContext? context,
            [NotNullWhen(false)] out IResult? rejection) =>
            TryResolve(outcome, () => LoginRedirect(http), () => ServiceUnavailable(http), out context, out rejection);

        public static bool TryResolveApi(
            HttpContext http,
            AccountRequestOutcome outcome,
            [NotNullWhen(true)] out AccountContext? context,
            [NotNullWhen(false)] out IResult? rejection) =>
            TryResolve(outcome, TypedResults.Unauthorized, () => ServiceUnavailable(http), out context, out rejection);

        public static bool TryResolve(
            AccountRequestOutcome outcome,
            Func<IResult> unauthenticated,
            Func<IResult> unavailable,
            [NotNullWhen(true)] out AccountContext? context,
            [NotNullWhen(false)] out IResult? rejection)
        {
            if (outcome is AccountContext active)
            {
                context = active;
                rejection = null;
                return true;
            }

            context = null;
            rejection = outcome switch
            {
                AccountUnauthenticated => unauthenticated(),
                SessionUnavailable => unavailable(),
                AccountContext => unavailable(),
            };
            return false;
        }

        public static bool TryResolveSession(
            SessionResolutionOutcome outcome,
            Func<IResult> absent,
            Func<IResult> unavailable,
            [NotNullWhen(true)] out SessionRecord? session,
            [NotNullWhen(false)] out IResult? rejection)
        {
            if (outcome is SessionRecord active)
            {
                session = active;
                rejection = null;
                return true;
            }

            session = null;
            rejection = outcome switch
            {
                SessionAbsent => absent(),
                SessionUnavailable => unavailable(),
                SessionRecord => unavailable(),
            };
            return false;
        }

        private static string? Scope(HttpContext http, string name)
        {
            if (!http.Request.Query.TryGetValue(name, out var values))
            {
                return null;
            }

            if (values.Count != 1 || !TerraScale.Dashboard.Common.Accounts.TerraScaleIds.IsValid(values[0]))
            {
                throw new AccountAccessException("Workspace identity is invalid.", 400);
            }

            return values[0];
        }

        public static IResult LoginRedirect(HttpContext http)
        {
            return DashboardCookies.Read(http, TerraScale.Dashboard.Features.Profile.AccountDeletion.Cookie) is { Length: 64 } deletion && deletion.All(char.IsAsciiHexDigitLower)
                ? TypedResults.Redirect("/account-deletion")
                : TypedResults.Redirect("/login?returnTo=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString));
        }
    }
}
