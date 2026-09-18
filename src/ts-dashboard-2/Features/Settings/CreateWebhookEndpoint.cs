using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

public sealed class CreateWebhookEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/webhooks");
        Summary("Creates a project webhook endpoint");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var url = form["url"].ToString().Trim();
        if (url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            store.CreateWebhook(url);
        }

        return Results.Redirect("/settings?tab=webhooks", false, false);
    }
}
