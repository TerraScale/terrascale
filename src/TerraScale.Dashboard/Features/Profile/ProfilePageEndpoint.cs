using System.Globalization;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class ProfilePageEndpoint(DashboardSessions sessions, AccountService accounts, IProfileTokens tokens, ITsAuthMfa mfa, ITsAuthProviders providers, ITsAuthPlatform platform) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/profile").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var required = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (!AccountRequest.TryResolvePage(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            var tab = http.Request.Query["tab"].ToString();
            if (tab is not ("security" or "sessions" or "access-tokens" or "agents" or "danger"))
            {
                tab = "profile";
            }

            var list = tab == "access-tokens" ? await tokens.ListAsync(context, http.RequestAborted) : null;
            var agents = tab == "agents" ? await platform.ListAccountAgentsAsync(context.Session, http.RequestAborted) : null;
            var active = await sessions.ListAsync(http, context.Session);
            var status = tab == "security" ? await mfa.GetAccountMfaAsync(context.Session, http.RequestAborted) : null;
            var linked = tab == "profile" ? await providers.ListProvidersAsync(context.Session, http.RequestAborted) : null;
            var notice = http.Request.Query["status"].ToString() switch
            {
                "profile-saved" => tab == "profile" ? "Profile saved." : "",
                "language-saved" => tab == "profile" ? "Language preference saved." : "",
                "token-revoked" => tab == "access-tokens" ? "Access token revoked." : "",
                "agent-revoked" => tab == "agents" ? "Agent revoked." : "",
                "agent-email-shared" => tab == "agents" ? "Email sharing on." : "",
                "agent-email-private" => tab == "agents" ? "Email sharing off." : "",
                "session-revoked" => tab == "sessions" ? "Session revoked." : "",
                "code-sent" => tab == "profile" ? $"We emailed a code to {context.Account.Email}. It expires in 30 minutes." : "",
                _ => ""
            };
            var wait = int.TryParse(http.Request.Query["wait"], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? Math.Clamp(seconds, 1, 3600) : (int?)null;
            return ProfileView.Create(new(ShellModel.For(http, context, "Profile"), tab, Status: notice, Tokens: list, Sessions: active, Mfa: status)
            {
                Providers = linked,
                Agents = agents,
                ConfirmProvider = http.Request.Query["unlink"].ToString() is var selected && TsAuthGateway.IsSignInProvider(selected) ? selected : null,
                CodeWait = wait
            });
        }
    }
}
