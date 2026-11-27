using Dapper;
using System.Data;
using System.Linq;
using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Observability;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.Dashboard.Platform.Clients;
using TerraBaseStorageException = TerraScale.DotNet.TerraBase.TerraBaseException;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class DeletionReconciler(ITsAuthDeletions deletions, DashboardDb storage, DashboardDbUpRunner initializer, AccountStore accounts, OrganizationStore organizations, DashboardSessions sessions,
        AccessTokens tokens, StripeBilling billing, IDashboardIdentity identity, TimeProvider clock, ServiceSloTelemetry telemetry, ILogger<DeletionReconciler> logger, BackgroundJobs jobs, BillingSchemaGate gate) : BackgroundService
    {
        private const string RemoveSavedQueries = "DELETE FROM saved_queries WHERE organization_id = @OrganizationId";
        private const string ObservedProjects = """
            SELECT project_id FROM alert_rules WHERE organization_id = @OrganizationId
            UNION SELECT project_id FROM log_drains WHERE organization_id = @OrganizationId
            """;
        private const string RemoveStripe = """
            DELETE FROM stripe_checkouts WHERE organization_id = @OrganizationId;
            DELETE FROM stripe_customers WHERE organization_id = @OrganizationId
            """;

        private const string CleanupDue = """
            SELECT EXISTS (SELECT 1 FROM deletion_cleanup
                    WHERE targets_complete = 1
                        AND (acknowledged_at IS NULL OR (scope = 'organization' AND billing_settled_at IS NULL AND acknowledged_at <= @CheckoutExpired)
                            OR (acknowledged_at < @Retained AND (scope <> 'organization' OR billing_settled_at < @Retained))))
                OR EXISTS (SELECT 1 FROM account_deletions WHERE completed_until <= @Now OR (completed_until IS NULL AND created_at <= @Expired))
            """;

        private const string AuditExpired = "SELECT EXISTS (SELECT 1 FROM audit_events WHERE occurred_at < @AuditCutoff)";

        private static readonly TimeSpan AuditPruneInterval = TimeSpan.FromHours(1);
        private readonly Dictionary<string, string> failing = new(StringComparer.Ordinal);
        private readonly HashSet<string> reported = new(StringComparer.Ordinal);
        private long auditPrunedAt;
        private readonly DeletionCleanupJournal journal = new(storage, organizations, clock);
        private readonly DeletionProjectRecords projectRecords = new(storage);
        private readonly DeletionRelatedRecords related = new(storage, accounts);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await initializer.WhenInitialized.WaitAsync(stoppingToken);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
                do
                {
                    try { await RunOnceAsync(stoppingToken); }
                    catch (Exception error) when (Retryable(error, stoppingToken)) { Retry(error); }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (!await gate.AllowsAsync(cancellationToken)) { return; }
            IReadOnlyList<TsAuthDeletionItem> peeked = [];
            await jobs.RunAsync("deletion-cleanup", TimeSpan.Zero, async (claim, token) =>
            {
                var failed = new Dictionary<string, string>(StringComparer.Ordinal);
                var cleaned = new HashSet<string>(StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    await journal.PruneAsync(token);
                    await AccountDeletion.WipeExpiredAsync(storage, clock, token);
                    if (AuditPruneDue())
                    {
                        auditPrunedAt = clock.GetTimestamp();
                        await organizations.PruneAuditAsync(token);
                    }

                    var pending = await journal.ListPendingAsync(token);
                    foreach (var id in pending.Malformed)
                    {
                        if (Report(seen, "row:" + id))
                        {
                            logger.DashboardDeletionJournalRowMalformed(id);
                        }
                    }

                    foreach (var entry in pending.Entries)
                    {
                        try
                        {
                            var started = clock.GetUtcNow().UtcDateTime;
                            await CleanAsync(entry, token);
                            await SettleAsync(entry, started, token);
                            cleaned.Add(entry.Proof.Id);
                        }
                        catch (Exception error) when (Retryable(error, token)) { Fail(failed, entry.Proof, error); }
                    }
                }
                catch (Exception error) when (Retryable(error, token)) { Retry(error); }
                IReadOnlyList<TsAuthDeletionItem> items;
                try { items = await deletions.ListAsync(peek: false, token); }
                catch (Exception error) when (Retryable(error, token)) { Retry(error); items = []; }
                var proofs = new List<TsAuthDeletionProof>(peeked.Count + items.Count);
                var proofIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in peeked.Concat(items))
                {
                    var accepted = item switch
                    {
                        TsAuthDeletionProof proof => proof,
                        TsAuthRejectedDeletion rejected => Reject(seen, rejected),
                    };
                    if (accepted is not null && proofIds.Add(accepted.Id))
                    {
                        proofs.Add(accepted);
                    }
                }

                foreach (var proof in proofs)
                {
                    try
                    {
                        var remembered = await journal.RememberAsync(proof, token);
                        var entry = remembered switch
                        {
                            DeletionCleanupJournal.Entry saved => saved,
                            StorageConflicted => FailStorage(failed, proof, "StorageConflicted", "conflict"),
                            StorageFailed failure => FailStorage(failed, proof, "StorageFailed", failure.Failure),
                        };
                        if (entry is null)
                        {
                            continue;
                        }

                        await CleanAsync(entry, token);
                        await deletions.AcknowledgeAsync(proof, token);
                        await journal.AcknowledgedAsync(proof.Id, token);
                        cleaned.Add(proof.Id);
                    }
                    catch (Exception error) when (Retryable(error, token)) { Fail(failed, proof, error); }
                }

                reported.IntersectWith(seen);
                cleaned.ExceptWith(failed.Keys);
                foreach (var id in cleaned)
                {
                    failing.Remove(id);
                }

                foreach (var (id, cause) in failed)
                {
                    failing[id] = cause;
                }

                telemetry.ReportDeletionCleanupFailures(failing.Count);
            }, cancellationToken, due: async token =>
            {
                var (isDue, items) = await CheckDueAsync(token);
                peeked = items;
                return isDue;
            });
        }

        private async Task<(bool Due, IReadOnlyList<TsAuthDeletionItem> Peeked)> CheckDueAsync(CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var auditDue = AuditPruneDue();
            long local;
            long expired = 0;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                local = await connection.ExecuteScalarAsync<long>(CleanupDue, new
                {
                    Retained = (now - DeletionCleanupJournal.Retention).UtcDateTime,
                    CheckoutExpired = (now - StripeBilling.CheckoutLifetime).UtcDateTime,
                    Now = now.UtcDateTime,
                    Expired = (now - AccountDeletion.GrantLifetime).UtcDateTime,
                });
                if (auditDue)
                {
                    expired = await connection.ExecuteScalarAsync<long>(AuditExpired, new { AuditCutoff = (now - OrganizationStore.AuditRetentionCap).UtcDateTime });
                }
            }

            if (auditDue && expired == 0)
            {
                auditPrunedAt = clock.GetTimestamp();
            }

            if (local > 0 || expired > 0)
            {
                return (true, []);
            }

            var peeked = await deletions.ListAsync(peek: true, cancellationToken);
            return (peeked.Count > 0, peeked);
        }

        private bool AuditPruneDue() => auditPrunedAt == 0 || clock.GetElapsedTime(auditPrunedAt) >= AuditPruneInterval;

        internal static (string Type, string Code) Failure(Exception error)
        {
            for (var cause = error; cause is not null; cause = cause.InnerException)
            {
                var code = cause switch
                {
                    StripeException stripe => Cause(stripe.StripeError?.Type, stripe.StripeError?.Code, (int?)stripe.HttpStatusCode),
                    TerraBaseStorageException storage => Cause((int?)storage.StatusCode, storage.ErrorCode),
                    TerraBaseException rpc => Cause(rpc.Status, rpc.ErrorCode),
                    TsAuthException auth => Cause((int?)auth.StatusCode, auth.ErrorCode),
                    AccountAccessException access => Cause(access.StatusCode, access.Code),
                    HttpRequestException request => Cause((int?)request.StatusCode),
                    _ => null,
                };
                if (code is not null)
                {
                    return (cause.GetType().Name, code);
                }
            }

            return (error.GetType().Name, "-");
        }

        private static string Cause(params object?[] parts)
        {
            var cause = string.Join(' ', parts.Where(part => part is int or string { Length: > 0 }));
            return cause.Length > 0 ? cause : "-";
        }

        private async Task SettleAsync(DeletionCleanupJournal.Entry entry, DateTime started, CancellationToken cancellationToken)
        {
            switch (entry)
            {
                case { TargetsComplete: true, AcknowledgedAt: null }:
                    await deletions.AcknowledgeAsync(entry.Proof, cancellationToken);
                    await journal.AcknowledgedAsync(entry.Proof.Id, cancellationToken);
                    break;
                case { TargetsComplete: true, Proof.Scope: DeletionCleanupRow.Organization, BillingSettledAt: null, AcknowledgedAt: { } acknowledged } when started >= acknowledged + StripeBilling.CheckoutLifetime:
                    await journal.BillingSettledAsync(entry.Proof.Id, cancellationToken);
                    break;
            }
        }

        private TsAuthDeletionProof? Reject(HashSet<string> seen, TsAuthRejectedDeletion rejected)
        {
            if (Report(seen, "proof:" + rejected.Id + ":" + rejected.Reason))
            {
                logger.DashboardDeletionProofRejected(rejected.Id, rejected.Reason);
            }

            return null;
        }

        private bool Report(HashSet<string> seen, string key)
        {
            return seen.Add(key) && reported.Add(key);
        }

        private void Fail(Dictionary<string, string> failed, TsAuthDeletionProof proof, Exception error)
        {
            var (type, code) = Failure(error);
            RecordFailure(failed, proof, type, code);
        }

        private DeletionCleanupJournal.Entry? FailStorage(Dictionary<string, string> failed, TsAuthDeletionProof proof, string type, string code)
        {
            RecordFailure(failed, proof, type, code);
            return null;
        }

        private void RecordFailure(Dictionary<string, string> failed, TsAuthDeletionProof proof, string type, string code)
        {
            if (failed.TryAdd(proof.Id, type + " " + code) && failing.GetValueOrDefault(proof.Id) != failed[proof.Id])
            {
                logger.DashboardDeletionCleanupFailed(proof.Id, proof.Scope, proof.SubjectId, type, code);
            }
        }

        private async Task CleanAsync(DeletionCleanupJournal.Entry entry, CancellationToken cancellationToken)
        {
            var proof = entry.Proof;
            switch (proof.Scope)
            {
                case "account":
                    await accounts.FenceAsync(proof.SubjectId, proof.Id, cancellationToken);
                    await tokens.ForgetAllAsync(proof.SubjectId, cancellationToken);
                    await sessions.RevokeUserAsync(proof.SubjectId, cancellationToken);
                    await sessions.ForgetUserAsync(proof.SubjectId, cancellationToken);
                    await related.CleanAccountAsync(proof.SubjectId, cancellationToken);
                    await AccountDeletion.CleanOperationsAsync(storage, identity, proof.SubjectId, clock, cancellationToken);
                    if (await accounts.FindAsync(proof.SubjectId, cancellationToken) is not { Erased: true })
                    {
                        throw new IOException("Erasure fence is unavailable.");
                    }

                    break;
                case "organization":
                    var ended = await billing.EndSubscriptionAsync(proof.SubjectId, cancellationToken, completedDeletion: true);
                    if (entry.BillingSettledAt is null)
                    {
                        await billing.EndCustomersForDeletionAsync(proof.SubjectId, entry.CustomerIds, cancellationToken);
                    }

                    await Features.Data.SqlWorkspace.EraseOrganizationAsync(storage, proof.SubjectId, cancellationToken);
                    await using (var connection = await storage.OpenAsync(cancellationToken))
                    {
                        await connection.ExecuteAsync(RemoveSavedQueries, new { OrganizationId = proof.SubjectId });
                    }

                    var projects = new HashSet<string>(entry.ProjectIds, StringComparer.Ordinal);
                    if (await organizations.FindAsync(proof.SubjectId, cancellationToken) is { Erased: false } existing)
                    {
                        var org = existing.Organization;
                        projects.UnionWith(org.Projects.Keys);
                        projects.UnionWith(org.DeletedProjectIds);
                    }
                    await using (var connection = await storage.OpenAsync(cancellationToken))
                    {
                        projects.UnionWith(await connection.QueryAsync<string>(ObservedProjects, new { OrganizationId = proof.SubjectId }));
                    }

                    foreach (var projectId in projects)
                    {
                        await CleanProjectAsync(new(proof.Id, "project", projectId, proof.SubjectId), cancellationToken);
                    }

                    await related.CleanOrganizationAsync(proof.SubjectId, cancellationToken);
                    await using (var connection = await storage.OpenAsync(cancellationToken))
                    {
                        await connection.ExecuteAsync(RemoveStripe, new { OrganizationId = proof.SubjectId });
                    }

                    await organizations.FenceAsync(proof.SubjectId, proof.Id, cancellationToken);
                    await billing.FenceAsync(proof.SubjectId, proof.Id, row =>
                    {
                        var current = StripeBilling.ReadBinding(row, proof.SubjectId);
                        return ended is not null && current.RequestId == ended.RequestId && current.CustomerId == ended.CustomerId &&
                            current.CheckoutId == ended.CheckoutId && current.SubscriptionId == ended.SubscriptionId &&
                            (current.Kind != "stripe" || current.State == "deleting" && current.Status == "canceled");
                    }, cancellationToken);
                    await billing.VerifyFenceAsync(proof.SubjectId, cancellationToken);
                    if (!await organizations.IsErasedAsync(proof.SubjectId, cancellationToken))
                    {
                        throw new IOException("Erasure fence is unavailable.");
                    }
                    break;
                case "project":
                    await CleanProjectAsync(proof, cancellationToken);
                    break;
            }
        }

        private async Task CleanProjectAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken)
        {
            var organizationId = proof.OrganizationId ?? throw new IOException("Deletion organization identity is missing.");
            await DropProjectAsync(proof, organizationId, cancellationToken);
            await projectRecords.CleanAsync(organizationId, proof.SubjectId, cancellationToken);
            await related.CleanProjectAsync(organizationId, proof.SubjectId, cancellationToken);
        }

        private async Task DropProjectAsync(TsAuthDeletionProof proof, string organizationId, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                var stored = await organizations.FindAsync(organizationId, cancellationToken);
                if (stored is { Erased: true })
                {
                    return;
                }
                var org = stored?.Organization ?? new OrganizationRecord { Id = organizationId, Kind = "business", BillingStatus = "billing_required" };
                var removed = org.Projects.Remove(proof.SubjectId);
                removed |= org.PendingProjectDeletions.Remove(proof.SubjectId);
                removed |= org.DeletedProjectIds.Add(proof.SubjectId);
                foreach (var id in org.InvitationReservations.Where(pair => pair.Value.ProjectId == proof.SubjectId).Select(pair => pair.Key).ToArray())
                {
                    removed |= org.InvitationReservations.Remove(id);
                }

                if (!removed)
                {
                    return;
                }

                try
                {
                    await (stored is null ? organizations.CreateAsync(org, cancellationToken) : organizations.ReplaceAsync(stored, cancellationToken));
                    return;
                }
                catch (DBConcurrencyException) when (attempt < 3) { await DashboardDb.ConflictBackoffAsync(attempt, clock, cancellationToken); }
            }
        }

        private static bool Retryable(Exception error, CancellationToken cancellationToken)
        {
            return error is IOException or HttpRequestException or DBConcurrencyException or TerraScale.Dashboard.Platform.Clients.TerraBaseException or JsonException or AccountAccessException or Stripe.StripeException or TimeoutException or InvalidOperationException or FormatException or ArgumentException or System.Security.Cryptography.CryptographicException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }

        private void Retry(Exception error)
        {
            var (type, code) = Failure(error);
            logger.DashboardDeletionCleanupIsUnavailableRetrying(type + " " + code);
        }
    }
}
