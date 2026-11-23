using Microsoft.AspNetCore.Mvc;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management.Logs;
using TerraBase.Hosting;
using TerraScale.Contracts.Http;
using BoundSqlRequest = TerraScale.Endpoints.Binding.Bound<TerraBase.Features.Sql.SqlExecuteInput>;

namespace TerraBase.Features.Sql;

internal readonly union SqlRunResolution(SqlRun, SqlRefusal, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

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

    public async ValueTask<SqlRunResolution> ExecuteAsync(
        HttpContext context, string? project, string? database, IReadOnlyList<SqlHttpStatement>? statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcome = await access.DataAsync(context.Caller, project, database, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await RunAsync(context, target, statements, cancellationToken),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<SqlRunResolution> ExecuteByIdAsync(
        HttpContext context, Ulid databaseId, IReadOnlyList<SqlHttpStatement>? statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcome = await access.DataByIdAsync(context.Caller, databaseId, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await RunAsync(context, target, statements, cancellationToken),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<SqlRunResolution> RunAsync(
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

    private static SqlRunResolution Written(HttpContext context, SqlRun run)
    {
        SqlRunner.WriteHeaders(context.Response, run);
        return run;
    }
}
