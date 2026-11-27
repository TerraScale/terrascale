using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TerraBase.Benchmarks;

internal sealed record ReportLoadFailure(string Path, string Reason);

internal readonly union LoadReportOutcome(BenchReport, ReportLoadFailure);

internal sealed record BenchCompareColumn(
    string Label,
    string Path,
    int SchemaVersion,
    string GitSha,
    DateTimeOffset TimestampUtc,
    string CpuModel,
    bool GcServerMode,
    int Rounds,
    double IterationsScale,
    double TotalWallSeconds);

internal sealed record BenchCompareRow(
    string Cohort,
    string Suite,
    string Name,
    bool LowerIsBetter,
    IReadOnlyList<BenchMetrics?> Samples,
    IReadOnlyList<double?> PrimaryDeltaPcts,
    double? FinalPrimaryDeltaPct,
    double? FinalP99DeltaPct,
    double? FinalAllocDeltaPct,
    IReadOnlyList<string> Flags);

internal sealed record BenchCohortSummary(
    string Cohort,
    bool LowerIsBetter,
    int MatchedCount,
    double? GeoMeanRatio,
    double? GeoMeanDeltaPct,
    int ImprovedCount,
    int RegressedCount,
    int NoisyCount,
    int FdLeakCount);

internal sealed record BenchComparisonReport(
    IReadOnlyList<BenchCompareColumn> Columns,
    IReadOnlyList<BenchCohortSummary> Summaries,
    IReadOnlyList<BenchCompareRow> CoreThroughput,
    IReadOnlyList<BenchCompareRow> Replication,
    IReadOnlyList<BenchCompareRow> ResourceSnapshots,
    IReadOnlyList<BenchCompareRow> NewOrUnmatched);

internal static class BenchCompare
{
    private const string CohortCore = "Core Throughput";
    private const string CohortReplication = "Replication";
    private const string CohortResources = "Resource Snapshots";
    private const string CohortUnmatched = "New/Unmatched";

    private const double ThroughputRegressionPct = -5.0;
    private const double ResourceRegressionPct = 5.0;
    private const double LatencyRegressionPct = 15.0;
    private const double AllocRegressionPct = 15.0;
    private const double NoisyCvPctThreshold = 10.0;

    private static readonly (string Label, Func<BackupCostMetrics, double> Select)[] BackupCostFields =
    [
        ("S3 GET", static cost => cost.S3GetCount),
        ("S3 PUT", static cost => cost.S3PutCount),
        ("S3 DELETE", static cost => cost.S3DeleteCount),
        ("S3 batch DELETE", static cost => cost.S3DeleteBatchCount),
        ("S3 LIST", static cost => cost.S3ListCount),
        ("S3 304", static cost => cost.S3NotModifiedCount),
        ("S3 412", static cost => cost.S3ConflictCount),
        ("Uploaded (B)", static cost => cost.UploadedBytes),
        ("Downloaded (B)", static cost => cost.DownloadedBytes),
        ("Manifest uploaded (B)", static cost => cost.ManifestBytesUploaded),
        ("Snapshot raw (B)", static cost => cost.SnapshotRawBytes),
        ("Snapshot stored (B)", static cost => cost.SnapshotStoredBytes),
        ("Objects deleted", static cost => cost.ObjectsDeleted),
        ("RSS delta (MiB)", static cost => cost.RssDeltaMiB),
        ("FD delta", static cost => cost.OpenFdDelta),
    ];

    public static LoadReportOutcome LoadReport(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return new ReportLoadFailure(path, "file not found");
        }

        return ReadAndDeserializeReport(fullPath, path);
    }

    public static BenchComparisonReport Compare(IReadOnlyList<(string Path, BenchReport Report)> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is < 2 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(inputs), "Comparison requires 2 or 3 reports.");
        }

        var columns = new BenchCompareColumn[inputs.Count];
        var maps = new Dictionary<BenchKey, BenchMetrics>[inputs.Count];
        var orderedKeys = new List<BenchKey>();
        var seenKeys = new HashSet<BenchKey>();

        for (var i = 0; i < inputs.Count; i++)
        {
            var (path, report) = inputs[i];
            var fallbackLabel = i switch
            {
                0 => "A",
                1 => "B",
                _ => "C",
            };
            var label = string.IsNullOrWhiteSpace(report.Label) ? fallbackLabel : report.Label;
            columns[i] = new BenchCompareColumn(
                label,
                path,
                report.SchemaVersion,
                report.GitSha,
                report.TimestampUtc,
                report.CpuModel,
                report.GcServerMode,
                report.Rounds,
                report.IterationsScale,
                report.TotalWallSeconds);

            var map = new Dictionary<BenchKey, BenchMetrics>();
            foreach (var metric in report.Metrics)
            {
                var key = new BenchKey(metric.Suite, metric.Name);
                map[key] = metric;
                if (seenKeys.Add(key))
                {
                    orderedKeys.Add(key);
                }
            }

            maps[i] = map;
        }

        List<BenchCompareRow> core = [];
        List<BenchCompareRow> replication = [];
        List<BenchCompareRow> resources = [];
        List<BenchCompareRow> unmatched = [];

        foreach (var key in orderedKeys)
        {
            var samples = new BenchMetrics?[inputs.Count];
            var presentCount = 0;
            for (var i = 0; i < inputs.Count; i++)
            {
                if (maps[i].TryGetValue(key, out var metric))
                {
                    samples[i] = metric;
                    presentCount++;
                }
            }

            var allPresent = presentCount == inputs.Count;
            var lowerIsBetter = string.Equals(key.Suite, "resources", StringComparison.Ordinal);
            var cohort = !allPresent
                ? CohortUnmatched
                : lowerIsBetter
                    ? CohortResources
                    : string.Equals(key.Suite, "replication", StringComparison.Ordinal)
                        ? CohortReplication
                        : CohortCore;

            var deltas = new double?[inputs.Count - 1];
            var baseline = samples[0];
            for (var i = 1; i < inputs.Count; i++)
            {
                deltas[i - 1] = ComputePrimaryDeltaPct(baseline, samples[i], lowerIsBetter);
            }

            var candidate = samples[^1];
            var candidateCol = columns[^1];
            var finalPrimaryDelta = deltas[^1];
            var finalP99Delta = ComputeMetricDeltaPct(baseline?.P99Us, candidate?.P99Us);
            var finalAllocDelta = ComputeMetricDeltaPct(baseline?.AllocBytesPerOp, candidate?.AllocBytesPerOp);
            var flags = EvaluateFlags(lowerIsBetter, finalPrimaryDelta, finalP99Delta, finalAllocDelta, candidate, candidateCol.SchemaVersion);

            var row = new BenchCompareRow(
                cohort,
                key.Suite,
                key.Name,
                lowerIsBetter,
                samples,
                deltas,
                finalPrimaryDelta,
                finalP99Delta,
                finalAllocDelta,
                flags);

            switch (cohort)
            {
                case CohortCore:
                    core.Add(row);
                    break;
                case CohortReplication:
                    replication.Add(row);
                    break;
                case CohortResources:
                    resources.Add(row);
                    break;
                default:
                    unmatched.Add(row);
                    break;
            }
        }

        BenchCohortSummary[] summaries =
        [
            SummarizeCohort(CohortCore, lowerIsBetter: false, core),
            SummarizeCohort(CohortReplication, lowerIsBetter: false, replication),
            SummarizeCohort(CohortResources, lowerIsBetter: true, resources),
        ];

        return new BenchComparisonReport(columns, summaries, core, replication, resources, unmatched);
    }

    public static string RenderMarkdown(BenchComparisonReport comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var sb = new StringBuilder(8192);
        var columns = comparison.Columns;
        var hasThree = columns.Count == 3;

        _ = sb.AppendLine("# TerraBase Benchmark Comparison");
        _ = sb.AppendLine();
        _ = sb.AppendLine("| Slot | Label | Schema | Git SHA | Rounds | Scale | GC Server | Wall (s) | CPU |");
        _ = sb.AppendLine("| --- | --- | ---: | --- | ---: | ---: | --- | ---: | --- |");
        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            var slot = i switch
            {
                0 => "A",
                1 => "B",
                _ => "C",
            };
            var shortSha = col.GitSha.Length > 12 ? col.GitSha[..12] : col.GitSha.Length > 0 ? col.GitSha : "n/a";
            var cpu = col.SchemaVersion >= 2 && col.CpuModel.Length > 0 ? col.CpuModel : "n/a";
            var gcServer = col.SchemaVersion >= 2 ? (col.GcServerMode ? "Server" : "Workstation") : "n/a";
            var wall = col.SchemaVersion >= 2 && col.TotalWallSeconds > 0
                ? col.TotalWallSeconds.ToString("F1", CultureInfo.InvariantCulture)
                : "n/a";
            _ = sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {slot} | {col.Label} | v{col.SchemaVersion} | `{shortSha}` | {col.Rounds} | {col.IterationsScale:F2} | {gcServer} | {wall} | {cpu} |"));
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("## Cohort Summary (final vs A)");
        _ = sb.AppendLine();
        _ = sb.AppendLine("| Cohort | Matched | Geomean Delta | Improved | Regressed | Noisy | FD Leaks |");
        _ = sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var summary in comparison.Summaries)
        {
            var geo = summary.GeoMeanDeltaPct is { } pct ? FormatSignedPct(pct) : "n/a";
            _ = sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {summary.Cohort} | {summary.MatchedCount} | {geo} | {summary.ImprovedCount} | {summary.RegressedCount} | {summary.NoisyCount} | {summary.FdLeakCount} |"));
        }

        AppendThroughputSection(sb, "Core Throughput", comparison.CoreThroughput, columns, hasThree);
        AppendThroughputSection(sb, "Replication", comparison.Replication, columns, hasThree);
        AppendResourceSection(sb, comparison.ResourceSnapshots, columns, hasThree);
        AppendUnmatchedSection(sb, comparison.NewOrUnmatched, columns);
        AppendBackupCostSection(sb, comparison, columns);

        return sb.ToString();
    }

    public static void PrintComparison(BenchComparisonReport comparison) =>
        Console.WriteLine(RenderMarkdown(comparison));

    private static void AppendThroughputSection(
        StringBuilder sb,
        string title,
        IReadOnlyList<BenchCompareRow> rows,
        IReadOnlyList<BenchCompareColumn> columns,
        bool hasThree)
    {
        if (rows.Count == 0)
        {
            return;
        }

        _ = sb.AppendLine();
        _ = sb.Append("## ").AppendLine(title);
        _ = sb.AppendLine();

        var aLabel = columns[0].Label;
        var bLabel = columns[1].Label;
        var cLabel = hasThree ? columns[2].Label : string.Empty;
        var lastCol = columns[^1];
        var v2Candidate = lastCol.SchemaVersion >= 2;

        if (hasThree)
        {
            _ = sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| Suite | Benchmark | {aLabel} ops/s | {bLabel} ops/s | {cLabel} ops/s | B vs A | C vs A | P99 (us) | P999 (us) | Alloc (B/op) | Ops/CPU-s | Heap/Nat (MiB) | FDs | Flags |"));
            _ = sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        }
        else
        {
            _ = sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| Suite | Benchmark | {aLabel} ops/s | {bLabel} ops/s | Delta | P99 (us) | P99 Delta | P999 (us) | Alloc (B/op) | Alloc Delta | Ops/CPU-s | Heap/Nat (MiB) | FDs | Flags |"));
            _ = sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        }

        foreach (var row in rows)
        {
            var a = row.Samples[0];
            var b = row.Samples[1];
            var last = row.Samples[^1];
            var p99Str = last is not null ? last.P99Us.ToString("F1", CultureInfo.InvariantCulture) : "n/a";
            var p999Str = v2Candidate && last is not null ? last.P999Us.ToString("F1", CultureInfo.InvariantCulture) : "n/a";
            var allocStr = last is not null ? last.AllocBytesPerOp.ToString("F0", CultureInfo.InvariantCulture) : "n/a";
            var opsCpuStr = v2Candidate && last is not null ? last.OpsPerCpuSec.ToString("F1", CultureInfo.InvariantCulture) : "n/a";
            var heapNatStr = v2Candidate && last is not null
                ? string.Create(CultureInfo.InvariantCulture, $"{last.ManagedHeapMiB:F1}/{last.NativeRssMiB:F1}")
                : "n/a";
            var fdStr = v2Candidate && last is not null
                ? string.Create(CultureInfo.InvariantCulture, $"{last.OpenFdCount}({last.OpenFdDelta:+#;-#;0})")
                : "n/a";
            var flagsStr = row.Flags.Count > 0 ? string.Join(' ', row.Flags) : string.Empty;

            if (hasThree)
            {
                var c = row.Samples[2];
                _ = sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {row.Suite} | {row.Name} | {FormatOps(a)} | {FormatOps(b)} | {FormatOps(c)} | {FormatOptionalPct(row.PrimaryDeltaPcts[0])} | {FormatOptionalPct(row.PrimaryDeltaPcts[1])} | {p99Str} | {p999Str} | {allocStr} | {opsCpuStr} | {heapNatStr} | {fdStr} | {flagsStr} |"));
            }
            else
            {
                _ = sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {row.Suite} | {row.Name} | {FormatOps(a)} | {FormatOps(b)} | {FormatOptionalPct(row.PrimaryDeltaPcts[0])} | {p99Str} | {FormatOptionalPct(row.FinalP99DeltaPct)} | {p999Str} | {allocStr} | {FormatOptionalPct(row.FinalAllocDeltaPct)} | {opsCpuStr} | {heapNatStr} | {fdStr} | {flagsStr} |"));
            }
        }
    }

    private static void AppendResourceSection(
        StringBuilder sb,
        IReadOnlyList<BenchCompareRow> rows,
        IReadOnlyList<BenchCompareColumn> columns,
        bool hasThree)
    {
        if (rows.Count == 0)
        {
            return;
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("## Resource Snapshots (lower is better)");
        _ = sb.AppendLine();

        var aLabel = columns[0].Label;
        var bLabel = columns[1].Label;
        var cLabel = hasThree ? columns[2].Label : string.Empty;
        var v2Candidate = columns[^1].SchemaVersion >= 2;

        if (hasThree)
        {
            _ = sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| Benchmark | Unit | {aLabel} | {bLabel} | {cLabel} | B vs A | C vs A | Heap/Nat (MiB) | FDs | Flags |"));
            _ = sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        }
        else
        {
            _ = sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| Benchmark | Unit | {aLabel} | {bLabel} | Delta | Heap/Nat (MiB) | FDs | Flags |"));
            _ = sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
        }

        foreach (var row in rows)
        {
            var a = row.Samples[0];
            var b = row.Samples[1];
            var last = row.Samples[^1];
            var unit = ResourceUnitLabel(a ?? last);
            var heapNatStr = v2Candidate && last is not null
                ? string.Create(CultureInfo.InvariantCulture, $"{last.ManagedHeapMiB:F1}/{last.NativeRssMiB:F1}")
                : "n/a";
            var fdStr = v2Candidate && last is not null
                ? string.Create(CultureInfo.InvariantCulture, $"{last.OpenFdCount}({last.OpenFdDelta:+#;-#;0})")
                : "n/a";
            var flagsStr = row.Flags.Count > 0 ? string.Join(' ', row.Flags) : string.Empty;

            if (hasThree)
            {
                var c = row.Samples[2];
                _ = sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {row.Name} | {unit} | {FormatResourcePrimary(a)} | {FormatResourcePrimary(b)} | {FormatResourcePrimary(c)} | {FormatOptionalPct(row.PrimaryDeltaPcts[0])} | {FormatOptionalPct(row.PrimaryDeltaPcts[1])} | {heapNatStr} | {fdStr} | {flagsStr} |"));
            }
            else
            {
                _ = sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {row.Name} | {unit} | {FormatResourcePrimary(a)} | {FormatResourcePrimary(b)} | {FormatOptionalPct(row.PrimaryDeltaPcts[0])} | {heapNatStr} | {fdStr} | {flagsStr} |"));
            }
        }
    }

    private static void AppendUnmatchedSection(
        StringBuilder sb,
        IReadOnlyList<BenchCompareRow> rows,
        IReadOnlyList<BenchCompareColumn> columns)
    {
        if (rows.Count == 0)
        {
            return;
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("## New / Unmatched");
        _ = sb.AppendLine();

        _ = sb.Append("| Suite | Benchmark |");
        foreach (var col in columns)
        {
            _ = sb.Append(' ').Append(col.Label).Append(" |");
        }

        _ = sb.AppendLine(" Flags |");
        _ = sb.Append("| --- | --- |");
        for (var i = 0; i < columns.Count; i++)
        {
            _ = sb.Append(" ---: |");
        }

        _ = sb.AppendLine(" --- |");

        foreach (var row in rows)
        {
            _ = sb.Append("| ").Append(row.Suite).Append(" | ").Append(row.Name).Append(" |");
            foreach (var sample in row.Samples)
            {
                var cell = sample is null
                    ? "n/a"
                    : row.LowerIsBetter
                        ? string.Create(CultureInfo.InvariantCulture, $"{FormatResourcePrimary(sample)} {ResourceUnitLabel(sample)}")
                        : string.Create(CultureInfo.InvariantCulture, $"{sample.OpsPerSec:F1} ops/s");
                _ = sb.Append(' ').Append(cell).Append(" |");
            }

            var flagsStr = row.Flags.Count > 0 ? string.Join(' ', row.Flags) : string.Empty;
            _ = sb.Append(' ').Append(flagsStr).AppendLine(" |");
        }
    }

    private static void AppendBackupCostSection(
        StringBuilder sb,
        BenchComparisonReport comparison,
        IReadOnlyList<BenchCompareColumn> columns)
    {
        var rows = comparison.CoreThroughput
            .Concat(comparison.Replication)
            .Concat(comparison.ResourceSnapshots)
            .Concat(comparison.NewOrUnmatched)
            .Where(static row => row.Samples.Any(static sample => sample?.BackupCost is not null))
            .ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("## Backup Cost (per op, lower is better)");
        _ = sb.AppendLine();
        _ = sb.Append("| Suite | Benchmark | Metric |");
        foreach (var col in columns)
        {
            _ = sb.Append(' ').Append(col.Label).Append(" |");
        }

        _ = sb.AppendLine(" Delta (final vs A) |");
        _ = sb.Append("| --- | --- | --- |");
        for (var i = 0; i < columns.Count; i++)
        {
            _ = sb.Append(" ---: |");
        }

        _ = sb.AppendLine(" ---: |");

        foreach (var row in rows)
        {
            foreach (var (label, select) in BackupCostFields)
            {
                var values = new double?[row.Samples.Count];
                var anyNonZero = false;
                for (var i = 0; i < row.Samples.Count; i++)
                {
                    if (row.Samples[i]?.BackupCost is { } cost)
                    {
                        var value = select(cost);
                        values[i] = value;
                        anyNonZero |= value != 0;
                    }
                }

                if (!anyNonZero)
                {
                    continue;
                }

                _ = sb.Append("| ").Append(row.Suite).Append(" | ").Append(row.Name).Append(" | ").Append(label).Append(" |");
                foreach (var value in values)
                {
                    _ = sb.Append(' ')
                        .Append(value is { } present ? present.ToString(present % 1 == 0 ? "F0" : "F1", CultureInfo.InvariantCulture) : "n/a")
                        .Append(" |");
                }

                _ = sb.Append(' ').Append(FormatOptionalPct(ComputeMetricDeltaPct(values[0], values[^1]))).AppendLine(" |");
            }
        }
    }

    private static BenchCohortSummary SummarizeCohort(string cohort, bool lowerIsBetter, List<BenchCompareRow> rows)
    {
        var logSum = 0.0;
        var ratioCount = 0;
        var improved = 0;
        var regressed = 0;
        var noisy = 0;
        var fdLeaks = 0;

        foreach (var row in rows)
        {
            if (row.Flags.Contains("[REG]", StringComparer.Ordinal))
            {
                regressed++;
            }
            else if (row.FinalPrimaryDeltaPct is { } delta)
            {
                var isImprovement = lowerIsBetter ? delta <= -2.0 : delta >= 2.0;
                if (isImprovement)
                {
                    improved++;
                }
            }

            if (row.Flags.Contains("[NOISY]", StringComparer.Ordinal))
            {
                noisy++;
            }

            foreach (var flag in row.Flags)
            {
                if (flag.StartsWith("[FD+", StringComparison.Ordinal))
                {
                    fdLeaks++;
                    break;
                }
            }

            var baseVal = PrimaryValue(row.Samples[0], lowerIsBetter);
            var candVal = PrimaryValue(row.Samples[^1], lowerIsBetter);
            if (baseVal > 0 && candVal > 0)
            {
                logSum += Math.Log(candVal / baseVal);
                ratioCount++;
            }
        }

        double? geoRatio = ratioCount > 0 ? Math.Exp(logSum / ratioCount) : null;
        double? geoDelta = geoRatio is { } ratio ? (ratio - 1.0) * 100.0 : null;
        return new BenchCohortSummary(cohort, lowerIsBetter, rows.Count, geoRatio, geoDelta, improved, regressed, noisy, fdLeaks);
    }

    private static List<string> EvaluateFlags(
        bool lowerIsBetter,
        double? primaryDeltaPct,
        double? p99DeltaPct,
        double? allocDeltaPct,
        BenchMetrics? candidate,
        int candidateSchemaVersion)
    {
        List<string> flags = [];
        var regressed = lowerIsBetter
            ? primaryDeltaPct is > ResourceRegressionPct
            : primaryDeltaPct is < ThroughputRegressionPct
                || p99DeltaPct is > LatencyRegressionPct
                || allocDeltaPct is > AllocRegressionPct;
        if (regressed)
        {
            flags.Add("[REG]");
        }

        if (candidate is not null && candidateSchemaVersion >= 2)
        {
            if (candidate.RoundOpsCvPct > NoisyCvPctThreshold)
            {
                flags.Add("[NOISY]");
            }

            if (!lowerIsBetter && candidate.OpenFdDelta > 0)
            {
                flags.Add(string.Create(CultureInfo.InvariantCulture, $"[FD+{candidate.OpenFdDelta}]"));
            }
        }

        return flags;
    }

    private static double? ComputePrimaryDeltaPct(BenchMetrics? baseline, BenchMetrics? candidate, bool lowerIsBetter)
    {
        var baseVal = PrimaryValue(baseline, lowerIsBetter);
        var candVal = PrimaryValue(candidate, lowerIsBetter);
        return baseVal > 0 && candVal > 0 ? ((candVal - baseVal) / baseVal) * 100.0 : null;
    }

    private static double? ComputeMetricDeltaPct(double? baseline, double? candidate) =>
        baseline is > 0 && candidate is >= 0 ? ((candidate.Value - baseline.Value) / baseline.Value) * 100.0 : null;

    private static bool IsRssPerDbResource(BenchMetrics metrics) =>
        string.Equals(metrics.UnitLabel, "kib/db", StringComparison.OrdinalIgnoreCase)
        || metrics.Name.Contains("rss", StringComparison.Ordinal)
        || metrics.Name.Contains("density", StringComparison.Ordinal);

    private static double PrimaryValue(BenchMetrics? metrics, bool lowerIsBetter)
    {
        if (metrics is null)
        {
            return 0;
        }

        if (!lowerIsBetter)
        {
            return metrics.OpsPerSec;
        }

        return IsRssPerDbResource(metrics) ? metrics.MeanUs : metrics.DiskBytes;
    }

    private static string ResourceUnitLabel(BenchMetrics? metrics) =>
        metrics is not null && IsRssPerDbResource(metrics) ? "KiB/DB" : "B";

    private static string FormatResourcePrimary(BenchMetrics? metrics)
    {
        if (metrics is null)
        {
            return "n/a";
        }

        return IsRssPerDbResource(metrics)
            ? metrics.MeanUs.ToString("F1", CultureInfo.InvariantCulture)
            : metrics.DiskBytes.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatOps(BenchMetrics? metrics) =>
        metrics is not null ? metrics.OpsPerSec.ToString("F1", CultureInfo.InvariantCulture) : "n/a";

    private static string FormatOptionalPct(double? pct) =>
        pct is { } value ? FormatSignedPct(value) : "n/a";

    private static string FormatSignedPct(double pct) =>
        string.Create(CultureInfo.InvariantCulture, $"{pct:+0.0;-0.0;0.0}%");

    private static LoadReportOutcome ReadAndDeserializeReport(string fullPath, string displayPath)
    {
        try
        {
            var json = File.ReadAllText(fullPath);
            var report = JsonSerializer.Deserialize(json, BenchJsonContext.Default.BenchReport);
            return report is not null
                ? report
                : new ReportLoadFailure(displayPath, "JSON report deserialized to null");
        }
        catch (IOException error)
        {
            return new ReportLoadFailure(displayPath, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return new ReportLoadFailure(displayPath, error.Message);
        }
        catch (JsonException error)
        {
            return new ReportLoadFailure(displayPath, error.Message);
        }
    }

    private readonly record struct BenchKey(string Suite, string Name);
}
