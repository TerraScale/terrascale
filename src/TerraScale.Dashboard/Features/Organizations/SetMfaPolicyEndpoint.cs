using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class SetMfaPolicyEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthMfa mfa, IWorkspacePlacement placement) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/organization/mfa-policy");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return TypedResults.Unauthorized();
            }

            if (context.Role != "owner")
            {
                return TypedResults.StatusCode(403);
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/organization", TypedResults.BadRequest("Invalid form token."));
            }

            try
            {
                if (context.Organization is not { } organization)
                {
                    return await RefusedAsync(http, context, placement, "Select an organization.");
                }

                await mfa.SetRequireMfaAsync(context.Session, organization.Id, form["requireMfa"] == "true", http.RequestAborted);
                if (http.Request.IsDatastarRequest)
                {
                    context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, reload: true);
                    await DatastarEvents.PatchElementsAsync(http, TerraScale.Dashboard.Features.Organizations.OrganizationPageView.Create(await OrganizationEndpoints.PageModelAsync(http, context, placement)));
                    return TypedResults.Empty;
                }
                return HttpRedirects.SeeOther("/organization");
            }
            catch (AccountAccessException error)
            {
                return await RefusedAsync(http, context, placement, error.Message, error.StatusCode);
            }
        }

        private static async Task<IResult> RefusedAsync(HttpContext http, AccountContext context, IWorkspacePlacement placement, string message, int status = 403)
        {
            if (http.Request.IsDatastarRequest)
            {
                await OrganizationEndpoints.PatchAsync(http, context, placement, message, "organization");
                return TypedResults.Empty;
            }
            return await OrganizationEndpoints.PageAsync(http, context, placement, message, status, "organization");
        }
    }
}
