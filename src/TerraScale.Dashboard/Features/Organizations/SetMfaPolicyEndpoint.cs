using TerraScale.Dashboard.Features.Auth;
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
                return Results.Unauthorized();
            }

            if (context.Role != "owner")
            {
                return Results.StatusCode(403);
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            try
            {
                var organization = context.Organization ?? throw new AccountAccessException("Select an organization.");
                await mfa.SetRequireMfaAsync(context.Session, organization.Id, form["requireMfa"] == "true", http.RequestAborted);
                return HttpRedirects.SeeOther("/organization");
            }
            catch (AccountAccessException error) { return await OrganizationEndpoints.PageAsync(http, context, placement, error.Message, error.StatusCode, "organization"); }
        }
    }
}
