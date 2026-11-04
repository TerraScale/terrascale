namespace TerraScale.DotNet.TerraBase;

/// <summary>Sent and received bytes of an observing region; neither includes the other direction.</summary>
/// <param name="BytesSent">Bytes Sent.</param>
/// <param name="BytesReceived">Bytes Received.</param>
public sealed record TerraBaseBandwidthUsage(long BytesSent, long BytesReceived);

/// <summary>Approximate latency percentiles in milliseconds, merged from fixed upper-bound histogram bins; null without requests.</summary>
/// <param name="P50Ms">P50 Ms.</param>
/// <param name="P95Ms">P95 Ms.</param>
/// <param name="P99Ms">P99 Ms.</param>
public sealed record TerraBaseLatencyPercentiles(double? P50Ms, double? P95Ms, double? P99Ms);

/// <summary>One protocol's SQL statements, apart from the requests that carried them; a SQL error inside an HTTP 200 is a statement error.</summary>
/// <param name="Statements">Statements run or refused.</param>
/// <param name="Errors">Failed statements.</param>
/// <param name="Latency">Statement latency percentiles.</param>
public sealed record TerraBaseStatementUsage(long Statements, long Errors, TerraBaseLatencyPercentiles Latency);

/// <summary>One UTC analytics bucket. Storage is the average of sampled on-disk bytes, or null before sampling.</summary>
/// <param name="Start">Start.</param>
/// <param name="Requests">Requests.</param>
/// <param name="Operations">Operations.</param>
/// <param name="Errors">Errors.</param>
/// <param name="Latency">Latency.</param>
/// <param name="StorageBytes">Storage Bytes.</param>
/// <param name="ClientBandwidth">Client Bandwidth.</param>
/// <param name="Statements">SQL statements by protocol.</param>
public sealed record TerraBaseAnalyticsPoint(DateTimeOffset Start, IReadOnlyDictionary<string, long> Requests, IReadOnlyDictionary<string, long> Operations, long Errors, TerraBaseLatencyPercentiles Latency, long? StorageBytes, TerraBaseBandwidthUsage ClientBandwidth, IReadOnlyDictionary<string, TerraBaseStatementUsage> Statements);

/// <summary>A database and observing region's analytics, oldest bucket first.</summary>
/// <param name="DatabaseId">Database Id.</param>
/// <param name="DatabaseName">Database Name.</param>
/// <param name="Region">Region.</param>
/// <param name="Series">Series.</param>
public sealed record TerraBaseDatabaseAnalytics(string DatabaseId, string DatabaseName, string Region, IReadOnlyList<TerraBaseAnalyticsPoint> Series);

/// <summary>Project analytics, with automatic UTC buckets and optional database and region filters.</summary>
/// <param name="ProjectId">Project Id.</param>
/// <param name="Range">Range.</param>
/// <param name="From">From.</param>
/// <param name="To">To.</param>
/// <param name="BucketSeconds">Bucket Seconds.</param>
/// <param name="Items">Items.</param>
public sealed record TerraBaseProjectAnalytics(string ProjectId, string Range, DateTimeOffset From, DateTimeOffset To, int BucketSeconds, IReadOnlyList<TerraBaseDatabaseAnalytics> Items);
