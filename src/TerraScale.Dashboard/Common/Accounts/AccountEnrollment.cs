using Dapper;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Common.Accounts;

public static class AccountEnrollment
{
    private const string Erased = "SELECT count(*) FROM accounts WHERE subject_hash = @Hash AND erasure_id IS NOT NULL";

    public static async Task ThrowIfErasedAsync(DashboardDb storage, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var hash = AccountService.AccountStorageKey(userId);
        await using var connection = await storage.OpenAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<long>(Erased, new { Hash = hash }) > 0)
        {
            throw new AccountAccessException("Account was erased.", 401);
        }
    }
}
