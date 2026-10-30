using TerraScale.TerraBase.Contracts;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.Common.Databases
{
    public sealed partial class DatabaseService
    {
        // Discover one missing ID, never reconcile inventory. No name or caller-chosen project reaches the producer.
        public async Task<(AccountContext Context, DatabaseRecord Database)> LookupAsync(AccountContext context, string id, CancellationToken ct)
        {
            if (!TerraScaleIds.IsValid(id))
            {
                throw new AccountAccessException("Pass the database ID (a ULID), not its name.", 404);
            }

            context = await CurrentAsync(context, ct);
            var project = context.Project!;
            if (PlatformDatabases.IsPlatform(context.Organization!.Id))
            {
                if (!PlatformDatabases.Databases.TryGetValue(id, out var platform) || platform.ProjectId != project.Id)
                {
                    throw new AccountAccessException("Database is unavailable.", 404);
                }

                return (context, Checked(Database(context, id)));
            }
            if (PlatformDatabases.Databases.ContainsKey(id))
            {
                throw new AccountAccessException("Platform admin access is required.");
            }

            if (project.Databases.TryGetValue(id, out var known))
            {
                return (context, Checked(known));
            }

            var scope = Scope(context);
            DatabaseRecord? imported = null;
            try
            {
                var catalog = await backend.CatalogAsync(ct);
                // A missing record has no known home: project GRU does not exclude an SJC+FRA database.
                var listings = await Task.WhenAll(catalog.Select(async region =>
                {
                    try { return (Region: region.Id, Items: (IReadOnlyList<ManagedDatabase>?)await backend.ListAsync(scope, region, ct)); }
                    catch (Exception error) when (!ct.IsCancellationRequested && (error is OperationCanceledException || error is IOException || error is TerraBaseException { Status: 404 or 429 or 421 or >= 500 } ||
                        error is HttpRequestException request && request is not DatabaseListingException &&
                        (request.StatusCode is null or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests or HttpStatusCode.MisdirectedRequest ||
                            request.StatusCode is { } status && (int)status >= 500)))
                    { return (Region: region.Id, Items: (IReadOnlyList<ManagedDatabase>?)null); }
                }));
                var matches = listings.SelectMany(listing => listing.Items ?? []).Where(item => item.Id == id).ToArray();
                if (matches.Length == 0 && listings.Any(listing => listing.Items is null))
                {
                    throw Unavailable();
                }

                if (matches.Length > 0)
                {
                    var remote = matches[0];
                    // Producer names allow underscores/trailing hyphens; creation-form policy must not hide valid databases.
                    if (remote.ProjectId != project.Id || remote.CreatedAt == default || remote.Name is null || !ProducerName().IsMatch(remote.Name) ||
                        ReservedProducerName().IsMatch(remote.Name) || remote.BranchedFrom is { } source && (!TerraScaleIds.IsValid(source) || source == id) ||
                        remote.AllRegions && remote.ReplicaRegions is not { Count: 0 })
                    {
                        throw Unavailable();
                    }

                    var placement = await RemotePlacementAsync(remote, ct);
                    if (!listings.Any(listing => listing.Region == remote.HomeRegion && listing.Items?.Any(item => item.Id == id) == true) ||
                        matches.Any(item => item.ProjectId != remote.ProjectId || item.Name != remote.Name || item.CreatedAt != remote.CreatedAt ||
                            item.BranchedFrom != remote.BranchedFrom || item.HomeRegion != remote.HomeRegion || item.AllRegions != remote.AllRegions ||
                            item.ReplicaRegions is null || !item.ReplicaRegions.Order(StringComparer.Ordinal).SequenceEqual(remote.ReplicaRegions!.Order(StringComparer.Ordinal))))
                    {
                        throw Unavailable();
                    }

                    imported = Record(remote, placement, "ready");
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                (DatabaseResults.KnownFailure(error) || error is TerraBaseException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException))
            { throw Unavailable(); }
            // Only complete, successful regional pagination can establish absence.
            if (imported is null)
            {
                throw new AccountAccessException("Database is unavailable.", 404);
            }

            var resolved = imported;
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                var target = org.Projects.GetValueOrDefault(project.Id) ?? throw new AccountAccessException("Project is unavailable.", 404);
                if (target.Id != scope.ProjectId || target.OrganizationId != scope.OrganizationId)
                {
                    throw Unavailable();
                }

                if (target.Databases.TryGetValue(id, out var winner)) { resolved = Checked(winner); return; }
                if (target.Databases.Values.Any(item => item.Name == imported.Name))
                {
                    throw Unavailable();
                }

                target.Databases[id] = imported;
                resolved = imported;
            }, ct);
            // Do not publish uncommitted metadata into the request, or replace a concurrent winner's settings/revision.
            project.Databases[id] = resolved;
            return (context, resolved);

            DatabaseRecord Checked(DatabaseRecord record)
            {
                return record.Id == id && record.ProjectId == project.Id ? record : throw Unavailable();
            }
        }

        private static DatabaseProviderException Unavailable()
        {
            return new(HttpStatusCode.ServiceUnavailable, "Database discovery is unavailable. Try again shortly.");
        }

        [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
        private static partial Regex ProducerName();
        [GeneratedRegex(@"\A(con|prn|aux|nul|com[0-9]|lpt[0-9])\z", RegexOptions.CultureInvariant)]
        private static partial Regex ReservedProducerName();
    }
}
