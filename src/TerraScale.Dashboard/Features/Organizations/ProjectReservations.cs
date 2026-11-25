using Dapper;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed record ProjectSlotReserved(string ReservationId);

    public sealed record ProjectSlotsFull(int Limit);

    public sealed record ProjectInventoryChanged;

    public readonly union ProjectReservation(ProjectSlotReserved, ProjectSlotsFull, ProjectInventoryChanged);

    public sealed class ProjectReservations(DashboardDb storage, TimeProvider time)
    {
        private static readonly TimeSpan Hold = TimeSpan.FromMinutes(10);
        private const string Expired = "DELETE FROM project_reservations WHERE organization_id = @OrganizationId AND expires_at <= @Now";
        private const string Held = "SELECT id AS Id, project_id AS ProjectId FROM project_reservations WHERE organization_id = @OrganizationId AND expires_at > @Now";
        private const string RootVersion = "SELECT version FROM organizations WHERE id = @Id AND erasure_id IS NULL";
        private const string Fence = "UPDATE organizations SET version = @Next WHERE id = @Id AND version = @Current";
        private const string Insert = "INSERT INTO project_reservations (id, organization_id, project_id, created_at, expires_at) VALUES (@Id, @OrganizationId, NULL, @CreatedAt, @ExpiresAt)";
        private const string Bind = "UPDATE project_reservations SET project_id = @ProjectId WHERE id = @Id AND project_id IS NULL";
        private const string Remove = "DELETE FROM project_reservations WHERE id = @Id";

        public async Task<string?> VersionAsync(string organizationId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            return (await DashboardDb.RowsAsync(connection, RootVersion, new Dictionary<string, object?> { ["Id"] = organizationId },
                static reader => reader.GetString(0), null, cancellationToken)).SingleOrDefault();
        }

        public async Task<ProjectReservation> ReserveAsync(string organizationId, string inventoryVersion, IReadOnlyCollection<string> liveProjectIds, int limit,
            CancellationToken cancellationToken)
        {
            string? reserved = null;
            var full = false;
            var write = await storage.TryWriteAsync(async (connection, transaction) =>
            {
                var current = (await connection.QueryAsync<string>(RootVersion, new { Id = organizationId }, transaction)).SingleOrDefault();
                if (current != inventoryVersion)
                {
                    return false;
                }

                var now = time.GetUtcNow().UtcDateTime;
                await connection.ExecuteAsync(Expired, new { OrganizationId = organizationId, Now = now }, transaction);
                var held = (await connection.QueryAsync<ProjectReservationRow>(Held, new { OrganizationId = organizationId, Now = now }, transaction))
                    .Count(row => row.ProjectId is not { } projectId || !liveProjectIds.Contains(projectId));
                if (liveProjectIds.Count + held >= limit)
                {
                    full = true;
                    return true;
                }

                if (await connection.ExecuteAsync(Fence, new { Id = organizationId, Current = inventoryVersion, Next = AccountService.NewId() }, transaction) != 1)
                {
                    return false;
                }

                var id = AccountService.NewId();
                await connection.ExecuteAsync(Insert, new { Id = id, OrganizationId = organizationId, CreatedAt = now, ExpiresAt = now + Hold }, transaction);
                reserved = id;
                return true;
            }, cancellationToken);
            return write switch
            {
                StorageConflicted => new ProjectInventoryChanged(),
                StorageWritten => reserved is { } id ? new ProjectSlotReserved(id) : full ? new ProjectSlotsFull(limit) : new ProjectInventoryChanged(),
            };
        }

        public async Task BindAsync(string reservationId, string projectId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            await DashboardDb.RunAsync(connection, Bind, new Dictionary<string, object?> { ["Id"] = reservationId, ["ProjectId"] = projectId }, null, cancellationToken);
        }

        public async Task ReleaseAsync(string reservationId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            await DashboardDb.RunAsync(connection, Remove, new Dictionary<string, object?> { ["Id"] = reservationId }, null, cancellationToken);
        }
    }

    internal sealed class ProjectReservationRow
    {
        public string Id { get; set; } = "";

        public string? ProjectId { get; set; }
    }
}
