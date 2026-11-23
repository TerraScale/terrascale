using Dapper;
using static OpenIddict.Abstractions.OpenIddictConstants;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Impersonation;

public readonly union StartImpersonationResult(ImpersonationTicket, Validation, NotFound, Forbidden);

public sealed record ImpersonationRecord(string Id, string InstanceId, string OrganizationId, string UserId, string ActorId,
    string ActorEmail, string ActorStamp, long ActorEpoch, string UserStamp, long UserEpoch, string Reason, string ClientId,
    string TicketHash, DateTimeOffset CreatedAt, DateTimeOffset TicketExpiresAt, DateTimeOffset? RedeemedAt = null,
    DateTimeOffset? ExpiresAt = null, DateTimeOffset? EndedAt = null, string? CookieHash = null, string? TicketOwner = null)
{
    public string? Version { get; init; }

    public override string ToString() => nameof(ImpersonationRecord);
}

public sealed record ImpersonationLive(ImpersonationRecord Record, UserRecord User);

public sealed class Impersonations(AuthData data, Credentials credentials, TenantDirectory tenants, PlatformDirectory platform, WebhookOutbox outbox, TimeProvider clock)
{
    public const string Claim = "ts_auth_impersonation";
    public const string Item = "ts-auth:impersonation";

    public static string Cookie(string tenant) => "__Host-tsauth_impersonation_" + tenant;

    public async Task<StartImpersonationResult> StartAsync(UserRecord actor, string project, string target, StartImpersonationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!Tenancy.IsProject(project) || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
        {
            return new Validation("Supply a reason of 1 to 500 characters and a project client_id.");
        }

        await tenants.CheckAccessAsync(actor.Id, project, Roles.Admin, cancellationToken);
        var user = await data.ReadUserAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @project AND id = @target AND disabled = 0", new { project, target }), cancellationToken);
        if (user is null)
        {
            return new NotFound("No such resource.", "not_found");
        }

        var clientId = request.ClientId;
        var client = await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<OidcApplication>(
            "SELECT * FROM oidc_applications WHERE instance_id = @project AND client_id = @clientId", new { project, clientId }), cancellationToken);
        if (client is null)
        {
            return new NotFound("No such resource.", "not_found");
        }

        if (!client.Permissions.Contains(Permissions.GrantTypes.AuthorizationCode))
        {
            return new Validation("Choose an authorization-code client in this project.");
        }

        var organization = (await platform.ProjectAsync(project, cancellationToken))?.Value.OrganizationId;
        if (organization is null)
        {
            return new NotFound("No such resource.", "not_found");
        }

        var proof = await credentials.RequireAsync(actor.InstanceId, actor.StepUpBindings, cancellationToken);
        if (actor.StepUpBindings.Count == 0 || proof is null)
        {
            return new Forbidden("A fresh, approved, purpose-bound single-use proof is required.", "step_up_required");
        }

        var ticket = Tokens.RandomToken(48);
        var now = clock.GetUtcNow();
        var record = new ImpersonationRecord(AuthIds.New(), project, organization, user.Id, actor.Id, actor.Email,
            actor.AuthStamp!, actor.AuthEpoch, user.AuthStamp!, user.AuthEpoch, request.Reason.Trim(), client.ClientId!,
            Tokens.Sha256Hex(ticket), now, now.AddSeconds(60));
        if (!await data.SaveAsync([.. proof, Change.Assert(actor), Change.Assert(user), Change.Assert(client), Change.Insert(record),
            this.Audit(record, "started"), this.Event(record, "started")], cancellationToken))
        {
            throw ApiException.Unavailable();
        }

        return new ImpersonationTicket(record.Id, ticket, record.TicketExpiresAt);
    }

    public async Task<(ImpersonationLive Live, string Cookie)> RedeemAsync(string tenant, string? client, string ticket, UserRecord? actor, Change? session, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(tenant) || ticket.Length is < 1 or > 256)
        {
            throw InvalidTicket();
        }

        var hash = Tokens.Sha256Hex(ticket);
        var current = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ImpersonationRecord>(
            "SELECT * FROM impersonations WHERE instance_id = @tenant AND ticket_hash = @hash", new { tenant, hash }), cancellationToken));
        if (current is null || actor is null || session is null || actor.InstanceId != OidcIssuer.InstanceId
            || actor.Id != current.Value.ActorId || actor.AuthStamp != current.Value.ActorStamp || actor.AuthEpoch != current.Value.ActorEpoch
            || current.Value.ClientId != client || current.Value.RedeemedAt is not null
            || current.Value.EndedAt is not null || current.Value.TicketExpiresAt <= clock.GetUtcNow()
            || await this.SubjectAsync(current.Value, cancellationToken) is not { } user)
        {
            throw InvalidTicket();
        }

        var lineage = await credentials.RequireAsync(actor.InstanceId, actor.AuthenticationBindings, cancellationToken);
        if (lineage is null)
        {
            throw InvalidTicket();
        }

        var cookie = Tokens.RandomToken(48);
        var binding = Credentials.New("impersonation-ticket", hash);
        var now = clock.GetUtcNow();
        var updated = current.Value with { RedeemedAt = now, ExpiresAt = now.AddMinutes(60), CookieHash = Tokens.Sha256Hex(cookie), TicketOwner = binding.OwnerId };
        if (!await data.SaveAsync([.. lineage, session, Change.Assert(actor), Change.Assert(user), Change.Update(current, updated), Credentials.Claim(tenant, binding), this.Audit(updated, "redeemed")], cancellationToken))
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
        var record = await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ImpersonationRecord>(
            "SELECT * FROM impersonations WHERE instance_id = @tenant AND cookie_hash = @hash", new { tenant, hash }), cancellationToken);
        return record is null ? null : await this.LiveAsync(record, cancellationToken);
    }

    public async Task<ImpersonationLive?> FindAsync(string tenant, string id, CancellationToken cancellationToken)
    {
        var record = (await this.ReadAsync(tenant, id, cancellationToken))?.Value;
        return record is null ? null : await this.LiveAsync(record, cancellationToken);
    }

    public async Task<IReadOnlyList<ImpersonationBody>> ListAsync(string project, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await data.ReadAsync(connection => connection.QueryAsync<ImpersonationRecord>(
            "SELECT * FROM impersonations WHERE instance_id = @project AND ended_at IS NULL AND expires_at > @now ORDER BY id DESC LIMIT 100", new { project, now }),
            cancellationToken);
        return [.. rows.Select(row => new ImpersonationBody(row.Id, row.UserId, row.ActorId, row.ActorEmail, row.ClientId, row.Reason, row.ExpiresAt!.Value))];
    }

    public async Task EndAsync(string project, string id, string endedBy, string reason, CancellationToken cancellationToken)
    {
        var current = await this.ReadAsync(project, id, cancellationToken) ?? throw PlatformDirectory.NotFound();
        if (current.Value.EndedAt is not null)
        {
            return;
        }

        var ended = current.Value with { EndedAt = clock.GetUtcNow() };
        if (!await data.SaveAsync([Change.Update(current, ended), .. ImpersonationEndings.Events(ended, reason, endedBy, outbox, clock)], cancellationToken))
        {
            throw new ApiException(409, "impersonation_conflict", "The impersonation changed. Reload its current state.");
        }
    }

    public async Task ExpireAsync(CancellationToken cancellationToken)
    {
        var after = "";
        while (true)
        {
            var rows = (await data.ReadAsync(connection => connection.QueryAsync<ImpersonationRecord>(
                "SELECT * FROM impersonations WHERE ended_at IS NULL AND id > @after ORDER BY id LIMIT 100", new { after }), cancellationToken)).AsList();
            foreach (var record in rows)
            {
                if ((record.ExpiresAt ?? record.TicketExpiresAt) <= clock.GetUtcNow())
                {
                    await this.EndAsync(record.InstanceId, record.Id, record.ActorId, record.RedeemedAt is null ? "ticket_expired" : "expired", cancellationToken);
                }
                else
                {
                    _ = await this.SubjectAsync(record, cancellationToken);
                }
            }

            if (rows.Count < 100)
            {
                return;
            }

            after = rows[^1].Id;
        }
    }

    private async Task<ImpersonationLive?> LiveAsync(ImpersonationRecord record, CancellationToken cancellationToken)
    {
        if (record.EndedAt is not null || record.RedeemedAt is null || record.ExpiresAt is not { } expires)
        {
            return null;
        }

        if (expires <= clock.GetUtcNow())
        {
            await this.EndAsync(record.InstanceId, record.Id, record.ActorId, "expired", cancellationToken);
            return null;
        }

        if (await this.SubjectAsync(record, cancellationToken) is not { } user)
        {
            return null;
        }

        if (record.TicketOwner is null || !await credentials.AreCurrentAsync(record.InstanceId, [TicketBinding(record)], cancellationToken))
        {
            await this.EndAsync(record.InstanceId, record.Id, record.ActorId, "ticket_revoked", cancellationToken);
            return null;
        }

        return new(record, Bind(user, record));
    }

    private async Task<UserRecord?> SubjectAsync(ImpersonationRecord record, CancellationToken cancellationToken)
    {
        var (platform, actorId) = (OidcIssuer.InstanceId, record.ActorId);
        var actor = await data.ReadUserAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @platform AND id = @actorId", new { platform, actorId }), cancellationToken);
        if (actor is null || actor.Disabled || actor.AuthEpoch != record.ActorEpoch || actor.AuthStamp != record.ActorStamp)
        {
            await this.EndAsync(record.InstanceId, record.Id, record.ActorId, actor is null ? "admin_deleted" : actor.Disabled ? "admin_disabled" : "admin_changed", cancellationToken);
            return null;
        }

        try
        {
            await tenants.CheckAccessAsync(actor.Id, record.InstanceId, Roles.Admin, cancellationToken);
        }
        catch (ApiException error) when (error.Status is 403 or 404)
        {
            await this.EndAsync(record.InstanceId, record.Id, record.ActorId, "admin_role_lost", cancellationToken);
            return null;
        }

        var (instance, userId) = (record.InstanceId, record.UserId);
        var user = await data.ReadUserAsync(connection => connection.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM users WHERE instance_id = @instance AND id = @userId", new { instance, userId }), cancellationToken);
        if (user is null || user.Disabled || user.AuthEpoch != record.UserEpoch || user.AuthStamp != record.UserStamp)
        {
            await this.EndAsync(record.InstanceId, record.Id, record.ActorId, user is null ? "target_deleted" : user.Disabled ? "target_disabled" : "target_changed", cancellationToken);
            return null;
        }

        return user;
    }

    private static CredentialBinding TicketBinding(ImpersonationRecord record) => new("credential:impersonation-ticket", record.TicketHash, record.TicketOwner!);

    private static UserRecord Bind(UserRecord user, ImpersonationRecord record) =>
        user with { AuthenticationBindings = [Credentials.UserState(user.Id, user.AuthStamp!), TicketBinding(record)] };

    private Change Audit(ImpersonationRecord record, string action) =>
        platform.Audit(record.OrganizationId, record.ActorId, "impersonation." + action, record.UserId,
            $"impersonation={record.Id}; actor={record.ActorId}; reason={record.Reason}");

    private Change Event(ImpersonationRecord record, string action) =>
        Change.Insert(outbox.NewEvent(record.InstanceId, "impersonation." + action, record.UserId, sessionId: record.Id) with { ActorId = record.ActorId, Reason = record.Reason });

    private static ApiException InvalidTicket() => new(400, "invalid_impersonation_ticket", "The impersonation ticket is expired, consumed or not valid for this client.");

    private async Task<Versioned<ImpersonationRecord>?> ReadAsync(string tenant, string id, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ImpersonationRecord>(
            "SELECT * FROM impersonations WHERE instance_id = @tenant AND id = @id", new { tenant, id }), cancellationToken));
}
