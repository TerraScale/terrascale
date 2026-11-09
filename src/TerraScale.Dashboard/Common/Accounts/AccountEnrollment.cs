using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Common.Accounts;

public static class AccountEnrollment
{
    public static async Task ThrowIfErasedAsync(IDbContextFactory<DashboardDbContext> storage, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var hash = AccountService.AccountStorageKey(userId);
        await using var db = await storage.CreateDbContextAsync(ct);
        if (await db.Accounts.AnyAsync(account => account.SubjectHash == hash && account.ErasureId != null, ct))
        {
            throw new AccountAccessException("Account was erased.", 401);
        }
    }
}
