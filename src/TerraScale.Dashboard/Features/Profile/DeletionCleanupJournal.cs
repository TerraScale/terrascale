using System.Data.Common;
using System.Globalization;
using System.Text;
using Dapper;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Profile
{
    public sealed class DeletionCleanupJournal(DashboardDb storage, OrganizationStore organizations, TimeProvider clock)
    {
        private const string Pending = DashboardSql.DeletionCleanup.Select +
            " WHERE targets_complete = 1 AND (acknowledged_at IS NULL OR (scope = 'organization' AND billing_settled_at IS NULL AND acknowledged_at <= @CheckoutExpired)) ORDER BY id LIMIT 100";
        private const string Completed = """
            SELECT id FROM deletion_cleanup
             WHERE targets_complete = 1 AND acknowledged_at < @Cutoff AND (scope <> 'organization' OR billing_settled_at < @Cutoff) ORDER BY id LIMIT 20
            """;
        private const string CompletedTargets = """
            DELETE FROM deletion_cleanup_targets WHERE cleanup_id = @Id AND target_id IN (
                SELECT target_id FROM deletion_cleanup_targets WHERE cleanup_id = @Id ORDER BY target_id LIMIT 250)
            """;
        private const string CompletedRoot = "DELETE FROM deletion_cleanup WHERE id = @Id";
        internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
        private const string ById = DashboardSql.DeletionCleanup.Select + " WHERE id = @Id";
        private const int TargetReadPageSize = 1000;
        private const string FirstTargetsOf = DashboardSql.DeletionCleanupTargets.Select +
            " WHERE cleanup_id = @CleanupId ORDER BY kind, target_id LIMIT 1000";
        private const string NextTargetsOf = DashboardSql.DeletionCleanupTargets.Select +
            " WHERE cleanup_id = @CleanupId AND (kind, target_id) > (@Kind, @TargetId) ORDER BY kind, target_id LIMIT 1000";
        private const string Checkouts = DashboardSql.StripeCheckouts.Select + " WHERE organization_id = @OrganizationId";
        private const int TargetBatchSize = 250;
        private const string IncompleteOrganizationMarker = "pending";
        private const string MarkTargetsComplete = "UPDATE deletion_cleanup SET targets_complete = 1, organization_id = CASE WHEN scope = 'organization' THEN NULL ELSE organization_id END WHERE id = @Id";
        private const string Acknowledge = "UPDATE deletion_cleanup SET acknowledged_at = @Now WHERE id = @Id AND targets_complete = 1";
        private const string SettleBilling = "UPDATE deletion_cleanup SET billing_settled_at = @Now WHERE id = @Id AND targets_complete = 1";

        public sealed record Entry(TsAuthDeletionProof Proof, string[] ProjectIds, string[] CustomerIds, DateTime? AcknowledgedAt, DateTime? BillingSettledAt, bool TargetsComplete);

        public sealed record PendingEntries(IReadOnlyList<Entry> Entries, IReadOnlyList<string> Malformed);

        public readonly union RememberOutcome(Entry, StorageConflicted, StorageFailed);

        private sealed record CleanupEntryMissing;

        private readonly union FindMatchedOutcome(Entry, CleanupEntryMissing, StorageFailed);

        private readonly record struct InitialLookup(Entry? Entry, StorageFailed? Failure);

        public async Task<PendingEntries> ListPendingAsync(CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            var rows = (await connection.QueryAsync<DeletionCleanupRow>(Pending, new { CheckoutExpired = (clock.GetUtcNow() - StripeBilling.CheckoutLifetime).UtcDateTime })).AsList();
            if (rows.Count == 0)
            {
                return new([], []);
            }

            List<Entry> entries = [];
            List<string> malformed = [];
            foreach (var row in rows)
            {
                var targets = await ReadTargetsAsync(connection, row.Id, cancellationToken);
                if (TryRead(row, targets) is { } entry)
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

        public async Task<RememberOutcome> RememberAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken)
        {
            var lookup = await TryFindMatchedAsync(proof, cancellationToken);
            var initial = lookup switch
            {
                Entry found => new InitialLookup(found, null),
                CleanupEntryMissing => new InitialLookup(null, null),
                StorageFailed failed => new InitialLookup(null, failed),
            };
            if (initial.Failure is { } lookupFailure)
            {
                return lookupFailure;
            }

            var existing = initial.Entry;
            if (existing is { TargetsComplete: true })
            {
                return existing;
            }

            HashSet<string> projects = existing is null ? [] : [.. existing.ProjectIds];
            HashSet<string> customers = existing is null ? [] : [.. existing.CustomerIds];
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

            var entry = new Entry(proof, [.. projects.Order(StringComparer.Ordinal)], [.. customers.Order(StringComparer.Ordinal)], null, null, true);
            DeletionCleanupTargetRow[] targets =
            [
                .. entry.CustomerIds.Select(id => Target(proof.Id, DeletionCleanupTargetRow.Customer, id)),
                .. entry.ProjectIds.Select(id => Target(proof.Id, DeletionCleanupTargetRow.Project, id)),
            ];
            if (existing is not null)
            {
                return await PersistTargetsAndCompleteAsync(proof, targets, 0, knownEntry: null, cancellationToken);
            }

            var firstCount = Math.Min(targets.Length, TargetBatchSize - 1);
            var fitsSingleBatch = firstCount == targets.Length;
            var root = new DeletionCleanupRow
            {
                Id = proof.Id,
                Scope = proof.Scope,
                SubjectId = proof.SubjectId,
                OrganizationId = proof.Scope == DeletionCleanupRow.Organization
                    ? fitsSingleBatch ? null : IncompleteOrganizationMarker
                    : proof.OrganizationId,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
                TargetsComplete = fitsSingleBatch,
            };
            var write = await storage.TryWriteAsync(async (connection, transaction) =>
            {
                await connection.ExecuteAsync(DashboardSql.DeletionCleanup.Insert, root, transaction);
                await InsertTargetsBatchAsync(connection, transaction, proof.Id, targets, 0, firstCount, cancellationToken);
                return true;
            }, cancellationToken);
            return write switch
            {
                StorageWritten => await FinishWrittenAsync(proof, targets, firstCount, fitsSingleBatch, entry, cancellationToken),
                StorageDuplicate => await FinishDuplicateAsync(proof, targets, cancellationToken),
                StorageConflicted conflicted => conflicted,
                StorageFailed failed => failed,
            };
        }

        private async Task<RememberOutcome> FinishWrittenAsync(
            TsAuthDeletionProof proof,
            DeletionCleanupTargetRow[] targets,
            int firstCount,
            bool fitsSingleBatch,
            Entry entry,
            CancellationToken cancellationToken)
        {
            if (fitsSingleBatch)
            {
                return entry;
            }

            return await PersistTargetsAndCompleteAsync(proof, targets, firstCount, entry, cancellationToken);
        }

        private async Task<RememberOutcome> FinishDuplicateAsync(
            TsAuthDeletionProof proof,
            DeletionCleanupTargetRow[] targets,
            CancellationToken cancellationToken)
        {
            var found = await TryFindMatchedAsync(proof, cancellationToken);
            return found switch
            {
                Entry { TargetsComplete: true } complete => complete,
                Entry => await PersistTargetsAndCompleteAsync(proof, targets, 0, knownEntry: null, cancellationToken),
                CleanupEntryMissing => new StorageFailed("Cleanup journal disappeared."),
                StorageFailed failed => failed,
            };
        }

        private async Task<RememberOutcome> PersistTargetsAndCompleteAsync(
            TsAuthDeletionProof proof,
            DeletionCleanupTargetRow[] targets,
            int offset,
            Entry? knownEntry,
            CancellationToken cancellationToken)
        {
            var index = offset;
            while (true)
            {
                var remaining = targets.Length - index;
                var isFinal = remaining <= TargetBatchSize - 1;
                var start = index;
                var end = isFinal ? targets.Length : start + TargetBatchSize;
                var missingRoot = false;
                var write = await storage.TryWriteAsync(async (connection, transaction) =>
                {
                    await InsertTargetsBatchAsync(connection, transaction, proof.Id, targets, start, end, cancellationToken);
                    if (isFinal && await connection.ExecuteAsync(MarkTargetsComplete, new { proof.Id }, transaction) != 1)
                    {
                        missingRoot = true;
                        return false;
                    }

                    return true;
                }, cancellationToken);
                RememberOutcome? failure = write switch
                {
                    StorageWritten => null,
                    StorageDuplicate => new StorageConflicted(),
                    StorageConflicted conflicted => missingRoot ? new StorageFailed("Cleanup journal disappeared.") : conflicted,
                    StorageFailed failed => failed,
                };
                if (failure is { } failedOutcome)
                {
                    return failedOutcome;
                }

                if (isFinal)
                {
                    return knownEntry is not null
                        ? knownEntry
                        : await ReloadMatchedAsync(proof, cancellationToken);
                }

                index = end;
            }
        }

        private async Task<RememberOutcome> ReloadMatchedAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken)
        {
            var found = await TryFindMatchedAsync(proof, cancellationToken);
            return found switch
            {
                Entry { TargetsComplete: true } entry => entry,
                Entry => new StorageFailed("Cleanup journal targets are not yet visible."),
                CleanupEntryMissing => new StorageFailed("Cleanup journal disappeared."),
                StorageFailed failed => failed,
            };
        }

        private async Task<FindMatchedOutcome> TryFindMatchedAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken)
        {
            DeletionCleanupRow? row = null;
            List<DeletionCleanupTargetRow> targets = [];
            var readFailure = await storage.TryReadAsync(async connection =>
            {
                row = await connection.QuerySingleOrDefaultAsync<DeletionCleanupRow>(ById, new { proof.Id });
                if (row is not null)
                {
                    targets = await ReadTargetsAsync(connection, proof.Id, cancellationToken);
                }
            }, cancellationToken);
            if (readFailure is not null)
            {
                return readFailure;
            }

            if (row is null)
            {
                return new CleanupEntryMissing();
            }

            if (TryRead(row, targets) is not { } entry)
            {
                return new StorageFailed("Cleanup journal row is malformed.");
            }

            return entry.Proof == proof
                ? entry
                : new StorageFailed("Cleanup journal proof changed.");
        }

        private static Task InsertTargetsBatchAsync(
            TerraBaseDbConnection connection,
            DbTransaction transaction,
            string cleanupId,
            DeletionCleanupTargetRow[] targets,
            int start,
            int end,
            CancellationToken cancellationToken)
        {
            if (start >= end)
            {
                return Task.CompletedTask;
            }

            var count = end - start;
            var sql = new StringBuilder(128 + count * 20);
            sql.Append("INSERT INTO deletion_cleanup_targets (cleanup_id, kind, target_id) VALUES ");
            var parameters = new Dictionary<string, object?>(1 + count * 2, StringComparer.Ordinal)
            {
                ["c"] = cleanupId,
            };
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                var kindParam = string.Create(CultureInfo.InvariantCulture, $"k{i}");
                var targetParam = string.Create(CultureInfo.InvariantCulture, $"t{i}");
                sql.Append("(@c, @").Append(kindParam).Append(", @").Append(targetParam).Append(')');
                var target = targets[start + i];
                parameters[kindParam] = target.Kind;
                parameters[targetParam] = target.TargetId;
            }

            sql.Append(" ON CONFLICT (cleanup_id, kind, target_id) DO NOTHING");
            return DashboardDb.RunAsync(connection, sql.ToString(), parameters, transaction, cancellationToken);
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

        private static async Task<List<DeletionCleanupTargetRow>> ReadTargetsAsync(
            TerraBaseDbConnection connection,
            string cleanupId,
            CancellationToken cancellationToken)
        {
            List<DeletionCleanupTargetRow> targets = [];
            string? afterKind = null;
            string? afterTargetId = null;
            while (true)
            {
                var page = afterKind is null
                    ? await DashboardDb.RowsAsync(
                        connection,
                        FirstTargetsOf,
                        new Dictionary<string, object?>(1, StringComparer.Ordinal) { ["CleanupId"] = cleanupId },
                        ReadTargetRow,
                        null,
                        cancellationToken)
                    : await DashboardDb.RowsAsync(
                        connection,
                        NextTargetsOf,
                        new Dictionary<string, object?>(3, StringComparer.Ordinal)
                        {
                            ["CleanupId"] = cleanupId,
                            ["Kind"] = afterKind,
                            ["TargetId"] = afterTargetId,
                        },
                        ReadTargetRow,
                        null,
                        cancellationToken);
                targets.AddRange(page);
                if (page.Count < TargetReadPageSize)
                {
                    return targets;
                }

                var last = page[^1];
                afterKind = last.Kind;
                afterTargetId = last.TargetId;
            }
        }

        private static DeletionCleanupTargetRow ReadTargetRow(DbDataReader reader) => new()
        {
            CleanupId = reader.GetString(0),
            Kind = reader.GetString(1),
            TargetId = reader.GetString(2),
        };

        private static DeletionCleanupTargetRow Target(string cleanupId, string kind, string targetId)
        {
            return new() { CleanupId = cleanupId, Kind = kind, TargetId = targetId };
        }

        private static Entry? TryRead(DeletionCleanupRow row, IEnumerable<DeletionCleanupTargetRow> targets)
        {
            var kept = targets.ToLookup(target => target.Kind, StringComparer.Ordinal);
            var organizationId = row.Scope != DeletionCleanupRow.Organization ? row.OrganizationId
                : row.OrganizationId is null || (!row.TargetsComplete && row.OrganizationId == IncompleteOrganizationMarker) ? row.SubjectId : null;
            TsAuthDeletionProof proof = new(row.Id, row.Scope, row.SubjectId, organizationId);
            if (!TsAuthPlatformClient.ValidDeletionProof(proof))
            {
                return null;
            }

            return new(proof,
                [.. kept[DeletionCleanupTargetRow.Project].Select(target => target.TargetId).Order(StringComparer.Ordinal)],
                [.. kept[DeletionCleanupTargetRow.Customer].Select(target => target.TargetId).Order(StringComparer.Ordinal)], row.AcknowledgedAt, row.BillingSettledAt, row.TargetsComplete);
        }
    }
}
