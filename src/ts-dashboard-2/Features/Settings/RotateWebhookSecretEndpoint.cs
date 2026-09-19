using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Settings;

public sealed class RotateWebhookSecretEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/webhooks/rotate");
        Summary("Rotates a webhook endpoint signing secret");
        Tags("Settings");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var webhookId = form["webhookId"].ToString();
        var url = store.ProjectWebhooks.FirstOrDefault(w => w.Id == webhookId)?.Url ?? webhookId;
        store.SavePendingWebhookSecret(webhookId, url, store.RotateWebhookSecret(webhookId));
        return Results.Redirect($"/settings?tab=webhooks&rotated=1", false, false);
    }
}
