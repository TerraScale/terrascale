using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AttackProtection;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<AccountLockRecord> AccountLocks => this.Set<AccountLockRecord>();

    public DbSet<KnownDeviceRecord> KnownDevices => this.Set<KnownDeviceRecord>();

    private static void ConfigureAttackProtection(ModelBuilder model)
    {
        model.Entity<AccountLockRecord>(row =>
        {
            row.ToTable("account_locks", table => table.HasCheckConstraint("ck_account_locks_failures", "failures BETWEEN 0 AND 10"));
            row.HasKey(value => new { value.InstanceId, value.UserId });
            row.HasOne<UserRecord>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.Cascade);
            Versioned(row);
        });
        model.Entity<KnownDeviceRecord>(row =>
        {
            row.ToTable("known_devices");
            row.HasKey(value => new { value.InstanceId, value.UserId, value.Fingerprint });
            row.HasOne<UserRecord>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.Cascade);
            row.HasIndex(value => value.LastSeen);
            Versioned(row);
        });
    }
}
