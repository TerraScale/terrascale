using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Logs;

public sealed class ListDatabaseLogsEndpoint(IManagementService management, IRequestLogs logs) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/logs");
        Name("listDatabaseLogs");
        Summary("Lists recent database requests and slow query requests");
        Description("Newest 100 recorded requests within 24 hours. status, when supplied, is 2xx, 3xx, 4xx or 5xx. " +
            "Route templates contain no document keys, query text or credentials. Operations count committed document operations. " +
            "Recording is best effort; pending observations can be lost on crash. " +
            "RecordingLimited reports capacity omissions on this server since startup; truncated reports a full recent window. " +
            "Expired records are excluded from reads and physically pruned on that database's next log flush. " +
            "Query requests with latencyMs >= 100 are slow queries. " + CursorDescription + " " + ProjectApiRoute.IdentityDescription);
        Tags("Logs");
        Produces<DatabaseRequestLogs>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new OpenApiParameter
        {
            Name = "status",
            In = ParameterLocation.Query,
            Description = "Optional HTTP status class. Omit it to read every status class; duplicate values are rejected.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("2xx"), JsonValue.Create("3xx"), JsonValue.Create("4xx"), JsonValue.Create("5xx")],
            },
        });
        Metadata(CursorParameter());
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var filter = HttpContext.Request.Query["status"].ToString();
        if (HttpContext.Request.Query["status"].Count > 1 || filter is not ("" or "2xx" or "3xx" or "4xx" or "5xx")) { return ErrorResponse.From(new Validation("status must be 2xx, 3xx, 4xx or 5xx", "invalid_status")); }
        if (!TryCursor(HttpContext, out var after)) { return ErrorResponse.From(new Validation("after must be one canonical uppercase ULID", "invalid_cursor")); }
        return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            TenantContext database => await logs.ReadAsync(database, filter.Length == 0 ? null : filter[0] - '0', after, cancellationToken) switch
            {
                RequestLogRead read => Retain(read),
                Unavailable error => ErrorResponse.From(error),
            },
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    internal const string CursorDescription =
        "after, when supplied, is a cursor: the response lists retained requests with ids after it and recorded at least 30 seconds ago, " +
        "oldest first, and returns nextCursor for the next read. Start from a ULID of the export start time. gap is true when requests " +
        "after the cursor may have been discarded unread: the 100-entry window overflowed past it or it is older than 24 hours.";

    internal static OpenApiParameter CursorParameter() => new()
    {
        Name = "after",
        In = ParameterLocation.Query,
        Description = "Optional cursor: a ULID, usually the previous nextCursor.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-7][0-9A-HJKMNP-TV-Z]{25}$" },
    };

    internal static bool TryCursor(HttpContext context, out string? after)
    {
        var values = context.Request.Query["after"];
        after = values.Count == 1 ? values[0] : null;
        return values.Count == 0 || EntityIds.IsValid(after);
    }

    private RequestLogRead Retain(RequestLogRead read)
    {
        HttpContext.Response.RegisterForDispose(read);
        return read;
    }
}
