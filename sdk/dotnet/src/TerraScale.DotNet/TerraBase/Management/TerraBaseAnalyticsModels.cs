namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseBandwidthUsage(long BytesSent, long BytesReceived);

public sealed record TerraBaseLatencyPercentiles(double? P50Ms, double? P95Ms, double? P99Ms);

public sealed record TerraBaseStatementUsage(long Statements, long Errors, TerraBaseLatencyPercentiles Latency);

public sealed record TerraBaseAnalyticsPoint(DateTimeOffset Start, IReadOnlyDictionary<string, long> Requests, IReadOnlyDictionary<string, long> Operations, long Errors, TerraBaseLatencyPercentiles Latency, long? StorageBytes, TerraBaseBandwidthUsage ClientBandwidth, IReadOnlyDictionary<string, TerraBaseStatementUsage> Statements);

public sealed record TerraBaseDatabaseAnalytics(string DatabaseId, string DatabaseName, string Region, IReadOnlyList<TerraBaseAnalyticsPoint> Series);

public sealed record TerraBaseProjectAnalytics(string ProjectId, string Range, DateTimeOffset From, DateTimeOffset To, int BucketSeconds, IReadOnlyList<TerraBaseDatabaseAnalytics> Items);
