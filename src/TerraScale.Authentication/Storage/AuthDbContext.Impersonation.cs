using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Impersonation;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<ImpersonationRecord> Impersonations => this.Set<ImpersonationRecord>();

    private static void ConfigureImpersonation(ModelBuilder model)
    {
        model.Entity<ImpersonationRecord>(row =>
        {
            row.ToTable("impersonations", table =>
            {
                table.HasCheckConstraint("ck_impersonations_tenant", "instance_id <> 'terrascale'");
                table.HasCheckConstraint("ck_impersonations_reason", "length(reason) BETWEEN 1 AND 500");
                table.HasCheckConstraint("ck_impersonations_ticket", "length(ticket_hash) = 64");
                table.HasCheckConstraint("ck_impersonations_expiry", "expires_at IS NULL OR (redeemed_at IS NOT NULL AND unixepoch(expires_at) BETWEEN unixepoch(redeemed_at) AND unixepoch(redeemed_at) + 3600)");
            });
            row.HasKey(item => item.Id);
            row.HasIndex(item => item.TicketHash).IsUnique();
            row.HasIndex(item => item.CookieHash).IsUnique();
            row.HasIndex(item => new { item.InstanceId, item.ExpiresAt });
            row.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
            row.HasOne<UserRecord>().WithMany().HasForeignKey(item => item.ActorId).OnDelete(DeleteBehavior.Cascade);
            row.Property(item => item.Reason).HasMaxLength(500);
            Versioned(row);
        });
    }
}
