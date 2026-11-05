using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Databases
{
    public static class DatabaseReplicaEndpoints
    {
        public static IEndpointRouteBuilder MapDatabaseReplicas(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/v1/dashboard/databases/replicas", async (HttpContext http, DashboardSessions sessions, AccountService accounts,
                IDatabaseBackend backend, DatabaseService databases) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest("Invalid form token.");
                }

                var id = form["databaseId"].ToString();
                try
                {
                    var database = await databases.SetReplicaRegionsAsync(context, id, form["replicaRegions"], http.RequestAborted);
                    return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "replication") + "?saved=1") : Results.Json(DatabaseEndpoints.DatabaseOutput(database));
                }
                catch (Exception error) when (!http.RequestAborted.IsCancellationRequested && (DatabaseResults.KnownFailure(error) || error is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException))
                {
                    if (!DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404)
                    {
                        return DatabaseResults.PageFailure(http, context, error);
                    }

                    return await DatabaseEndpoints.DetailPageAsync(http, context, id, "replication", backend, databases,
                        DatabaseResults.Status(error) >= 500 ? "The placement change could not be confirmed. Reload Replication before retrying." : DatabaseResults.Message(error),
                        status: DatabaseResults.Status(error));
                }
            });
            return endpoints;
        }
    }
}
