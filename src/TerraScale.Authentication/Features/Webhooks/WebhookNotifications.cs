using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Webhooks;

public sealed class WebhookNotifications(AuthData data, IEmailSender sender)
{
    private const string Kind = "webhook_disabled";

    public async Task<IReadOnlyList<Change>> ReserveAsync(string projectId, string endpointId, CancellationToken cancellationToken)
    {
        var organizationId = await data.ReadAsync((db, token) => db.Projects.Where(row => row.Id == projectId)
            .Select(row => row.OrganizationId).SingleAsync(token), cancellationToken).ConfigureAwait(false);
        var recipients = await data.ReadAsync((db, token) => db.Memberships
            .Where(row => row.OrganizationId == organizationId && (row.Role == Roles.Owner || row.Role == Roles.Admin))
            .Select(row => row.UserId).Union(db.ProjectMembers.Where(row => row.ProjectId == projectId && row.Role == Roles.Admin)
                .Select(row => row.UserId)).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return [.. recipients.Select(userId => Change.Insert(new EmailDeliveryRecord(Kind, endpointId, userId, "pending")))];
    }

    public async Task SendAsync(CancellationToken cancellationToken)
    {
        var pending = await data.ReadAsync((db, token) => db.EmailDeliveries.Where(row => row.Kind == Kind && row.Status == "pending")
            .OrderBy(row => row.Key).Take(50).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        foreach (var row in pending)
        {
            var endpoint = await data.ReadAsync((db, token) => db.WebhookEndpoints.SingleOrDefaultAsync(item => item.Id == row.Value.Key, token), cancellationToken).ConfigureAwait(false);
            var recipient = await data.ReadAsync((db, token) => db.Users.SingleOrDefaultAsync(user => user.Id == row.Value.Recipient && user.InstanceId == OidcIssuer.InstanceId, token), cancellationToken).ConfigureAwait(false);
            var claim = Change.Update(row, row.Value with { Status = "sending" });
            if (!await data.SaveAsync(claim, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var outcome = endpoint is not null && recipient is not null
                ? await sender.DeliverAsync(recipient.Email, new EmailMessage("Webhook endpoint disabled",
                    $"Your webhook endpoint {endpoint.Url} in project {endpoint.ProjectId} was disabled after five days of consecutive failed delivery attempts. Register a working replacement.")
                { Tenant = endpoint.ProjectId }, cancellationToken).ConfigureAwait(false)
                : EmailDeliveryOutcome.Rejected;
            var status = outcome switch
            {
                EmailDeliveryOutcome.Accepted => "accepted",
                EmailDeliveryOutcome.Retryable => "pending",
                EmailDeliveryOutcome.Rejected => "rejected",
                EmailDeliveryOutcome.Unknown => "unknown",
                _ => throw new InvalidOperationException("Unknown email delivery outcome."),
            };
            if (!await data.SaveAsync(Change.Update(row.Value with { Status = status }, claim.Version), CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The webhook notification claim changed during delivery.");
            }
        }
    }
}
