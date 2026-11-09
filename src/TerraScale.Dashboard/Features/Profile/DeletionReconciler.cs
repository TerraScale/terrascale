using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Profile
{
    // Local fences guard CAS writers; immutable authorization and enrollment references
    // replay after ACK to repair delayed regional writes.
    public sealed class DeletionReconciler(ITsAuthDeletions deletions, IDbContextFactory<DashboardDbContext> storage, AccountStore accounts, OrganizationStore organizations, DashboardSessions sessions,
        AccessTokens tokens, StripeBilling billing, IDashboardIdentity identity, TimeProvider clock, ILogger<DeletionReconciler> logger) : BackgroundService
    {
        private readonly DeletionCleanupJournal journal = new(storage, organizations, clock);
        private readonly DeletionProjectRecords projectRecords = new(storage);
        private readonly DeletionRelatedRecords related = new(storage, accounts);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
            try
            {
                do { await RunOnceAsync(stoppingToken); }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            // Replay does not depend on producer availability or delivery of an already ACKed proof.
            try
            {
                foreach (var entry in await journal.ListAsync(cancellationToken))
                {
                    try
                    {
                        Validate(entry.Proof);
                        await CleanAsync(entry, cancellationToken);
                    }
                    catch (Exception error) when (Retryable(error, cancellationToken)) { Retry(error); }
                }
            }
            catch (Exception error) when (Retryable(error, cancellationToken)) { Retry(error); }
            IReadOnlyList<TsAuthDeletionProof> proofs;
            try
            {
                proofs = await deletions.ListAsync(cancellationToken);
                if (proofs.Count > 20 || proofs.Any(proof => proof is null) || proofs.Select(proof => proof.Id).Distinct(StringComparer.Ordinal).Count() != proofs.Count)
                {
                    throw new IOException("ts-auth returned an invalid deletion page.");
                }

                foreach (var proof in proofs)
                {
                    Validate(proof);
                }
            }
            catch (Exception error) when (Retryable(error, cancellationToken)) { Retry(error); return; }
            foreach (var proof in proofs)
            {
                try
                {
                    var entry = await journal.RememberAsync(proof, cancellationToken);
                    await CleanAsync(entry, cancellationToken);
                    await deletions.AcknowledgeAsync(proof, cancellationToken);
                }
                catch (Exception error) when (Retryable(error, cancellationToken)) { Retry(error); }
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
                    await billing.EndCustomersForDeletionAsync(proof.SubjectId, entry.CustomerIds, cancellationToken);
                    await Features.Data.SqlWorkspace.EraseAsync(storage, row => row.OrganizationId == proof.SubjectId, cancellationToken);
                    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
                    {
                        await db.SavedQueries.Where(row => row.OrganizationId == proof.SubjectId).ExecuteDeleteAsync(cancellationToken);
                    }

                    var projects = new HashSet<string>(entry.ProjectIds, StringComparer.Ordinal);
                    if (await organizations.FindAsync(proof.SubjectId, cancellationToken) is { Erased: false } existing)
                    {
                        var org = existing.Organization;
                        projects.UnionWith(org.Projects.Keys);
                        projects.UnionWith(org.DeletedProjectIds);
                    }
                    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
                    {
                        projects.UnionWith(await db.AlertRules.Where(row => row.OrganizationId == proof.SubjectId).Select(row => row.ProjectId).ToListAsync(cancellationToken));
                        projects.UnionWith(await db.LogDrains.Where(row => row.OrganizationId == proof.SubjectId).Select(row => row.ProjectId).ToListAsync(cancellationToken));
                    }

                    foreach (var projectId in projects)
                    {
                        await CleanProjectAsync(new(proof.Id, "project", projectId, proof.SubjectId), cancellationToken);
                    }

                    await related.CleanOrganizationAsync(proof.SubjectId, cancellationToken);
                    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
                    {
                        await db.StripeCheckouts.Where(row => row.OrganizationId == proof.SubjectId).ExecuteDeleteAsync(cancellationToken);
                        await db.StripeCustomers.Where(row => row.OrganizationId == proof.SubjectId).ExecuteDeleteAsync(cancellationToken);
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
            await DropProjectAsync(proof, cancellationToken);
            await projectRecords.CleanAsync(proof.OrganizationId!, proof.SubjectId, cancellationToken);
            await related.CleanProjectAsync(proof.OrganizationId!, proof.SubjectId, cancellationToken);
        }

        private static void Validate(TsAuthDeletionProof proof)
        {
            if (!TerraScaleIds.IsValid(proof.Id) || !TerraScaleIds.IsValid(proof.SubjectId) || (proof.Scope switch
            {
                "account" => proof.OrganizationId is not null,
                "organization" => proof.OrganizationId != proof.SubjectId,
                "project" => !TerraScaleIds.IsValid(proof.OrganizationId),
                _ => true
            }))
            {
                throw new IOException("ts-auth returned an invalid deletion proof.");
            }
        }

        private async Task DropProjectAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken)
        {
            var organizationId = proof.OrganizationId ?? throw new IOException("Deletion organization identity is missing.");
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
                catch (DbUpdateConcurrencyException) when (attempt < 3) { }
            }
        }

        private static bool Retryable(Exception error, CancellationToken cancellationToken)
        {
            return error is IOException or HttpRequestException or DbUpdateException or TerraScale.TerraBase.Contracts.TerraBaseException or JsonException or AccountAccessException or Stripe.StripeException or TimeoutException or InvalidOperationException or FormatException or ArgumentException or System.Security.Cryptography.CryptographicException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }

        private void Retry(Exception error)
        {
            logger.DashboardDeletionCleanupIsUnavailableRetrying(error.GetType().Name);
        }
    }
}
