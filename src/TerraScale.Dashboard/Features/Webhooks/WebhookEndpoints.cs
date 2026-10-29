using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Common.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Webhooks
{
    public static class WebhookEndpoints
    {
        public static IEndpointRouteBuilder MapChangeWebhooks(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/databases/{databaseId}/webhooks", async (HttpContext http, string databaseId, DashboardSessions sessions, AccountService accounts, DataService data, WebhookService hooks) =>
            {
                NoStore(http);
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                var collection = http.Request.Query["collection"].ToString();
                try
                {
                    var database = DatabaseService.Database(context, databaseId);
                    var collections = await data.CollectionsAsync(context, databaseId, http.RequestAborted);
                    if (collection.Length == 0)
                    {
                        collection = collections.FirstOrDefault()?.Name ?? "";
                    }

                    var items = collection.Length == 0 ? [] : await hooks.ListAsync(context, databaseId, collection, http.RequestAborted);
                    var selected = http.Request.Query["webhook"].ToString();
                    if (selected.Length > 0 && !items.Any(item => item.Id == selected))
                    {
                        throw new AccountAccessException("Webhook is unavailable.", 404);
                    }

                    var deliveries = selected.Length == 0 ? [] : await hooks.DeliveriesAsync(context, databaseId, collection, selected, http.RequestAborted);
                    return View(http, context, database, collection, items, deliveries, selected);
                }
                catch (Exception error) when (KnownFailure(error)) { return Failure(http, context, databaseId, collection, error); }
            });
            foreach (var delete in new[] { false, true })
            {
                endpoints.MapPost("/api/v1/dashboard/webhooks" + (delete ? "/delete" : ""), async (HttpContext http, DashboardSessions sessions, AccountService accounts, WebhookService hooks) =>
                {
                    NoStore(http);
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
                    var collection = form["collection"].ToString();
                    try
                    {
                        var database = DatabaseService.Database(context, id);
                        if (delete)
                        {
                            await hooks.DeleteAsync(context, id, collection, form["id"].ToString(), http.RequestAborted);
                            return HttpRedirects.SeeOther(DatabaseService.Path(id, "webhooks", collection));
                        }
                        var created = await hooks.CreateAsync(context, id, collection, form["url"].ToString(), http.RequestAborted);
                        // No further I/O may lose the only response containing this secret.
                        return View(http, context, database, collection, [], [], "", created: created, status: 201);
                    }
                    catch (Exception error) when (KnownFailure(error)) { return Failure(http, context, id, collection, error, creation: !delete); }
                });
            }
            return endpoints;
        }

        private static void NoStore(HttpContext http)
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            http.Response.Headers["Referrer-Policy"] = "same-origin";
        }

        private static bool KnownFailure(Exception error)
        {
            return DatabaseResults.KnownFailure(error) || error is HttpRequestException or ArgumentException or TimeoutException or IOException;
        }

        private static IResult Failure(HttpContext http, AccountContext context, string id, string collection, Exception error, bool creation = false)
        {
            if (DatabaseResults.Status(error) == 404)
            {
                return DatabaseResults.PageFailure(http, context, error);
            }

            try
            {
                return View(http, context, DatabaseService.Database(context, id), collection, [], [], "",
                    error: error is ArgumentException ? "Enter an HTTPS URL on port 443 without credentials or a fragment (up to 2048 characters)." :
                        creation && (error is TimeoutException or IOException ||
                            error is TerraBaseException { ErrorCode: "invalid_response" } invalid && (int?)invalid.StatusCode is >= 200 and <= 299 ||
                            error is HttpRequestException request && (request.StatusCode is null || (int)request.StatusCode >= 500))
                            ? "The result is unknown. Check the endpoint list before retrying; delete and recreate it if its signing secret was lost." : DatabaseResults.Message(error),
                    status: error is ArgumentException ? 422 : DatabaseResults.Status(error));
            }
            catch (AccountAccessException access) { return DatabaseResults.PageFailure(http, context, access); }
        }

        private static RazorSlices.RazorSlice<WebhooksPage> View(HttpContext http, AccountContext context, DatabaseRecord database, string collection,
            IReadOnlyList<TerraBaseChangeWebhook> hooks, IReadOnlyList<TerraBaseChangeWebhookDelivery> deliveries, string selected,
            TerraBaseCreatedChangeWebhook? created = null, string error = "", int status = 200)
        {
            if (created is not null)
            {
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
            }

            var view = WebhooksView.Create(new WebhooksPage(ShellModel.For(http, context, database.Name + " · Webhooks"), database, collection, hooks, deliveries, selected, created, error));
            view.StatusCode = status;
            return view;
        }
    }

    public sealed record WebhooksPage(ShellModel Shell, DatabaseRecord Database, string Collection,
        IReadOnlyList<TerraBaseChangeWebhook> Hooks, IReadOnlyList<TerraBaseChangeWebhookDelivery> Deliveries, string Selected,
        TerraBaseCreatedChangeWebhook? Created, string Error);
}
