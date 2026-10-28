using System.Globalization;
using System.Text.Json;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Hrana;

/// <summary>A statement ready to run, or why it cannot.</summary>
internal readonly union HranaCommand(SqlCommand, HranaError);

/// <summary>Runs one HTTP request's stream requests against its stream, bounding the response it builds.</summary>
/// <param name="stream">The request's stream.</param>
/// <param name="memory">Admits stored statements.</param>
/// <param name="time">The request deadline clock.</param>
/// <param name="usage">Records each statement's outcome; Hrana answers SQL errors inside an HTTP 200.</param>
/// <param name="statements">What <see cref="Statements"/> counted for the request; a sequence may run the rest of the allowance.</param>
internal sealed class HranaExecutor(HranaStream stream, IMemoryBudget memory, int statements, TimeProvider time, IUsageMeter usage)
{
    /// <summary>
    /// SQL statements (execute requests and batch steps) per HTTP request, the core's per-request limit; the core bounds a
    /// sequence's statements itself.
    /// </summary>
    public const int MaxStatements = 256;

    /// <summary>Estimated bytes of results per HTTP request.</summary>
    public const long MaxResponseBytes = 16 * 1024 * 1024;

    private static readonly HranaError Protocol = new("the request is not a valid Hrana request", "PROTOCOL_ERROR");

    private readonly long _started = time.GetTimestamp();

    private long _responseBytes;

    private bool Expired => time.GetElapsedTime(_started) > SqlLimits.MaxRequestTime;

    // Statements a sequence may run beyond its own count; the first sequence of a request takes all of them.
    private int _spare = Math.Max(0, MaxStatements - statements);

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
            case "batch" when request.Batch is { } batch:
                return await BatchAsync(batch, cancellationToken).ConfigureAwait(false) is { } steps
                    ? HranaResult.Ok(new HranaResponse("batch", steps))
                    : HranaResult.Failed(Protocol);
            case "sequence":
                return await SequenceAsync(request, cancellationToken).ConfigureAwait(false) is { } failure
                    ? HranaResult.Failed(failure)
                    : HranaResult.Ok(new HranaResponse("sequence"));
            case "describe" when Expired:
                return HranaResult.Failed(HranaError.From(SqlErrors.Interrupt));
            case "describe":
                return Command(new HranaStmt(request.Sql, request.SqlId), bind: false) switch
                {
                    HranaError error => HranaResult.Failed(error),
                    SqlCommand command => await stream.Session.DescribeAsync(command, cancellationToken).ConfigureAwait(false) switch
                    {
                        SqlError error => HranaResult.Failed(HranaError.From(error)),
                        SqlDescription description => HranaResult.Ok(new HranaResponse("describe", new HranaDescribeResult(
                            [.. description.Params.Select(name => new HranaParam(name))],
                            [.. description.Columns.Select(column => new HranaCol(column.Name, column.DeclType))],
                            description.IsExplain, description.IsReadOnly))),
                    },
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

    /// <summary>
    /// SQL statements a pipeline runs: one per execute, describe and sequence, one per batch step; other requests run none.
    /// </summary>
    /// <remarks>A sequence counts once here; it runs at most the allowance the other statements leave (see <c>SequenceAsync</c>).</remarks>
    public static int Statements(IReadOnlyList<HranaRequest> requests) =>
        requests.Sum(request => request?.Type is "execute" or "describe" or "sequence" ? 1 : request?.Batch?.Steps?.Count ?? 0);

    /// <summary>Runs a batch; null when its steps or conditions are malformed (nothing ran).</summary>
    public async ValueTask<HranaBatchResult?> BatchAsync(HranaBatch batch, CancellationToken cancellationToken)
    {
        if (!IsValid(batch))
        {
            return null;
        }

        var results = new HranaStmtResult?[batch.Steps.Count];
        var errors = new HranaError?[batch.Steps.Count];
        for (var step = 0; step < batch.Steps.Count; step++)
        {
            if (batch.Steps[step].Condition is { } condition && !Holds(condition, results, errors))
            {
                continue;
            }

            switch (await ExecuteAsync(batch.Steps[step].Stmt, cancellationToken).ConfigureAwait(false))
            {
                case HranaStmtResult result:
                    results[step] = result;
                    break;
                case HranaError error:
                    errors[step] = error;
                    break;
            }
        }

        return new HranaBatchResult(results, errors);
    }

    /// <summary>Whether every step has a statement and every condition names only earlier steps.</summary>
    public static bool IsValid(HranaBatch batch) =>
        batch.Steps is not null && batch.Steps.Select((step, index) => step?.Stmt is not null && (step.Condition is null || IsValid(step.Condition, index))).All(valid => valid);

    private static bool IsValid(HranaCondition condition, int step) => condition.Type switch
    {
        "ok" or "error" => condition.Step is { } target && target >= 0 && target < step,
        "not" => condition.Cond is { } inner && IsValid(inner, step),
        "and" or "or" => condition.Conds is { } all && all.All(inner => inner is not null && IsValid(inner, step)),
        "is_autocommit" => true,
        _ => false,
    };

    /// <summary>Evaluates a validated condition against the steps run so far.</summary>
    public bool Holds(HranaCondition condition, IReadOnlyList<HranaStmtResult?> results, IReadOnlyList<HranaError?> errors) => condition.Type switch
    {
        "ok" => results[condition.Step!.Value] is not null,
        "error" => errors[condition.Step!.Value] is not null,
        "not" => !Holds(condition.Cond!, results, errors),
        "and" => condition.Conds!.All(inner => Holds(inner, results, errors)),
        "or" => condition.Conds!.Any(inner => Holds(inner, results, errors)),
        _ => stream.Session.IsAutocommit,
    };

    /// <summary>Runs one statement into a wire result, refusing results past the response bound.</summary>
    public async ValueTask<HranaStmtOutcome> ExecuteAsync(HranaStmt stmt, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var outcome = await RunStatementAsync(stmt, cancellationToken).ConfigureAwait(false);
        usage.RecordStatement(stream.Database, "hrana", outcome is HranaError, time.GetElapsedTime(started));
        return outcome;
    }

    private async ValueTask<HranaStmtOutcome> RunStatementAsync(HranaStmt stmt, CancellationToken cancellationToken)
    {
        if (Expired)
        {
            return HranaError.From(SqlErrors.Interrupt);
        }

        // Past the bound nothing runs: a statement that ran and then reported an error would invite a duplicate retry.
        if (_responseBytes > MaxResponseBytes)
        {
            return TooBig;
        }

        return Command(stmt) switch
        {
            HranaError error => error,
            SqlCommand command => await stream.Session.ExecuteAsync(command, result => Admit(result, stmt.WantRows ?? true), cancellationToken).ConfigureAwait(false) switch
            {
                SqlError error => HranaError.From(error),
                SqlResult result => Accept(result, stmt.WantRows ?? true),
            },
        };
    }

    private static readonly HranaError TooBig = new($"the response exceeds {MaxResponseBytes / 1024 / 1024} MiB", "SQLITE_TOOBIG");

    private static readonly SqlError TooBigSql = SqlErrors.TooBig(TooBig.Message);

    // Asked before the statement takes effect: the result that crosses the bound is refused whole, so its writes never
    // commit and a retry cannot duplicate them; a success always carries the rows it asked for.
    private SqlError? Admit(SqlResult result, bool wantRows)
    {
        _responseBytes += 64 + result.Columns.Sum(column => 32L + column.Name.Length + (column.DeclType?.Length ?? 0))
            + (wantRows ? result.Rows.Sum(row => row.Sum(value => 32L + (value.Text?.Length ?? 0) * 3L)) : 0);
        return _responseBytes <= MaxResponseBytes ? null : TooBigSql;
    }

    private static HranaStmtOutcome Accept(SqlResult result, bool wantRows)
    {
        result.ReadCharge?.Accept();
        return HranaStmtResult.From(result, wantRows);
    }

    // A sequence is recorded as one statement: the core reports neither how many of its statements ran nor which one failed.
    private async ValueTask<HranaError?> SequenceAsync(HranaRequest request, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var failure = await RunSequenceAsync(request, cancellationToken).ConfigureAwait(false);
        usage.RecordStatement(stream.Database, "hrana", failure is not null, time.GetElapsedTime(started));
        return failure;
    }

    // The core splits the text and runs its statements in order, stopping at the first failure.
    private async ValueTask<HranaError?> RunSequenceAsync(HranaRequest request, CancellationToken cancellationToken)
    {
        if (Expired)
        {
            return HranaError.From(SqlErrors.Interrupt);
        }

        var text = request switch
        {
            { Sql: { } sql, SqlId: null } => sql,
            { Sql: null, SqlId: { } id } => stream.Sql(id),
            _ => null,
        };
        if (text is null)
        {
            return request.SqlId is null ? Protocol : UnknownSql;
        }

        if (_responseBytes > MaxResponseBytes)
        {
            return TooBig;
        }

        var allowance = _spare + 1;
        _spare = 0;
        return await stream.Session.ExecuteScriptAsync(text, allowance, cancellationToken).ConfigureAwait(false) switch
        {
            SqlError error => HranaError.From(error),
            SqlResult => null,
        };
    }

    private static readonly HranaError UnknownSql = new("sql_id does not name a stored statement", "SQL_NOT_FOUND");

    private HranaCommand Command(HranaStmt stmt, bool bind = true)
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

            if (!named.TryAdd(name, value))
            {
                return new HranaError("named arguments must have unique names", "ARGS_INVALID");
            }
        }

        if (bind)
        {
            switch (SqlParse.Parse(sql))
            {
                case SqlError error:
                    return HranaError.From(error);
                case ParsedSql parsed:
                    // SQLite numbers through the highest slot, including ?NNN gaps. Repeated names share a slot.
                    // Keep this stricter Hrana contract out of the HTTP SQL core, where unbound values are NULL.
                    var expected = new HashSet<string>(StringComparer.Ordinal);
                    var bindings = new Dictionary<string, SqlValue>(StringComparer.Ordinal);
                    for (var i = 0; i < parsed.Params.Count; i++)
                    {
                        var supplied = i < args.Length;
                        if (parsed.Params[i] is [':' or '@' or '$', ..] name)
                        {
                            expected.Add(name);
                            expected.Add(name[1..]);
                            if (named.TryGetValue(name, out var bound) || named.TryGetValue(name[1..], out bound))
                            {
                                bindings.Add(name, bound);
                                supplied = true;
                            }
                        }

                        if (!supplied)
                        {
                            return new HranaError("a value is required for every parameter slot", "ARGS_INVALID");
                        }
                    }

                    if (args.Length > parsed.Params.Count || named.Keys.Any(name => !expected.Contains(name)))
                    {
                        return new HranaError("an argument does not match a statement parameter", "ARGS_INVALID");
                    }

                    named = bindings;
                    break;
            }
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
