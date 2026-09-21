using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth.Endpoints;

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
        Task.FromResult<IResult>(
            store.IsAuthenticated(HttpContext)
                ? Results.Ok(new
                {
                    authenticated = true,
                    user = new { store.CurrentUser.Id, store.CurrentUser.Email, store.CurrentUser.Name, store.CurrentUser.Role },
                })
                : Results.Ok(new { authenticated = false }));
}
