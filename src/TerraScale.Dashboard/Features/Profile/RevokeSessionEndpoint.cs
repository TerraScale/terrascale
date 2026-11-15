using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class RevokeSessionEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/sessions/{id}/revoke");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/profile?tab=sessions", Results.BadRequest("Invalid form token."));
            }

            await sessions.RevokeAsync(http, context.Session, http.Request.RouteValues["id"]?.ToString() ?? "");
            if (http.Request.IsDatastarRequest)
            {
                await DatastarEvents.PatchElementsAsync(http, ProfilePatch.Create(new(ShellModel.For(http, context, "Profile"), "sessions", Status: "Session revoked.", Sessions: await sessions.ListAsync(http, context.Session))));
                return Results.Empty;
            }
            return HttpRedirects.SeeOther("/profile?tab=sessions&status=session-revoked");
        }
    }
}
