using Dapper;
using ZLogger;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.AttackProtection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.Identity;

public sealed class SessionPruner(AuthSchema schema, Sessions sessions, Passcodes passcodes, Challenges challenges, StepUpProofs proofs, AgentEnrollments enrollments, OidcTokenStore oidcTokens, OidcAuthorizationStore oidcAuthorizations, ILogger<SessionPruner> logger, AccountProtection protection, AgentAssertions agents, AgentIdTransactions agentSignIns, AuthData data, TimeProvider clock) : BackgroundService
{
    internal const int BatchSize = 500;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ClaimRetention = TimeSpan.FromDays(400);

    private static readonly TimeSpan ReplayRetention = TimeSpan.FromDays(2);

    private static readonly TimeSpan RateLimitRetention = TimeSpan.FromDays(7);

    private static readonly TimeSpan AuditRetention = TimeSpan.FromDays(400);

    private static readonly TimeSpan DeliveryRetention = TimeSpan.FromDays(90);

    private static readonly TimeSpan InvitationRetention = TimeSpan.FromDays(30);

    private static readonly byte[] NoRandomness = new byte[10];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await this.PruneAsync("attack protection", protection.PruneAsync, stoppingToken);
            await this.PruneAsync("sessions", sessions.PruneAsync, stoppingToken);
            await this.PruneAsync("passcodes", passcodes.PruneAsync, stoppingToken);
            await this.PruneAsync("challenges", challenges.PruneAsync, stoppingToken);
            await this.PruneAsync("step-up proofs", proofs.PruneAsync, stoppingToken);
            await this.PruneAsync("agent enrollments", enrollments.PruneAsync, stoppingToken);
            await this.PruneAsync("OIDC tokens", oidcTokens.PruneAsync, stoppingToken);
            await this.PruneAsync("OIDC authorizations", oidcAuthorizations.PruneAsync, stoppingToken);
            await this.PruneAsync("agent assertions", agents.PruneAsync, stoppingToken);
            await this.PruneAsync("agent sign-in transactions", agentSignIns.PruneAsync, stoppingToken);
            await this.PruneAsync("credential claims", this.PruneClaimsAsync, stoppingToken);
            await this.PruneAsync("rate limits", this.PruneRateLimitsAsync, stoppingToken);
            await this.PruneAsync("audit events", this.PruneAuditEventsAsync, stoppingToken);
            await this.PruneAsync("email deliveries", this.PruneEmailDeliveriesAsync, stoppingToken);
            await this.PruneAsync("support deliveries", this.PruneSupportDeliveriesAsync, stoppingToken);
            await this.PruneAsync("project invitations", this.PruneInvitationsAsync, stoppingToken);
            await this.PruneAsync("project waitlist", this.PruneWaitlistAsync, stoppingToken);
        }
    }

    private Task<int> PruneClaimsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var (cutoff, replayCutoff, replay, credential, platform) =
            (this.Before(ClaimRetention), this.Before(ReplayRetention), GitHubActionsGrant.TokenIdKind, "credential:%", OidcIssuer.InstanceId);
        return data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            """
            DELETE FROM credential_claims WHERE (instance_id, kind, "key") IN (
                SELECT c.instance_id, c.kind, c."key" FROM credential_claims c
                WHERE c.kind = @replay AND c.version < @replayCutoff
                    OR c.kind LIKE @credential AND c.version < @cutoff
                        AND (c.instance_id <> @platform OR NOT EXISTS (
                            SELECT 1 FROM oidc_applications a
                            WHERE instr(a.secret_bindings, '{"kind":"' || c.kind || '","key":"' || c."key" || '","ownerId":"' || c.owner_id || '"}') > 0
                            UNION ALL
                            SELECT 1 FROM api_keys k
                            WHERE instr(k.secret_bindings, '{"kind":"' || c.kind || '","key":"' || c."key" || '","ownerId":"' || c.owner_id || '"}') > 0
                                OR instr(k.previous_secret_bindings, '{"kind":"' || c.kind || '","key":"' || c."key" || '","ownerId":"' || c.owner_id || '"}') > 0))
                LIMIT @batchSize)
            """, new { replay, replayCutoff, credential, cutoff, platform, batchSize }), cancellationToken);
    }

    private Task<int> PruneRateLimitsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var cutoff = this.Before(RateLimitRetention);
        return data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            """
            DELETE FROM rate_limits WHERE (instance_id, "key") IN (
                SELECT instance_id, "key" FROM rate_limits WHERE version < @cutoff LIMIT @batchSize)
            """, new { cutoff, batchSize }), cancellationToken);
    }

    private Task<int> PruneAuditEventsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var cutoff = this.Before(AuditRetention);
        return data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            "DELETE FROM audit_events WHERE id IN (SELECT id FROM audit_events WHERE id < @cutoff ORDER BY id LIMIT @batchSize)",
            new { cutoff, batchSize }), cancellationToken);
    }

    private Task<int> PruneEmailDeliveriesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var cutoff = this.Before(DeliveryRetention);
        return data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            """
            DELETE FROM email_deliveries WHERE (kind, "key", recipient) IN (
                SELECT kind, "key", recipient FROM email_deliveries
                WHERE status IN ('accepted', 'rejected', 'unknown') AND version < @cutoff LIMIT @batchSize)
            """, new { cutoff, batchSize }), cancellationToken);
    }

    private Task<int> PruneSupportDeliveriesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var cutoff = this.Before(DeliveryRetention);
        return data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            "DELETE FROM support_deliveries WHERE request_id IN (SELECT request_id FROM support_deliveries WHERE version < @cutoff LIMIT @batchSize)",
            new { cutoff, batchSize }), cancellationToken);
    }

    private Task<int> PruneInvitationsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var (cutoff, expired) = (this.Before(InvitationRetention), clock.GetUtcNow() - InvitationRetention);
        return data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            """
            DELETE FROM project_invitations WHERE id IN (
                SELECT id FROM project_invitations
                WHERE status <> 'pending' AND version < @cutoff OR status = 'pending' AND expires_at <= @expired
                LIMIT @batchSize)
            """, new { cutoff, expired, batchSize }), cancellationToken);
    }

    private Task<int> PruneWaitlistAsync(int batchSize, CancellationToken cancellationToken)
    {
        var cutoff = this.Before(InvitationRetention);
        return data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            "DELETE FROM project_waitlist WHERE id IN (SELECT id FROM project_waitlist WHERE status = 'approved' AND version < @cutoff LIMIT @batchSize)",
            new { cutoff, batchSize }), cancellationToken);
    }

    private string Before(TimeSpan age) => Ulid.NewUlid(clock.GetUtcNow() - age, NoRandomness).ToString();

    private async Task PruneAsync(string store, Func<int, CancellationToken, Task<int>> prune, CancellationToken stoppingToken)
    {
        try
        {
            await prune(BatchSize, stoppingToken);
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"Pruning {store} failed (TerraBase code {Code(error)}); retrying at the next interval");
        }
    }

    private static string Code(Exception error) => error switch
    {
        TerraBaseException { ErrorCode: { } code } => code,
        { InnerException: { } inner } => Code(inner),
        _ => "none",
    };
}
