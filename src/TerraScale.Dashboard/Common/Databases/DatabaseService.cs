using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.Dashboard.Platform.Http;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Common.Databases
{
    public sealed partial class DatabaseService(AccountService accounts, IDatabaseBackend backend, TimeProvider time, ILogger<DatabaseService> logger)
    {
        private const int MaxRemembered = 10_000;
        private static readonly TimeSpan UnknownDatabaseMemory = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RememberedSyncLifetime = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, Task> syncing = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> synced = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> unknown = new(StringComparer.Ordinal);

        public static DatabaseScope Scope(AccountContext context)
        {
            return new(context.UserId, context.Session.AccessToken,
            context.Organization?.Id ?? throw new AccountAccessException("Select an organization.", 404), context.Organization.Name,
            context.Project?.Id ?? throw new AccountAccessException("Select a project.", 404), context.Project.Name)
            { Session = context.Session };
        }

        public static DatabaseRecord Database(AccountContext context, string id)
        {
            return context.Project?.Databases.GetValueOrDefault(id) is { } database && database.ProjectId == context.Project.Id
                ? database : PlatformDatabases.Databases.ContainsKey(id) ? throw new AccountAccessException("Platform admin access is required.")
                : throw new AccountAccessException(TerraScaleIds.IsValid(id) ? "Database " + id + " was not found in this project." : DatabaseIdRule, TerraScaleIds.IsValid(id) ? 404 : 422, DatabaseIdCode);
        }

        public const string DatabaseIdCode = "database_id";
        public const string DatabaseIdRule = "Pass the database ID (a ULID), not its name.";

        public async Task<DatabaseRegion> HomeAsync(AccountContext context, DatabaseRecord? database, CancellationToken cancellationToken)
        {
            var code = database?.Region ?? context.Project?.Region;
            return DatabaseRegion.Resolve(await backend.RegionsAsync(cancellationToken), code)
                ?? throw new HttpRequestException("This database's region is not a TerraBase region.");
        }

        public readonly union ConfiguredPlacement(IReadOnlyList<DatabaseRegion>, DatabaseInvalid);

        internal async Task<ConfiguredPlacement> ConfiguredPlacementAsync(IEnumerable<string> codes, CancellationToken cancellationToken)
        {
            var catalog = await backend.RegionsAsync(cancellationToken);
            var placement = new List<DatabaseRegion>();
            foreach (var code in codes.Where(code => !string.IsNullOrWhiteSpace(code)))
            {
                var region = DatabaseRegion.Resolve(catalog, code.Trim()) ?? ResolveRegion(catalog, code);
                if (!placement.Any(selected => selected.Id == region.Id && selected.Code == region.Code))
                {
                    placement.Add(region);
                }
            }
            if (placement.Count == 0)
            {
                return new DatabaseInvalid("Choose at least one region.");
            }

            return placement;
        }

        internal static DatabaseRegion ResolveRegion(IReadOnlyList<DatabaseRegion> catalog, string code)
        {
            var value = code.Trim();
            return catalog.FirstOrDefault(region => region.Code.Equals(value, StringComparison.OrdinalIgnoreCase) || region.Id == value)
                ?? throw new AccountAccessException("Choose regions listed by TerraScale discovery.", 422);
        }

        public async Task<DatabaseMutation> CreateAsync(AccountContext context, string name, string homeRegion, CancellationToken cancellationToken, decimal? ttlHours = null, IReadOnlyList<string>? replicaRegions = null, bool allRegions = false)
        {
            try
            {
                var ttlSeconds = DatabaseTtl.Seconds(ttlHours);
                name = ValidName(name);
                context = await CurrentAsync(context, cancellationToken);
                var scope = Scope(context);
                DemandProvision(context.Organization!, context.UserId);
                DemandName(context.Project!, name);
                DemandRoom(context.Organization!);
                var discovery = await backend.DiscoveryAsync(cancellationToken);
                var catalog = discovery.Regions;
                var home = ResolveRegion(catalog, homeRegion);
                var replicas = ReplicaIds(catalog, home, replicaRegions ?? []);
                ManagedDatabase remote;
                var recovered = false;
                try { remote = await backend.CreateAsync(scope, name, home, replicas, allRegions, cancellationToken, ttlSeconds); }
                catch (TerraBaseConflictException error) when (error.ErrorCode is "name_taken" or "database_limit")
                {
                    var listed = await backend.ListAsync(scope, home, cancellationToken);
                    var found = listed.FirstOrDefault(database => database.Name == name);
                    if (found is null)
                    {
                        return DatabaseMutations.MutationRefused(new DatabaseStateConflict(error.ErrorCode == "name_taken" ? "A database with this name already exists."
                            : listed.Count == 0 ? DatabaseResults.Message(error) : $"The organization's plan allows no more databases. This project already has {string.Join(", ", listed.Select(database => database.Name))}."));
                    }

                    remote = found;
                    recovered = true;
                }
                if (remote.ProjectId != scope.ProjectId || remote.Name != name || string.IsNullOrEmpty(remote.Id))
                {
                    throw new HttpRequestException("Database provisioning returned an invalid identity.");
                }

                if (recovered && discovery.Catalog.Count == 0 && remote.HomeRegion != home.Id)
                {
                    return DatabaseMutations.MutationRefused(new DatabaseStateConflict("A database with this name already exists in another region."));
                }

                if (ttlSeconds is not null && remote.ExpiresAt is null)
                    throw new HttpRequestException("Database provisioning did not confirm the requested TTL.");
                var placement = await RemotePlacementAsync(remote, cancellationToken);
                var expected = allRegions ? discovery.Catalog.Select(region => region.Id) : replicas.Prepend(home.Id);
                if (discovery.Catalog.Count > 0 && !placement.Select(region => region.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
                {
                    if (recovered) { return DatabaseMutations.MutationRefused(new DatabaseStateConflict("A database with this name already exists in other regions.")); }
                    throw new HttpRequestException("Database provisioning returned a different placement.");
                }

                if (recovered && (placement[0].Id != home.Id || (!allRegions && !placement.Select(region => region.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expected))))
                {
                    return DatabaseMutations.MutationRefused(new DatabaseStateConflict("A database with this name already exists with a different placement."));
                }

                if (discovery.Catalog.Count == 0 && !recovered && placement[0].Id != home.Id)
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
            catch (AccountAccessException error) when (DatabaseMutations.Mappable(error))
            {
                return DatabaseMutations.ToRefusal(error);
            }
        }

        public async Task<DatabaseMutation> BranchAsync(AccountContext context, string id, string name, CancellationToken cancellationToken, decimal? ttlHours = null)
        {
            try
            {
                var ttlSeconds = DatabaseTtl.Seconds(ttlHours);
                context = await CurrentAsync(context, cancellationToken);
                var source = Database(context, id);
                DemandProvision(context.Organization!, context.UserId);
                if (source.Status != "ready")
                {
                    return DatabaseMutations.MutationRefused(new DatabaseStateConflict("Only a ready database can be branched."));
                }

                name = ValidName(string.IsNullOrWhiteSpace(name) ? PreviewName(source.Name) : name);
                DemandName(context.Project!, name);
                DemandRoom(context.Organization!);
                var scope = Scope(context);
                var home = await HomeAsync(context, source, cancellationToken);
                ManagedDatabase remote;
                try { remote = await backend.BranchAsync(scope, home, id, name, cancellationToken, ttlSeconds); }
                catch (TerraBaseConflictException error) when (error.ErrorCode == "name_taken")
                {
                    var found = (await backend.ListAsync(scope, home, cancellationToken)).FirstOrDefault(database => database.Name == name && database.BranchedFrom == source.Id);
                    if (found is null) { return DatabaseMutations.MutationRefused(new DatabaseStateConflict("A database with this name already exists.")); }
                    remote = found;
                }
                if (remote.ProjectId != scope.ProjectId || remote.Name != name || string.IsNullOrEmpty(remote.Id) || remote.Id == source.Id || remote.BranchedFrom != source.Id)
                {
                    throw new HttpRequestException("Database branching returned an invalid identity.");
                }

                if (remote.HomeRegion != home.Id)
                {
                    return DatabaseMutations.MutationRefused(new DatabaseStateConflict("A branch with this name is in a different region."));
                }

                if (ttlSeconds is not null && remote.ExpiresAt is null)
                    throw new HttpRequestException("Database provisioning did not confirm the requested TTL.");
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
            catch (AccountAccessException error) when (DatabaseMutations.Mappable(error))
            {
                return DatabaseMutations.ToRefusal(error);
            }
        }

        public async Task<DatabaseMutation> SetTtlAsync(AccountContext context, string id, decimal? ttlHours, CancellationToken cancellationToken)
        {
            try
            {
                var ttlSeconds = DatabaseTtl.Seconds(ttlHours);
                context = await CurrentAsync(context, cancellationToken);
                var database = Database(context, id);
                if (!context.CanManage) { throw new AccountAccessException("Organization management access is required."); }
                if (PlatformDatabases.IsPlatformProject(context.Project!.Id)) { throw PlatformDatabases.ReadOnly(); }
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
            catch (AccountAccessException error) when (DatabaseMutations.Mappable(error))
            {
                return DatabaseMutations.ToRefusal(error);
            }
        }

        public async Task<DatabaseMutation> RenameAsync(AccountContext context, string id, string name, string confirmation, CancellationToken cancellationToken)
        {
            try
            {
                context = await CurrentAsync(context, cancellationToken);
                var database = Database(context, id);
                if (!context.CanManage)
                {
                    throw new AccountAccessException("Organization management access is required.");
                }

                if (database.Status != "ready")
                {
                    return DatabaseMutations.MutationRefused(new DatabaseStateConflict("Only a ready database can be renamed."));
                }

                if (confirmation != database.Name)
                {
                    return DatabaseMutations.MutationRefused(new DatabaseInvalid("Type the current database name to confirm the rename."));
                }

                name = ValidName(name);

                if (name == database.Name && database.ConflictOf is null)
                {
                    return DatabaseMutations.MutationRefused(new DatabaseInvalid("Choose a different database name."));
                }

                if (context.Project!.Databases.Values.Any(other => other.Id != id && other.Name == name))
                {
                    return DatabaseMutations.MutationRefused(new DatabaseStateConflict("A database with this name already exists."));
                }

                if (PlatformDatabases.IsPlatformProject(context.Project.Id))
                {
                    return DatabaseMutations.MutationRefused(new DatabaseStateConflict("Platform databases keep their names."));
                }

                var scope = Scope(context);
                (var remote, database, var home) = await RoutedAsync(context, database, async (current, route) => (await backend.RenameAsync(scope, route, id, name, cancellationToken), current, route), cancellationToken);
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
            catch (AccountAccessException error) when (DatabaseMutations.Mappable(error))
            {
                return DatabaseMutations.ToRefusal(error);
            }
        }

        public async Task<DatabaseMutation> PlaceAsync(AccountContext context, string id, IReadOnlyList<string> replicaRegions, bool allRegions, CancellationToken cancellationToken)
        {
            try
            {
                context = await CurrentAsync(context, cancellationToken);
                var database = DemandPlacement(context, id);
                var home = await HomeAsync(context, database, cancellationToken);
                var replicas = ReplicaIds(await backend.RegionsAsync(cancellationToken), home, replicaRegions);
                var remote = await backend.PlaceAsync(Scope(context), home, id, replicas, allRegions, cancellationToken);
                return await SavePlacementAsync(context, database, remote, "database.placement", cancellationToken);
            }
            catch (AccountAccessException error) when (DatabaseMutations.Mappable(error))
            {
                return DatabaseMutations.ToRefusal(error);
            }
        }

        public async Task<DatabaseMutation> MoveHomeAsync(AccountContext context, string id, string homeRegion, CancellationToken cancellationToken)
        {
            try
            {
                context = await CurrentAsync(context, cancellationToken);
                var database = DemandPlacement(context, id);
                var home = await HomeAsync(context, database, cancellationToken);
                var target = DatabaseRegion.Resolve(await backend.RegionsAsync(cancellationToken), homeRegion.Trim());
                if (target is null || target.Id == home.Id || !Regions(database).Skip(1).Any(code => code.Equals(target.Code, StringComparison.OrdinalIgnoreCase) || code == target.Id))
                {
                    return DatabaseMutations.MutationRefused(new DatabaseInvalid("Choose one of this database's regions."));
                }

                ManagedDatabase remote;
                try { remote = await backend.MoveHomeAsync(Scope(context), home, id, target.Id, cancellationToken); }
                catch (TerraBaseException error) when (error.ErrorCode is "move_in_progress" or "replica_not_ready" or "move_unresolved")
                {
                    var message = error.ErrorCode switch
                    {
                        "move_in_progress" => "Another region change of this database is running. Try again when it finishes.",
                        "replica_not_ready" => DatabaseFormat.Region(target.Id) + " was added and is still syncing. Save again in a moment to finish; until then " +
                            DatabaseFormat.Region(home.Id) + " keeps serving.",
                        _ => "The region change is still being settled, and writes are paused until it is. Try again."
                    };
                    return DatabaseMutations.MutationRefused(error.ErrorCode == "move_in_progress" ? new DatabaseStateConflict(message) : new DatabaseUnavailable(message));
                }
                if (remote.HomeRegion != target.Id)
                {
                    throw new HttpRequestException("The region change returned different regions. Reload to check them.");
                }

                return await SavePlacementAsync(context, database, remote, "database.home_moved", cancellationToken);
            }
            catch (AccountAccessException error) when (DatabaseMutations.Mappable(error))
            {
                return DatabaseMutations.ToRefusal(error);
            }
        }

        private static DatabaseRecord DemandPlacement(AccountContext context, string id)
        {
            var database = Database(context, id);
            if (!context.CanManage)
            {
                throw new AccountAccessException("Organization management access is required.");
            }

            if (database.Status != "ready")
            {
                throw new AccountAccessException("Only a ready database can change its placement.", 409);
            }

            if (PlatformDatabases.IsPlatformProject(database.ProjectId))
            {
                throw new AccountAccessException("Platform databases keep their placement.", 409);
            }

            return database;
        }

        private static string[] ReplicaIds(IReadOnlyList<DatabaseRegion> catalog, DatabaseRegion home, IEnumerable<string> codes)
        {
            var ids = codes.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => ResolveRegion(catalog, code).Id)
                .Where(regionId => regionId != home.Id).Distinct(StringComparer.Ordinal).ToArray();
            return ids.Length > 8 ? throw new AccountAccessException("Choose at most 9 regions.", 422) : ids;
        }

        private async Task<DatabaseRecord> SavePlacementAsync(AccountContext context, DatabaseRecord database, ManagedDatabase remote, string? audit, CancellationToken cancellationToken)
        {
            var scope = Scope(context);
            if (remote.Id != database.Id || remote.ProjectId != scope.ProjectId)
            {
                throw new HttpRequestException("The placement change returned an unexpected database identity.");
            }

            var record = Record(remote, await RemotePlacementAsync(remote, cancellationToken), database.Status);
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                if (!org.CanManage(context.UserId))
                {
                    throw new AccountAccessException("Organization management access is required.");
                }

                var current = org.Projects.GetValueOrDefault(scope.ProjectId)?.Databases.GetValueOrDefault(database.Id)
                    ?? throw new AccountAccessException("Database is unavailable.", 404);
                current.Region = record.Region;
                current.Regions = [.. record.Regions];
                current.AllRegions = record.AllRegions;
                current.Endpoint = record.Endpoint;
                current.PlacementRevision = checked(current.PlacementRevision + 1);
                if (audit is not null)
                {
                    org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, audit, current.Name, "database", accounts.Now));
                }
            }, cancellationToken);
            database.Region = record.Region;
            database.Regions = [.. record.Regions];
            database.AllRegions = record.AllRegions;
            database.Endpoint = record.Endpoint;
            return database;
        }

        public async Task<DatabaseDeletion> DeleteAsync(AccountContext context, string id, string confirmation, CancellationToken cancellationToken)
        {
            try
            {
                context = await CurrentAsync(context, cancellationToken);
                var database = Database(context, id);
                if (!context.CanManage)
                {
                    throw new AccountAccessException("Organization management access is required.");
                }

                if (confirmation != database.Name)
                {
                    return DatabaseMutations.DeletionRefused(new DatabaseInvalid("Type the database name to confirm deletion."));
                }

                var scope = Scope(context);
                database = await RoutedAsync(context, database, async (current, route) =>
                {
                    await backend.DeleteAsync(scope, route, id, cancellationToken);
                    return current;
                }, cancellationToken);
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
                return new Done();
            }
            catch (AccountAccessException error) when (DatabaseMutations.Mappable(error))
            {
                return DatabaseMutations.ToRefusal(error);
            }
        }

        public async Task SyncAsync(AccountContext context, CancellationToken cancellationToken)
        {
            if (context.Project is not { } project || PlatformDatabases.IsPlatformProject(project.Id))
            {
                return;
            }

            var key = context.UserId + ":" + project.Id;
            var owned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = syncing.GetOrAdd(key, owned.Task);
            if (running != owned.Task)
            {
                await running.WaitAsync(cancellationToken);
                return;
            }

            try
            {
                await SyncNowAsync(context, project, true, cancellationToken);
                Remember(synced, key, RememberedSyncLifetime);
            }
            finally
            {
                syncing.TryRemove(new(key, owned.Task));
                owned.SetResult();
            }
        }

        public Task SyncIfStaleAsync(AccountContext context, TimeSpan freshness, CancellationToken cancellationToken)
        {
            return context.Project is { } project && synced.TryGetValue(context.UserId + ":" + project.Id, out var at) && time.GetElapsedTime(at) < freshness
                ? Task.CompletedTask : SyncAsync(context, cancellationToken);
        }

        private void Remember(ConcurrentDictionary<string, long> memory, string key, TimeSpan lifetime)
        {
            if (memory.Count >= MaxRemembered)
            {
                foreach (var (stale, at) in memory)
                {
                    if (time.GetElapsedTime(at) >= lifetime)
                    {
                        memory.TryRemove(stale, out _);
                    }
                }
            }

            if (memory.Count < MaxRemembered || memory.ContainsKey(key))
            {
                memory[key] = time.GetTimestamp();
            }
        }

        public Task ViewRemoteAsync(AccountContext context, CancellationToken cancellationToken)
        {
            return context.Project is not { } project || PlatformDatabases.IsPlatformProject(project.Id)
                ? Task.CompletedTask : SyncNowAsync(context, project, false, cancellationToken);
        }

        private async Task SyncNowAsync(AccountContext context, ProjectRecord project, bool persist, CancellationToken cancellationToken)
        {
            try
            {
                var scope = Scope(context);
                var catalog = await backend.RegionsAsync(cancellationToken);
                var seen = project.Databases.Values.ToArray();
                var observed = seen.ToDictionary(record => record.Id, record =>
                    (record.Name, record.ProjectId, record.PreviousName, record.ConflictOf, record.ExpiresAt, record.Region, Regions: Regions(record).ToArray(), record.AllRegions, record.PlacementRevision));
                var homes = catalog;

                var listings = await Task.WhenAll(homes.Select(async home =>
                {
                    try { return (home, Listed: await backend.ListAsync(scope, home, cancellationToken)); }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is TerraBaseException or HttpRequestException or JsonException || DatabaseResults.KnownFailure(error)))
                    { return (home, Listed: (IReadOnlyList<ManagedDatabase>?)null); }
                }));
                var remote = listings.SelectMany(listing => (listing.Listed ?? []).Select(database => (Database: database, Home: listing.home.Id)))
                    .Where(item => item.Database.ProjectId == project.Id).GroupBy(item => item.Database.Id)
                    .Select(group => group.OrderByDescending(item => item.Database.HomeRegion == item.Home).First().Database).ToArray();
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
                        else if (record.Name == before.Name && record.ProjectId == before.ProjectId &&
                            (!record.Region.Equals(before.Region, StringComparison.OrdinalIgnoreCase) || record.ExpiresAt != before.ExpiresAt || record.AllRegions != before.AllRegions || !Regions(record).SequenceEqual(before.Regions, StringComparer.OrdinalIgnoreCase)))
                        {
                            refreshed.Add(record);
                        }
                    }
                    catch (Exception error) when (error is HttpRequestException or TerraBaseException)
                    {
                        logger.DatabaseSyncPlacementSkipped(database.Id, error.GetType().Name);
                    }
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
                            current.Region = record.Region;
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
                if (!persist || context.Organization is not { } owner || !owner.ContainsMember(context.UserId))
                {
                    Apply(project);
                    return;
                }
                ProjectRecord? saved = null;
                await accounts.UpdateOrganizationAsync(context, org =>
                {
                    if (org.Projects.GetValueOrDefault(project.Id) is { } stored) { Apply(stored); saved = stored; }
                }, cancellationToken);
                if (saved is not null)
                {
                    project.Databases = saved.Databases;
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is TerraBaseException or HttpRequestException or JsonException || DatabaseResults.KnownFailure(error)))
            {
                logger.DatabaseSyncSkipped(project.Id, error.GetType().Name);
            }
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

        public const string NameInputPattern = @"(?!(?:con|prn|aux|nul|com[0-9]|lpt[0-9])$)[a-z][a-z0-9_\-]{0,62}";
        public const string NameHint = "Database names must start with a lowercase letter and use 1–63 lowercase letters, digits, underscores or hyphens. Reserved names: con, prn, aux, nul, com0–com9, lpt0–lpt9.";

        public static string PreviewName(string name)
        {
            return name[..Math.Min(name.Length, 48)].TrimEnd('-') + "-preview-branch";
        }

        public static string Path(string id, string tab = "overview")
        {
            return "/databases/" + Uri.EscapeDataString(id) + (tab == "overview" ? "" : "/" + tab);
        }

        public static IReadOnlyList<string> Regions(DatabaseRecord record)
        {
            return record.Regions.Count > 0 ? record.Regions : string.IsNullOrEmpty(record.Region) ? [] : [record.Region];
        }

        public static IReadOnlyList<string> ServingRegions(DatabaseRecord record)
        {
            return Regions(record);
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

            string[] ids = remote.Regions is { } regions ? [remote.HomeRegion, .. regions.Where(region => region != remote.HomeRegion)]
                : [remote.HomeRegion, .. remote.ReplicaRegions];
            if (remote.Regions is { } canonical && (canonical.Count != ids.Length || !canonical.Contains(remote.HomeRegion, StringComparer.Ordinal)))
            {
                throw new HttpRequestException("The database service returned ambiguous placement.");
            }
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
