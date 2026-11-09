using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class AccessTokenRow
{
    public required string Id { get; init; }

    public required string UserId { get; init; }

    public required string Name { get; init; }

    public required string LastFour { get; init; }

    public required string SecretHash { get; init; }

    public required string CredentialId { get; init; }

    public string? McpClientId { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required DateTime ExpiresAt { get; init; }

    public DateTime? RevokedAt { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<AccessTokenRow>
    {
        public void Configure(EntityTypeBuilder<AccessTokenRow> builder)
        {
            builder.ToTable("access_tokens", table =>
            {
                table.HasCheckConstraint("ck_access_tokens_id", "length(id) = 26");
                table.HasCheckConstraint("ck_access_tokens_name", "length(name) BETWEEN 1 AND 256");
                table.HasCheckConstraint("ck_access_tokens_last_four", "length(last_four) = 4");
                table.HasCheckConstraint("ck_access_tokens_secret_hash", "length(secret_hash) = 64");
                table.HasCheckConstraint("ck_access_tokens_lifetime", "expires_at > created_at");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => row.SecretHash).IsUnique();
            builder.HasIndex(row => row.CredentialId).IsUnique();
            builder.HasIndex(row => new { row.UserId, row.ExpiresAt });
            builder.HasOne<SessionRow>().WithMany().HasForeignKey(row => row.CredentialId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<McpClientRow>().WithMany().HasForeignKey(row => row.McpClientId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
