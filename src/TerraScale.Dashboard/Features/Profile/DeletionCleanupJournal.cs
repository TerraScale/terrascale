using Dapper;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class DeletionCleanupJournal(DashboardDb storage, OrganizationStore organizations, TimeProvider clock)
    {
        private const string Pending = DashboardSql.DeletionCleanup.Select +
            " WHERE acknowledged_at IS NULL OR (scope = 'organization' AND billing_settled_at IS NULL) ORDER BY id LIMIT 100";
        private const string PendingTargets = DashboardSql.DeletionCleanupTargets.Select +
            " WHERE cleanup_id IN (SELECT id FROM deletion_cleanup WHERE acknowledged_at IS NULL OR (scope = 'organization' AND billing_settled_at IS NULL) ORDER BY id LIMIT 100)";
        private const string Completed = """
            SELECT id FROM deletion_cleanup
             WHERE acknowledged_at < @Cutoff AND (scope <> 'organization' OR billing_settled_at < @Cutoff) ORDER BY id LIMIT 20
            """;
        private const string CompletedTargets = """
            DELETE FROM deletion_cleanup_targets WHERE cleanup_id = @Id AND target_id IN (
                SELECT target_id FROM deletion_cleanup_targets WHERE cleanup_id = @Id ORDER BY target_id LIMIT 250)
            """;
        private const string CompletedRoot = "DELETE FROM deletion_cleanup WHERE id = @Id";
        internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
        private const string ById = DashboardSql.DeletionCleanup.Select + " WHERE id = @Id";
        private const string TargetsOf = DashboardSql.DeletionCleanupTargets.Select + " WHERE cleanup_id = @CleanupId";
        private const string Checkouts = DashboardSql.StripeCheckouts.Select + " WHERE organization_id = @OrganizationId";
        private const string Acknowledge = "UPDATE deletion_cleanup SET acknowledged_at = @Now WHERE id = @Id";
        private const string SettleBilling = "UPDATE deletion_cleanup SET billing_settled_at = @Now WHERE id = @Id";

        public sealed record Entry(TsAuthDeletionProof Proof, string[] ProjectIds, string[] CustomerIds, DateTime? AcknowledgedAt, DateTime? BillingSettledAt);

        public sealed record PendingEntries(IReadOnlyList<Entry> Entries, IReadOnlyList<string> Malformed);

        public async Task<PendingEntries> ListPendingAsync(CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<DeletionCleanupRow>(Pending);
            var targets = (await connection.QueryAsync<DeletionCleanupTargetRow>(PendingTargets)).ToLookup(row => row.CleanupId, StringComparer.Ordinal);
            List<Entry> entries = [];
            List<string> malformed = [];
            foreach (var row in rows)
            {
                if (TryRead(row, targets[row.Id]) is { } entry)
                {
                    entries.Add(entry);
                }
                else
                {
                    malformed.Add(row.Id);
                }
            }

            return new(entries, malformed);
        }

        public async Task PruneAsync(CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            foreach (var id in (await connection.QueryAsync<string>(Completed, new { Cutoff = (clock.GetUtcNow() - Retention).UtcDateTime })).AsList())
            {
                int removed;
                do
                {
                    removed = await connection.ExecuteAsync(CompletedTargets, new { Id = id });
                }
                while (removed > 0);

                await connection.ExecuteAsync(CompletedRoot, new { Id = id });
            }
        }

        public async Task<Entry> RememberAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken)
        {
            if (await FindAsync(proof.Id, cancellationToken) is { } existing)
            {
                return Match(existing, proof);
            }

            HashSet<string> projects = [];
            HashSet<string> customers = [];
            if (proof.Scope == DeletionCleanupRow.Organization)
            {
                if (await organizations.FindAsync(proof.SubjectId, cancellationToken) is { Erased: false } org)
                {
                    var value = org.Organization;
                    projects.UnionWith(value.Projects.Keys);
                    projects.UnionWith(value.DeletedProjectIds);
                }
                if (await storage.BindingRowAsync(proof.SubjectId, cancellationToken) is { ErasureId: null } billing)
                {
                    var binding = StripeBilling.ReadBinding(billing, proof.SubjectId);
                    if (binding.CustomerId.Length > 0)
                    {
                        customers.Add(binding.CustomerId);
                    }
                }

                List<StripeCheckoutRow> checkouts;
                await using (var connection = await storage.OpenAsync(cancellationToken))
                {
                    checkouts = (await connection.QueryAsync<StripeCheckoutRow>(Checkouts, new { OrganizationId = proof.SubjectId })).AsList();
                }

                foreach (var checkout in checkouts)
                {
                    customers.Add(checkout.CustomerId);
                    if (TerraScaleIds.IsValid(checkout.ProjectId))
                    {
                        projects.Add(checkout.ProjectId);
                    }
                }
            }

            var entry = new Entry(proof, [.. projects.Order(StringComparer.Ordinal)], [.. customers.Order(StringComparer.Ordinal)], null, null);
            var root = new DeletionCleanupRow
            {
                Id = proof.Id,
                Scope = proof.Scope,
                SubjectId = proof.SubjectId,
                OrganizationId = proof.Scope == DeletionCleanupRow.Organization ? null : proof.OrganizationId,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            DeletionCleanupTargetRow[] targets = [.. entry.ProjectIds.Select(id => Target(proof.Id, DeletionCleanupTargetRow.Project, id)),
                .. entry.CustomerIds.Select(id => Target(proof.Id, DeletionCleanupTargetRow.Customer, id))];
            try
            {
                await storage.WriteAsync(async (connection, transaction) =>
                {
                    await connection.ExecuteAsync(DashboardSql.DeletionCleanup.Insert, root, transaction);
                    foreach (var target in targets)
                    {
                        await connection.ExecuteAsync(DashboardSql.DeletionCleanupTargets.Insert, target, transaction);
                    }
                }, cancellationToken);
                return entry;
            }
            catch (Exception error) when (DashboardDb.IsDuplicate(error))
            {
            }

            return Match(await FindAsync(proof.Id, cancellationToken) ?? throw new IOException("Cleanup journal disappeared."), proof);
        }

        public Task AcknowledgedAsync(string id, CancellationToken cancellationToken) => StampAsync(Acknowledge, id, cancellationToken);

        public Task BillingSettledAsync(string id, CancellationToken cancellationToken) => StampAsync(SettleBilling, id, cancellationToken);

        private async Task StampAsync(string sql, string id, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            if (await connection.ExecuteAsync(sql, new { Id = id, Now = clock.GetUtcNow().UtcDateTime }) != 1)
            {
                throw new IOException("Cleanup journal disappeared.");
            }
        }

        private async Task<Entry?> FindAsync(string id, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<DeletionCleanupRow>(ById, new { Id = id });
            return row is null ? null : Read(row, await connection.QueryAsync<DeletionCleanupTargetRow>(TargetsOf, new { CleanupId = id }));
        }

        private static DeletionCleanupTargetRow Target(string cleanupId, string kind, string targetId)
        {
            return new() { CleanupId = cleanupId, Kind = kind, TargetId = targetId };
        }

        private static Entry Read(DeletionCleanupRow row, IEnumerable<DeletionCleanupTargetRow> targets)
        {
            return TryRead(row, targets) ?? throw new IOException("Cleanup journal row is malformed.");
        }

        private static Entry? TryRead(DeletionCleanupRow row, IEnumerable<DeletionCleanupTargetRow> targets)
        {
            var kept = targets.ToLookup(target => target.Kind, StringComparer.Ordinal);
            var organizationId = row.Scope != DeletionCleanupRow.Organization ? row.OrganizationId
                : row.OrganizationId is null ? row.SubjectId : null;
            TsAuthDeletionProof proof = new(row.Id, row.Scope, row.SubjectId, organizationId);
            if (!TsAuthPlatformClient.ValidDeletionProof(proof))
            {
                return null;
            }

            return new(proof,
                [.. kept[DeletionCleanupTargetRow.Project].Select(target => target.TargetId).Order(StringComparer.Ordinal)],
                [.. kept[DeletionCleanupTargetRow.Customer].Select(target => target.TargetId).Order(StringComparer.Ordinal)], row.AcknowledgedAt, row.BillingSettledAt);
        }

        private static Entry Match(Entry entry, TsAuthDeletionProof proof)
        {
            return entry.Proof == proof ? entry : throw new IOException("Cleanup journal proof changed.");
        }
    }
}
