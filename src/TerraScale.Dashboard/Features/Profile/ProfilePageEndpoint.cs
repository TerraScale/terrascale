using System.Globalization;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class ProfilePageEndpoint(DashboardSessions sessions, AccountService accounts, IProfileTokens tokens, ITsAuthMfa mfa, ITsAuthProviders providers) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/profile");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            var tab = http.Request.Query["tab"].ToString();
            if (tab is not ("security" or "sessions" or "access-tokens" or "danger"))
            {
                tab = "profile";
            }

            var list = tab == "access-tokens" ? await tokens.ListAsync(context, http.RequestAborted) : null;
            var active = await sessions.ListAsync(http, context.Session);
            var status = tab == "security" ? await mfa.GetAccountMfaAsync(context.Session, http.RequestAborted) : null;
            var linked = tab == "profile" ? await providers.ListProvidersAsync(context.Session, http.RequestAborted) : null;
            var notice = http.Request.Query["status"].ToString() switch
            {
                "profile-saved" => tab == "profile" ? "Profile saved." : "",
                "language-saved" => tab == "profile" ? "Language preference saved." : "",
                "token-revoked" => tab == "access-tokens" ? "Access token revoked." : "",
                "session-revoked" => tab == "sessions" ? "Session revoked." : "",
                "code-sent" => tab == "profile" ? $"We emailed a code to {context.Account.Email}. It expires in 30 minutes." : "",
                _ => ""
            };
            var wait = int.TryParse(http.Request.Query["wait"], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? Math.Clamp(seconds, 1, 3600) : (int?)null;
            return ProfileView.Create(new(ShellModel.For(http, context, "Profile"), tab, Status: notice, Tokens: list, Sessions: active, Mfa: status)
            {
                Providers = linked,
                ConfirmProvider = http.Request.Query["unlink"].ToString() is var selected && TsAuthGateway.IsSignInProvider(selected) ? selected : null,
                CodeWait = wait
            });
        }
    }
}
