using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Profile
{
    public static class AccessTokenEndpoints
    {
        internal const string ReturnTo = "/profile?tab=access-tokens";

        public static async Task<IResult> IssueAsync(HttpContext http, OidcFlow flow, SessionRecord fresh, DateTimeOffset expiresAt)
        {
            var retained = false;
            try
            {
                var sessions = http.RequestServices.GetRequiredService<DashboardSessions>();
                var tokens = http.RequestServices.GetRequiredService<IProfileTokens>();
                var resolvedBrowser = await sessions.ReadAsync(http);
                if (!AccountRequest.TryResolveSession(
                    resolvedBrowser,
                    () => TypedResults.Problem(StepUpFlow.Required().Message, statusCode: 403),
                    () => AccountRequest.ServiceUnavailable(http),
                    out var browser,
                    out var browserRejection))
                {
                    return browserRejection;
                }

                if (flow.StepUp is not { } intent || browser.UserId != intent.UserId || StepUpFlow.Browser(http) != intent.Browser)
                {
                    return TypedResults.Problem(StepUpFlow.Required().Message, statusCode: 403);
                }

                http.Request.QueryString = new(intent.Query);
                var requiredAccount = await AccountRequest.RequireAsync(http, sessions, http.RequestServices.GetRequiredService<AccountService>());
                if (!AccountRequest.TryResolve(
                    requiredAccount,
                    () => HttpRedirects.SeeOther("/login?returnTo=" + Uri.EscapeDataString(ReturnTo)),
                    () => AccountRequest.ServiceUnavailable(http),
                    out var context,
                    out var accountRejection))
                {
                    return accountRejection;
                }

                try
                {
                    StepUpFlow.Validate(http, context, intent, "create_pat");
                    var created = await tokens.CreateAsync(context, flow.TokenName, expiresAt, fresh, intent, http.RequestAborted);
                    retained = true;
                    return ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "access-tokens", Tokens: await tokens.ListAsync(context, http.RequestAborted), CreatedToken: created, Sessions: await sessions.ListAsync(http, context.Session)));
                }
                catch (AccountAccessException error) { return await FailureAsync(http, context, sessions, tokens, error.Message, error.StatusCode); }
            }
            finally
            {
                if (!retained)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await http.RequestServices.GetRequiredService<IAuthGateway>().RevokeAsync(fresh, cleanup.Token);
                }
            }
        }

        internal static async Task<IResult> FailureAsync(HttpContext http, AccountContext context, DashboardSessions sessions, IProfileTokens tokens, string message, int statusCode)
        {
            var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "access-tokens", Error: message, Tokens: await tokens.ListAsync(context, http.RequestAborted), Sessions: await sessions.ListAsync(http, context.Session)));
            view.StatusCode = statusCode;
            return view;
        }
    }
}
