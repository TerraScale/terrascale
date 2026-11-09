using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Backups;

public sealed class PendingRestoreRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; init; }

    public required string SourceDatabaseId { get; init; }

    public required DateTime CreatedAt { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<PendingRestoreRow>
    {
        public void Configure(EntityTypeBuilder<PendingRestoreRow> builder)
        {
            builder.ToTable("pending_restores", table =>
            {
                table.HasCheckConstraint("ck_pending_restores_id",
                    "length(id) = 26 AND length(organization_id) = 26 AND length(project_id) = 26 AND length(source_database_id) = 26");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => new { row.OrganizationId, row.ProjectId });
        }
    }
}

internal static class PendingRestores
{
    extension(IDbContextFactory<DashboardDbContext> storage)
    {
        public async Task RecordRestoreAsync(string operationId, string organizationId, string projectId, string sourceDatabaseId, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            db.PendingRestores.Add(new PendingRestoreRow
            {
                Id = operationId,
                OrganizationId = organizationId,
                ProjectId = projectId,
                SourceDatabaseId = sourceDatabaseId,
                CreatedAt = now.UtcDateTime,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<List<PendingRestoreRow>> PendingRestoresAsync(string organizationId, string projectId, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            return await db.PendingRestores.AsNoTracking().Where(row => row.OrganizationId == organizationId && row.ProjectId == projectId)
                .OrderBy(row => row.Id).ToListAsync(cancellationToken);
        }

        public async Task DropRestoreAsync(string operationId, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            await db.PendingRestores.Where(row => row.Id == operationId).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
