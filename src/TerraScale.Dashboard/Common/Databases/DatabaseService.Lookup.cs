using TerraScale.TerraBase.Contracts;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.Common.Databases
{
    public sealed partial class DatabaseService
    {
        // A write addressed by id must survive a listing gap: refresh once from TerraBase before refusing it.
        public async Task<DatabaseRecord> RequireAsync(AccountContext context, string id, CancellationToken ct)
        {
            try
            {
                return Database(context, id);
            }
            catch (AccountAccessException error) when (error.StatusCode == 404 && context is { Project: not null, Organization: not null } &&
                !PlatformDatabases.IsPlatform(context.Organization.Id) && !PlatformDatabases.Databases.ContainsKey(id))
            {
                var (_, database) = await LookupAsync(context, id, ct);
                return database;
            }
        }

        // Discover one missing ID, never reconcile inventory. No name or caller-chosen project reaches the producer.
        public async Task<(AccountContext Context, DatabaseRecord Database)> LookupAsync(AccountContext context, string id, CancellationToken ct)
        {
            if (!TerraScaleIds.IsValid(id))
            {
                throw new AccountAccessException(DatabaseIdRule, 422, DatabaseIdCode);
            }

            context = await CurrentAsync(context, ct);
            var project = context.Project!;
            if (PlatformDatabases.IsPlatform(context.Organization!.Id))
            {
                if (!PlatformDatabases.Databases.TryGetValue(id, out var platform) || platform.ProjectId != project.Id)
                {
                    throw NotFound(id);
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
                    throw Unavailable(id);
                }

                if (matches.Length > 0)
                {
                    var remote = matches[0];
                    // Producer names allow underscores/trailing hyphens; creation-form policy must not hide valid databases.
                    if (remote.ProjectId != project.Id || remote.CreatedAt == default || remote.Name is null || !ProducerName().IsMatch(remote.Name) ||
                        ReservedProducerName().IsMatch(remote.Name) || remote.BranchedFrom is { } source && (!TerraScaleIds.IsValid(source) || source == id) ||
                        remote.AllRegions && remote.Regions is not [_])
                    {
                        throw Unavailable(id);
                    }

                    var placement = await RemotePlacementAsync(remote, ct);
                    if (!listings.Any(listing => listing.Region == remote.Route && listing.Items?.Any(item => item.Id == id) == true) ||
                        matches.Any(item => item.ProjectId != remote.ProjectId || item.Name != remote.Name || item.CreatedAt != remote.CreatedAt ||
                            item.BranchedFrom != remote.BranchedFrom || item.Route != remote.Route || item.AllRegions != remote.AllRegions ||
                            item.Regions is null || !item.Regions.Order(StringComparer.Ordinal).SequenceEqual(remote.Regions!.Order(StringComparer.Ordinal))))
                    {
                        throw Unavailable(id);
                    }

                    imported = Record(remote, placement, "ready");
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                (DatabaseResults.KnownFailure(error) || error is TerraBaseException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException))
            { throw Unavailable(id); }
            // Only complete, successful regional pagination can establish absence.
            if (imported is null)
            {
                throw NotFound(id);
            }

            var resolved = imported;
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                var target = org.Projects.GetValueOrDefault(project.Id) ?? throw new AccountAccessException("Project is unavailable.", 404);
                if (target.Id != scope.ProjectId || target.OrganizationId != scope.OrganizationId)
                {
                    throw Unavailable(id);
                }

                if (target.Databases.TryGetValue(id, out var winner)) { resolved = Checked(winner); return; }
                if (target.Databases.Values.Any(item => item.Name == imported.Name))
                {
                    throw Unavailable(id);
                }

                target.Databases[id] = imported;
                resolved = imported;
            }, ct);
            // Do not publish uncommitted metadata into the request, or replace a concurrent winner's settings/revision.
            project.Databases[id] = resolved;
            return (context, resolved);

            DatabaseRecord Checked(DatabaseRecord record)
            {
                return record.Id == id && record.ProjectId == project.Id ? record : throw Unavailable(id);
            }
        }

        // Definitive absence, proven only by complete pagination: the database is not this project's.
        private static AccountAccessException NotFound(string id)
        {
            return new("Database " + id + " was not found in this project.", 404);
        }

        // Transient upstream states stay retryable and say so; the id was never the problem.
        private static AccountAccessException Unavailable(string id)
        {
            return new("Database " + id + " is temporarily unavailable; retry in a few seconds.", 503);
        }

        [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
        private static partial Regex ProducerName();
        [GeneratedRegex(@"\A(con|prn|aux|nul|com[0-9]|lpt[0-9])\z", RegexOptions.CultureInvariant)]
        private static partial Regex ReservedProducerName();
    }
}
