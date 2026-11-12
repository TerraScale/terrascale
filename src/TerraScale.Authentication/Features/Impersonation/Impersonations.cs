using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Impersonation;

public sealed record ImpersonationRecord(string Id, string InstanceId, string OrganizationId, string UserId, string ActorId,
    string ActorEmail, string ActorStamp, long ActorEpoch, string UserStamp, long UserEpoch, string Reason, string ClientId,
    string TicketHash, DateTimeOffset CreatedAt, DateTimeOffset TicketExpiresAt, DateTimeOffset? RedeemedAt = null,
    DateTimeOffset? ExpiresAt = null, DateTimeOffset? EndedAt = null, string? CookieHash = null, string? TicketOwner = null)
{
    public override string ToString() => nameof(ImpersonationRecord);
}

public sealed record ImpersonationLive(ImpersonationRecord Record, UserRecord User);

public sealed class Impersonations(AuthData data, Credentials credentials, TenantDirectory tenants, PlatformDirectory platform, WebhookOutbox outbox, TimeProvider clock)
{
    public const string Claim = "ts_auth_impersonation";
    public const string Item = "ts-auth:impersonation";

    public static string Cookie(string tenant) => "__Host-tsauth_impersonation_" + tenant;

    public async Task<ImpersonationTicket> StartAsync(UserRecord actor, string project, string target, StartImpersonationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!Tenancy.IsProject(project) || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
        {
            throw new ApiException(400, "invalid_request", "Supply a reason of 1 to 500 characters and a project client_id.");
        }

        await tenants.CheckAccessAsync(actor.Id, project, Roles.Admin, cancellationToken).ConfigureAwait(false);
        var user = await data.ReadAsync((db, token) => db.Users.SingleOrDefaultAsync(row => row.InstanceId == project && row.Id == target && !row.Disabled, token), cancellationToken).ConfigureAwait(false)
            ?? throw PlatformDirectory.NotFound();
        var client = await data.ReadAsync((db, token) => db.OidcApplications.SingleOrDefaultAsync(row => row.InstanceId == project && row.ClientId == request.ClientId, token), cancellationToken).ConfigureAwait(false)
            ?? throw PlatformDirectory.NotFound();
        if (!client.Permissions.Contains(Permissions.GrantTypes.AuthorizationCode))
        {
            throw new ApiException(400, "invalid_request", "Choose an authorization-code client in this project.");
        }

        var organization = (await platform.ProjectAsync(project, cancellationToken).ConfigureAwait(false))?.Value.OrganizationId
            ?? throw PlatformDirectory.NotFound();
        var proof = await credentials.RequireAsync(actor.InstanceId, actor.StepUpBindings, cancellationToken).ConfigureAwait(false);
        if (actor.StepUpBindings.Count == 0 || proof is null)
        {
            throw StepUpProofs.Required();
        }

        var ticket = Tokens.RandomToken(48);
        var now = clock.GetUtcNow();
        var record = new ImpersonationRecord(AuthIds.New(), project, organization, user.Id, actor.Id, actor.Email,
            actor.AuthStamp!, actor.AuthEpoch, user.AuthStamp!, user.AuthEpoch, request.Reason.Trim(), client.ClientId!,
            Tokens.Sha256Hex(ticket), now, now.AddSeconds(60));
        if (!await data.SaveAsync([.. proof, Change.Assert(actor), Change.Assert(user), Change.Assert(client), Change.Insert(record),
            this.Audit(record, "started"), this.Event(record, "started")], cancellationToken).ConfigureAwait(false))
        {
            throw ApiException.Unavailable();
        }

        return new(record.Id, ticket, record.TicketExpiresAt);
    }

    public async Task<(ImpersonationLive Live, string Cookie)> RedeemAsync(string tenant, string? client, string ticket, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(tenant) || ticket.Length is < 1 or > 256)
        {
            throw InvalidTicket();
        }

        var hash = Tokens.Sha256Hex(ticket);
        var current = await data.ReadAsync((db, token) => db.Impersonations.Where(row => row.InstanceId == tenant && row.TicketHash == hash)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (current is null || current.Value.ClientId != client || current.Value.RedeemedAt is not null
            || current.Value.EndedAt is not null || current.Value.TicketExpiresAt <= clock.GetUtcNow()
            || await this.SubjectAsync(current.Value, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            throw InvalidTicket();
        }

        var cookie = Tokens.RandomToken(48);
        var binding = Credentials.New("impersonation-ticket", hash);
        var now = clock.GetUtcNow();
        var updated = current.Value with { RedeemedAt = now, ExpiresAt = now.AddMinutes(60), CookieHash = Tokens.Sha256Hex(cookie), TicketOwner = binding.OwnerId };
        if (!await data.SaveAsync([Change.Assert(user), Change.Update(current, updated), Credentials.Claim(tenant, binding), this.Audit(updated, "redeemed")], cancellationToken).ConfigureAwait(false))
        {
            throw InvalidTicket();
        }

        return (new(updated, Bind(user, updated)), cookie);
    }

    public async Task<ImpersonationLive?> ResolveAsync(string tenant, string? cookie, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(tenant) || cookie is not { Length: > 0 and <= 256 })
        {
            return null;
        }

        var hash = Tokens.Sha256Hex(cookie);
        var record = await data.ReadAsync((db, token) => db.Impersonations.SingleOrDefaultAsync(row => row.InstanceId == tenant && row.CookieHash == hash, token), cancellationToken).ConfigureAwait(false);
        return record is null ? null : await this.LiveAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImpersonationLive?> FindAsync(string tenant, string id, CancellationToken cancellationToken)
    {
        var record = await data.ReadAsync((db, token) => db.Impersonations.SingleOrDefaultAsync(row => row.InstanceId == tenant && row.Id == id, token), cancellationToken).ConfigureAwait(false);
        return record is null ? null : await this.LiveAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImpersonationBody>> ListAsync(string project, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await data.ReadAsync((db, token) => db.Impersonations.Where(row => row.InstanceId == project && row.EndedAt == null && row.ExpiresAt > now)
            .OrderByDescending(row => row.Id).Take(100).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => new ImpersonationBody(row.Id, row.UserId, row.ActorId, row.ActorEmail, row.ClientId, row.Reason, row.ExpiresAt!.Value))];
    }

    public async Task EndAsync(string project, string id, string endedBy, CancellationToken cancellationToken)
    {
        var current = await data.ReadAsync((db, token) => db.Impersonations.Where(row => row.InstanceId == project && row.Id == id)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (current.Value.EndedAt is not null)
        {
            return;
        }

        var ended = current.Value with { EndedAt = clock.GetUtcNow() };
        if (!await data.SaveAsync([Change.Update(current, ended), this.Audit(ended, "ended", endedBy), this.Event(ended, "ended")], cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(409, "impersonation_conflict", "The impersonation changed. Reload its current state.");
        }
    }

    public async Task ExpireAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await data.ReadAsync((db, token) => db.Impersonations.Where(row => row.EndedAt == null
            && (row.ExpiresAt <= now || row.RedeemedAt == null && row.TicketExpiresAt <= now))
            .OrderBy(row => row.Id).Take(100).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        foreach (var current in rows)
        {
            var ended = current.Value with { EndedAt = now };
            _ = await data.SaveAsync([Change.Update(current, ended), this.Audit(ended, "ended"), this.Event(ended, "ended")], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ImpersonationLive?> LiveAsync(ImpersonationRecord record, CancellationToken cancellationToken)
    {
        if (record.EndedAt is not null || record.RedeemedAt is null || record.ExpiresAt is not { } expires || expires <= clock.GetUtcNow()
            || record.TicketOwner is null || await this.SubjectAsync(record, cancellationToken).ConfigureAwait(false) is not { } user
            || !await credentials.AreCurrentAsync(record.InstanceId, [TicketBinding(record)], cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new(record, Bind(user, record));
    }

    private async Task<UserRecord?> SubjectAsync(ImpersonationRecord record, CancellationToken cancellationToken)
    {
        var actor = await data.ReadAsync((db, token) => db.Users.SingleOrDefaultAsync(row => row.InstanceId == OidcIssuer.InstanceId && row.Id == record.ActorId, token), cancellationToken).ConfigureAwait(false);
        if (actor is null || actor.Disabled || actor.AuthEpoch != record.ActorEpoch || actor.AuthStamp != record.ActorStamp)
        {
            return null;
        }

        try
        {
            await tenants.CheckAccessAsync(actor.Id, record.InstanceId, Roles.Admin, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException error) when (error.Status is 403 or 404)
        {
            return null;
        }

        var user = await data.ReadAsync((db, token) => db.Users.SingleOrDefaultAsync(row => row.InstanceId == record.InstanceId && row.Id == record.UserId, token), cancellationToken).ConfigureAwait(false);
        return user is not null && !user.Disabled && user.AuthEpoch == record.UserEpoch && user.AuthStamp == record.UserStamp ? user : null;
    }

    private static CredentialBinding TicketBinding(ImpersonationRecord record) => new("credential:impersonation-ticket", record.TicketHash, record.TicketOwner!);

    private static UserRecord Bind(UserRecord user, ImpersonationRecord record) =>
        user with { AuthenticationBindings = [Credentials.UserState(user.Id, user.AuthStamp!), TicketBinding(record)] };

    private Change Audit(ImpersonationRecord record, string action, string? endedBy = null) =>
        platform.Audit(record.OrganizationId, endedBy ?? record.ActorId, "impersonation." + action, record.UserId,
            $"impersonation={record.Id}; actor={record.ActorId}; reason={record.Reason}");

    private Change Event(ImpersonationRecord record, string action) =>
        Change.Insert(outbox.NewEvent(record.InstanceId, "impersonation." + action, record.UserId, sessionId: record.Id) with { ActorId = record.ActorId, Reason = record.Reason });

    private static ApiException InvalidTicket() => new(400, "invalid_impersonation_ticket", "The impersonation ticket is expired, consumed or not valid for this client.");
}
