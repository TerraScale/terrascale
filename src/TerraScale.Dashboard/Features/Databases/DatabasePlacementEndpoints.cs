using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Databases
{
    public static class DatabasePlacementEndpoints
    {
        internal const string Minimum = "A database needs one region.";

        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases,
            IDatabaseBackend backend)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "replication"), Results.BadRequest("Invalid form token."));
            }

            var id = form["databaseId"].ToString();
            var asked = form["regions"].OfType<string>().Select(code => code.Trim()).Where(code => code.Length > 0).ToArray();
            try
            {
                var database = await databases.PlaceAsync(context, id, Selection(form, ""), [.. form["offered"].OfType<string>()], http.RequestAborted);
                return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "replication")) : Results.Json(DatabaseEndpoints.DatabaseOutput(database));
            }
            catch (DatabaseRegionsCopyingException copying) when (DatabaseResults.WantsHtml(http))
            {
                return await DatabaseEndpoints.DetailPageAsync(http, await databases.CurrentAsync(context, http.RequestAborted), id, "replication", backend, databases,
                    status: StatusCodes.Status202Accepted, regions: new(asked, copying.Message));
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                return !DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404
                    ? DatabaseResults.PageFailure(http, context, error)
                    : await DatabaseEndpoints.DetailPageAsync(http, await databases.CurrentAsync(context, http.RequestAborted), id, "replication", backend, databases,
                        DatabaseResults.Message(error), status: DatabaseResults.Status(error), regions: new(asked, ""));
            }
        }

        internal static IReadOnlyList<string> Selection(IFormCollection form, string orderField)
        {
            var chosen = form["regions"].OfType<string>().Select(code => code.Trim()).Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var order = orderField.Length == 0 ? [] : form[orderField].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string[] selected = [.. order.Select(code => Array.Find(chosen, match => match.Equals(code, StringComparison.OrdinalIgnoreCase)))
                .OfType<string>().Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase)];
            return selected.Length == 0 ? throw new AccountAccessException(Minimum, 422) : selected;
        }
    }

    public sealed record DatabaseRegionsForm(IReadOnlyList<string> Asked, string Notice);
}
