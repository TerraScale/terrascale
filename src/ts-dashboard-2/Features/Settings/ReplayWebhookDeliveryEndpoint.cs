using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

public sealed class ReplayWebhookDeliveryEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/webhooks/replay");
        Summary("Replays a webhook delivery");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.ReplayWebhookDelivery(form["deliveryId"].ToString());
        return Results.Redirect($"/settings?tab=webhooks&toast={Uri.EscapeDataString("Delivery replayed. It will be retried shortly.")}", false, false);
    }
}
