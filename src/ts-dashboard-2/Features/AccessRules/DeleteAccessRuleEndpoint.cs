using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.AccessRules;

public sealed class DeleteAccessRuleEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/access-rules/{ruleId}/delete");
        Summary("Deletes a stored access rule");
        Tags("AccessRules");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        store.DeleteAccessRule(HttpContext.Request.RouteValues["ruleId"] as string ?? "");
        return Task.FromResult(Results.Redirect("/access-rules", false, false));
    }
}
