using System.Data;
using Dapper;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Organizations
{
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

        public async Task<string?> ReserveAsync(string organizationId, IReadOnlyCollection<string> liveProjectIds, int limit, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                string? reserved = null;
                try
                {
                    await storage.WriteAsync(async (connection, transaction) =>
                    {
                        var now = time.GetUtcNow().UtcDateTime;
                        await connection.ExecuteAsync(Expired, new { OrganizationId = organizationId, Now = now }, transaction);
                        var held = (await connection.QueryAsync<ProjectReservationRow>(Held, new { OrganizationId = organizationId, Now = now }, transaction))
                            .Count(row => row.ProjectId is not { } projectId || !liveProjectIds.Contains(projectId));
                        if (liveProjectIds.Count + held >= limit)
                        {
                            return;
                        }

                        var version = (await connection.QueryAsync<string>(RootVersion, new { Id = organizationId }, transaction)).FirstOrDefault()
                            ?? throw new AccountAccessException("Organization is unavailable.", StatusCodes.Status404NotFound);
                        DashboardDb.Expect(await connection.ExecuteAsync(Fence, new { Id = organizationId, Current = version, Next = AccountService.NewId() }, transaction));
                        var id = AccountService.NewId();
                        await connection.ExecuteAsync(Insert, new { Id = id, OrganizationId = organizationId, CreatedAt = now, ExpiresAt = now + Hold }, transaction);
                        reserved = id;
                    }, cancellationToken);
                    return reserved;
                }
                catch (DBConcurrencyException) when (attempt < 4)
                {
                    await DashboardDb.ConflictBackoffAsync(attempt, time, cancellationToken);
                }
            }
        }

        public async Task BindAsync(string reservationId, string projectId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(Bind, new { Id = reservationId, ProjectId = projectId }, cancellationToken: cancellationToken));
        }

        public async Task ReleaseAsync(string reservationId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(Remove, new { Id = reservationId }, cancellationToken: cancellationToken));
        }
    }

    internal sealed class ProjectReservationRow
    {
        public string Id { get; set; } = "";

        public string? ProjectId { get; set; }
    }
}
