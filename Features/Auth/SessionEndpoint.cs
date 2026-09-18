using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed class SessionEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/api/v1/auth/me");
        Summary("Returns the signed-in dashboard session user");
        Tags("Auth");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct) =>
        Task.FromResult<IResult>(Results.Ok(new
        {
            authenticated = DashboardAuthMiddleware.IsAuthenticated(HttpContext),
            user = new { store.CurrentUser.Id, store.CurrentUser.Email, store.CurrentUser.Name, store.CurrentUser.Role },
        }));
}
