using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

public sealed partial class DashboardStore
{
    /// <summary>Port of WEBHOOK_MAX_RETRY_ATTEMPTS.</summary>
    public const int WebhookMaxRetryAttempts = 5;

    // Port of WEBHOOK_ENDPOINT_LIMITS — the starter plan guardrail, never a billable quota.
    private const string WebhookPlanName = "Starter";
    private const int WebhookPlanEndpointLimit = 10;

    private bool _webhookSeedsExtended;

    public ImmutableArray<WebhookDelivery> WebhookDeliveries { get; private set; } =
    [
        new("dlv_01JCBA3X5Z7B9D2F4H6J8N1QK", "wh_01JCA0RBD2F4H7J0M3P6S1V4Y", "document.batch_upsert", "Delivered", 1, 5, 200, "2026-09-18T07:41:12Z", "2026-09-18T07:41:13Z", "", ""),
        new("dlv_01JCBA4Y6A8C1E3G5I7K9P2RL", "wh_01JCA0RBD2F4H7J0M3P6S1V4Y", "database.created", "Retrying", 3, 5, 502, "2026-09-18T09:02:44Z", "", "2026-09-18T09:07:44Z", "Upstream returned 502 Bad Gateway"),
        new("dlv_01JCBA5Z7B9D2F4H6J8N0R3SM", "wh_01JCA0RBD2F4H7J0M3P6S1V4Y", "document.batch_upsert", "Failed", 5, 5, 500, "2026-09-17T22:10:03Z", "", "", "Connection timed out after 5 attempts"),
        new("dlv_01JCBA6A8C1E3G5I7K9N1S4TN", "wh_01JCB8VF2H4J6M1P4S7U0X3ZC", "database.created", "Delivered", 1, 5, 200, "2026-09-18T04:55:20Z", "2026-09-18T04:55:21Z", "", ""),
        new("dlv_01JCBA7B9D2F4H6J8N0Q2U5VO", "wh_01JCB8VF2H4J6M1P4S7U0X3ZC", "member.role_changed", "Pending", 0, 5, null, "2026-09-19T09:12:00Z", "", "2026-09-19T09:12:30Z", ""),
        new("dlv_01JCBA8C1E3G5I7K9N0R1V6WP", "wh_01JCB9WG3I5K7N2Q5T8V1Y4AD", "document.batch_upsert", "Failed", 5, 5, 410, "2026-08-29T18:33:47Z", "", "", "Endpoint returned 410 Gone after 5 attempts"),
    ];

    /// <summary>Secret revealed once by a rotate; consumed by the next webhooks tab render.</summary>
    public (string WebhookId, string Url, string Secret)? PendingWebhookSecret { get; private set; }

    /// <summary>Webhook endpoints for the active project; seeds the demo endpoints on first read.</summary>
    public ImmutableArray<WebhookEndpoint> ProjectWebhooks
    {
        get
        {
            EnsureWebhookSeeds();
            return [.. Webhooks.Where(w => w.ProjectId == ActiveProjectId)];
        }
    }

    /// <summary>Port of the plan allowance check: "N of limit endpoints used".</summary>
    public (string Plan, int Used, int Limit) WebhookEndpointAllowance =>
        (WebhookPlanName, ProjectWebhooks.Length, WebhookPlanEndpointLimit);

    public ImmutableArray<WebhookDelivery> DeliveriesFor(string webhookId) =>
        [.. WebhookDeliveries.Where(d => d.WebhookId == webhookId)];

    /// <summary>Port of deleteWebhookEndpoint: drops the endpoint and its deliveries.</summary>
    public void RemoveWebhook(string webhookId)
    {
        Webhooks = [.. Webhooks.Where(w => w.Id != webhookId)];
        WebhookDeliveries = [.. WebhookDeliveries.Where(d => d.WebhookId != webhookId)];
    }

    /// <summary>Port of the pause/resume/enable/disable endpoint controls via a state field.</summary>
    public void ToggleWebhook(string webhookId, string state)
    {
        var status = state switch { "paused" => "Paused", "disabled" => "Disabled", _ => "Active" };
        Webhooks = [.. Webhooks.Select(w => w.Id == webhookId ? w with { Status = status } : w)];
    }

    public string RotateWebhookSecret(string webhookId)
    {
        var secret = $"whsec_{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant()}";
        Webhooks = [.. Webhooks.Select(w => w.Id == webhookId
            ? w with { SecretPreview = $"whsec_••••{secret[^4..]}" }
            : w)];
        return secret;
    }

    public void SavePendingWebhookSecret(string webhookId, string url, string secret) =>
        PendingWebhookSecret = (webhookId, url, secret);

    public (string WebhookId, string Url, string Secret)? ConsumePendingWebhookSecret()
    {
        var pending = PendingWebhookSecret;
        PendingWebhookSecret = null;
        return pending;
    }

    /// <summary>Port of replayWebhookDelivery: requeues the delivery for a fresh attempt.</summary>
    public void ReplayWebhookDelivery(string deliveryId) =>
        WebhookDeliveries = [.. WebhookDeliveries.Select(d => d.Id == deliveryId
            ? d with
            {
                Status = "Retrying",
                StatusCode = null,
                Error = "",
                DeliveredAt = "",
                NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(30).ToString("O"),
            }
            : d)];

    private void EnsureWebhookSeeds()
    {
        if (_webhookSeedsExtended)
        {
            return;
        }

        _webhookSeedsExtended = true;
        Webhooks =
        [
            .. Webhooks,
            new("wh_01JCB8VF2H4J6M1P4S7U0X3ZC", ActiveProjectId, "https://billing.internal.example.com/v1/terrascale/hooks", "Paused", "2026-04-02T13:26:00Z", "whsec_••••b21e"),
            new("wh_01JCB9WG3I5K7N2Q5T8V1Y4AD", ActiveProjectId, "https://legacy-partner.example.net/terrascale", "Disabled", "2025-11-20T10:05:00Z", "whsec_••••9d50"),
        ];
    }
}
