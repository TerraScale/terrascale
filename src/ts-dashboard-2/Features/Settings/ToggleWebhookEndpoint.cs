using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

public sealed class ToggleWebhookEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/webhooks/toggle");
        Summary("Pauses, resumes, enables, or disables a webhook endpoint");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var webhookId = form["webhookId"].ToString();
        var state = form["state"].ToString();
        var previous = store.ProjectWebhooks.FirstOrDefault(w => w.Id == webhookId)?.Status ?? "";
        store.ToggleWebhook(webhookId, state);

        var toast = state switch
        {
            "paused" => "Webhook paused. Deliveries are held until it is resumed.",
            "disabled" => "Webhook disabled. New events are no longer delivered.",
            _ => previous == "Disabled" ? "Webhook enabled." : "Webhook resumed. Held deliveries are being sent.",
        };
        return Results.Redirect($"/settings?tab=webhooks&toast={Uri.EscapeDataString(toast)}", false, false);
    }
}
