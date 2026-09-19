using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

/// <summary>Port of deleteWebhookEndpoint.</summary>
public sealed class WebhookDeleteEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/webhooks/delete");
        Summary("Deletes a webhook endpoint and its deliveries");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.RemoveWebhook(form["webhookId"].ToString());
        return Results.Redirect("/settings?tab=webhooks", false, false);
    }
}
