using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed record AgentApprovePageModel(
    bool Authenticated,
    DashboardStore.AgentApprovalRequest? Request,
    string? Outcome,
    string? Error) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AgentApprovePageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/agent/approve");
        Summary("Renders the agent access approval page");
        Tags("Pages");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var model = new AgentApprovePageModel(
            Authenticated: DashboardAuthMiddleware.IsAuthenticated(HttpContext),
            Request: DashboardAuthMiddleware.IsAuthenticated(HttpContext) ? store.DemoAgentApproval : null,
            Outcome: query["outcome"].FirstOrDefault(),
            Error: query["error"].FirstOrDefault());
        return Task.FromResult<IResult>(AgentApproveSlice.Create(model));
    }
}
