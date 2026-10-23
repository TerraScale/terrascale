using System.Globalization;
using System.Text.Json;
using TerraBase.Features.Memory;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Hrana;

/// <summary>A statement ready to run, or why it cannot.</summary>
internal readonly union HranaCommand(SqlCommand, HranaError);

/// <summary>Runs one HTTP request's stream requests against its stream, bounding the response it builds.</summary>
internal sealed class HranaExecutor(HranaStream stream, IMemoryBudget memory)
{
    /// <summary>Statements (requests, batch steps and sequence statements) per HTTP request.</summary>
    public const int MaxStatements = 1000;

    /// <summary>Estimated bytes of results per HTTP request.</summary>
    public const long MaxResponseBytes = 16 * 1024 * 1024;

    private static readonly HranaError Protocol = new("the request is not a valid Hrana request", "PROTOCOL_ERROR");

    private long _responseBytes;

    /// <summary>Runs one pipeline request; exactly one result whose response type matches the request type.</summary>
    public async ValueTask<HranaResult> RunAsync(HranaRequest? request, CancellationToken cancellationToken)
    {
        switch (request?.Type)
        {
            case "execute" when request.Stmt is { } stmt:
                return await ExecuteAsync(stmt, cancellationToken).ConfigureAwait(false) switch
                {
                    HranaStmtResult result => HranaResult.Ok(new HranaResponse("execute", result)),
                    HranaError error => HranaResult.Failed(error),
                };
            case "store_sql" when request is { SqlId: { } id, Sql: { } sql }:
                return stream.Store(id, sql, memory) is { } refused ? HranaResult.Failed(refused) : HranaResult.Ok(new HranaResponse("store_sql"));
            case "close_sql" when request.SqlId is { } id:
                stream.Forget(id);
                return HranaResult.Ok(new HranaResponse("close_sql"));
            case "close":
                return HranaResult.Ok(new HranaResponse("close"));
            case "get_autocommit":
                return HranaResult.Ok(new HranaResponse("get_autocommit", IsAutocommit: stream.Session.IsAutocommit));
            default:
                return HranaResult.Failed(Protocol);
        }
    }

    /// <summary>Statements one request runs.</summary>
    public static int Statements(IReadOnlyList<HranaRequest> requests) => requests.Count;

    /// <summary>Runs one statement into a wire result, refusing results past the response bound.</summary>
    public async ValueTask<HranaStmtOutcome> ExecuteAsync(HranaStmt stmt, CancellationToken cancellationToken) => Command(stmt) switch
    {
        HranaError error => error,
        SqlCommand command => await stream.Session.ExecuteAsync(command, cancellationToken).ConfigureAwait(false) switch
        {
            SqlError error => HranaError.From(error),
            SqlResult result => Bound(result, stmt.WantRows ?? true),
        },
    };

    private HranaStmtOutcome Bound(SqlResult result, bool wantRows)
    {
        _responseBytes += 64 + result.Columns.Sum(column => 32L + column.Name.Length + (column.DeclType?.Length ?? 0))
            + (wantRows ? result.Rows.Sum(row => row.Sum(value => 32L + (value.Text?.Length ?? 0) * 3L)) : 0);
        return _responseBytes > MaxResponseBytes
            ? new HranaError($"the response exceeds {MaxResponseBytes / 1024 / 1024} MiB", "SQLITE_TOOBIG")
            : HranaStmtResult.From(result, wantRows);
    }

    private static readonly HranaError UnknownSql = new("sql_id does not name a stored statement", "SQL_NOT_FOUND");

    private HranaCommand Command(HranaStmt stmt)
    {
        var sql = stmt switch
        {
            { Sql: { } text, SqlId: null } => text,
            { Sql: null, SqlId: { } id } => stream.Sql(id),
            _ => null,
        };
        if (sql is null)
        {
            return stmt.SqlId is null || stmt.Sql is not null ? Protocol : UnknownSql;
        }

        var args = new SqlValue[stmt.Args?.Count ?? 0];
        for (var i = 0; i < args.Length; i++)
        {
            if (Value(stmt.Args![i]) is not { } value)
            {
                return Refused(stmt.Args[i]);
            }

            args[i] = value;
        }

        var named = new Dictionary<string, SqlValue>(StringComparer.Ordinal);
        foreach (var argument in stmt.NamedArgs ?? [])
        {
            if (argument?.Name is not { Length: > 0 } name || argument.Value is null || Value(argument.Value) is not { } value)
            {
                return argument?.Value is { } refused ? Refused(refused) : Protocol;
            }

            named[name[0] is ':' or '@' or '$' ? name[1..] : name] = value;
        }

        return new SqlCommand(sql, args, named);
    }

    private static HranaError Refused(HranaValue? value) => value?.Type == "blob"
        ? new HranaError("blob values are not supported", "SQLITE_ERROR")
        : new HranaError("an argument is not a valid Hrana value", "PROTOCOL_ERROR");

    // Integers arrive as decimal strings; floats stay reals (SQLite affinity converts them for INTEGER columns).
    private static SqlValue? Value(HranaValue? value) => value switch
    {
        { Type: "null" } => SqlValue.Null,
        { Type: "integer", Value: { ValueKind: JsonValueKind.String } text }
            when long.TryParse(text.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) => SqlValue.Of(integer),
        { Type: "integer", Value: { ValueKind: JsonValueKind.Number } number } when number.TryGetInt64(out var integer) => SqlValue.Of(integer),
        { Type: "float", Value: { ValueKind: JsonValueKind.Number } number } => SqlValue.Of(number.GetDouble()),
        { Type: "text", Value: { ValueKind: JsonValueKind.String } text } => SqlValue.Of(text.GetString()!),
        _ => null,
    };
}

/// <summary>A statement's wire result, or its error.</summary>
internal readonly union HranaStmtOutcome(HranaStmtResult, HranaError);
