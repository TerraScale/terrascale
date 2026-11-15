using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class BackgroundJobRow
{
    public required string Id { get; init; }
    public required string Owner { get; set; }
    public required string Checkpoint { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime LeaseUntil { get; set; }
    public int Failures { get; set; }
    public DateTime? AttemptedAt { get; set; }
    public DateTime? SucceededAt { get; set; }
    public string? Cursor { get; set; }
    public string? NextCursor { get; set; }
    public string? DeliveryId { get; set; }
    public byte[]? Payload { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<BackgroundJobRow>
    {
        public void Configure(EntityTypeBuilder<BackgroundJobRow> builder)
        {
            builder.ToTable("background_jobs", table =>
            {
                table.HasCheckConstraint("ck_background_jobs_id", "length(id) BETWEEN 1 AND 256 AND length(owner) = 26 AND length(checkpoint) = 26");
                table.HasCheckConstraint("ck_background_jobs_delivery", "(payload IS NULL AND next_cursor IS NULL AND delivery_id IS NULL) OR (payload IS NOT NULL AND (next_cursor IS NULL OR length(next_cursor) = 26) AND length(delivery_id) = 26)");
            });
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Owner).IsConcurrencyToken();
            builder.Property(row => row.Checkpoint).IsConcurrencyToken();
        }
    }
}
