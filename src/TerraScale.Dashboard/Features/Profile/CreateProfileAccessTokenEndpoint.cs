using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class CreateProfileAccessTokenEndpoint(DashboardSessions sessions, AccountService accounts, IProfileTokens tokens) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/access-tokens");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/profile?tab=access-tokens", Results.StatusCode(403));
            }

            if (!int.TryParse(form["expiresInDays"], out var days))
            {
                return await AccessTokenEndpoints.FailureAsync(http, context, sessions, tokens, "Choose a valid token expiration.", 422);
            }

            try
            {
                _ = AccessTokens.Validate(form["name"].ToString(), days);
            }
            catch (AccountAccessException error) { return await AccessTokenEndpoints.FailureAsync(http, context, sessions, tokens, error.Message, error.StatusCode); }
            return await StepUpFlow.StartAsync(http, context, "create_pat", "ts-dashboard");
        }
    }
}
