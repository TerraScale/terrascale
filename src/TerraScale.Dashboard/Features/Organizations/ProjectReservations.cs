using System.Data.Common;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed record ProjectSlotReserved(string ReservationId, DateTimeOffset ExpiresAt);

    public sealed record ProjectSlotsFull(int Limit);

    public sealed record ProjectInventoryChanged;

    public sealed record ProjectReservationFailed(string Failure);

    public readonly union ProjectReservation(ProjectSlotReserved, ProjectSlotsFull, ProjectInventoryChanged, ProjectReservationFailed);

    public sealed class ProjectReservations(DashboardDb storage, TimeProvider time)
    {
        public static readonly TimeSpan Hold = TimeSpan.FromMinutes(10);
        private const string Expired = "DELETE FROM project_reservations WHERE organization_id = @OrganizationId AND expires_at <= @Now";
        private const string Held = "SELECT id, project_id FROM project_reservations WHERE organization_id = @OrganizationId AND expires_at > @Now";
        private const string RootVersion = "SELECT version FROM organizations WHERE id = @Id AND erasure_id IS NULL";
        private const string Fence = "UPDATE organizations SET version = @Next WHERE id = @Id AND version = @Current";
        private const string Insert = "INSERT INTO project_reservations (id, organization_id, project_id, created_at, expires_at) VALUES (@Id, @OrganizationId, NULL, @CreatedAt, @ExpiresAt)";
        private const string Remove = "DELETE FROM project_reservations WHERE id = @Id";

        public async Task<string?> VersionAsync(string organizationId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            return await VersionAsync(connection, null, organizationId, cancellationToken);
        }

        public async Task<ProjectReservation> ReserveAsync(string organizationId, string inventoryVersion, IReadOnlyCollection<string> liveProjectIds, int limit,
            CancellationToken cancellationToken)
        {
            ProjectSlotReserved? reserved = null;
            var full = false;
            var write = await storage.TryWriteAsync(async (connection, transaction) =>
            {
                if (await VersionAsync(connection, transaction, organizationId, cancellationToken) != inventoryVersion)
                {
                    return false;
                }

                var now = time.GetUtcNow();
                var at = now.UtcDateTime;
                await DashboardDb.RunAsync(connection, Expired, new Dictionary<string, object?> { ["OrganizationId"] = organizationId, ["Now"] = at }, transaction, cancellationToken);
                var held = (await DashboardDb.RowsAsync(connection, Held, new Dictionary<string, object?> { ["OrganizationId"] = organizationId, ["Now"] = at },
                        static reader => reader.IsDBNull(1) ? null : reader.GetString(1), transaction, cancellationToken))
                    .Count(projectId => projectId is null || !liveProjectIds.Contains(projectId));
                if (liveProjectIds.Count + held >= limit)
                {
                    full = true;
                    return true;
                }

                var fenced = await DashboardDb.RunAsync(connection, Fence,
                    new Dictionary<string, object?> { ["Id"] = organizationId, ["Current"] = inventoryVersion, ["Next"] = AccountService.NewId() }, transaction, cancellationToken);
                if (fenced != 1)
                {
                    return false;
                }

                var slot = new ProjectSlotReserved(AccountService.NewId(), now + Hold);
                await DashboardDb.RunAsync(connection, Insert, new Dictionary<string, object?>
                {
                    ["Id"] = slot.ReservationId,
                    ["OrganizationId"] = organizationId,
                    ["CreatedAt"] = at,
                    ["ExpiresAt"] = slot.ExpiresAt.UtcDateTime,
                }, transaction, cancellationToken);
                reserved = slot;
                return true;
            }, cancellationToken);
            return write switch
            {
                StorageConflicted or StorageDuplicate => new ProjectInventoryChanged(),
                StorageWritten => reserved is { } slot ? slot : full ? new ProjectSlotsFull(limit) : new ProjectInventoryChanged(),
                StorageFailed failed => new ProjectReservationFailed(failed.Failure),
            };
        }

        public async Task ReleaseAsync(string reservationId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            await DashboardDb.RunAsync(connection, Remove, new Dictionary<string, object?> { ["Id"] = reservationId }, null, cancellationToken);
        }

        private static async Task<string?> VersionAsync(TerraBaseDbConnection connection, DbTransaction? transaction,
            string organizationId, CancellationToken cancellationToken) =>
            (await DashboardDb.RowsAsync(connection, RootVersion, new Dictionary<string, object?> { ["Id"] = organizationId },
                static reader => reader.GetString(0), transaction, cancellationToken)).SingleOrDefault();
    }
}
