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
            var asked = form["regions"].OfType<string>().Select(code => code.Trim()).Where(code => code.Length > 0).ToArray();
            try
            {
                var discovery = await backend.DiscoveryAsync(http.RequestAborted);
                var regionsContract = discovery.Catalog.Count > 0 && await databases.RoutedAsync(context, DatabaseService.Database(context, id),
                    async (_, home) => (await backend.GetAsync(DatabaseService.Scope(context), home, id, http.RequestAborted)).Regions is not null, http.RequestAborted);
                var database = regionsContract
                    ? await databases.PlaceRegionsAsync(context, id, Selection(form, ""), [.. form["offered"].OfType<string>()], http.RequestAborted)
                    : await ApplyAsync(databases, backend, context, id, Selection(form, ""), http.RequestAborted);
                if (http.Request.Headers["Datastar-Request"] == "true")
                {
                    return await DatabaseEndpoints.DetailPageAsync(http, await databases.CurrentAsync(context, http.RequestAborted), id, "replication", backend, databases,
                        regions: new(asked, ""));
                }
                return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "replication")) : Results.Json(DatabaseEndpoints.DatabaseOutput(database));
            }
            catch (DatabaseRegionsCopyingException copying) when (DatabaseResults.WantsHtml(http) || http.Request.Headers["Datastar-Request"] == "true")
            {
                return await DatabaseEndpoints.DetailPageAsync(http, await databases.CurrentAsync(context, http.RequestAborted), id, "replication", backend, databases,
                    status: StatusCodes.Status202Accepted, regions: new(asked, copying.Message));
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                return !(DatabaseResults.WantsHtml(http) || http.Request.Headers["Datastar-Request"] == "true") || DatabaseResults.Status(error) == 404
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

        private static async Task<DatabaseRecord> ApplyAsync(DatabaseService databases, IDatabaseBackend backend, AccountContext context, string id,
            IReadOnlyList<string> selected, CancellationToken cancellationToken)
        {
            var database = DatabaseService.Database(context, id);
            if (database.Region.Length == 0)
            {
                throw new AccountAccessException("This database's regions are unavailable. Reload the page and try again.", 409);
            }

            var catalog = await backend.RegionsAsync(cancellationToken);
            string Stored(string code)
            {
                return DatabaseRegion.Resolve(catalog, code)?.Id ?? code.ToLowerInvariant();
            }

            var keeper = Stored(database.Region);
            var others = DatabaseService.Regions(database).Select(Stored).Where(region => region != keeper).Distinct(StringComparer.Ordinal).ToArray();
            string[] listed = [.. selected.Select(code => Stored(code) == keeper ? keeper : DatabaseService.ResolveRegion(catalog, code).Id)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            var keeps = listed.Contains(keeper, StringComparer.Ordinal);
            if (keeps && database.AllRegions && catalog.All(region => listed.Contains(region.Id, StringComparer.Ordinal)))
            {
                return await databases.PlaceAsync(context, id, [], true, cancellationToken);
            }

            var wanted = DatabaseService.Capped(listed);
            if (keeps)
            {
                return await databases.PlaceAsync(context, id, [.. wanted.Where(region => region != keeper)], false, cancellationToken);
            }

            var successor = wanted.FirstOrDefault(region => others.Contains(region, StringComparer.Ordinal)) ?? wanted[0];
            string[] staged = [.. wanted.Where(region => region != successor).Prepend(successor).Take(Limit - 1)];
            if (database.AllRegions || !others.ToHashSet(StringComparer.Ordinal).SetEquals(staged))
            {
                await databases.PlaceAsync(context, id, staged, false, cancellationToken);
            }

            await databases.MoveHomeAsync(context, id, successor, cancellationToken);
            return await databases.PlaceAsync(context, id, [.. wanted.Where(region => region != successor)], false, cancellationToken);
        }
    }

    public sealed record DatabaseRegionsForm(IReadOnlyList<string> Asked, string Notice);
}
