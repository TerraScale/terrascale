using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class RevokeSessionEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/profile/sessions/{id}/revoke").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var required = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (!AccountRequest.TryResolveApi(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/profile?tab=sessions", TypedResults.BadRequest("Invalid form token."));
            }

            await sessions.RevokeAsync(http, context.Session, http.Request.RouteValues["id"]?.ToString() ?? "");
            if (http.Request.IsDatastarRequest)
            {
                await DatastarEvents.PatchElementsAsync(http, ProfilePatch.Create(new(ShellModel.For(http, context, "Profile"), "sessions", Status: "Session revoked.", Sessions: await sessions.ListAsync(http, context.Session))));
                return TypedResults.Empty;
            }
            return HttpRedirects.SeeOther("/profile?tab=sessions&status=session-revoked");
        }
    }
}
