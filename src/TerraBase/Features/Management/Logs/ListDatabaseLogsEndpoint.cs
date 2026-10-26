using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Logs;

/// <summary>Recent redacted request observations for an authorized database.</summary>
public sealed class ListDatabaseLogsEndpoint(IManagementService management, IRequestLogs logs) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/logs");
        Name("listDatabaseLogs");
        Summary("Lists recent database requests and slow query requests");
        Description("Newest 100 recorded requests within 24 hours. status, when supplied, is 2xx, 3xx, 4xx or 5xx. " +
            "Route templates contain no document keys, query text or credentials. Operations count committed document operations. " +
            "Recording is best effort; pending observations can be lost on crash and remote observations arrive through catalog replication. " +
            "RecordingLimited reports capacity omissions on this server since startup; truncated reports a full recent window. " +
            "Expired records are excluded from reads and physically pruned on that database's next log flush. " +
            "Query requests with latencyMs >= 100 are slow queries. " + ProjectApiRoute.IdentityDescription);
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
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var filter = HttpContext.Request.Query["status"].ToString();
        if (HttpContext.Request.Query["status"].Count > 1 || filter is not ("" or "2xx" or "3xx" or "4xx" or "5xx")) { return ErrorResponse.From(new Validation("status must be 2xx, 3xx, 4xx or 5xx", "invalid_status")); }
        return await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            TenantContext database => await logs.ReadAsync(database, filter.Length == 0 ? null : filter[0] - '0', cancellationToken) switch
            {
                RequestLogRead read => Retain(read),
                Unavailable error => ErrorResponse.From(error),
            },
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    private RequestLogRead Retain(RequestLogRead read)
    {
        HttpContext.Response.RegisterForDispose(read);
        return read;
    }
}
