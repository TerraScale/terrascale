using System.Text.Json.Serialization;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Logs;

/// <summary>
/// A completed authorized request: an HTTP request, or a PostgreSQL wire exchange (route <c>postgres</c>, ending at its Sync).
/// Route is a template, never a URL, query text or parameters. Statements and statement errors count the SQL statements it carried.
/// </summary>
public sealed record DatabaseRequestLog(
    [property: JsonRequired] string Id,
    [property: JsonRequired] DateTimeOffset Time,
    [property: JsonRequired] string Region,
    [property: JsonRequired] string Method,
    [property: JsonRequired] string Route,
    [property: JsonRequired] int Status,
    [property: JsonRequired] double LatencyMs,
    [property: JsonRequired] long Operations,
    [property: JsonRequired] bool IsQuery,
    string? RequestId = null,
    long Statements = 0,
    long StatementErrors = 0);

/// <summary>
/// The newest recorded requests, bounded to 100 entries and 24 hours. Recording is best effort. A cursor read lists settled
/// requests after the cursor oldest first, returns the next cursor, and sets <see cref="Gap"/> when requests after the cursor
/// may have left the retained window unread.
/// </summary>
public sealed record DatabaseRequestLogs(string DatabaseId, IReadOnlyList<DatabaseRequestLog> Items, bool Truncated, bool RecordingLimited,
    string? NextCursor = null, bool Gap = false);

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
    void Record(TenantContext database, string method, string route, int status, TimeSpan elapsed, long operations, string? requestId = null,
        long statements = 0, long statementErrors = 0);

    /// <summary>Reads recent observations, with an optional HTTP status class (2 through 5) and an optional cursor (a ULID).</summary>
    ValueTask<RequestLogReadResult> ReadAsync(TenantContext database, int? statusClass, string? after, CancellationToken cancellationToken);

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

    private long _statements;
    private long _statementErrors;

    internal void Statement(TenantContext owner, bool failed)
    {
        lock (_gate)
        {
            _database ??= owner;
            if (Owns(owner))
            {
                _statements = _statements == long.MaxValue ? long.MaxValue : _statements + 1;
                _statementErrors = !failed || _statementErrors == long.MaxValue ? _statementErrors : _statementErrors + 1;
            }
        }
    }

    internal (long Statements, long Errors) Statements(TenantContext owner)
    {
        lock (_gate) { return Owns(owner) ? (_statements, _statementErrors) : (0, 0); }
    }

    private bool Owns(TenantContext owner) =>
        _database?.OrganizationId == owner.OrganizationId && _database.ProjectId == owner.ProjectId && _database.DatabaseId == owner.DatabaseId;

    internal long Count(TenantContext owner)
    {
        lock (_gate) { return _database?.OrganizationId == owner.OrganizationId && _database.ProjectId == owner.ProjectId && _database.DatabaseId == owner.DatabaseId ? _count : 0; }
    }
}
