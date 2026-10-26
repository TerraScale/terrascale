using System.Globalization;
using System.Text.Json;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Hrana;

/// <summary><c>POST …/databases/{database}/libsql/v3/cursor</c>: one batch streamed as newline-delimited JSON.</summary>
internal sealed class HranaCursorEndpoint(ITenantDirectory tenants, IHranaStreams streams, IMemoryBudget memory, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    private const string MediaType = "application/x-ndjson";

    private static readonly string[] Versions = ["v3"];

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    /// <inheritdoc />
    public override void Configure()
    {
        Post(HranaRoute.Base + "/cursor");
        Name("libsqlCursor");
        Summary("Streams a Hrana batch (libSQL clients)");
        Description("Hrana v3 cursor: the body is {baton, batch}; the response is newline-delimited JSON: first {baton, base_url}, then per step step_begin, row…, step_end, or step_error. "
            + "Skipped steps write nothing. The baton continues the stream for 30 idle seconds, with the pipeline's rules for lost batons. Writes need documents:write.");
        Tags("SQL");
        Metadata(new RequestBodyMetadata(typeof(HranaCursorRequest), HranaRoute.JsonMediaType));
        Produces<HranaCursorEntry>(StatusCodes.Status200OK, MediaType);
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
        var (database, body, refusal) = await HranaRoute.ReadAsync<HranaCursorRequest>(HttpContext, tenants, Versions, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (body!.Batch is not { } batch || !HranaExecutor.IsValid(batch) || batch.Steps.Count > HranaExecutor.MaxStatements)
        {
            return HranaRoute.Refuse(StatusCodes.Status400BadRequest, "PROTOCOL_ERROR", $"a cursor needs a valid batch of at most {HranaExecutor.MaxStatements} steps");
        }

        var (stream, refused) = HranaRoute.Acquire(HttpContext, streams, database!, body.Baton);
        if (stream is null)
        {
            return refused!;
        }

        // Every step runs before the first line: the baton's suffix states whether the batch left a transaction open.
        // The core materializes each result anyway, and the executor bounds their total size.
        stream.Session.BeginRequest();
        var executor = new HranaExecutor(stream, memory, batch.Steps.Count, time);
        var results = new HranaStmtResult?[batch.Steps.Count];
        var errors = new HranaError?[batch.Steps.Count];
        string baton;
        try
        {
            for (var step = 0; step < batch.Steps.Count; step++)
            {
                if (batch.Steps[step].Condition is not { } condition || executor.Holds(condition, results, errors))
                {
                    switch (await executor.ExecuteAsync(batch.Steps[step].Stmt, cancellationToken))
                    {
                        case HranaStmtResult result:
                            results[step] = result;
                            break;
                        case HranaError error:
                            errors[step] = error;
                            break;
                    }
                }
            }

            await stream.Session.EndRequestAsync();
            baton = streams.Park(stream);
        }
        catch
        {
            await streams.CloseAsync(stream);
            throw;
        }

        return TypedResults.Stream(output => WriteAsync(output, baton, results, errors, cancellationToken), MediaType);
    }

    private static async Task WriteAsync(Stream output, string baton, HranaStmtResult?[] results, HranaError?[] errors, CancellationToken cancellationToken)
    {
        await WriteAsync(output, new HranaCursorHead(baton, null), cancellationToken);
        for (var step = 0; step < results.Length; step++)
        {
            if (results[step] is { } result)
            {
                await WriteAsync(output, new HranaCursorEntry("step_begin", Step: step, Cols: result.Cols), cancellationToken);
                foreach (var row in result.Rows)
                {
                    await WriteAsync(output, new HranaCursorEntry("row", Row: row), cancellationToken);
                }

                await WriteAsync(output, new HranaStepEnd("step_end", result.AffectedRowCount,
                    result.LastInsertRowid is { } rowid ? long.Parse(rowid, CultureInfo.InvariantCulture) : null), cancellationToken);
            }
            else if (errors[step] is { } error)
            {
                await WriteAsync(output, new HranaCursorEntry("step_error", Step: step, Error: error), cancellationToken);
            }
        }
    }

    private static async Task WriteAsync<T>(Stream output, T line, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(output, line, HranaRoute.Json<T>(), cancellationToken);
        await output.WriteAsync(NewLine, cancellationToken);
    }
}
