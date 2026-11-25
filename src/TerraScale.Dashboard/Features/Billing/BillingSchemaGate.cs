using System.Collections.Frozen;
using Dapper;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing;

public sealed class BillingSchemaGate(DashboardDb storage, ILogger<BillingSchemaGate> logger)
{
    private const string Applied = "SELECT script_name FROM schema_versions";
    private static readonly Lazy<FrozenSet<string>> Known = new(() => DashboardDbUpRunner.Scripts().Select(static script => script.Name).ToFrozenSet(StringComparer.Ordinal));
    private int _logged;

    public async Task<bool> AllowsAsync(CancellationToken cancellationToken)
    {
        var known = Known.Value;
        await using var connection = await storage.OpenAsync(cancellationToken);
        var unknown = (await connection.QueryAsync<string>(Applied)).Where(name => !known.Contains(name)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count == 0)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _logged, 1) == 0)
        {
            BillingLog.BillingJobsIdle(logger, unknown[^1]);
        }

        return false;
    }
}
