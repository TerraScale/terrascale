namespace TerraScale.DotNet.TerraBase;

/// <summary>Sent and received bytes of an observing region; neither includes the other direction.</summary>
/// <param name="BytesSent">Bytes Sent.</param>
/// <param name="BytesReceived">Bytes Received.</param>
public sealed record TerraBaseBandwidthUsage(long BytesSent, long BytesReceived);

/// <summary>Replication traffic by source, destination and kind, as observed at the sender and receiver.</summary>
/// <param name="SourceRegion">Source Region.</param>
/// <param name="DestinationRegion">Destination Region.</param>
/// <param name="Kind">Kind.</param>
/// <param name="BytesSent">Bytes Sent.</param>
/// <param name="BytesReceived">Bytes Received.</param>
public sealed record TerraBaseReplicationBandwidth(string SourceRegion, string DestinationRegion, string Kind, long BytesSent, long BytesReceived);

/// <summary>Approximate latency percentiles in milliseconds, merged from fixed upper-bound histogram bins; null without requests.</summary>
/// <param name="P50Ms">P50 Ms.</param>
/// <param name="P95Ms">P95 Ms.</param>
/// <param name="P99Ms">P99 Ms.</param>
public sealed record TerraBaseLatencyPercentiles(double? P50Ms, double? P95Ms, double? P99Ms);

/// <summary>One protocol's SQL statements, apart from the requests that carried them; a Hrana SQL error inside an HTTP 200 is a statement error.</summary>
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
/// <param name="ReplicationBandwidth">Replication bytes sent and received.</param>
/// <param name="Statements">SQL statements by protocol (postgres, hrana).</param>
public sealed record TerraBaseAnalyticsPoint(DateTimeOffset Start, IReadOnlyDictionary<string, long> Requests, IReadOnlyDictionary<string, long> Operations, long Errors, TerraBaseLatencyPercentiles Latency, long? StorageBytes, TerraBaseBandwidthUsage ClientBandwidth, TerraBaseBandwidthUsage ReplicationBandwidth, IReadOnlyDictionary<string, TerraBaseStatementUsage> Statements);

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

/// <summary>One replication period bucket, including bytes by traffic kind and directed region pair.</summary>
/// <param name="Start">Start.</param>
/// <param name="ReplicatedOps">Replicated Ops.</param>
/// <param name="Bandwidth">Bandwidth.</param>
public sealed record TerraBaseReplicationPoint(DateTimeOffset Start, long ReplicatedOps, IReadOnlyList<TerraBaseReplicationBandwidth> Bandwidth);

/// <summary>A database and observing region's replication. Null lag or divergence means no recent observation.</summary>
/// <param name="DatabaseId">Database Id.</param>
/// <param name="DatabaseName">Database Name.</param>
/// <param name="Region">Region.</param>
/// <param name="Status">Status.</param>
/// <param name="ReplicatedOps">Replicated Ops.</param>
/// <param name="LagMs">Lag Ms.</param>
/// <param name="Divergence">Divergence.</param>
/// <param name="ConvergenceMs">Convergence Ms.</param>
/// <param name="LastApplied">Last Applied.</param>
/// <param name="Bandwidth">Bandwidth.</param>
/// <param name="Series">Series.</param>
public sealed record TerraBaseDatabaseReplication(string DatabaseId, string DatabaseName, string Region, string Status, long ReplicatedOps, double? LagMs, bool? Divergence, double? ConvergenceMs, DateTimeOffset? LastApplied, IReadOnlyList<TerraBaseReplicationBandwidth> Bandwidth, IReadOnlyList<TerraBaseReplicationPoint> Series);

/// <summary>Replication usage and the latest observed health per database and region of an authorized project.</summary>
/// <param name="ProjectId">Project Id.</param>
/// <param name="Range">Range.</param>
/// <param name="From">From.</param>
/// <param name="To">To.</param>
/// <param name="BucketSeconds">Bucket Seconds.</param>
/// <param name="Items">Items.</param>
public sealed record TerraBaseProjectReplication(string ProjectId, string Range, DateTimeOffset From, DateTimeOffset To, int BucketSeconds, IReadOnlyList<TerraBaseDatabaseReplication> Items);
