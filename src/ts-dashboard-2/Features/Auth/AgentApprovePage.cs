using System.Globalization;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed record AgentApprovePageModel(
    DashboardStore.AgentApprovalRequest? Request,
    string? UserCode,
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
        var expiresAt = DateTimeOffset.Parse(
            store.DemoAgentApproval.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var model = new AgentApprovePageModel(
            Request: store.DemoAgentApproval with
            {
                ExpiresAt = expiresAt.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture),
            },
            UserCode: query["code"].ToString() is { Length: > 0 } code ? code : null,
            Outcome: query["outcome"].FirstOrDefault(),
            Error: query["error"].FirstOrDefault());
        return Task.FromResult<IResult>(AgentApproveSlice.Create(model));
    }
}
