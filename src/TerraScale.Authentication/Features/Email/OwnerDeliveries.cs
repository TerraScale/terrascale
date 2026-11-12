using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Email;

public sealed record EmailDeliveryRecord(string Kind, string Key, string Recipient, string Status, DateTimeOffset? LeaseUntil = null);

public sealed class OwnerDeliveries(AuthData data, Users users, IEmailSender sender)
{
    private const int MaxRecipients = 100;

    public async Task<string> SendAsync(string kind, string key, string organizationId, EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (await this.ReadAsync(kind, key, cancellationToken).ConfigureAwait(false) is { Count: 0 })
        {
            var owners = await data.ReadAsync((db, token) => db.Memberships.Where(member => member.OrganizationId == organizationId && member.Role == Roles.Owner)
                .OrderBy(member => member.UserId).Select(member => member.UserId).Take(MaxRecipients + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
            if (owners.Count > MaxRecipients)
            {
                throw ApiException.Unavailable();
            }

            await data.SaveAsync([.. owners.Select(owner => Change.Insert(new EmailDeliveryRecord(kind, key, owner, "pending")))], cancellationToken).ConfigureAwait(false);
        }

        foreach (var reserved in await this.ReadAsync(kind, key, cancellationToken).ConfigureAwait(false))
        {
            if (reserved.Value.Status != "pending")
            {
                continue;
            }

            var claim = Change.Update(reserved, reserved.Value with { Status = "sending" });
            if (!await data.SaveAsync(claim, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var recipient = reserved.Value.Recipient;
            var owner = await data.ReadAsync((db, token) => db.Memberships.AnyAsync(member => member.OrganizationId == organizationId && member.UserId == recipient
                && member.Role == Roles.Owner, token), cancellationToken).ConfigureAwait(false);
            var user = await users.GetAsync(OidcIssuer.InstanceId, recipient, cancellationToken).ConfigureAwait(false);
            var outcome = owner && user is not null
                ? await sender.DeliverAsync(user.Email, message, cancellationToken).ConfigureAwait(false)
                : EmailDeliveryOutcome.Rejected;
            var status = outcome switch
            {
                EmailDeliveryOutcome.Accepted => "accepted",
                EmailDeliveryOutcome.Retryable => "pending",
                EmailDeliveryOutcome.Rejected => "rejected",
                _ => "unknown",
            };
            if (!await data.SaveAsync(Change.Update(reserved.Value with { Status = status }, claim.Version), CancellationToken.None).ConfigureAwait(false))
            {
                throw ApiException.Unavailable();
            }
        }

        var states = (await this.ReadAsync(kind, key, cancellationToken).ConfigureAwait(false)).Select(row => row.Value.Status).ToList();
        return states.Count == 0 ? "no_recipients" : states.Any(state => state is "unknown" or "sending") ? "unknown"
            : states.Contains("pending") ? "retryable" : states.Contains("rejected") ? "rejected" : "accepted";
    }

    private Task<List<Versioned<EmailDeliveryRecord>>> ReadAsync(string kind, string key, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.EmailDeliveries.Where(row => row.Kind == kind && row.Key == key).OrderBy(row => row.Recipient).WithVersion().ToListAsync(token),
            cancellationToken);
}
