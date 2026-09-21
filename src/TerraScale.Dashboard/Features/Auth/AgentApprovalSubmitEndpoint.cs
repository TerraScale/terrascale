using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class AgentApprovalSubmitEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/agent/approve/submit");
        Summary("Approves or denies a pending agent access request");
        Tags("Auth");
        AllowAnonymous();
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var action = form["action"].ToString();
        var outcome = action == "approve" ? "approved" : "denied";
        return Results.Redirect($"/agent/approve?outcome={outcome}", false, false);
    }
}
