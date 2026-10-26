using TerraBase.Features.ApiKeys;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Hrana;

/// <summary><c>POST …/databases/{database}/libsql/{v2|v3}/pipeline</c>: Hrana over HTTP for libSQL clients.</summary>
internal sealed class HranaPipelineEndpoint(ITenantDirectory tenants, IHranaStreams streams, IMemoryBudget memory, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    private static readonly string[] Versions = ["v2", "v3"];

    /// <inheritdoc />
    public override void Configure()
    {
        Post(HranaRoute.Base + "/pipeline");
        Name("libsqlPipeline");
        Summary("Runs a Hrana pipeline (libSQL clients)");
        Description("Hrana over HTTP, versions v2 and v3 (JSON). Point a libSQL client at …/databases/{database}/libsql/ with a current project access JWT as authToken (mint it from a scoped API key using client_credentials; raw API keys are refused). "
            + "Requests: execute, batch, sequence, describe, store_sql, close_sql, close, get_autocommit; SQL errors are error results inside a 200. "
            + "A response baton continues the stream (and its transaction) for 30 idle seconds. A lost baton whose stream held a transaction or stored SQL answers 400 STREAM_EXPIRED; one that held neither opens a fresh stream. "
            + "Writes need documents:write. Body at most 1 MiB, 256 statements (400 PROTOCOL_ERROR past it); at most 64 open streams per database and 256 per organization on a node (503 DATABASE_STREAMS_EXHAUSTED, ORGANIZATION_STREAMS_EXHAUSTED).");
        Tags("SQL");
        Metadata(new RequestBodyMetadata(typeof(HranaPipelineRequest), HranaRoute.JsonMediaType));
        Produces<HranaPipelineResponse>();
        Produces<HranaErrorBody>(StatusCodes.Status400BadRequest);
        Produces<HranaErrorBody>(StatusCodes.Status404NotFound);
        Produces<HranaErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<HranaErrorBody>(StatusCodes.Status503ServiceUnavailable);
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

        if (body!.Requests is null || body.Requests.Count > HranaRoute.MaxRequests || HranaExecutor.Statements(body.Requests) > HranaExecutor.MaxStatements)
        {
            return HranaRoute.Refuse(StatusCodes.Status400BadRequest, "PROTOCOL_ERROR", $"a pipeline needs a requests list of at most {HranaRoute.MaxRequests} requests and {HranaExecutor.MaxStatements} statements");
        }

        var (stream, refused) = HranaRoute.Acquire(HttpContext, streams, database!, body.Baton);
        if (stream is null)
        {
            return refused!;
        }

        stream.Session.BeginRequest();
        var executor = new HranaExecutor(stream, memory, HranaExecutor.Statements(body.Requests), time);
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
        catch
        {
            await streams.CloseAsync(stream);
            throw;
        }

        string? baton = null;
        if (closed)
        {
            await streams.CloseAsync(stream);
        }
        else
        {
            baton = streams.Park(stream);
        }

        return TypedResults.Json(new HranaPipelineResponse(baton, null, results), HranaRoute.Json<HranaPipelineResponse>(), HranaRoute.JsonMediaType);
    }
}
