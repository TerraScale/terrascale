using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Email;

public sealed record EmailDeliveryRecord(string Kind, string Key, string Recipient, string Status, DateTimeOffset? LeaseUntil = null)
{
    public string? Version { get; init; }
}

public sealed class OwnerDeliveries(AuthData data, Users users, IEmailSender sender)
{
    private const int MaxRecipients = 100;

    public async Task<string> SendAsync(string kind, string key, string organizationId, EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (await this.ReadAsync(kind, key, cancellationToken) is { Count: 0 })
        {
            var (owner, take) = (Roles.Owner, MaxRecipients + 1);
            var owners = (await data.ReadAsync(connection => connection.QueryAsync<string>(
                "SELECT user_id FROM memberships WHERE organization_id = @organizationId AND role = @owner ORDER BY user_id LIMIT @take", new { organizationId, owner, take }),
                cancellationToken)).AsList();
            if (owners.Count > MaxRecipients)
            {
                throw ApiException.Unavailable();
            }

            await data.SaveAsync([.. owners.Select(owner => Change.Insert(new EmailDeliveryRecord(kind, key, owner, "pending")))], cancellationToken);
        }

        foreach (var reserved in await this.ReadAsync(kind, key, cancellationToken))
        {
            if (reserved.Value.Status != "pending")
            {
                continue;
            }

            var claim = Change.Update(reserved, reserved.Value with { Status = "sending" });
            if (!await data.SaveAsync(claim, cancellationToken))
            {
                continue;
            }

            var recipient = reserved.Value.Recipient;
            var ownerRole = Roles.Owner;
            var owner = await data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM memberships WHERE organization_id = @organizationId AND user_id = @recipient AND role = @ownerRole)",
                new { organizationId, recipient, ownerRole }), cancellationToken);
            var user = await users.GetAsync(OidcIssuer.InstanceId, recipient, cancellationToken);
            var outcome = owner && user is not null
                ? await sender.DeliverAsync(user.Email, message, cancellationToken)
                : EmailDeliveryOutcome.Rejected;
            var status = outcome switch
            {
                EmailDeliveryOutcome.Accepted => "accepted",
                EmailDeliveryOutcome.Retryable => "pending",
                EmailDeliveryOutcome.Rejected => "rejected",
                _ => "unknown",
            };
            if (!await data.SaveAsync(Change.Update(reserved.Value with { Status = status }, claim.Version), CancellationToken.None))
            {
                throw ApiException.Unavailable();
            }
        }

        var states = (await this.ReadAsync(kind, key, cancellationToken)).Select(row => row.Value.Status).ToList();
        return states.Count == 0 ? "no_recipients" : states.Any(state => state is "unknown" or "sending") ? "unknown"
            : states.Contains("pending") ? "retryable" : states.Contains("rejected") ? "rejected" : "accepted";
    }

    private async Task<IReadOnlyList<Versioned<EmailDeliveryRecord>>> ReadAsync(string kind, string key, CancellationToken cancellationToken) =>
        Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<EmailDeliveryRecord>(
            "SELECT * FROM email_deliveries WHERE kind = @kind AND \"key\" = @key ORDER BY recipient", new { kind, key }), cancellationToken));
}
