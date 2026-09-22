using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth.Endpoints;

public sealed class CliApprovalSubmitEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/cli/approve/submit");
        Summary("Approves or denies a CLI device login request");
        Tags("Auth");
        AllowAnonymous();
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var userCode = form["user_code"].ToString();
        var action = form["action"].ToString();
        if (userCode.Length == 0 || action is not ("approve" or "deny"))
        {
            return Results.Redirect("/cli/approve?error=" + Uri.EscapeDataString("The connection was interrupted. Retry to check the request status."), false, false);
        }

        var outcome = action == "approve" ? "approved" : "denied";
        return Results.Redirect($"/cli/approve?user_code={Uri.EscapeDataString(userCode)}&outcome={outcome}", false, false);
    }
}
