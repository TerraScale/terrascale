using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AgentId;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<AgentIdTransactionRecord> AgentIdTransactions => this.Set<AgentIdTransactionRecord>();

    private static void ConfigureAgentId(ModelBuilder model)
    {
        model.Entity<AgentIdTransactionRecord>(transaction =>
        {
            transaction.ToTable("agentid_transactions", table =>
            {
                table.HasCheckConstraint("ck_agentid_transactions_expiry", "expires_at > created_at");
                table.HasCheckConstraint("ck_agentid_transactions_proof", "(proved_at IS NULL) = (agent_id IS NULL) AND (agent_id IS NULL) = (owner_user_id IS NULL)"
                    + " AND (agent_id IS NULL) = (kid IS NULL) AND (completed_at IS NULL OR proved_at IS NOT NULL)");
            });
            transaction.HasKey(row => row.Id);
            transaction.HasIndex(row => row.RequestHash).IsUnique();
            transaction.HasIndex(row => row.ExpiresAt);
            transaction.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            transaction.Property(row => row.Id).HasMaxLength(26);
            transaction.Property(row => row.RequestHash).HasMaxLength(AgentIdTransactionRecord.RequestHashLength).IsRequired();
            transaction.Property(row => row.ClientId).HasMaxLength(255).IsRequired();
            transaction.Property(row => row.AgentId).HasMaxLength(26);
            transaction.Property(row => row.OwnerUserId).HasMaxLength(26);
            transaction.Property(row => row.Kid).HasMaxLength(26);
            Versioned(transaction);
        });
    }
}
