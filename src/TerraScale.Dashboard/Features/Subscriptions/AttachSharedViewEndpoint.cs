using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Subscriptions;

public sealed class AttachSharedViewEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/subscriptions/attach");
        Summary("Attaches a shared view for the selected database");
        Tags("Subscriptions");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var databaseId = form["databaseId"].ToString();
        if (databaseId.Length == 0)
        {
            return Results.Redirect("/subscriptions", false, false);
        }

        var viewId = $"sub-{databaseId[..Math.Min(12, databaseId.Length)]}";
        return Results.Redirect($"/subscriptions?viewId={Uri.EscapeDataString(viewId)}&databaseId={Uri.EscapeDataString(databaseId)}", false, false);
    }
}
