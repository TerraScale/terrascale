using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.Common.Databases
{
    public sealed partial class DatabaseService(AccountService accounts, IDatabaseBackend backend)
    {
        public static DatabaseScope Scope(AccountContext context)
        {
            return new(context.UserId, context.Session.AccessToken,
            context.Organization?.Id ?? throw new AccountAccessException("Select an organization.", 404), context.Organization.Name,
            context.Project?.Id ?? throw new AccountAccessException("Select a project.", 404), context.Project.Name)
            { Session = context.Session };
        }

        public static DatabaseRecord Database(AccountContext context, string id)
        {
            // Caller mistakes are 422; an unresolvable ID is a 404 naming it. Never blame the id format for a miss.
            return context.Project?.Databases.GetValueOrDefault(id) is { } database && database.ProjectId == context.Project.Id
                ? database : PlatformDatabases.Databases.ContainsKey(id) ? throw new AccountAccessException("Platform admin access is required.")
                : throw new AccountAccessException(TerraScaleIds.IsValid(id) ? "Database " + id + " was not found in this project." : DatabaseIdRule, TerraScaleIds.IsValid(id) ? 404 : 422, DatabaseIdCode);
        }

        public const string DatabaseIdCode = "database_id";
        public const string DatabaseIdRule = "Pass the database ID (a ULID), not its name.";

        public static void ValidateContents(IReadOnlyList<CollectionStats> contents)
        {
            if (contents is null || contents.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name) || item.Documents < 0 || item.Bytes < 0) ||
                contents.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != contents.Count)
            {
                throw new HttpRequestException("The data plane returned ambiguous database counts.");
            }

            _ = contents.Sum(item => item.Documents);
            _ = contents.Sum(item => item.Bytes);
        }

        public async Task<DatabaseRegion> HomeAsync(AccountContext context, DatabaseRecord? database, CancellationToken cancellationToken)
        {
            var code = database?.Region ?? context.Project?.Region;
            return DatabaseRegion.Resolve(await backend.RegionsAsync(cancellationToken), code)
                ?? throw new HttpRequestException("This database's home region is not a TerraBase region.");
        }

        internal async Task<IReadOnlyList<DatabaseRegion>> ConfiguredPlacementAsync(IEnumerable<string> codes, CancellationToken cancellationToken)
        {
            var catalog = await backend.RegionsAsync(cancellationToken);
            var placement = new List<DatabaseRegion>();
            foreach (var code in codes.Where(code => !string.IsNullOrWhiteSpace(code)))
            {
                // Stored regions keep their own region when the catalog does not list them.
                var region = DatabaseRegion.Resolve(catalog, code.Trim()) ?? ResolveRegion(catalog, code);
                if (!placement.Any(selected => selected.Id == region.Id && selected.Code == region.Code))
                {
                    placement.Add(region);
                }
            }
            if (placement.Count == 0)
            {
                throw new AccountAccessException("Choose at least one region.", 422);
            }

            return placement;
        }

        private static DatabaseRegion ResolveRegion(IReadOnlyList<DatabaseRegion> catalog, string code)
        {
            var value = code.Trim();
            return catalog.FirstOrDefault(region => region.Code.Equals(value, StringComparison.OrdinalIgnoreCase) || region.Id == value)
                ?? throw new AccountAccessException("Choose regions listed by TerraScale discovery.", 422);
        }

        // TerraBase serves a database only in its home region until replicas are available, so a create names only that region.
        public async Task<DatabaseRecord> CreateAsync(AccountContext context, string name, string homeRegion, CancellationToken cancellationToken, decimal? ttlHours = null)
        {
            var ttlSeconds = DatabaseTtl.Seconds(ttlHours);
            name = ValidName(name);
            context = await CurrentAsync(context, cancellationToken);
            var scope = Scope(context);
            DemandProvision(context.Organization!, context.UserId);
            DemandName(context.Project!, name);
            DemandRoom(context.Organization!);
            var home = ResolveRegion(await backend.RegionsAsync(cancellationToken), homeRegion);
            ManagedDatabase remote;
            var recovered = false;
            try { remote = await backend.CreateAsync(scope, name, home, cancellationToken, ttlSeconds); }
            catch (TerraBaseConflictException error) when (error.ErrorCode == "name_taken")
            {
                // A prior provision whose dashboard commit failed is recovered by name. Other conflicts (database_limit, a
                // plan allowance held in an unreachable region) reach the caller with TerraBase's own code and message.
                remote = (await backend.ListAsync(scope, home, cancellationToken)).FirstOrDefault(database => database.Name == name)
                    ?? throw new AccountAccessException("A database with this name already exists.", 409);
                recovered = true;
            }
            if (remote.ProjectId != scope.ProjectId || remote.Name != name || string.IsNullOrEmpty(remote.Id))
            {
                throw new HttpRequestException("Database provisioning returned an invalid identity.");
            }

            if (recovered && remote.HomeRegion != home.Id)
            {
                throw new AccountAccessException("A database with this name already exists in another home region.", 409);
            }

            var placement = await RemotePlacementAsync(remote, cancellationToken);
            if (!recovered && placement[0].Id != home.Id)
            {
                throw new HttpRequestException("Database provisioning returned a different placement.");
            }

            var record = Record(remote, placement, "ready");
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                DemandProvision(org, context.UserId);
                var project = org.Projects.GetValueOrDefault(scope.ProjectId) ?? throw new AccountAccessException("Project is unavailable.", 404);
                if (project.Databases.ContainsKey(record.Id))
                {
                    return;
                }

                DemandName(project, name);
                DemandRoom(org);
                project.Databases[record.Id] = record;
                org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "database.created", record.Name, "database", accounts.Now));
            }, cancellationToken);
            return record;
        }

        public async Task<DatabaseRecord> BranchAsync(AccountContext context, string id, string name, CancellationToken cancellationToken, decimal? ttlHours = null, string? pointInTime = null)
        {
            var ttlSeconds = DatabaseTtl.Seconds(ttlHours);
            var instant = DatabaseTtl.Instant(pointInTime);
            context = await CurrentAsync(context, cancellationToken);
            var source = Database(context, id);
            DemandProvision(context.Organization!, context.UserId);
            if (source.Status != "ready")
            {
                throw new AccountAccessException("Only a ready database can be branched.", 409);
            }

            name = ValidName(string.IsNullOrWhiteSpace(name) ? PreviewName(source.Name) : name);
            DemandName(context.Project!, name);
            DemandRoom(context.Organization!);
            var scope = Scope(context);
            var home = await HomeAsync(context, source, cancellationToken);
            ManagedDatabase remote;
            try { remote = await backend.BranchAsync(scope, home, id, name, cancellationToken, ttlSeconds, instant); }
            catch (TerraBaseConflictException error) when (error.ErrorCode == "name_taken" && instant is null)
            {
                remote = (await backend.ListAsync(scope, home, cancellationToken)).FirstOrDefault(database => database.Name == name && database.BranchedFrom == source.Id)
                    ?? throw new AccountAccessException("A database with this name already exists.", 409);
            }
            if (remote.ProjectId != scope.ProjectId || remote.Name != name || string.IsNullOrEmpty(remote.Id) || remote.Id == source.Id || remote.BranchedFrom != source.Id)
            {
                throw new HttpRequestException("Database branching returned an invalid identity.");
            }

            if (remote.HomeRegion != home.Id)
            {
                throw new AccountAccessException("A branch with this name has a different home region.", 409);
            }

            var placement = await RemotePlacementAsync(remote, cancellationToken);
            var record = Record(remote, placement, "ready");
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                DemandProvision(org, context.UserId);
                var project = org.Projects.GetValueOrDefault(scope.ProjectId) ?? throw new AccountAccessException("Project is unavailable.", 404);
                var currentSource = project.Databases.GetValueOrDefault(id);
                if (currentSource is null || currentSource.Id != source.Id || currentSource.ProjectId != scope.ProjectId)
                {
                    throw new AccountAccessException("Source database is unavailable.", 404);
                }

                if (currentSource.Status != "ready")
                {
                    throw new AccountAccessException("Only a ready database can be branched.", 409);
                }

                if (project.Databases.ContainsKey(record.Id))
                {
                    return;
                }

                DemandName(project, name);
                DemandRoom(org);
                project.Databases[record.Id] = record;
            }, cancellationToken);
            return record;
        }

        public async Task<DatabaseRecord> SetTtlAsync(AccountContext context, string id, decimal? ttlHours, CancellationToken cancellationToken)
        {
            var ttlSeconds = DatabaseTtl.Seconds(ttlHours);
            context = await CurrentAsync(context, cancellationToken);
            var database = Database(context, id);
            if (!context.CanManage) { throw new AccountAccessException("Organization management access is required."); }
            if (PlatformDatabases.IsPlatform(context.Organization!.Id)) { throw PlatformDatabases.ReadOnly(); }
            var remote = await backend.SetTtlAsync(Scope(context), await HomeAsync(context, database, cancellationToken), id, ttlSeconds, cancellationToken);
            if (remote.Id != id || remote.ProjectId != context.Project!.Id || remote.Name != database.Name ||
                ttlSeconds is null && remote.ExpiresAt is not null || ttlSeconds is not null && remote.ExpiresAt is null)
            {
                throw new HttpRequestException("The database expiry could not be confirmed. Reload Settings before retrying.");
            }
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                if (!org.CanManage(context.UserId)) { throw new AccountAccessException("Organization management access is required."); }
                var current = org.Projects.GetValueOrDefault(context.Project.Id)?.Databases.GetValueOrDefault(id)
                    ?? throw new AccountAccessException("Database is unavailable.", 404);
                current.ExpiresAt = remote.ExpiresAt;
                current.PlacementRevision = checked(current.PlacementRevision + 1);
            }, cancellationToken);
            database.ExpiresAt = remote.ExpiresAt;
            return database;
        }

        public async Task<DatabaseRecord> RenameAsync(AccountContext context, string id, string name, string confirmation, CancellationToken cancellationToken)
        {
            context = await CurrentAsync(context, cancellationToken);
            var database = Database(context, id);
            if (!context.CanManage)
            {
                throw new AccountAccessException("Organization management access is required.");
            }

            if (database.Status != "ready")
            {
                throw new AccountAccessException("Only a ready database can be renamed.", 409);
            }

            if (confirmation != database.Name)
            {
                throw new AccountAccessException("Type the current database name to confirm the rename.", 422);
            }

            if (!TerraScale.Dashboard.Features.Databases.DatabaseRenameName.IsValid(name))
            {
                throw new AccountAccessException("Start with a lowercase letter, then use lowercase letters, digits, underscores or hyphens; at most 63 characters. Device names are reserved.", 422);
            }

            if (name == database.Name && database.ConflictOf is null)
            {
                throw new AccountAccessException("Choose a different database name.", 422);
            }

            if (context.Project!.Databases.Values.Any(other => other.Id != id && other.Name == name))
            {
                throw new AccountAccessException("A database with this name already exists.", 409);
            }

            if (PlatformDatabases.IsPlatform(context.Organization?.Id))
            {
                throw new AccountAccessException("Platform databases keep their names.", 409);
            }

            var scope = Scope(context);
            var home = await HomeAsync(context, database, cancellationToken);
            var remote = await backend.RenameAsync(scope, home, id, name, cancellationToken);
            var placement = await RemotePlacementAsync(remote, cancellationToken);
            if (remote.Id != id || remote.ProjectId != scope.ProjectId || remote.Name != name || remote.HomeRegion != home.Id ||
                remote.CreatedAt != database.CreatedAt || remote.BranchedFrom != database.BranchedFrom || remote.AllRegions != database.AllRegions ||
                !placement.Select(region => region.Code).Order(StringComparer.Ordinal).SequenceEqual(Regions(database).Order(StringComparer.Ordinal)))
            {
                throw new HttpRequestException("The rename returned an unexpected database identity or placement. Reload Settings to check its current name.");
            }

            await accounts.UpdateOrganizationAsync(context, org =>
            {
                if (!org.CanManage(context.UserId))
                {
                    throw new AccountAccessException("Organization management access is required.");
                }

                var current = org.Projects.GetValueOrDefault(scope.ProjectId)?.Databases.GetValueOrDefault(id)
                    ?? throw new AccountAccessException("Database is unavailable.", 404);
                current.Name = name;
                current.PreviousName = remote.PreviousName;
                current.ConflictOf = remote.ConflictOf;
                org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "database.renamed", name, "database", accounts.Now));
            }, cancellationToken);
            database.Name = name;
            database.PreviousName = remote.PreviousName;
            database.ConflictOf = remote.ConflictOf;
            return database;
        }

        public async Task DeleteAsync(AccountContext context, string id, string confirmation, CancellationToken cancellationToken)
        {
            context = await CurrentAsync(context, cancellationToken);
            var database = Database(context, id);
            if (!context.CanManage)
            {
                throw new AccountAccessException("Organization management access is required.");
            }

            if (confirmation != database.Name)
            {
                throw new AccountAccessException("Type the database name to confirm deletion.", 422);
            }

            var scope = Scope(context);
            await backend.DeleteAsync(scope, await HomeAsync(context, database, cancellationToken), id, cancellationToken);
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                if (!org.CanManage(context.UserId))
                {
                    throw new AccountAccessException("Organization management access is required.");
                }

                var project = org.Projects.GetValueOrDefault(scope.ProjectId) ?? throw new AccountAccessException("Project is unavailable.", 404);
                if (project.Databases.Remove(id))
                {
                    org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "database.deleted", database.Name, "database", accounts.Now));
                }
            }, cancellationToken);
        }

        // TerraBase owns databases; the project's records follow its listings. A database created outside the dashboard (the
        // CLI, an SDK) gets a record, so it is listed and counts toward the plan, and a record whose database its home region
        // no longer lists is dropped. A region that does not answer leaves its records as they are; the list still renders.
        public async Task SyncAsync(AccountContext context, CancellationToken cancellationToken)
        {
            if (context.Project is not { } project || PlatformDatabases.IsPlatform(context.Organization?.Id))
            {
                return;
            }

            try
            {
                var scope = Scope(context);
                var catalog = await backend.RegionsAsync(cancellationToken);
                var seen = project.Databases.Values.ToArray();
                // Copy placement before awaiting the provider; mutable record references are not a concurrency snapshot.
                var observed = seen.ToDictionary(record => record.Id, record =>
                    (record.Name, record.ProjectId, record.PreviousName, record.ConflictOf, record.ExpiresAt, record.Region, Regions: Regions(record).ToArray(), record.AllRegions, record.PlacementRevision));
                // TerraBase homes a database in whichever region accepted its creation, whatever created it: a project's
                // preferred region is only a preference, and a project made outside the dashboard has none. Listing every
                // serving region is the complete catalog; the regions answer in parallel and converge on the same list.
                var homes = catalog;

                var listings = await Task.WhenAll(homes.Select(async home =>
                {
                    try { return (home, Listed: await backend.ListAsync(scope, home, cancellationToken)); }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is TerraBaseException or HttpRequestException or JsonException || DatabaseResults.KnownFailure(error)))
                    { return (home, Listed: (IReadOnlyList<ManagedDatabase>?)null); }
                }));
                var remote = listings.SelectMany(listing => listing.Listed ?? []).Where(database => database.ProjectId == project.Id).DistinctBy(database => database.Id).ToArray();
                var answered = listings.Where(listing => listing.Listed is not null).Select(listing => listing.home.Id).ToHashSet(StringComparer.Ordinal);
                var gone = seen.Where(record => DatabaseRegion.Resolve(catalog, record.Region) is { } home && answered.Contains(home.Id) &&
                    remote.All(database => database.Id != record.Id)).Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
                var imported = new List<DatabaseRecord>();
                var refreshed = new List<DatabaseRecord>();
                foreach (var database in remote)
                {
                    try
                    {
                        var record = Record(database, await RemotePlacementAsync(database, cancellationToken, catalog), "ready");
                        if (!observed.TryGetValue(record.Id, out var before))
                        {
                            imported.Add(record);
                        }
                        // Existing identity and immutable home must match; refresh configuration, never inferred health/status.
                        else if (record.Name == before.Name && record.ProjectId == before.ProjectId && record.Region.Equals(before.Region, StringComparison.OrdinalIgnoreCase) &&
                            (record.ExpiresAt != before.ExpiresAt || record.AllRegions != before.AllRegions || !Regions(record).SequenceEqual(before.Regions, StringComparer.OrdinalIgnoreCase)))
                        {
                            refreshed.Add(record);
                        }
                    }
                    catch (Exception error) when (error is HttpRequestException or TerraBaseException) { }
                }
                var renamed = remote.Where(database => observed.TryGetValue(database.Id, out var before) &&
                    (before.Name != database.Name || before.PreviousName != database.PreviousName || before.ConflictOf != database.ConflictOf)).ToArray();
                if (gone.Count == 0 && imported.Count == 0 && refreshed.Count == 0 && renamed.Length == 0)
                {
                    return;
                }

                bool Unchanged(DatabaseRecord record)
                {
                    return observed.TryGetValue(record.Id, out var before) && record.Name == before.Name && record.ProjectId == before.ProjectId &&
                    record.PreviousName == before.PreviousName && record.ConflictOf == before.ConflictOf && record.ExpiresAt == before.ExpiresAt &&
                    record.PlacementRevision == before.PlacementRevision && record.Region == before.Region && record.AllRegions == before.AllRegions && Regions(record).SequenceEqual(before.Regions, StringComparer.OrdinalIgnoreCase);
                }

                void Apply(ProjectRecord target)
                {
                    // Rechecked on every storage CAS attempt: a newer dashboard placement must survive an older listing.
                    foreach (var id in gone)
                    {
                        if (target.Databases.TryGetValue(id, out var current) && Unchanged(current))
                        {
                            target.Databases.Remove(id);
                        }
                    }

                    foreach (var record in refreshed)
                    {
                        if (target.Databases.TryGetValue(record.Id, out var current) && Unchanged(current))
                        {
                            current.ExpiresAt = record.ExpiresAt;
                            current.Regions = [.. record.Regions];
                            current.AllRegions = record.AllRegions;
                            current.Endpoint = record.Endpoint;
                            current.PlacementRevision = checked(current.PlacementRevision + 1);
                        }
                    }

                    foreach (var database in renamed)
                    {
                        if (target.Databases.GetValueOrDefault(database.Id) is { } current && current.Name == observed[database.Id].Name &&
                            current.PreviousName == observed[database.Id].PreviousName && current.ConflictOf == observed[database.Id].ConflictOf)
                        {
                            current.Name = database.Name;
                            current.PreviousName = database.PreviousName;
                            current.ConflictOf = database.ConflictOf;
                        }
                    }

                    foreach (var record in imported.Where(record => !target.Databases.ContainsKey(record.Id) && target.Databases.Values.All(database => database.Name != record.Name)))
                    {
                        target.Databases[record.Id] = record;
                    }
                }
                if (!context.Organization!.ContainsMember(context.UserId))
                {
                    Apply(project);
                    return;
                }
                ProjectRecord? saved = null;
                await accounts.UpdateOrganizationAsync(context, org =>
                {
                    if (org.Projects.GetValueOrDefault(project.Id) is { } stored) { Apply(stored); saved = stored; }
                }, cancellationToken);
                // The response follows the successful CAS too, including mutations that made this sync skip a stale update.
                if (saved is not null)
                {
                    project.Databases = saved.Databases;
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is TerraBaseException or HttpRequestException or JsonException || DatabaseResults.KnownFailure(error))) { }
        }

        public static string ValidName(string name)
        {
            name = name.Trim();
            if (!NamePattern().IsMatch(name))
            {
                throw new AccountAccessException(NameHint, 422);
            }

            return name;
        }

        public const string NameInputPattern = @"(?!(?:con|prn|aux|nul|com[0-9]|lpt[0-9])$)[a-z](?:[a-z0-9\-]{0,61}[a-z0-9])?";
        public const string NameHint = "Database names must start with a lowercase letter and use 1–63 lowercase letters, digits, and inner hyphens. Reserved names: con, prn, aux, nul, com0–com9, lpt0–lpt9.";

        public static string PreviewName(string name)
        {
            return name[..Math.Min(name.Length, 48)].TrimEnd('-') + "-preview-branch";
        }

        public static string Path(string id, string tab = "overview", string collection = "")
        {
            return "/databases/" + Uri.EscapeDataString(id) + (tab == "overview" ? "" : "/" + tab) + (collection.Length == 0 ? "" : "?collection=" + Uri.EscapeDataString(collection));
        }

        public static IReadOnlyList<string> Regions(DatabaseRecord record)
        {
            return record.Regions.Count > 0 ? record.Regions : string.IsNullOrEmpty(record.Region) ? [] : [record.Region];
        }

        public static IReadOnlyList<string> ServingRegions(DatabaseRecord record, IReadOnlyList<DatabaseRegion> catalog)
        {
            return record.AllRegions
                ? [.. Regions(record).Concat(catalog.Select(region => region.Code)).Distinct(StringComparer.OrdinalIgnoreCase)] : Regions(record);
        }

        internal static void DemandName(ProjectRecord project, string name)
        {
            if (project.Databases.Values.Any(database => database.Name == name))
            {
                throw new AccountAccessException("A database with this name already exists.", 409);
            }
        }
        internal static void DemandProvision(OrganizationRecord org, string userId)
        {
            if (!org.CanProvision(userId))
            {
                throw new AccountAccessException("Provisioning access and active billing are required.");
            }
        }
        public static int DatabaseLimit(OrganizationRecord org)
        {
            return org.InternalBilling is not null
            ? PlanCatalog.ResourceLimits(org).Databases is > 0 and var limit ? limit : int.MaxValue
            : PlanCatalog.For(org)?.Resources.Databases
                ?? throw new AccountAccessException("The organization plan is unavailable.", 503);
        }

        internal static void DemandRoom(OrganizationRecord org)
        {
            if (org.Projects.Values.Sum(project => project.Databases.Count) >= DatabaseLimit(org))
            {
                throw new AccountAccessException("Your organization has reached its plan's database limit. Upgrade the plan or delete a database.", 409);
            }
        }
        internal Task<AccountContext> CurrentAsync(AccountContext context, CancellationToken cancellationToken)
        {
            return AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
        }

        internal static DatabaseRecord Record(ManagedDatabase remote, IReadOnlyList<DatabaseRegion> placement, string status)
        {
            return new()
            {
                Id = remote.Id,
                ProjectId = remote.ProjectId,
                Name = remote.Name,
                PreviousName = remote.PreviousName,
                ConflictOf = remote.ConflictOf,
                CreatedAt = remote.CreatedAt,
                ExpiresAt = remote.ExpiresAt,
                BranchedFrom = remote.BranchedFrom,
                Region = placement[0].Code,
                Regions = [.. placement.Select(region => region.Code)],
                AllRegions = remote.AllRegions,
                Endpoint = placement[0].Api.ToString(),
                Status = status
            };
        }

        internal async Task<IReadOnlyList<DatabaseRegion>> RemotePlacementAsync(ManagedDatabase remote, CancellationToken cancellationToken, IReadOnlyList<DatabaseRegion>? knownCatalog = null)
        {
            if (string.IsNullOrWhiteSpace(remote.HomeRegion) || remote.ReplicaRegions is null ||
                remote.ReplicaRegions.Any(string.IsNullOrWhiteSpace))
            {
                throw new HttpRequestException("The database service did not return its canonical placement.");
            }

            var ids = remote.ReplicaRegions.Prepend(remote.HomeRegion).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            {
                throw new HttpRequestException("The database service returned ambiguous placement.");
            }

            var catalog = knownCatalog ?? await backend.RegionsAsync(cancellationToken);
            return [.. ids.Select(id => (catalog.SingleOrDefault(region => region.Id == id) ?? DatabaseRegion.FromId(id))
                ?? throw new HttpRequestException("The database service returned an invalid region."))];
        }

        [GeneratedRegex(@"\A" + NameInputPattern + @"\z", RegexOptions.CultureInvariant)]
        private static partial Regex NamePattern();
    }
}
