using MessagePack;

namespace TerraScale.TerraBase.Contracts.Logs
{
    /// <summary>A completed authorized request, without customer parameters.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRequestLog(string Id, DateTimeOffset Time, string Region, string Method, string Route,
        int Status, double LatencyMs, long Operations, bool IsQuery, string? RequestId = null, long Statements = 0, long StatementErrors = 0);

    /// <summary>At most 100 recent requests; cursor pages are oldest first and preserve gap detection.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRequestLogPage(string DatabaseId, IReadOnlyList<TerraBaseRequestLog> Items,
        bool Truncated, bool RecordingLimited, string? NextCursor = null, bool Gap = false);

    /// <summary>Measurements for one normalized query shape.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseQueryShape(string Id, string Collection, string Shape, long Queries,
        double TotalLatencyMs, double MaxLatencyMs, long RowsScanned, DateTimeOffset LastSeenAt,
        IReadOnlyList<long>? LatencyBuckets = null, double? P95LatencyMs = null);

    /// <summary>The retained query shapes.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseQueryShapePage(IReadOnlyList<TerraBaseQueryShape> Items);
}
