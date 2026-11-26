using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class DatabaseTtlEndpoint(DashboardSessions sessions, AccountService accounts, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            AuthenticateInHandler();
            Post("/v1/databases/ttl");
            Summary("Set or clear database expiry");
            Description("Session and CSRF protected form. Supply databaseId and ttlHours, or action=clear. TTL is between one minute and 365 days.");
            Tags("Databases");
        }

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) { return TypedResults.Unauthorized(); }
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form)) { return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "settings"), TypedResults.BadRequest("Invalid form token.")); }
            var id = form["databaseId"].ToString();
            try
            {
                var clear = form["action"] == "clear";
                if (!clear && string.IsNullOrWhiteSpace(form["ttlHours"]))
                {
                    return (DatabaseResults.WantsHtml(http) || http.Request.IsDatastarRequest)
                        ? await DatabaseSettingsEndpoints.PageAsync(http, context, id, status: StatusCodes.Status422UnprocessableEntity, ttlHours: form["ttlHours"].ToString(), expiryError: "Enter a TTL in hours.")
                        : DatabaseResults.ToResult(new DatabaseInvalid("Enter a TTL in hours."));
                }
                var saved = await databases.SetTtlAsync(context, id, clear ? null : DatabaseTtl.Hours(form["ttlHours"].ToString()), http.RequestAborted);
                return saved switch
                {
                    DatabaseRecord when http.Request.IsDatastarRequest =>
                        await DatabaseSettingsEndpoints.PageAsync(http, await databases.CurrentAsync(context, http.RequestAborted), id, ttlHours: clear ? "" : form["ttlHours"].ToString(), expirySaved: true),
                    DatabaseRecord database =>
                        DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "settings")) : TypedResults.Json(database, DatabaseTtlJson.Default.DatabaseRecord),
                    DatabaseRefusal failure => (DatabaseResults.WantsHtml(http) || http.Request.IsDatastarRequest) && failure.Status != 404
                        ? await DatabaseSettingsEndpoints.PageAsync(http, context, id, status: failure.Status, ttlHours: form["ttlHours"].ToString(), expiryError: failure.Status >= 500 ? "The expiry could not be confirmed. Reload Settings before retrying." : failure.Message)
                        : DatabaseResults.PageFailureOf(http, context, failure),
                };
            }
            catch (Exception error) when (!http.RequestAborted.IsCancellationRequested && (DatabaseResults.KnownFailure(error) || error is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException))
            {
                return (DatabaseResults.WantsHtml(http) || http.Request.IsDatastarRequest) && DatabaseResults.Status(error) != 404
                    ? await DatabaseSettingsEndpoints.PageAsync(http, context, id, status: DatabaseResults.Status(error), ttlHours: form["ttlHours"].ToString(), expiryError: DatabaseResults.Status(error) >= 500 ? "The expiry could not be confirmed. Reload Settings before retrying." : DatabaseResults.Message(error))
                    : DatabaseResults.PageFailure(http, context, error);
            }
        }
    }
}
