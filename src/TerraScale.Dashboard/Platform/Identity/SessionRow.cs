using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Platform.Identity;

public sealed class SessionRow
{
    public const string Browser = "browser";
    public const string Credential = "credential";

    public required string Id { get; init; }

    public required string Kind { get; init; }

    public string? TokenHash { get; init; }

    public required string UserId { get; init; }

    public required string Device { get; set; }

    public required DateTime ExpiresAt { get; set; }

    public required string Version { get; set; }

    public required byte[] Nonce { get; set; }

    public required byte[] Ciphertext { get; set; }

    public required byte[] Tag { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<SessionRow>
    {
        public void Configure(EntityTypeBuilder<SessionRow> builder)
        {
            builder.ToTable("sessions", table =>
            {
                table.HasCheckConstraint("ck_sessions_id", "length(id) = 26");
                table.HasCheckConstraint("ck_sessions_kind", $"(kind = '{Browser}' AND length(token_hash) = 64) OR (kind = '{Credential}' AND token_hash IS NULL)");
                table.HasCheckConstraint("ck_sessions_user_id", "length(user_id) BETWEEN 1 AND 128");
                table.HasCheckConstraint("ck_sessions_version", "length(version) = 26");
                table.HasCheckConstraint("ck_sessions_nonce", "length(nonce) = 12");
                table.HasCheckConstraint("ck_sessions_tag", "length(tag) = 16");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => row.TokenHash).IsUnique();
            builder.HasIndex(row => new { row.UserId, row.Kind, row.ExpiresAt });
            builder.Property(row => row.Version).IsConcurrencyToken();
        }
    }
}
