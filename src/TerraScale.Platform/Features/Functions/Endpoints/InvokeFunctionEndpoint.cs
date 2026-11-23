using System.Buffers;
using System.Text;

namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class InvokeFunctionEndpoint(PlatformAccess access, FunctionInvoker invoker)
    : EndpointWithoutRequest<Result<FunctionAnswer, Validation, PayloadTooLarge, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly PayloadTooLarge TooLarge = new("the request body is at most 1 MiB");

    public override void Configure()
    {
        Post(FunctionNames.RouteItem + "/invoke");
        Name("invokeFunction");
        Summary("Runs a function");
        Description(FunctionNames.Description + " The body (at most 1 MiB) is event.body: parsed JSON, otherwise text. Query values are event.query. "
            + "Answers the handler's {status, body}, or its JSON result with 200; a failed run answers 500 function_error. TerraScale-Bookmark is honored and returned.");
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FunctionAnswer, Validation, PayloadTooLarge, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        CancellationToken cancellationToken)
    {
        var request = HttpContext.Request;
        if (request.ContentLength > MaxBodyBytes)
        {
            return TooLarge;
        }

        var body = new ArrayBufferWriter<byte>();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (body.WrittenCount + read > MaxBodyBytes)
            {
                return TooLarge;
            }

            body.Write(chunk.AsSpan(0, read));
        }

        var project = RouteValue("project");
        var database = RouteValue("database");
        var function = RouteValue("function");
        var bookmark = request.Headers[ConsistencyHeaders.Bookmark].ToString();
        if (!CausalBookmark.TryDecode(bookmark, out _))
        {
            return new Validation("TerraScale-Bookmark is not a valid bookmark", "invalid_bookmark");
        }

        var eventJson = Event(body.WrittenMemory, request.Query);
        return await FunctionTargets.ResolveAsync(access, HttpContext, project, database, function, Scopes.Read, cancellationToken) switch
        {
            FunctionTarget target => await invoker.InvokeAsync(target, function, "http", eventJson, bookmark, HttpContext.TraceIdentifier, cancellationToken) switch
            {
                FunctionAnswer answer => answer,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private string RouteValue(string name) =>
        HttpContext.Request.RouteValues[name] as string ?? throw new InvalidOperationException($"the route has no {name} value");

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
            else if (FunctionAnswers.Document(body) is { } parsed)
            {
                using (parsed)
                {
                    parsed.RootElement.WriteTo(writer);
                }
            }
            else
            {
                writer.WriteStringValue(Encoding.UTF8.GetString(body.Span));
            }

            writer.WriteStartObject("query");
            foreach (var (key, value) in query)
            {
                writer.WriteString(key, value.ToString());
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}
