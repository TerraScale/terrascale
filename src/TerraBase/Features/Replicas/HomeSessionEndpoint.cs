using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using TerraBase.Configuration;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

/// <summary>
/// <c>POST /v1/internal/sql/session</c>: one SQL session of a replica region's client, run at this node, the database's home
/// (docs/terrabase/region-replicas.md#writes-and-transactions). Internal listener only, behind the peer credential.
/// </summary>
internal sealed class HomeSessionEndpoint(
    ISqlService sql, ITenantDirectory tenants, IReplicaCatalog catalog, IMemoryBudget memory, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    private static readonly SqlError NotHere = new(SqlErrors.WrongRegion, "this region is not the database's home for that replica region");

    public override void Configure()
    {
        Post(HomeSessions.Path);
        Name("serveHomeSqlSession");
        Summary("Serves a replica region's SQL session at the database's home");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Both bodies are streams of length-prefixed JSON "
            + "frames for the whole session: an open frame naming the database, the replica region and whether the session may write, then execute "
            + "and describe calls, each answered in order. Only a database homed here whose placement names the replica region is served; the "
            + "replica region checks the client's credential before every call. Ending the request body ends the session and rolls back its transaction.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<byte[]>(StatusCodes.Status200OK, "application/octet-stream");
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = null;
        }

        if (HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } requestRate)
        {
            requestRate.MinDataRate = null;
        }

        if (HttpContext.Features.Get<IHttpMinResponseDataRateFeature>() is { } responseRate)
        {
            responseRate.MinDataRate = null;
        }

        HttpContext.Features.Get<QosRequestLease>()?.Lease.Dispose();
        HttpContext.Response.ContentType = "application/octet-stream";
        await HttpContext.Response.StartAsync(cancellationToken).ConfigureAwait(false);
        var input = HttpContext.Request.BodyReader;
        var output = HttpContext.Response.BodyWriter;
        try
        {
            var open = await HomeSessionFrames.ReadAsync(input, TerraBaseJsonContext.Default.HomeSessionOpen, memory, tenant: null, cancellationToken).ConfigureAwait(false);
            if (open is null)
            {
                return Results.Empty;
            }

            switch (await AdmitAsync(open, cancellationToken).ConfigureAwait(false))
            {
                case SqlError refused:
                    await AnswerAsync(output, new HomeSessionAnswer(true, refused), cancellationToken).ConfigureAwait(false);
                    return Results.Empty;
                case TenantContext database:
                    await ServeAsync(database, open.CanWrite, input, output, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException)
        {
            // A broken or cut stream ends the session; disposing it rolled its transaction back.
        }

        return Results.Empty;
    }

    private async ValueTask<object> AdmitAsync(HomeSessionOpen open, CancellationToken cancellationToken)
    {
        if (!EntityIds.IsValid(open.Organization) || !EntityIds.IsValid(open.Project) || !EntityIds.IsValid(open.Database)
            || !TerraBaseOptions.IsRegion(open.Region) || open.Region == options.Region)
        {
            return NotHere;
        }

        if (await tenants.FindDatabaseAsync(open.Organization, open.Project, open.Database, cancellationToken).ConfigureAwait(false) is not TenantContext database)
        {
            return NotHere;
        }

        return await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) switch
        {
            TenantDatabaseRecord { Placement: var placement } when placement.Home == options.Region && placement.Replicas.Contains(open.Region)
                && open.Epoch <= placement.Version => database,
            Errors.Unavailable unavailable => new SqlError(SqlErrors.Unavailable, unavailable.Message),
            _ => NotHere,
        };
    }

    private async Task ServeAsync(TenantContext database, bool canWrite, System.IO.Pipelines.PipeReader input, System.IO.Pipelines.PipeWriter output,
        CancellationToken cancellationToken)
    {
        var session = sql.Open(database, canWrite);
        await using (session.ConfigureAwait(false))
        {
            await AnswerAsync(output, new HomeSessionAnswer(true), cancellationToken).ConfigureAwait(false);
            while (await HomeSessionFrames.ReadAsync(input, TerraBaseJsonContext.Default.HomeSessionCall, memory, database, cancellationToken).ConfigureAwait(false) is { } call)
            {
                HomeSessionAnswer answer;
                using (QosContext.Enter(database, memory))
                {
                    session.BeginRequest();
                    try
                    {
                        var command = new SqlCommand(call.Sql, call.Args ?? [], call.Named ?? new Dictionary<string, SqlValue>());
                        answer = call.Op switch
                        {
                            "execute" => await session.ExecuteAsync(command, cancellationToken).ConfigureAwait(false) switch
                            {
                                SqlResult result => Answered(session, result),
                                SqlError error => new HomeSessionAnswer(session.IsAutocommit, error),
                            },
                            "describe" => await session.DescribeAsync(command, cancellationToken).ConfigureAwait(false) switch
                            {
                                SqlDescription description => new HomeSessionAnswer(session.IsAutocommit, Description: description),
                                SqlError error => new HomeSessionAnswer(session.IsAutocommit, error),
                            },
                            "batch" => await BatchAsync(session, call.Batch, cancellationToken).ConfigureAwait(false),
                            _ => new HomeSessionAnswer(session.IsAutocommit, new SqlError(SqlErrors.InvalidRequest, "op is execute, describe or batch")),
                        };
                    }
                    catch (QosMemoryException)
                    {
                        answer = new HomeSessionAnswer(session.IsAutocommit, new SqlError(SqlErrors.TenantBusy, "organization memory budget exceeded"));
                    }
                    finally
                    {
                        await session.EndRequestAsync().ConfigureAwait(false);
                    }
                }

                await AnswerAsync(output, answer, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<HomeSessionAnswer> BatchAsync(ISqlSession session, IReadOnlyList<HomeSessionCall>? batch, CancellationToken cancellationToken)
    {
        if (batch is not { Count: > 0 and <= SqlBatch.MaxStatements })
        {
            return new HomeSessionAnswer(session.IsAutocommit, SqlBatch.TooManyStatements);
        }

        var commands = batch.Select(call => new SqlCommand(call.Sql, call.Args ?? [], call.Named ?? new Dictionary<string, SqlValue>())).ToList();
        var control = commands.FindIndex(command => SqlScript.Classify(command.Sql) is SqlStatementPlan { IsControl: true });
        if (control >= 0)
        {
            return new HomeSessionAnswer(session.IsAutocommit,
                new SqlError(SqlErrors.TransactionState, "BEGIN, COMMIT and ROLLBACK are not statements here: a batch runs as one transaction"), Statement: control);
        }

        return await SqlBatch.RunAsync(session, commands, cancellationToken).ConfigureAwait(false) switch
        {
            SqlExecuteBody body => new HomeSessionAnswer(session.IsAutocommit, Batch: [.. body.Results.Select(result => new HomeSessionResult(
                [.. result.Columns.Select(column => new SqlColumn(column.Name, column.Decltype))], result.Rows, result.RowsAffected, result.LastInsertRowid))]),
            SqlBatchFailure failure => new HomeSessionAnswer(session.IsAutocommit, failure.Error, Statement: failure.Statement),
        };
    }

    // The home charges a read when it answers it: the replica region relays every answer it receives.
    private static HomeSessionAnswer Answered(ISqlSession session, SqlResult result)
    {
        result.ReadCharge?.Accept();
        return new HomeSessionAnswer(session.IsAutocommit, Result: new HomeSessionResult(result.Columns, result.Rows, result.RowsAffected, result.LastInsertRowid));
    }

    private static ValueTask AnswerAsync(System.IO.Pipelines.PipeWriter output, HomeSessionAnswer answer, CancellationToken cancellationToken) =>
        HomeSessionFrames.WriteAsync(output, answer, TerraBaseJsonContext.Default.HomeSessionAnswer, cancellationToken);
}
