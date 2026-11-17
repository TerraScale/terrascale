using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

internal sealed partial class FunctionService
{
    public async ValueTask<FunctionResult> StatsAsync(TenantContext tenant, string name, string window, CancellationToken cancellationToken)
    {
        if (window is not ("1h" or "24h" or "7d"))
        {
            return new Validation("window must be 1h, 24h or 7d");
        }

        if (await FlushAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
        {
            return unavailable;
        }
        return await catalog.ReadAsync<FunctionResult>(async transaction =>
            await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null
                ? new NotFound()
                : await FunctionMetrics.ReadAsync(transaction, tenant, name, window, time.GetUtcNow(), cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken).ConfigureAwait(false);
    }
}
