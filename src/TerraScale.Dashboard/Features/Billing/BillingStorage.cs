using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing;

internal static class BillingStorage
{
    extension(IDbContextFactory<DashboardDbContext> storage)
    {
        public async Task<BillingBinding?> BindingRowAsync(string organizationId, CancellationToken cancellationToken)
        {
            var row = await ServerTiming.ReadAsync((storage, nameof(BillingBinding), organizationId), async () =>
            {
                await using var db = await storage.CreateDbContextAsync(cancellationToken);
                return await db.Billing.AsNoTracking().SingleOrDefaultAsync(row => row.OrganizationId == organizationId, cancellationToken);
            });
            return row?.Copy();
        }

        public async Task<BillingChangeReceipt?> ChangeRowAsync(string organizationId, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            return await db.BillingChanges.AsNoTracking().SingleOrDefaultAsync(row => row.OrganizationId == organizationId, cancellationToken);
        }

        public async Task<bool> InsertAsync<TRow>(TRow row, CancellationToken cancellationToken) where TRow : class
        {
            if (row is IBillingRow versioned)
            {
                versioned.Version = AccountService.NewId();
            }

            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            db.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException error) when (error.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation or TerraBaseSqlErrorCodes.PrimaryKeyViolation })
            {
                return false;
            }
            catch (DbUpdateException error)
            {
                throw new IOException("Billing state write failed.", error);
            }
        }

        public async Task<bool> ReplaceAsync<TRow>(TRow row, CancellationToken cancellationToken) where TRow : class, IBillingRow
        {
            var expected = row.Version;
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var entry = db.Attach(row);
            entry.State = EntityState.Modified;
            row.Version = AccountService.NewId();
            entry.Property(nameof(IBillingRow.Version)).OriginalValue = expected;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                row.Version = expected;
                return false;
            }
            catch (DbUpdateException error)
            {
                row.Version = expected;
                throw new IOException("Billing state write failed.", error);
            }
        }

        public async Task<bool> RemoveAsync<TRow>(TRow row, CancellationToken cancellationToken) where TRow : class
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            db.Remove(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
            catch (DbUpdateException error)
            {
                throw new IOException("Billing state write failed.", error);
            }
        }
    }
}
