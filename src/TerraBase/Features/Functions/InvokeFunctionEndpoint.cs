using System.Buffers;
using System.Text.Json;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Functions;

/// <summary>Runs a function over HTTPS.</summary>
internal sealed class InvokeFunctionEndpoint(IManagementService management, IFunctions functions) : EndpointWithoutRequest<IResult>
{
    private const int MaxBodyBytes = 1024 * 1024;

    /// <inheritdoc />
    public override void Configure()
    {
        Post(FunctionRoute.Item + "/invoke");
        Name("invokeFunction");
        Summary("Runs a function");
        Description(FunctionRoute.Description + " The body (at most 1 MiB) is event.body: parsed JSON, otherwise text. Query values are event.query. "
            + "Answers the handler's {status, body}, or its JSON result with 200; a failed run answers 500 function_error.");
        Tags("Functions");
        Metadata(new RequestBodyMetadata(typeof(JsonElement), "application/json"));
        Produces<JsonElement>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status500InternalServerError);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.ContentLength > MaxBodyBytes)
        {
            return ErrorResponse.From(new PayloadTooLarge("the request body is at most 1 MiB"));
        }

        var buffer = new ArrayBufferWriter<byte>();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await HttpContext.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.WrittenCount + read > MaxBodyBytes)
            {
                return ErrorResponse.From(new PayloadTooLarge("the request body is at most 1 MiB"));
            }

            buffer.Write(chunk.AsSpan(0, read));
        }

        var eventJson = Event(buffer.WrittenMemory, HttpContext.Request.Query);
        return await FunctionRoute.RunAsync(HttpContext, management,
            (tenant, name) => functions.InvokeAsync(tenant, name, "http", eventJson, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static string Event(ReadOnlyMemory<byte> body, IQueryCollection query)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("trigger", "http");
            writer.WritePropertyName("body");
            if (body.IsEmpty)
            {
                writer.WriteNullValue();
            }
            else
            {
                try
                {
                    using var parsed = JsonDocument.Parse(body);
                    parsed.RootElement.WriteTo(writer);
                }
                catch (JsonException)
                {
                    writer.WriteStringValue(System.Text.Encoding.UTF8.GetString(body.Span));
                }
            }

            writer.WriteStartObject("query");
            foreach (var (key, value) in query)
            {
                writer.WriteString(key, value.ToString());
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(output.WrittenSpan);
    }
}
