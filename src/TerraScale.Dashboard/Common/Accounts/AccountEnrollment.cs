using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Common.Accounts;

public static class AccountEnrollment
{
    public static async Task ThrowIfErasedAsync(IDashboardState state, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var key = AccountService.AccountStorageKey(userId);
        var row = await state.GetAsync(AccountService.AccountsTable, key, ct);
        if (row is null)
        {
            return;
        }

        if (row.Id != key)
        {
            throw new IOException("Account identity is invalid.");
        }

        if (AccountService.IsDeletionFence(row))
        {
            throw new AccountAccessException("Account was erased.", 401);
        }
    }
}
