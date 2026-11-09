using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Platform.Mcp;

public sealed class McpClientRow
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string[] RedirectUris { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required DateTime LastActivityAt { get; set; }

    public DateTime? FirstSignInCompletedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public required string Version { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<McpClientRow>
    {
        public void Configure(EntityTypeBuilder<McpClientRow> builder)
        {
            builder.ToTable("mcp_clients", table =>
            {
                table.HasCheckConstraint("ck_mcp_clients_id", "length(id) = 26 AND id = upper(id)");
                table.HasCheckConstraint("ck_mcp_clients_name", "length(name) BETWEEN 1 AND 256");
                table.HasCheckConstraint("ck_mcp_clients_redirect_uris", "json_array_length(redirect_uris) BETWEEN 1 AND 10");
                table.HasCheckConstraint("ck_mcp_clients_activity", "last_activity_at >= created_at");
                table.HasCheckConstraint("ck_mcp_clients_first_sign_in",
                    "(first_sign_in_completed_at IS NULL AND last_activity_at = created_at) OR (first_sign_in_completed_at BETWEEN created_at AND last_activity_at)");
                table.HasCheckConstraint("ck_mcp_clients_closed", "closed_at IS NULL OR closed_at >= last_activity_at");
                table.HasCheckConstraint("ck_mcp_clients_version", "length(version) = 26");
            });
            builder.HasKey(row => row.Id);
            builder.Property(row => row.RedirectUris).HasConversion(
                uris => JsonSerializer.Serialize(uris, McpRowJson.Default.StringArray),
                json => JsonSerializer.Deserialize(json, McpRowJson.Default.StringArray)!,
                new ValueComparer<string[]>((left, right) => left!.SequenceEqual(right!), uris => uris.Aggregate(0, (hash, uri) => HashCode.Combine(hash, uri.GetHashCode(StringComparison.Ordinal))),
                    uris => uris.ToArray()));
            builder.HasIndex(row => new { row.FirstSignInCompletedAt, row.CreatedAt });
            builder.Property(row => row.Version).IsConcurrencyToken();
        }
    }
}

public sealed class McpCodeRow
{
    public required string Id { get; init; }

    public required string CodeHash { get; init; }

    public required string ClientId { get; init; }

    public required string CredentialId { get; init; }

    public required string UserId { get; init; }

    public required string RedirectUri { get; init; }

    public required string CodeChallenge { get; init; }

    public required string Resource { get; set; }

    public required string ClientName { get; init; }

    public required DateTime ExpiresAt { get; init; }

    public required DateTime TokenExpiresAt { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<McpCodeRow>
    {
        public void Configure(EntityTypeBuilder<McpCodeRow> builder)
        {
            builder.ToTable("mcp_codes", table =>
            {
                table.HasCheckConstraint("ck_mcp_codes_id", "length(id) = 26");
                table.HasCheckConstraint("ck_mcp_codes_code_hash", "length(code_hash) = 64");
                table.HasCheckConstraint("ck_mcp_codes_code_challenge", "length(code_challenge) = 43");
                table.HasCheckConstraint("ck_mcp_codes_token_expiry", "token_expires_at > expires_at");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => row.CodeHash).IsUnique();
            builder.HasIndex(row => row.ExpiresAt);
            builder.HasOne<McpClientRow>().WithMany().HasForeignKey(row => row.ClientId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<SessionRow>().WithMany().HasForeignKey(row => row.CredentialId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class McpRowJson : JsonSerializerContext;
