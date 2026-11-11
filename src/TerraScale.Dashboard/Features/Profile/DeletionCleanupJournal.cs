using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class DeletionCleanupJournal(IDbContextFactory<DashboardDbContext> storage, OrganizationStore organizations, TimeProvider clock)
    {
        public sealed record Entry(TsAuthDeletionProof Proof, string[] ProjectIds, string[] CustomerIds, DateTime? AcknowledgedAt, DateTime? BillingSettledAt);

        public async Task<IReadOnlyList<Entry>> ListAsync(CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var rows = await db.DeletionCleanup.AsNoTracking().OrderBy(row => row.Id).ToListAsync(cancellationToken);
            var targets = (await db.DeletionCleanupTargets.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(row => row.CleanupId, StringComparer.Ordinal);
            return [.. rows.Select(row => Read(row, targets[row.Id]))];
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
                await using (var db = await storage.CreateDbContextAsync(cancellationToken))
                {
                    checkouts = await db.StripeCheckouts.AsNoTracking().Where(row => row.OrganizationId == proof.SubjectId).ToListAsync(cancellationToken);
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
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                db.DeletionCleanup.Add(new DeletionCleanupRow
                {
                    Id = proof.Id,
                    Scope = proof.Scope,
                    SubjectId = proof.SubjectId,
                    OrganizationId = proof.Scope == DeletionCleanupRow.Organization ? null : proof.OrganizationId,
                    CreatedAt = clock.GetUtcNow().UtcDateTime,
                });
                db.DeletionCleanupTargets.AddRange(entry.ProjectIds.Select(id => Target(proof.Id, DeletionCleanupTargetRow.Project, id)));
                db.DeletionCleanupTargets.AddRange(entry.CustomerIds.Select(id => Target(proof.Id, DeletionCleanupTargetRow.Customer, id)));
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    return entry;
                }
                catch (DbUpdateException error) when (error.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.PrimaryKeyViolation })
                {
                }
            }

            return Match(await FindAsync(proof.Id, cancellationToken) ?? throw new IOException("Cleanup journal disappeared."), proof);
        }

        public Task AcknowledgedAsync(string id, CancellationToken cancellationToken)
        {
            return StampAsync(id, row => row.AcknowledgedAt, cancellationToken);
        }

        public Task BillingSettledAsync(string id, CancellationToken cancellationToken)
        {
            return StampAsync(id, row => row.BillingSettledAt, cancellationToken);
        }

        private async Task StampAsync(string id, Expression<Func<DeletionCleanupRow, DateTime?>> column, CancellationToken cancellationToken)
        {
            DateTime? now = clock.GetUtcNow().UtcDateTime;
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            if (await db.DeletionCleanup.Where(row => row.Id == id).ExecuteUpdateAsync(row => row.SetProperty(column, now), cancellationToken) != 1)
            {
                throw new IOException("Cleanup journal disappeared.");
            }
        }

        private async Task<Entry?> FindAsync(string id, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var row = await db.DeletionCleanup.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
            return row is null ? null : Read(row, await db.DeletionCleanupTargets.AsNoTracking().Where(target => target.CleanupId == id).ToListAsync(cancellationToken));
        }

        private static DeletionCleanupTargetRow Target(string cleanupId, string kind, string targetId)
        {
            return new() { CleanupId = cleanupId, Kind = kind, TargetId = targetId };
        }

        private static Entry Read(DeletionCleanupRow row, IEnumerable<DeletionCleanupTargetRow> targets)
        {
            var kept = targets.ToLookup(target => target.Kind, StringComparer.Ordinal);
            var organizationId = row.Scope != DeletionCleanupRow.Organization ? row.OrganizationId
                : row.OrganizationId is null ? row.SubjectId : throw new IOException("Cleanup journal organization row names another organization.");
            return new(new(row.Id, row.Scope, row.SubjectId, organizationId),
                [.. kept[DeletionCleanupTargetRow.Project].Select(target => target.TargetId).Order(StringComparer.Ordinal)],
                [.. kept[DeletionCleanupTargetRow.Customer].Select(target => target.TargetId).Order(StringComparer.Ordinal)], row.AcknowledgedAt, row.BillingSettledAt);
        }

        private static Entry Match(Entry entry, TsAuthDeletionProof proof)
        {
            return entry.Proof == proof ? entry : throw new IOException("Cleanup journal proof changed.");
        }
    }
}
