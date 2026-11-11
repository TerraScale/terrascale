using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Databases
{
    public static class DatabasePlacementEndpoints
    {
        internal const string Minimum = "A database needs one region.";
        private const int Limit = 9;

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
            try
            {
                var database = await ApplyAsync(databases, backend, context, id, Selection(form, ""), http.RequestAborted);
                return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "replication")) : Results.Json(DatabaseEndpoints.DatabaseOutput(database));
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                return !DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404
                    ? DatabaseResults.PageFailure(http, context, error)
                    : await DatabaseEndpoints.DetailPageAsync(http, context, id, "replication", backend, databases, DatabaseResults.Message(error), status: DatabaseResults.Status(error));
            }
        }

        internal static IReadOnlyList<string> Selection(IFormCollection form, string orderField)
        {
            var chosen = form["regions"].OfType<string>().Select(code => code.Trim()).Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var order = orderField.Length == 0 ? [] : form[orderField].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string[] selected = [.. order.Select(code => Array.Find(chosen, match => match.Equals(code, StringComparison.OrdinalIgnoreCase)))
                .OfType<string>().Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase)];
            return selected.Length == 0 ? throw new AccountAccessException(Minimum, 422)
                : selected.Length > Limit ? throw new AccountAccessException($"Choose at most {Limit} regions.", 422) : selected;
        }

        private static async Task<DatabaseRecord> ApplyAsync(DatabaseService databases, IDatabaseBackend backend, AccountContext context, string id,
            IReadOnlyList<string> selected, CancellationToken cancellationToken)
        {
            var database = DatabaseService.Database(context, id);
            var catalog = await backend.RegionsAsync(cancellationToken);
            string Region(string code)
            {
                return DatabaseRegion.Resolve(catalog, code)?.Id ?? code.ToLowerInvariant();
            }

            if (database.Region.Length == 0)
            {
                throw new AccountAccessException("This database's regions are unavailable. Reload the page and try again.", 409);
            }

            var keeper = Region(database.Region);
            var others = DatabaseService.Regions(database).Select(Region).Where(region => region != keeper).Distinct(StringComparer.Ordinal).ToArray();
            var wanted = selected.Select(Region).Distinct(StringComparer.Ordinal).ToArray();
            if (wanted.Contains(keeper, StringComparer.Ordinal))
            {
                return await databases.PlaceAsync(context, id, [.. wanted.Where(region => region != keeper)],
                    database.AllRegions && catalog.All(region => wanted.Contains(region.Id, StringComparer.Ordinal)), cancellationToken);
            }

            var successor = others.FirstOrDefault(region => wanted.Contains(region, StringComparer.Ordinal)) ?? wanted[0];
            string[] staged = [.. wanted.Where(region => region != successor).Prepend(successor).Take(Limit - 1)];
            if (database.AllRegions || !others.ToHashSet(StringComparer.Ordinal).SetEquals(staged))
            {
                await databases.PlaceAsync(context, id, staged, false, cancellationToken);
            }

            await databases.MoveHomeAsync(context, id, successor, cancellationToken);
            return await databases.PlaceAsync(context, id, [.. wanted.Where(region => region != successor)], false, cancellationToken);
        }
    }
}
