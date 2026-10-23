using TerraBase.Features.ApiKeys;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Hrana;

/// <summary><c>POST …/databases/{database}/libsql/{v2|v3}/pipeline</c>: Hrana over HTTP for libSQL clients.</summary>
internal sealed class HranaPipelineEndpoint(ITenantDirectory tenants, ISqlService sql) : EndpointWithoutRequest<IResult>
{
    private static readonly string[] Versions = ["v2", "v3"];

    /// <inheritdoc />
    public override void Configure()
    {
        Post(HranaRoute.Base + "/pipeline");
        Name("libsqlPipeline");
        Summary("Runs a Hrana pipeline (libSQL clients)");
        Description("Hrana over HTTP, versions v2 and v3 (JSON). Point a libSQL client at …/databases/{database}/libsql/ with a project data API key as authToken. "
            + "Requests: execute, batch, sequence, describe, store_sql, close_sql, close, get_autocommit; SQL errors are error results inside a 200. "
            + "Every response closes its stream (baton null), so a request that sends a baton answers 400 STREAM_EXPIRED. Writes need documents:write. Body at most 1 MiB, 1000 statements.");
        Tags("SQL");
        Metadata(new RequestBodyMetadata(typeof(HranaPipelineRequest), HranaRoute.JsonMediaType));
        Produces<HranaPipelineResponse>();
        Produces<HranaErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (database, body, refusal) = await HranaRoute.ReadAsync<HranaPipelineRequest>(HttpContext, tenants, Versions, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (body!.Requests is null || HranaExecutor.Statements(body.Requests) > HranaExecutor.MaxStatements)
        {
            return HranaRoute.Refuse(StatusCodes.Status400BadRequest, "PROTOCOL_ERROR", $"a pipeline needs a requests list of at most {HranaExecutor.MaxStatements} statements");
        }

        if (body.Baton is not null)
        {
            return HranaRoute.Refuse(StatusCodes.Status400BadRequest, "STREAM_EXPIRED", "stream not found: the baton is unknown or expired; open a new stream");
        }

        // shortcut: every stream closes with its request, so transactions cannot span requests; the stream store replaces this.
        var canWrite = HttpContext.User.HasClaim(ApiKeyAuthenticationHandler.ScopeClaim, ApiKeyScopes.DocumentsWrite);
        var stream = new HranaStream(sql.Open(database!, canWrite), string.Empty, database!);
        var executor = new HranaExecutor(stream);
        var results = new List<HranaResult>(body.Requests.Count);
        var closed = false;
        try
        {
            foreach (var request in body.Requests)
            {
                results.Add(closed
                    ? HranaResult.Failed(new HranaError("the stream is closed", "STREAM_CLOSED"))
                    : await executor.RunAsync(request, cancellationToken));
                closed |= request?.Type == "close";
            }

            await stream.Session.EndRequestAsync();
        }
        finally
        {
            await stream.DisposeAsync();
        }

        string? baton = null;
        return TypedResults.Json(new HranaPipelineResponse(baton, null, results), HranaRoute.Json<HranaPipelineResponse>(), HranaRoute.JsonMediaType);
    }
}
