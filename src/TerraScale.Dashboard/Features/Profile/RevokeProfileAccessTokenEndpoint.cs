using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class RevokeProfileAccessTokenEndpoint(DashboardSessions sessions, AccountService accounts, IProfileTokens tokens) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/access-tokens/{id}/revoke");

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
                return Results.StatusCode(403);
            }

            try { await tokens.RevokeAsync(context, http.Request.RouteValues["id"]?.ToString() ?? "", http.RequestAborted); }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            return HttpRedirects.SeeOther(AccessTokenEndpoints.ReturnTo + "&status=token-revoked");
        }
    }
}
