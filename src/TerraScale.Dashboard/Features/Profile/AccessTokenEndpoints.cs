using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Profile
{
    public static class AccessTokenEndpoints
    {
        internal const string ReturnTo = "/profile?tab=access-tokens";

        // The ts-auth callback for a token flow: the fresh grant must belong to the signed-in browser's user.
        public static async Task<IResult> IssueAsync(HttpContext http, OidcFlow flow, SessionRecord fresh)
        {
            var retained = false;
            try
            {
                var sessions = http.RequestServices.GetRequiredService<DashboardSessions>();
                var tokens = http.RequestServices.GetRequiredService<IProfileTokens>();
                var browser = await sessions.ReadAsync(http);
                if (flow.StepUp is not { } intent || browser?.UserId != intent.UserId || StepUpFlow.Browser(http) != intent.Browser)
                {
                    return Results.Problem(StepUpFlow.Required().Message, statusCode: 403);
                }

                http.Request.QueryString = new(intent.Query);
                var context = await AccountRequest.RequireAsync(http, sessions, http.RequestServices.GetRequiredService<AccountService>());
                if (context is null)
                {
                    return HttpRedirects.SeeOther("/login?returnTo=" + Uri.EscapeDataString(ReturnTo));
                }

                try
                {
                    StepUpFlow.Validate(http, context, intent, "create_pat");
                    var created = await tokens.CreateAsync(context, flow.TokenName, flow.TokenDays, fresh, intent, http.RequestAborted);
                    // The credential now owns the grant, even if preparing/rendering its view fails.
                    retained = true;
                    return ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "access-tokens", Tokens: await tokens.ListAsync(context, http.RequestAborted), CreatedToken: created, Sessions: await sessions.ListAsync(http, context.Session)));
                }
                catch (AccountAccessException error) { return await FailureAsync(http, context, sessions, tokens, error); }
            }
            finally
            {
                if (!retained)
                {
                    // Includes browser/workspace validation and cancellation after code exchange.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await http.RequestServices.GetRequiredService<IAuthGateway>().RevokeAsync(fresh, cleanup.Token);
                }
            }
        }

        internal static async Task<IResult> FailureAsync(HttpContext http, AccountContext context, DashboardSessions sessions, IProfileTokens tokens, AccountAccessException error)
        {
            var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "access-tokens", Error: error.Message, Tokens: await tokens.ListAsync(context, http.RequestAborted), Sessions: await sessions.ListAsync(http, context.Session)));
            view.StatusCode = error.StatusCode;
            return view;
        }
    }
}
