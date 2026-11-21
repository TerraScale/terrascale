using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;
using TerraScale.Dashboard.Errors;
using System.Diagnostics;

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

            IFormCollection form;
            switch (await FormRequests.ReadAsync(http, http.RequestAborted))
            {
                case Validation invalid:
                    return FormRequests.Refused(http, invalid);
                case UnsupportedMediaType unsupported:
                    return FormRequests.Refused(http, unsupported);
                case IFormCollection parsed:
                    form = parsed;
                    break;
                case null:
                    throw new UnreachableException();
            }
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
                return await StepUpFlow.StartAsync(http, context, "create_pat", "ts-dashboard");
            }
            catch (AccountAccessException error) { return await AccessTokenEndpoints.FailureAsync(http, context, sessions, tokens, error.Message, error.StatusCode); }
        }
    }
}
