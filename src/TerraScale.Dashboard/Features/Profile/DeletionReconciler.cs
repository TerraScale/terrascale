using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.Features.Profile
{
    // Local fences guard CAS writers; immutable authorization and enrollment references
    // replay forever after ACK to repair later regional LWW joins and delayed writes.
    public sealed class DeletionReconciler(ITsAuthDeletions deletions, IDashboardState state, DashboardSessions sessions,
        AccessTokens tokens, StripeBilling billing, IDashboardIdentity identity, TimeProvider clock, ILogger<DeletionReconciler> logger) : BackgroundService
    {
        private readonly DeletionCleanupJournal journal = new(state);
        private readonly DeletionProjectRecords projectRecords = new(state);
        private readonly DeletionRelatedRecords related = new(state, sessions);

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
            try { await Features.Data.DatabaseWorkspaceCleanup.ReplayAsync(state, cancellationToken); }
            catch (Exception error) when (Retryable(error, cancellationToken)) { Retry(error); }
            // Replay does not depend on producer availability or delivery of an already ACKed proof.
            try
            {
                foreach (var document in await state.ListAsync(DeletionCleanupJournal.Collection, cancellationToken))
                {
                    try
                    {
                        var entry = await journal.ReadAsync(document, cancellationToken);
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
                    await AccountService.FenceDeletionAsync(state, proof.SubjectId, proof.Id, cancellationToken);
                    await tokens.ForgetAllAsync(proof.SubjectId, cancellationToken);
                    await sessions.RevokeUserAsync(proof.SubjectId, cancellationToken);
                    await sessions.CleanEnrollmentsAsync(proof.SubjectId, cancellationToken);
                    await related.CleanAccountAsync(proof.SubjectId, cancellationToken);
                    await AccountDeletion.CleanOperationsAsync(state, identity, proof.SubjectId, cancellationToken);
                    var key = AccountService.AccountStorageKey(proof.SubjectId);
                    foreach (var collection in new[] { AccountService.AccountsCollection, AccountService.IdentitiesCollection })
                    {
                        await VerifyFenceAsync(collection, key, cancellationToken);
                    }

                    await Task.WhenAll(new[] { AccessTokens.Collection, "session_indexes" }.Select(collection => VerifyAbsentAsync(collection, key, cancellationToken)));
                    break;
                case "organization":
                    var ended = await billing.EndSubscriptionAsync(proof.SubjectId, cancellationToken, completedDeletion: true);
                    await billing.EndCustomersForDeletionAsync(proof.SubjectId, entry.CustomerIds, cancellationToken);
                    await Features.Data.SqlWorkspace.EraseAsync(state, new Dictionary<string, string> { ["OrganizationId"] = proof.SubjectId }, cancellationToken);
                    var projects = new HashSet<string>(entry.ProjectIds, StringComparer.Ordinal);
                    if (await state.GetAsync(AccountService.OrganizationsCollection, proof.SubjectId, cancellationToken) is { } existing && !AccountService.IsDeletionFence(existing))
                    {
                        var org = AccountService.Read<OrganizationRecord>(existing);
                        if (existing.Id != proof.SubjectId || org.Id != proof.SubjectId)
                        {
                            throw new IOException("Deletion organization identity changed.");
                        }

                        projects.UnionWith(org.Projects.Keys);
                        projects.UnionWith(org.DeletedProjectIds);
                    }
                    foreach (var collection in new[] { "dashboard_saved_queries", "dashboard_alert_rules", "dashboard_log_drains" })
                    {
                        foreach (var document in await state.ListAsync(collection, cancellationToken))
                        {
                            if (!document.Document.TryGetProperty("OrganizationId", out var owner) || owner.GetString() != proof.SubjectId)
                            {
                                continue;
                            }

                            if (!document.Document.TryGetProperty("ProjectId", out var project) || !TerraScaleIds.IsValid(project.GetString()))
                            {
                                throw new IOException("Owned project record identity is invalid.");
                            }

                            projects.Add(project.GetString()!);
                        }
                    }

                    foreach (var collection in new[] { "dashboard_alert_projects", "dashboard_log_drain_projects" })
                    {
                        if (await state.GetAsync(collection, "all", cancellationToken) is { } index)
                        {
                            if (index.State != "value" || !index.Document.TryGetProperty("Projects", out var entries) || entries.ValueKind != JsonValueKind.Object)
                            {
                                throw new IOException("Owned project index is invalid.");
                            }

                            foreach (var project in entries.EnumerateObject())
                            {
                                if (project.Value.GetString() == proof.SubjectId)
                                {
                                    if (!TerraScaleIds.IsValid(project.Name))
                                    {
                                        throw new IOException("Owned project index identity is invalid.");
                                    }

                                    projects.Add(project.Name);
                                }
                            }
                        }
                    }

                    foreach (var projectId in projects)
                    {
                        await CleanProjectAsync(new(proof.Id, "project", projectId, proof.SubjectId), cancellationToken);
                    }

                    await related.CleanOrganizationAsync(proof.SubjectId, cancellationToken);
                    foreach (var collection in new[] { StripeBilling.CheckoutsCollection, StripeBilling.CustomersCollection })
                    {
                        foreach (var document in await state.ListAsync(collection, cancellationToken))
                        {
                            if (document.Document.TryGetProperty("OrganizationId", out var owner) && owner.GetString() == proof.SubjectId)
                            {
                                await state.DeleteListedAsync(collection, document, cancellationToken);
                            }
                        }
                    }

                    await AccountService.FenceRecordAsync(state, StripeBilling.Collection, proof.SubjectId, proof.Id, document =>
                    {
                        var current = StripeBilling.ReadBinding(document, proof.SubjectId);
                        return ended is not null && current.RequestId == ended.RequestId && current.CustomerId == ended.CustomerId &&
                            current.CheckoutId == ended.CheckoutId && current.SubscriptionId == ended.SubscriptionId &&
                            (current.Kind != "stripe" || current.State == "deleting" && current.Status == "canceled" && !current.PaymentValidated);
                    }, cancellationToken);
                    await AccountService.FenceOrganizationDeletionAsync(state, proof.SubjectId, proof.Id, cancellationToken);
                    await VerifyFenceAsync(StripeBilling.Collection, proof.SubjectId, cancellationToken);
                    await VerifyFenceAsync(AccountService.OrganizationsCollection, proof.SubjectId, cancellationToken);
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
            var document = await state.GetAsync(AccountService.OrganizationsCollection, organizationId, cancellationToken);
            if (document is not null && AccountService.IsDeletionFence(document))
            {
                if (document.Id != organizationId)
                {
                    throw new IOException("Deletion organization identity does not match.");
                }

                return;
            }
            // Preserve a terminal project ID even before this organization has a dashboard projection.
            var org = document is null ? new OrganizationRecord { Id = organizationId, Kind = "business", BillingStatus = "billing_required" } : AccountService.Read<OrganizationRecord>(document);
            if (org.PendingProjectDeletions is null)
            {
                throw new JsonException("Deletion project markers are invalid.");
            }

            if (document is not null && document.Id != organizationId || org.Id != organizationId)
            {
                throw new IOException("Deletion organization identity does not match.");
            }

            if (org.Projects.TryGetValue(proof.SubjectId, out var project) && project.Id != proof.SubjectId)
            {
                throw new IOException("Deletion project identity does not match.");
            }

            var removed = org.Projects.Remove(proof.SubjectId);
            removed |= org.PendingProjectDeletions.Remove(proof.SubjectId);
            removed |= org.DeletedProjectIds.Add(proof.SubjectId);
            foreach (var id in org.InvitationReservations.Where(pair => pair.Value.ProjectId == proof.SubjectId).Select(pair => pair.Key).ToArray())
            {
                removed |= org.InvitationReservations.Remove(id);
            }

            if (removed)
            {
                await state.PutAsync(AccountService.OrganizationsCollection, organizationId, JsonSerializer.SerializeToElement(org),
                document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken);
            }

            if (await state.GetAsync(AccountService.OrganizationsCollection, organizationId, cancellationToken) is { } latest)
            {
                if (AccountService.IsDeletionFence(latest))
                {
                    if (latest.Id != organizationId)
                    {
                        throw new IOException("Deletion organization identity does not match.");
                    }

                    return;
                }
                var current = AccountService.Read<OrganizationRecord>(latest);
                if (current.PendingProjectDeletions is null)
                {
                    throw new JsonException("Deletion project markers are invalid.");
                }

                if (latest.Id != organizationId || current.Id != organizationId || current.Projects.ContainsKey(proof.SubjectId) || current.PendingProjectDeletions.ContainsKey(proof.SubjectId) || !current.DeletedProjectIds.Contains(proof.SubjectId))
                {
                    throw new IOException("Deletion project cleanup is incomplete.");
                }
            }
            else
            {
                throw new IOException("Deletion project fence is unavailable.");
            }
        }

        private async Task VerifyFenceAsync(string collection, string key, CancellationToken cancellationToken)
        {
            var document = await state.GetAsync(collection, key, cancellationToken);
            if (document is null || document.Id != key || !AccountService.IsDeletionFence(document))
            {
                throw new IOException("Erasure fence is unavailable.");
            }
        }

        private async Task VerifyAbsentAsync(string collection, string key, CancellationToken cancellationToken)
        {
            if (await state.GetAsync(collection, key, cancellationToken) is not null)
            {
                throw new IOException("Deletion cleanup is incomplete.");
            }
        }

        private static bool Retryable(Exception error, CancellationToken cancellationToken)
        {
            return error is IOException or HttpRequestException or TerraScale.TerraBase.Contracts.TerraBaseUnavailableException or TerraScale.TerraBase.Contracts.TerraBaseRateLimitedException or JsonException or AccountAccessException or Stripe.StripeException or TimeoutException or InvalidOperationException or FormatException or ArgumentException or System.Security.Cryptography.CryptographicException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }

        private void Retry(Exception error)
        {
            logger.DashboardDeletionCleanupIsUnavailableRetrying(error.GetType().Name);
        }
    }
}
