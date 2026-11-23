using Microsoft.AspNetCore.Mvc;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management.Logs;
using TerraScale.Contracts.Http;
using BoundSqlRequest = TerraScale.Endpoints.Binding.Bound<TerraBase.Features.Sql.SqlExecuteInput>;

namespace TerraBase.Features.Sql;

internal sealed class SqlFlow(DatabaseAccess access, SqlRunner runner)
{
    public const int MaxRequestBytes = 1024 * 1024;

    public static SqlExecuteInput OversizedRequest { get; } = new(null, null, null);

    public static RequestSizeLimitAttribute RequestSizeLimit { get; } = new(MaxRequestBytes);

    public static async ValueTask<BoundSqlRequest> BindAsync(
        HttpRequest request, Func<CancellationToken, ValueTask<BoundSqlRequest>> bind, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxRequestBytes)
        {
            return OversizedRequest;
        }

        try
        {
            return await bind(cancellationToken);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return OversizedRequest;
        }
    }

    public async ValueTask<Result<SqlRun, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> ExecuteAsync(
        HttpContext context, string? project, string? database, IReadOnlyList<SqlHttpStatement>? statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await access.DataAsync(context.Caller, project, database, cancellationToken) switch
        {
            ResolvedDatabase target => await RunAsync(context, target, statements, cancellationToken),
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<SqlRun, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> ExecuteByIdAsync(
        HttpContext context, Ulid databaseId, IReadOnlyList<SqlHttpStatement>? statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await access.DataByIdAsync(context.Caller, databaseId, cancellationToken) switch
        {
            ResolvedDatabase target => await RunAsync(context, target, statements, cancellationToken),
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<SqlRun, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> RunAsync(
        HttpContext context, ResolvedDatabase target, IReadOnlyList<SqlHttpStatement>? statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        RequestLogRecorder.Mark(context, target.Database.Id);
        var outcome = SqlRunner.Causality(context.Request) switch
        {
            SqlCausality causality => SqlWire.Statements(statements) switch
            {
                IReadOnlyList<SqlStatement> converted => await runner.RunAsync(target, converted, causality, context.Features.Get<AdmittedCaller>() is not null, cancellationToken),
                SqlRefusal refusal => refusal,
            },
            SqlRefusal refusal => refusal,
        };
        return outcome switch
        {
            SqlRun run => Written(context, run),
            SqlRefusal refusal => refusal,
        };
    }

    private static Result<SqlRun, SqlRefusal, Validation, Unauthorized, Forbidden, NotFound, Unavailable> Written(HttpContext context, SqlRun run)
    {
        SqlRunner.WriteHeaders(context.Response, run);
        return run;
    }
}
