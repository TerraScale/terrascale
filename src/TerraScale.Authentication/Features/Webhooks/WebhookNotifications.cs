using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class WebhookNotifications(AuthData data, IEmailSender sender, TimeProvider clock)
{
    private const string Kind = "webhook_disabled";

    public async Task<IReadOnlyList<Change>> ReserveAsync(string projectId, string endpointId, CancellationToken cancellationToken)
    {
        var (owner, admin) = (Roles.Owner, Roles.Admin);
        var recipients = await data.ReadAsync(connection => connection.QueryAsync<string>(
            """
            SELECT m.user_id FROM memberships m WHERE m.organization_id = (SELECT organization_id FROM projects WHERE id = @projectId) AND m.role IN (@owner, @admin)
            UNION SELECT g.user_id FROM project_members g WHERE g.project_id = @projectId AND g.role = @admin
            """, new { projectId, owner, admin }), cancellationToken);
        return [.. recipients.Select(userId => Change.Insert(new EmailDeliveryRecord(Kind, endpointId, userId, "pending")))];
    }

    public async Task SendAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var pending = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<EmailDeliveryRecord>(
            """
            SELECT * FROM email_deliveries WHERE kind = @Kind AND (status = 'pending' OR status = 'sending' AND (lease_until IS NULL OR lease_until <= @now))
            ORDER BY "key" LIMIT 50
            """, new { Kind, now }), cancellationToken));
        foreach (var row in pending)
        {
            var (endpointId, recipientId, platform) = (row.Value.Key, row.Value.Recipient, OidcIssuer.InstanceId);
            var endpoint = await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<WebhookEndpointRecord>(
                "SELECT * FROM webhook_endpoints WHERE id = @endpointId", new { endpointId }), cancellationToken);
            var recipient = await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
                "SELECT * FROM users WHERE id = @recipientId AND instance_id = @platform", new { recipientId, platform }), cancellationToken);
            var claim = Change.Update(row, row.Value with { Status = "sending", LeaseUntil = clock.GetUtcNow().AddMinutes(2) });
            if (!await data.SaveAsync(claim, cancellationToken))
            {
                continue;
            }

            var projectId = endpoint?.ProjectId;
            var organizationId = endpoint is null ? null : await data.ReadAsync(connection => connection.QuerySingleAsync<string>(
                "SELECT organization_id FROM projects WHERE id = @projectId", new { projectId }), cancellationToken);
            var outcome = endpoint is not null && recipient is not null && organizationId is not null
                ? await sender.DeliverAsync(recipient.Email, new EmailMessage("Webhook endpoint disabled",
                    $"Your webhook endpoint {MaskedUrl(endpoint.Url)} in project {endpoint.ProjectId} was disabled after five days of consecutive failed delivery attempts. Register a working replacement.")
                { Tenant = organizationId }, cancellationToken)
                : EmailDeliveryOutcome.Rejected;
            var status = outcome switch
            {
                EmailDeliveryOutcome.Accepted => "accepted",
                EmailDeliveryOutcome.Retryable => "pending",
                EmailDeliveryOutcome.Rejected => "rejected",
                EmailDeliveryOutcome.Unknown => "unknown",
                _ => throw new InvalidOperationException("Unknown email delivery outcome."),
            };
            if (!await data.SaveAsync(Change.Update(row.Value with { Status = status, LeaseUntil = null }, claim.Version), CancellationToken.None))
            {
                throw new InvalidOperationException("The webhook notification claim changed during delivery.");
            }
        }
    }

    private static string MaskedUrl(string url)
    {
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = segments.Length == 0 ? "/" : "/" + segments[0] + (segments.Length > 1 ? "/[redacted]" : "");
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }
}
