using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using System.IO.Pipelines;
using TerraBase.Configuration;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class HomeSessionEndpoint(
    ISqlService sql, ITenantDirectory tenants, IReplicaCatalog catalog, IMemoryBudget memory, TerraBaseOptions options, HomePush.ILocalWrites local, QosSessions sessions,
    TimeProvider time)
    : EndpointWithoutRequest<IResult>
{
    private static readonly SqlError NotHere = new(SqlErrors.WrongRegion, "this region is not the database's home for that replica region");
    private static readonly SqlError Unsettled = new(SqlErrors.Unavailable, "the region's earlier writes have not reached the home yet; retry");
    private static readonly SqlError Idle = new(SqlErrors.Unavailable, "the session to the database's home timed out waiting for a call; retry");

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

        HttpContext.ReleaseRequestLease();
        HttpContext.Response.ContentType = "application/octet-stream";
        await HttpContext.Response.StartAsync(cancellationToken).ConfigureAwait(false);
        var input = HttpContext.Request.BodyReader;
        var output = HttpContext.Response.BodyWriter;
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            opening.CancelAfter(QosSessions.MessageTimeout);
            var open = await HomeSessionFrames.ReadAsync(input, ReplicasJsonContext.Default.HomeSessionOpen, memory, tenant: null, deadline: null, opening.Token).ConfigureAwait(false);
            if (open is null)
            {
                return Results.Empty;
            }

            switch (await AdmitAsync(open, cancellationToken).ConfigureAwait(false))
            {
                case SqlError refused:
                    await AnswerAsync(output, new HomeSessionAnswer(true, refused), cancellationToken).ConfigureAwait(false);
                    return Results.Empty;
                case TenantContext database when open.PushThrough is { } through && !await local.AppliedAsync(open.Region, database, through, cancellationToken).ConfigureAwait(false):
                    await AnswerAsync(output, new HomeSessionAnswer(true, Unsettled), cancellationToken).ConfigureAwait(false);
                    return Results.Empty;
                case TenantContext database:
                    await ServeAsync(database, open.Region, open.CanWrite, input, output, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException)
        {
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
            TenantDatabaseRecord { Placement: var placement } when placement.Home == options.Region && open.Region != options.Region && placement.Includes(open.Region)
                && open.Epoch == placement.Version => database,
            Errors.Unavailable unavailable => new SqlError(SqlErrors.Unavailable, unavailable.Message),
            _ => NotHere,
        };
    }

    private async Task ServeAsync(TenantContext database, string origin, bool canWrite, PipeReader input, PipeWriter output,
        CancellationToken cancellationToken)
    {
        using var lease = sessions.TryEnter(database);
        if (lease is null)
        {
            await AnswerAsync(output, new HomeSessionAnswer(true, new SqlError(SqlErrors.TenantBusy, "organization session limit exceeded")), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var session = sql.Open(database, canWrite);
        await using (session.ConfigureAwait(false))
        {
            await AnswerAsync(output, new HomeSessionAnswer(true), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var transaction = session.TransactionRemaining;
                HomeSessionCall? call;
                using (var deadline = new SessionMessageDeadline(time, transaction))
                using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
                {
                    try
                    {
                        call = await HomeSessionFrames.ReadAsync(input, ReplicasJsonContext.Default.HomeSessionCall, memory, database, deadline, waiting.Token).ConfigureAwait(false);
                        deadline.Complete();
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        using var drain = new CancellationTokenSource(Shutdown.DrainBudget);
                        await AnswerAsync(output, new HomeSessionAnswer(true, transaction is not null && !deadline.IsPartial ? SqlErrors.TransactionExpired : Idle), drain.Token).ConfigureAwait(false);
                        return;
                    }
                }

                if (call is null)
                {
                    return;
                }

                if (call.Op == "execute" && SqlScript.Classify(call.Sql) is SqlStatementPlan { Kind: SqlStatementKind.Begin })
                {
                    session.TransactionClock.Start();
                }

                if (call.Remaining is { } remaining)
                {
                    session.TransactionClock.Limit(remaining);
                }

                HomeSessionAnswer answer;
                if (call.PushThrough is { } through && !await local.AppliedAsync(origin, database, through, cancellationToken).ConfigureAwait(false))
                {
                    await AnswerAsync(output, new HomeSessionAnswer(session.IsAutocommit, Unsettled), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using (QosContext.Enter(database, memory))
                {
                    session.BeginRequest();
                    try
                    {
                        var command = new SqlCommand(call.Sql, call.Args ?? [], call.Named ?? new Dictionary<string, SqlValue>());
                        answer = call.Op switch
                        {
                            "execute" => SqlScript.Classify(call.Sql) switch
                            {
                                SqlError error => new HomeSessionAnswer(session.IsAutocommit, error),
                                SqlStatementPlan plan => await session.ExecuteAsync(new SqlPlannedCommand(plan, command), cancellationToken).ConfigureAwait(false) switch
                                {
                                    SqlResult result => Answered(session, result),
                                    SqlError error => new HomeSessionAnswer(session.IsAutocommit, error),
                                },
                            },
                            "describe" => SqlScript.Classify(call.Sql) switch
                            {
                                SqlError error => new HomeSessionAnswer(session.IsAutocommit, error),
                                SqlStatementPlan plan => await session.DescribeAsync(plan, command, cancellationToken).ConfigureAwait(false) switch
                                {
                                    SqlDescription description => new HomeSessionAnswer(session.IsAutocommit, Description: description),
                                    SqlError error => new HomeSessionAnswer(session.IsAutocommit, error),
                                },
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

        var commands = new List<SqlPlannedCommand>(batch.Count);
        SqlError? refused = null;
        var refusal = -1;
        var control = -1;
        for (var i = 0; i < batch.Count; i++)
        {
            switch (SqlScript.Classify(batch[i].Sql))
            {
                case SqlStatementPlan { IsControl: true } when control < 0:
                    control = i;
                    break;
                case SqlError refusedError when refused is null:
                    (refused, refusal) = (refusedError, i);
                    break;
                case SqlStatementPlan plan:
                    commands.Add(new(plan, new SqlCommand(batch[i].Sql ?? "", batch[i].Args ?? [], batch[i].Named ?? new Dictionary<string, SqlValue>())));
                    break;
            }
        }

        if (control >= 0)
        {
            return new HomeSessionAnswer(session.IsAutocommit,
                new SqlError(SqlErrors.TransactionState, "BEGIN, COMMIT and ROLLBACK are not statements here: a batch runs as one transaction"), Statement: control);
        }

        if (refused is not null)
        {
            return new HomeSessionAnswer(session.IsAutocommit, refused, Statement: refusal);
        }

        return await SqlBatch.RunAsync(session, commands, cancellationToken).ConfigureAwait(false) switch
        {
            SqlExecuteBody body => new HomeSessionAnswer(session.IsAutocommit, Batch: [.. body.Results.Select(result => new HomeSessionResult(
                [.. result.Columns.Select(column => new SqlColumn(column.Name, column.Decltype))], result.Rows, result.RowsAffected, result.LastInsertRowid))]),
            SqlBatchFailure failure => new HomeSessionAnswer(session.IsAutocommit, failure.Error, Statement: failure.Statement),
        };
    }

    private static HomeSessionAnswer Answered(ISqlSession session, SqlResult result)
    {
        result.ReadCharge?.Accept();
        return new HomeSessionAnswer(session.IsAutocommit, Result: new HomeSessionResult(result.Columns, result.Rows, result.RowsAffected, result.LastInsertRowid));
    }

    private static async ValueTask AnswerAsync(PipeWriter output, HomeSessionAnswer answer, CancellationToken cancellationToken)
    {
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sending.CancelAfter(QosSessions.MessageTimeout);
        await HomeSessionFrames.WriteAsync(output, answer, ReplicasJsonContext.Default.HomeSessionAnswer, sending: null, sending.Token).ConfigureAwait(false);
    }
}
