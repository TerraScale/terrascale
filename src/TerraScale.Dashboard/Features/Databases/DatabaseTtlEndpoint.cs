using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class DatabaseTtlEndpoint(DashboardSessions sessions, AccountService accounts, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Post("/v1/databases/ttl");
            Summary("Set or clear database expiry");
            Description("Session and CSRF protected form. Supply databaseId and ttlHours, or action=clear. TTL is between one minute and 365 days.");
            Tags("Databases");
        }

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) { return Results.Unauthorized(); }
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form)) { return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "settings"), Results.BadRequest("Invalid form token.")); }
            var id = form["databaseId"].ToString();
            try
            {
                var clear = form["action"] == "clear";
                if (!clear && string.IsNullOrWhiteSpace(form["ttlHours"]))
                {
                    throw new AccountAccessException("Enter a TTL in hours.", 422);
                }
                var database = await databases.SetTtlAsync(context, id, clear ? null : DatabaseTtl.Hours(form["ttlHours"].ToString()), http.RequestAborted);
                if (http.Request.Headers["Datastar-Request"] == "true")
                {
                    return await DatabaseSettingsEndpoints.PageAsync(http, context, id, ttlHours: clear ? "" : form["ttlHours"].ToString(), expirySaved: true);
                }
                return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "settings")) : Results.Json(database, DatabaseTtlJson.Default.DatabaseRecord);
            }
            catch (Exception error) when (!http.RequestAborted.IsCancellationRequested && (DatabaseResults.KnownFailure(error) || error is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException))
            {
                return (DatabaseResults.WantsHtml(http) || http.Request.Headers["Datastar-Request"] == "true") && DatabaseResults.Status(error) != 404
                    ? await DatabaseSettingsEndpoints.PageAsync(http, context, id, status: DatabaseResults.Status(error), ttlHours: form["ttlHours"].ToString(), expiryError: DatabaseResults.Status(error) >= 500 ? "The expiry could not be confirmed. Reload Settings before retrying." : DatabaseResults.Message(error))
                    : DatabaseResults.PageFailure(http, context, error);
            }
        }
    }
}
