using System.Text.Json.Serialization;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Logs;

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

public sealed record DatabaseRequestLogs(string DatabaseId, IReadOnlyList<DatabaseRequestLog> Items, bool Truncated, bool RecordingLimited,
    string? NextCursor = null, bool Gap = false);

public sealed class RequestLogRead(DatabaseRequestLogs body, IDisposable reservation) : IResult, IDisposable
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        try { await TypedResults.Ok(body).ExecuteAsync(httpContext).ConfigureAwait(false); }
        finally { Dispose(); }
    }

    public void Dispose() => reservation.Dispose();
}

public readonly union RequestLogReadResult(RequestLogRead, Unavailable);

public interface IRequestLogs
{
    void Record(TenantContext database, string method, string route, int status, TimeSpan elapsed, long operations, string? requestId = null,
        long statements = 0, long statementErrors = 0);

    ValueTask<RequestLogReadResult> ReadAsync(TenantContext database, int? statusClass, string? after, CancellationToken cancellationToken);

    ValueTask FlushAsync(CancellationToken cancellationToken);
}

internal sealed record RequestLogRecord([property: JsonRequired] string Format, [property: JsonRequired] DatabaseRequestLog Request);

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
