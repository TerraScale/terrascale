using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data;

public sealed class GetSqlSchemaEndpoint(DashboardSessions sessions, AccountService accounts, DatabaseService databases, DataService data) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/api/v1/dashboard/data/schema");
        Name("getDashboardSqlSchema");
        Summary("Lists the selected database's tables, columns and declared types for editor completion");
        Tags("Data");
        Produces<SqlCatalog>();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var context = await AccountRequest.RequireAsync(HttpContext, sessions, accounts);
        if (context is null)
        {
            return TypedResults.Unauthorized();
        }

        try
        {
            var id = HttpContext.Request.Query["databaseId"].ToString();
            var (current, _) = await databases.LookupAsync(context, id, cancellationToken);
            var catalog = await data.CatalogAsync(current, id, cancellationToken);
            return TypedResults.Json(catalog, SqlSchemaJson.Default.SqlCatalog);
        }
        catch (Exception error) when (DatabaseResults.KnownFailure(error) || error is HttpRequestException or JsonException)
        {
            return DatabaseResults.Failure(error);
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SqlCatalog))]
internal sealed partial class SqlSchemaJson : JsonSerializerContext;
