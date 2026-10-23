using System.Text.Json.Serialization;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Logs;

/// <summary>A completed authorized request. Route is a template, never a URL or query text.</summary>
public sealed record DatabaseRequestLog(
    [property: JsonRequired] string Id,
    [property: JsonRequired] DateTimeOffset Time,
    [property: JsonRequired] string Region,
    [property: JsonRequired] string Method,
    [property: JsonRequired] string Route,
    [property: JsonRequired] int Status,
    [property: JsonRequired] double LatencyMs,
    [property: JsonRequired] long Operations,
    [property: JsonRequired] bool IsQuery);

/// <summary>The newest recorded requests, bounded to 100 entries and 24 hours. Recording is best effort.</summary>
public sealed record DatabaseRequestLogs(string DatabaseId, IReadOnlyList<DatabaseRequestLog> Items, bool Truncated, bool RecordingLimited);

/// <summary>A response whose memory admission lasts through serialization.</summary>
public sealed class RequestLogRead(DatabaseRequestLogs body, IDisposable reservation) : IResult, IDisposable
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        try { await TypedResults.Ok(body).ExecuteAsync(httpContext).ConfigureAwait(false); }
        finally { Dispose(); }
    }

    /// <inheritdoc />
    public void Dispose() => reservation.Dispose();
}

/// <summary>Recorded requests, or an unavailable catalog or memory budget.</summary>
public readonly union RequestLogReadResult(RequestLogRead, Unavailable);

/// <summary>Bounded, persistent, tenant-scoped request observations.</summary>
public interface IRequestLogs
{
    /// <summary>Records only route templates and counters after database authorization.</summary>
    void Record(TenantContext database, string method, string route, int status, TimeSpan elapsed, long operations);

    /// <summary>Reads recent observations, with an optional HTTP status class (2 through 5).</summary>
    ValueTask<RequestLogReadResult> ReadAsync(TenantContext database, int? statusClass, CancellationToken cancellationToken);

    /// <summary>Persists pending observations; retrying the same ULIDs is idempotent.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken);
}

// The stored envelope is versioned independently of the public response.
internal sealed record RequestLogRecord([property: JsonRequired] string Format, [property: JsonRequired] DatabaseRequestLog Request);

// AsyncLocal holds one request's mutable counter across async calls, as ProjectAccess does for authorization.
internal sealed class RequestLogOperations
{
    internal static readonly AsyncLocal<RequestLogOperations?> Current = new();
    private long _count;
    private TenantContext? _database;
    private readonly Lock _gate = new();

    internal void Add(TenantContext owner, int count)
    {
        if (count <= 0) { return; }
        lock (_gate)
        {
            _database ??= owner;
            if (_database.OrganizationId == owner.OrganizationId && _database.ProjectId == owner.ProjectId && _database.DatabaseId == owner.DatabaseId) { _count = count > long.MaxValue - _count ? long.MaxValue : _count + count; }
        }
    }

    internal long Count(TenantContext owner)
    {
        lock (_gate) { return _database?.OrganizationId == owner.OrganizationId && _database.ProjectId == owner.ProjectId && _database.DatabaseId == owner.DatabaseId ? _count : 0; }
    }
}
