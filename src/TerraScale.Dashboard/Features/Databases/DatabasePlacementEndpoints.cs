
namespace TerraScale.Dashboard.Features.Databases
{
    public static class DatabasePlacementEndpoints
    {
        public static IEndpointRouteBuilder MapDatabasePlacement(this IEndpointRouteBuilder endpoints)
        {
            foreach (var operation in new[] { "placement", "move-home" })
            {
                var action = operation;
                endpoints.MapPost("/api/v1/dashboard/databases/" + action, async (HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend) =>
                {
                    var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                    if (context is null)
                    {
                        return Results.Unauthorized();
                    }

                    var form = await FormRequests.ReadAsync(http, http.RequestAborted);
                    if (!Csrf.Validate(http, form))
                    {
                        return Results.BadRequest("Invalid form token.");
                    }

                    var id = form["databaseId"].ToString();
                    try
                    {
                        var database = action == "placement"
                            ? await databases.PlaceAsync(context, id, [.. form["replicaRegions"].OfType<string>()], form["allRegions"].ToString() is "on" or "true", http.RequestAborted)
                            : await databases.MoveHomeAsync(context, id, form["homeRegion"].ToString(), http.RequestAborted);
                        return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "replication")) : Results.Json(DatabaseEndpoints.DatabaseOutput(database));
                    }
                    catch (Exception error) when (DatabaseResults.KnownFailure(error))
                    {
                        return !DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404
                            ? DatabaseResults.PageFailure(http, context, error)
                            : await DatabaseEndpoints.DetailPageAsync(http, context, id, "replication", backend, databases, DatabaseResults.Message(error), status: DatabaseResults.Status(error));
                    }
                });
            }
            return endpoints;
        }
    }
}
