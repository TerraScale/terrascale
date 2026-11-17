using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Tenancy;
using TerraScale.TerraBase.Contracts.Functions;

namespace TerraBase.Features.Functions;

internal sealed record FunctionAggregate(DateTimeOffset At, int Minutes, long Invocations, long Errors, long MaxMs, long SqlCalls,
    long RowsRead, long RowsWritten, IReadOnlyList<long> Histogram)
{
    public static FunctionAggregate Empty(DateTimeOffset at, int minutes = 1) => new(at, minutes, 0, 0, 0, 0, 0, 0, new long[64]);

    public FunctionAggregate Add(FunctionAggregate other) => new(At, Minutes, Invocations + other.Invocations, Errors + other.Errors,
        Math.Max(MaxMs, other.MaxMs), SqlCalls + other.SqlCalls, RowsRead + other.RowsRead, RowsWritten + other.RowsWritten,
        Histogram.Zip(other.Histogram, (left, right) => left + right).ToArray());

    public long Percentile(int percent)
    {
        var rank = Invocations / 100 * percent + (Invocations % 100 * percent + 99) / 100;
        var count = 0L;
        for (var i = 0; i < Histogram.Count && Invocations > 0; i++)
        {
            count += Histogram[i];
            if (count >= rank)
            {
                return Math.Min(MaxMs, i == 63 ? long.MaxValue : 1L << i);
            }
        }

        return 0;
    }
}

internal static class FunctionMetrics
{
    private static readonly int[] Resolutions = [1, 60];
    public static string Kind(TenantContext tenant, string name) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "function_stats/" + name;

    public static async Task RecordAsync(IStorageTransaction transaction, TenantContext tenant, string name, IReadOnlyList<FunctionLogBody> logs,
        CancellationToken cancellationToken)
    {
        foreach (var minutes in Resolutions)
        {
            foreach (var group in logs.GroupBy(log => Floor(log.At, minutes)))
            {
                var at = group.Key;
                var id = Slot(at, minutes);
                var stored = await SystemCatalog.GetAsync(transaction, Kind(tenant, name), id,
                    FunctionsJsonContext.Default.FunctionAggregate, cancellationToken).ConfigureAwait(false);
                var bucket = stored is not null && stored.At == at ? stored : FunctionAggregate.Empty(at, minutes);
                foreach (var log in group)
                {
                    long[] histogram = new long[64];
                    histogram[log.DurationMs <= 1 ? 0 : BitOperations.Log2((ulong)(log.DurationMs - 1)) + 1] = 1;
                    bucket = bucket.Add(new FunctionAggregate(at, minutes, 1, log.Error is null ? 0 : 1, log.DurationMs, log.SqlCalls,
                        log.RowsRead, log.RowsWritten, histogram));
                }
                await SystemCatalog.PutAsync(transaction, Kind(tenant, name), id, bucket,
                    FunctionsJsonContext.Default.FunctionAggregate, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static async Task<TerraBaseFunctionStats> ReadAsync(IStorageTransaction transaction, TenantContext tenant, string name,
        string window, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (minutes, count) = window switch
        {
            "1h" => (1, 60),
            "24h" => (60, 24),
            "7d" => (60, 168),
            _ => throw new UnreachableException(),
        };
        var stored = await SystemCatalog.ListAllAsync(transaction, Kind(tenant, name), FunctionsJsonContext.Default.FunctionAggregate,
            cancellationToken).ConfigureAwait(false);
        var byTime = stored.Where(bucket => bucket.Minutes == minutes).ToDictionary(bucket => bucket.At);
        var first = Floor(now, minutes).AddMinutes(-(count - 1) * minutes);
        var total = FunctionAggregate.Empty(first);
        var buckets = new List<TerraBaseFunctionStatsBucket>(count);
        for (var i = 0; i < count; i++)
        {
            var at = first.AddMinutes(i * minutes);
            var bucket = byTime.GetValueOrDefault(at) ?? FunctionAggregate.Empty(at, minutes);
            total = total.Add(bucket);
            buckets.Add(new(at, bucket.Invocations, bucket.Errors, bucket.Percentile(50), bucket.Percentile(95)));
        }

        return new(window, total.Invocations, total.Errors, total.Percentile(50), total.Percentile(95), total.Percentile(99),
            total.MaxMs, total.SqlCalls, total.RowsRead, total.RowsWritten, buckets);
    }

    private static DateTimeOffset Floor(DateTimeOffset at, int minutes) =>
        DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds() / (minutes * 60) * (minutes * 60));

    private static string Slot(DateTimeOffset at, int minutes) =>
        (minutes == 1 ? "m" : "h") + (at.ToUnixTimeSeconds() / (minutes * 60) % (minutes == 1 ? 60 : 168)).ToString(CultureInfo.InvariantCulture);
}
