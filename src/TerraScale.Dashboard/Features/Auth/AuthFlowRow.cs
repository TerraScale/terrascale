using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class AuthFlowRow
{
    public const string OidcPurpose = "ts-dashboard-oidc";
    public const string StepUpPurpose = "ts-dashboard-step-up";

    public required string Id { get; init; }

    public required string TokenHash { get; init; }

    public required string Purpose { get; init; }

    public required byte[] Nonce { get; init; }

    public required byte[] Ciphertext { get; init; }

    public required byte[] Tag { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required DateTime ExpiresAt { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<AuthFlowRow>
    {
        public void Configure(EntityTypeBuilder<AuthFlowRow> builder)
        {
            builder.ToTable("auth_flows", table =>
            {
                table.HasCheckConstraint("ck_auth_flows_id", "length(id) = 26");
                table.HasCheckConstraint("ck_auth_flows_token_hash", "length(token_hash) = 64");
                table.HasCheckConstraint("ck_auth_flows_purpose", $"purpose IN ('{OidcPurpose}', '{StepUpPurpose}')");
                table.HasCheckConstraint("ck_auth_flows_nonce", "length(nonce) = 12");
                table.HasCheckConstraint("ck_auth_flows_tag", "length(tag) = 16");
                table.HasCheckConstraint("ck_auth_flows_lifetime", "expires_at > created_at");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => row.TokenHash).IsUnique();
            builder.HasIndex(row => row.ExpiresAt);
        }
    }
}
