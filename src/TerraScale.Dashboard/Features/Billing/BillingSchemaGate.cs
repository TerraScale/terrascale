using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing;

public sealed class BillingSchemaGate(IDbContextFactory<DashboardDbContext> storage, ILogger<BillingSchemaGate> logger)
{
    private int logged;

    public async Task<bool> AllowsAsync(CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknown = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Where(id => !known.Contains(id)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count == 0) { return true; }
        if (Interlocked.Exchange(ref logged, 1) == 0) { BillingLog.BillingJobsIdle(logger, unknown[^1]); }
        return false;
    }
}
