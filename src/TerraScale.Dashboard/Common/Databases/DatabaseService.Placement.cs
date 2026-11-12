using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Common.Databases
{
    public sealed partial class DatabaseService
    {
        public async Task<DatabaseRecord> PlaceRegionsAsync(AccountContext context, string id, IReadOnlyList<string> regions, IReadOnlyList<string> offered, CancellationToken cancellationToken)
        {
            context = await CurrentAsync(context, cancellationToken);
            var database = DemandPlacement(context, id);
            if (database.Region.Length == 0)
            {
                throw new AccountAccessException("This database's regions are unavailable. Reload the page and try again.", 409);
            }

            var catalog = await backend.CatalogAsync(cancellationToken);
            HashSet<string> Ids(DatabaseRecord record) => Regions(record).Select(code => DatabaseRegion.Resolve(catalog, code)?.Id ?? code.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var seen = Ids(database);
            var scope = Scope(context);
            return await RoutedAsync(context, database, async (current, route) =>
            {
                var before = Ids(current);
                if (!before.SetEquals(seen))
                {
                    throw new AccountAccessException("This database's regions changed in another session. Check them and save again.", 409);
                }

                string[] selected = [.. regions.Select(code => before.FirstOrDefault(known => known.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase)) ?? ResolveRegion(catalog, code).Id)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                var chosen = selected.ToHashSet(StringComparer.Ordinal);
                var everywhere = current.AllRegions && offered.Count > 0 &&
                    offered.All(code => chosen.Contains(DatabaseRegion.Resolve(catalog, code.Trim())?.Id ?? code.Trim().ToLowerInvariant()));
                try { return await SavePlacementAsync(context, current, await backend.PlaceRegionsAsync(scope, route, id, everywhere ? [] : Capped(selected), everywhere, cancellationToken), "database.placement", cancellationToken); }
                catch (TerraBaseException error) when (error.ErrorCode == DatabaseRegionsCopyingException.Code)
                {
                    ManagedDatabase pending;
                    try { pending = await backend.GetAsync(scope, route, id, cancellationToken); }
                    catch (DatabaseWrongRegionException)
                    {
                        await SyncAsync(context, cancellationToken);
                        return Database(context, id);
                    }

                    var stored = await SavePlacementAsync(context, current, pending, null, cancellationToken);
                    var now = Regions(stored).Select(code => DatabaseRegion.Resolve(catalog, code)?.Id ?? code.ToLowerInvariant()).ToArray();
                    throw new DatabaseRegionsCopyingException([.. selected.Where(region => !before.Contains(region))], selected,
                        [.. now.Concat(before).Distinct(StringComparer.Ordinal).Where(region => !chosen.Contains(region))]);
                }
            }, cancellationToken);
        }

        private async Task<T> RoutedAsync<T>(AccountContext context, DatabaseRecord database, Func<DatabaseRecord, DatabaseRegion, Task<T>> call, CancellationToken cancellationToken)
        {
            var home = await HomeAsync(context, database, cancellationToken);
            try { return await call(database, home); }
            catch (DatabaseWrongRegionException)
            {
                await SyncAsync(context, cancellationToken);
                var synced = Database(context, database.Id);
                var route = await HomeAsync(context, synced, cancellationToken);
                if (route.Id == home.Id)
                {
                    throw;
                }

                return await call(synced, route);
            }
        }

        internal static IReadOnlyList<T> Capped<T>(IReadOnlyList<T> regions)
        {
            return regions.Count > RegionLimit ? throw new AccountAccessException($"Choose at most {RegionLimit} regions.", 422) : regions;
        }

        private const int RegionLimit = 9;

    }
}
