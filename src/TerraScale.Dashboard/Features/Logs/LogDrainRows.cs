using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Features.Logs;

public sealed class LogDrainRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; init; }

    public required string Revision { get; set; }

    public required string Url { get; set; }

    public string? HeaderName { get; set; }

    public string? HeaderNonce { get; set; }

    public string? HeaderCiphertext { get; set; }

    public string? HeaderTag { get; set; }

    public required DateTime CreatedAt { get; init; }

    public DateTime? LastSuccessAt { get; set; }

    public DateTime? LastErrorAt { get; set; }

    public int? LastErrorStatus { get; set; }

    public DateTime? LastGapAt { get; set; }

    public List<LogDrainCursorRow> Cursors { get; init; } = [];

    public sealed class Configuration : IEntityTypeConfiguration<LogDrainRow>
    {
        public void Configure(EntityTypeBuilder<LogDrainRow> builder)
        {
            builder.ToTable("log_drains", table =>
            {
                table.HasCheckConstraint("ck_log_drains_id", "length(id) = 26 AND length(organization_id) = 26 AND length(project_id) = 26");
                table.HasCheckConstraint("ck_log_drains_revision", "length(revision) = 26");
                table.HasCheckConstraint("ck_log_drains_url", "url LIKE 'https://%' AND length(url) <= 2048");
                table.HasCheckConstraint("ck_log_drains_header",
                    "(header_name IS NULL AND header_nonce IS NULL AND header_ciphertext IS NULL AND header_tag IS NULL) OR " +
                    "(length(header_name) BETWEEN 1 AND 64 AND header_nonce IS NOT NULL AND header_ciphertext IS NOT NULL AND header_tag IS NOT NULL)");
                table.HasCheckConstraint("ck_log_drains_error", "(last_error_at IS NULL) = (last_error_status IS NULL) AND (last_error_status IS NULL OR last_error_status BETWEEN 0 AND 599)");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => row.ProjectId).IsUnique();
            builder.HasIndex(row => row.OrganizationId);
            builder.Property(row => row.Revision).IsConcurrencyToken();
            builder.HasMany(row => row.Cursors).WithOne().HasForeignKey(row => row.DrainId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

public sealed class LogDrainCursorRow
{
    public required string Id { get; init; }

    public required string DrainId { get; init; }

    public required string DatabaseId { get; init; }

    public required string Cursor { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<LogDrainCursorRow>
    {
        public void Configure(EntityTypeBuilder<LogDrainCursorRow> builder)
        {
            builder.ToTable("log_drain_cursors", table =>
            {
                table.HasCheckConstraint("ck_log_drain_cursors_id", "length(id) = 26 AND length(drain_id) = 26 AND length(database_id) = 26");
                table.HasCheckConstraint("ck_log_drain_cursors_cursor", "length(cursor) = 26");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => new { row.DrainId, row.DatabaseId }).IsUnique();
        }
    }
}
