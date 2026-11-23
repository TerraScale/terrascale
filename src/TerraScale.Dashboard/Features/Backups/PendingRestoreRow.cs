using Dapper;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Backups;

public sealed record PendingRestoreRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; init; }

    public required string SourceDatabaseId { get; init; }

    public required DateTime CreatedAt { get; init; }
}

internal static class PendingRestores
{
    private const string Pending = DashboardSql.PendingRestores.Select + " WHERE organization_id = @OrganizationId AND project_id = @ProjectId ORDER BY id";
    private const string Drop = "DELETE FROM pending_restores WHERE id = @Id";

    extension(DashboardDb storage)
    {
        public async Task RecordRestoreAsync(string operationId, string organizationId, string projectId, string sourceDatabaseId, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(DashboardSql.PendingRestores.Insert, new PendingRestoreRow
            {
                Id = operationId,
                OrganizationId = organizationId,
                ProjectId = projectId,
                SourceDatabaseId = sourceDatabaseId,
                CreatedAt = now.UtcDateTime,
            });
        }

        public async Task<List<PendingRestoreRow>> PendingRestoresAsync(string organizationId, string projectId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            return (await connection.QueryAsync<PendingRestoreRow>(Pending, new { OrganizationId = organizationId, ProjectId = projectId })).AsList();
        }

        public async Task DropRestoreAsync(string operationId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(Drop, new { Id = operationId });
        }
    }
}
