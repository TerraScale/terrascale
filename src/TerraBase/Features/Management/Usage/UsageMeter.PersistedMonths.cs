using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Stats;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Usage;

internal sealed partial class UsageMeter
{

    internal readonly union MonthUnitsResult(UsageTotals, Unavailable);

    internal async ValueTask<Unavailable?> AddStorageHoursAsync(IReadOnlyDictionary<string, long> bytes, CancellationToken cancellationToken)
    {
        var hour = CurrentHour();
        var month = MonthId(hour);
        foreach (var batch in bytes.Where(entry => entry.Value > 0).Chunk(100))
        {
            var written = await catalog.WriteAsync<Unavailable?>(async transaction =>
            {
                foreach (var (organizationId, stored) in batch)
                {
                    var kind = OrganizationMonths(organizationId);
                    var id = Own(month);
                    var saved = await SystemCatalog.GetAsync(transaction, kind, id, ManagementJsonContext.Default.UsageTotals, cancellationToken).ConfigureAwait(false)
                        ?? new UsageTotals(0, 0);
                    if (saved.SampledHour >= hour)
                    {
                        continue;
                    }

                    var hours = saved.SampledHour >= 0 && MonthId(saved.SampledHour) == month ? hour - saved.SampledHour : 1;
                    await PutAsync(transaction, kind, id, saved with { StorageByteHours = checked(saved.StorageByteHours + (stored * hours)), SampledHour = hour },
                        ManagementJsonContext.Default.UsageTotals, cancellationToken).ConfigureAwait(false);
                }

                return null;
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (written is { } refused)
            {
                return refused;
            }
        }

        return null;
    }

    internal async ValueTask<MonthUnitsResult> GetMonthAsync(string organizationId, string? projectId, CancellationToken cancellationToken, DateTimeOffset? of = null)
    {
        var month = of is { } start ? start.ToString("yyyy-MM", CultureInfo.InvariantCulture) : MonthId(CurrentHour());
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish();
            if (await SettleAsync(cancellationToken).ConfigureAwait(false) is Unavailable refused)
            {
                return refused;
            }

            var read = await catalog.ReadAsync<MonthUnitsResult>(async transaction =>
            {
                var kind = projectId is null ? OrganizationMonths(organizationId) : ProjectMonths(projectId);
                var units = new UsageTotals(0, 0);

                var after = month;
                while (true)
                {
                    var page = await transaction.ListAsync(kind, after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
                    foreach (var record in page.Records)
                    {
                        if (Name(record.Id) != month)
                        {
                            return units;
                        }

                        var saved = JsonSerializer.Deserialize(record.Json.Span, ManagementJsonContext.Default.UsageTotals)!;
                        units = new(units.Reads + saved.Reads, units.Writes + saved.Writes) { StorageByteHours = checked(units.StorageByteHours + saved.StorageByteHours) };
                    }

                    if (!page.HasMore)
                    {
                        return units;
                    }

                    after = page.Records[^1].Id;
                }
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (read is not UsageTotals total)
            {
                return read switch
                {
                    Unavailable error => error,
                    UsageTotals => throw new UnreachableException(),
                };
            }

            foreach (var (key, counts) in Entries())
            {
                if (key.Database.OrganizationId == organizationId && (projectId is null || key.Database.ProjectId == projectId)
                    && key.Scope != UsageScope.Lifetime && MonthId(key.Hour) == month)
                {
                    total = total with { Reads = total.Reads + counts.ReadUnits, Writes = total.Writes + counts.WriteUnits };
                }
            }

            return total;
        }
        finally
        {
            _flush.Release();
        }
    }


    private static string ProjectMonths(string projectId) => ProjectState(projectId) + "usage-months";

    private static string OrganizationMonths(string organizationId) => $"organizations/{organizationId}/usage-months";

    private static string MonthId(long hour) => DateTimeOffset.UnixEpoch.AddHours(hour).ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
