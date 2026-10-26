using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Shared.Databases;

public sealed partial class DatabaseService(AccountService accounts, IDatabaseBackend backend)
{
    public const string MinimumRegionsMessage = "Choose at least two regions, or select All regions to include current and future regions.";
    public const string HomePlacementMessage = "Select the home region in placement, or choose a home region from the selected regions.";
    public static void RequireReplicas(IEnumerable<string> regions)
    {
        if (regions.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            throw new AccountAccessException(MinimumRegionsMessage, 422);
    }

    public static DatabaseScope Scope(AccountContext context) => new(context.UserId, context.Session.AccessToken,
        context.Organization?.Id ?? throw new AccountAccessException("Select an organization.", 404), context.Organization.Name,
        context.Project?.Id ?? throw new AccountAccessException("Select a project.", 404), context.Project.Name)
    { Session = context.Session };

    public static DatabaseRecord Database(AccountContext context, string id) =>
        context.Project?.Databases.GetValueOrDefault(id) is { } database && database.ProjectId == context.Project.Id
            ? database : PlatformDatabases.Databases.ContainsKey(id) ? throw new AccountAccessException("Platform admin access is required.")
            : throw new AccountAccessException("Database is unavailable. Pass the database ID (a ULID), not its name.", 404);

    public static void ValidateContents(IReadOnlyList<CollectionStats> contents)
    {
        if (contents is null || contents.Any(item => item is null || string.IsNullOrWhiteSpace(item.Name) || item.Documents < 0 || item.Bytes < 0) ||
            contents.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != contents.Count)
            throw new HttpRequestException("The data plane returned ambiguous database counts.");
        _ = contents.Sum(item => item.Documents);
        _ = contents.Sum(item => item.Bytes);
    }

    public async Task<DatabaseRegion> HomeAsync(AccountContext context, DatabaseRecord? database, CancellationToken cancellationToken)
    {
        var code = database?.Region ?? context.Project?.Region;
        return DatabaseRegion.Resolve(await backend.RegionsAsync(cancellationToken), code)
            ?? throw new HttpRequestException("This database's home region is not a TerraBase region.");
    }

    // kept: the database's current regions, which stay choosable though the catalog does not list them.
    public async Task<IReadOnlyList<DatabaseRegion>> PlacementAsync(IEnumerable<string> codes, CancellationToken cancellationToken, IReadOnlyList<string>? kept = null)
    {
        var catalog = await backend.RegionsAsync(cancellationToken);
        var placement = new List<DatabaseRegion>();
        foreach (var code in codes)
        {
            var region = kept?.Contains(code.Trim(), StringComparer.OrdinalIgnoreCase) == true
                ? DatabaseRegion.Resolve(catalog, code.Trim()) ?? ResolveRegion(catalog, code) : ResolveRegion(catalog, code);
            if (placement.Any(selected => selected.Id.Length > 0 && selected.Id == region.Id || selected.Code.Equals(region.Code, StringComparison.OrdinalIgnoreCase)))
                throw new AccountAccessException("Choose each region once.", 422);
            placement.Add(region);
        }
        if (placement.Count == 0) throw new AccountAccessException("Choose at least one region.", 422);
        return placement;
    }

    internal async Task<IReadOnlyList<DatabaseRegion>> ConfiguredPlacementAsync(IEnumerable<string> codes, CancellationToken cancellationToken)
    {
        var catalog = await backend.RegionsAsync(cancellationToken);
        var placement = new List<DatabaseRegion>();
        foreach (var code in codes.Where(code => !string.IsNullOrWhiteSpace(code)))
        {
            // Stored regions keep their own region when the catalog does not list them.
            var region = DatabaseRegion.Resolve(catalog, code.Trim()) ?? ResolveRegion(catalog, code);
            if (!placement.Any(selected => selected.Id == region.Id && selected.Code == region.Code)) placement.Add(region);
        }
        if (placement.Count == 0) throw new AccountAccessException("Choose at least one region.", 422);
        return placement;
    }

    private static DatabaseRegion ResolveRegion(IReadOnlyList<DatabaseRegion> catalog, string code)
    {
        var value = code.Trim();
        return catalog.FirstOrDefault(region => region.Code.Equals(value, StringComparison.OrdinalIgnoreCase) || region.Id == value)
            ?? throw new AccountAccessException("Choose regions listed by TerraScale discovery.", 422);
    }

    public async Task<DatabaseRecord> CreateAsync(AccountContext context, string name, IEnumerable<string> regions, bool allRegions, CancellationToken cancellationToken)
    {
        name = ValidName(name);
        context = await CurrentAsync(context, cancellationToken);
        var scope = Scope(context);
        DemandProvision(context.Organization!, context.UserId);
        DemandName(context.Project!, name);
        DemandRoom(context.Organization!);
        var placement = await PlacementAsync(AllRegionsPlacement(regions, allRegions, context.Project!.Region), cancellationToken);
        ManagedDatabase remote;
        var recovered = false;
        try { remote = await backend.CreateAsync(scope, name, placement, allRegions, cancellationToken); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // A prior provision whose dashboard commit failed is recovered by name.
            remote = (await backend.ListAsync(scope, placement[0], cancellationToken)).FirstOrDefault(database => database.Name == name)
                ?? throw new AccountAccessException("A database with this name already exists.", 409);
            recovered = true;
        }
        if (remote.ProjectId != scope.ProjectId || remote.Name != name || string.IsNullOrEmpty(remote.Id)) throw new HttpRequestException("Database provisioning returned an invalid identity.");
        if (recovered && remote.HomeRegion != placement[0].Id)
            throw new AccountAccessException("A database with this name already exists in another home region.", 409);
        var actualPlacement = await RemotePlacementAsync(remote, cancellationToken);
        // Only the home chosen: TerraBase picked the replicas (every other serving region), so any it returns is the placement.
        if (!recovered && (actualPlacement[0].Id != placement[0].Id || (placement.Count > 1 && !SameReplicas(actualPlacement, placement)) || remote.AllRegions != allRegions))
            throw new HttpRequestException("Database provisioning returned a different placement.");
        placement = actualPlacement;
        var record = Record(remote, placement, "ready");
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            DemandProvision(org, context.UserId);
            var project = org.Projects.GetValueOrDefault(scope.ProjectId) ?? throw new AccountAccessException("Project is unavailable.", 404);
            if (project.Databases.ContainsKey(record.Id)) return;
            DemandName(project, name);
            DemandRoom(org);
            project.Databases[record.Id] = record;
            org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "database.created", record.Name, "database", accounts.Now));
        }, cancellationToken);
        return record;
    }

    public async Task<DatabaseRecord> BranchAsync(AccountContext context, string id, string name, CancellationToken cancellationToken)
    {
        context = await CurrentAsync(context, cancellationToken);
        var source = Database(context, id);
        DemandProvision(context.Organization!, context.UserId);
        if (source.Status != "ready") throw new AccountAccessException("Only a ready database can be branched.", 409);
        name = ValidName(string.IsNullOrWhiteSpace(name) ? PreviewName(source.Name) : name);
        DemandName(context.Project!, name);
        DemandRoom(context.Organization!);
        var scope = Scope(context);
        var home = await HomeAsync(context, source, cancellationToken);
        ManagedDatabase remote;
        try { remote = await backend.BranchAsync(scope, home, id, name, cancellationToken); }
        catch (TerraBaseConflictException error) when (error.ErrorCode == "name_taken")
        {
            remote = (await backend.ListAsync(scope, home, cancellationToken)).FirstOrDefault(database => database.Name == name && database.BranchedFrom == source.Id)
                ?? throw new AccountAccessException("A database with this name already exists.", 409);
        }
        if (remote.ProjectId != scope.ProjectId || remote.Name != name || string.IsNullOrEmpty(remote.Id) || remote.Id == source.Id || remote.BranchedFrom != source.Id)
            throw new HttpRequestException("Database branching returned an invalid identity.");
        if (remote.HomeRegion != home.Id) throw new AccountAccessException("A branch with this name has a different home region.", 409);
        var placement = await RemotePlacementAsync(remote, cancellationToken);
        var record = Record(remote, placement, "ready");
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            DemandProvision(org, context.UserId);
            var project = org.Projects.GetValueOrDefault(scope.ProjectId) ?? throw new AccountAccessException("Project is unavailable.", 404);
            var currentSource = project.Databases.GetValueOrDefault(id);
            if (currentSource is null || currentSource.Id != source.Id || currentSource.ProjectId != scope.ProjectId)
                throw new AccountAccessException("Source database is unavailable.", 404);
            if (currentSource.Status != "ready") throw new AccountAccessException("Only a ready database can be branched.", 409);
            if (project.Databases.ContainsKey(record.Id)) return;
            DemandName(project, name);
            DemandRoom(org);
            project.Databases[record.Id] = record;
        }, cancellationToken);
        return record;
    }

    // allRegions replaces the replica choice: regions may then name only the home region, or nothing.
    public async Task SetRegionsAsync(AccountContext context, string id, IEnumerable<string> regions, bool allRegions, CancellationToken cancellationToken)
    {
        context = await CurrentAsync(context, cancellationToken);
        var database = Database(context, id);
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        var scope = Scope(context);
        var home = await HomeAsync(context, database, cancellationToken);
        var selected = regions.ToArray();
        if (!allRegions) RequireReplicas(selected);
        var placement = (await PlacementAsync(AllRegionsPlacement(selected, allRegions, home.Code), cancellationToken, Regions(database))).ToList();
        if (!placement.Remove(home)) throw new AccountAccessException("The home region must stay selected.", 422);
        placement.Insert(0, home);
        var remote = await backend.SetRegionsAsync(scope, home, id, placement, allRegions, cancellationToken);
        if (remote.Id != id || remote.ProjectId != scope.ProjectId || remote.Name != database.Name || remote.HomeRegion != home.Id)
            throw new HttpRequestException("The placement update returned an invalid database identity.");
        var actualPlacement = await RemotePlacementAsync(remote, cancellationToken);
        if (!SameReplicas(actualPlacement, placement) || remote.AllRegions != allRegions) throw new HttpRequestException("The placement update returned a different replica set.");
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management access is required.");
            var current = org.Projects.GetValueOrDefault(scope.ProjectId)?.Databases.GetValueOrDefault(id) ?? throw new AccountAccessException("Database is unavailable.", 404);
            current.Region = home.Code;
            current.Regions = placement.Select(region => region.Code).ToList();
            current.AllRegions = allRegions;
            current.PlacementRevision = checked(current.PlacementRevision + 1);
        }, cancellationToken);
    }

    public async Task<DatabaseRecord> RenameAsync(AccountContext context, string id, string name, string confirmation, CancellationToken cancellationToken)
    {
        context = await CurrentAsync(context, cancellationToken);
        var database = Database(context, id);
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        if (database.Status != "ready") throw new AccountAccessException("Only a ready database can be renamed.", 409);
        if (confirmation != database.Name) throw new AccountAccessException("Type the current database name to confirm the rename.", 422);
        if (!TerraScale.Dashboard.Features.Databases.DatabaseRenameName.IsValid(name)) throw new AccountAccessException("Start with a lowercase letter, then use lowercase letters, digits, underscores or hyphens; at most 63 characters. Device names are reserved.", 422);
        if (name == database.Name) throw new AccountAccessException("Choose a different database name.", 422);
        DemandName(context.Project!, name);
        var scope = Scope(context);
        var home = await HomeAsync(context, database, cancellationToken);
        var remote = await backend.RenameAsync(scope, home, id, name, cancellationToken);
        var placement = await RemotePlacementAsync(remote, cancellationToken);
        if (remote.Id != id || remote.ProjectId != scope.ProjectId || remote.Name != name || remote.HomeRegion != home.Id ||
            remote.CreatedAt != database.CreatedAt || remote.BranchedFrom != database.BranchedFrom || remote.AllRegions != database.AllRegions ||
            !placement.Select(region => region.Code).Order(StringComparer.Ordinal).SequenceEqual(Regions(database).Order(StringComparer.Ordinal)))
            throw new HttpRequestException("The rename returned an unexpected database identity or placement. Reload Settings to check its current name.");
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management access is required.");
            var current = org.Projects.GetValueOrDefault(scope.ProjectId)?.Databases.GetValueOrDefault(id)
                ?? throw new AccountAccessException("Database is unavailable.", 404);
            current.Name = name;
            org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "database.renamed", name, "database", accounts.Now));
        }, cancellationToken);
        database.Name = name;
        return database;
    }

    // All regions keeps one home region (the chosen one, else home) and no replica regions.
    private static IEnumerable<string> AllRegionsPlacement(IEnumerable<string> regions, bool allRegions, string home)
    {
        if (!allRegions) return regions;
        var chosen = regions.Where(code => !string.IsNullOrWhiteSpace(code)).ToArray();
        return chosen.Length switch
        {
            0 => [home],
            1 => chosen,
            _ => throw new AccountAccessException("All regions copies the database to every region automatically; choose either all regions or replica regions, not both.", 422)
        };
    }

    public async Task DeleteAsync(AccountContext context, string id, string confirmation, CancellationToken cancellationToken)
    {
        context = await CurrentAsync(context, cancellationToken);
        var database = Database(context, id);
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        if (confirmation != database.Name) throw new AccountAccessException("Type the database name to confirm deletion.", 422);
        var scope = Scope(context);
        await backend.DeleteAsync(scope, await HomeAsync(context, database, cancellationToken), id, cancellationToken);
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management access is required.");
            var project = org.Projects.GetValueOrDefault(scope.ProjectId) ?? throw new AccountAccessException("Project is unavailable.", 404);
            if (project.Databases.Remove(id)) org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "database.deleted", database.Name, "database", accounts.Now));
        }, cancellationToken);
    }

    // TerraBase owns databases; the project's records follow its listings. A database created outside the dashboard (the
    // CLI, an SDK) gets a record, so it is listed and counts toward the plan, and a record whose database its home region
    // no longer lists is dropped. A region that does not answer leaves its records as they are; the list still renders.
    public async Task SyncAsync(AccountContext context, CancellationToken cancellationToken)
    {
        if (context.Project is not { } project || PlatformDatabases.IsPlatform(context.Organization?.Id)) return;
        try
        {
            var scope = Scope(context);
            var catalog = await backend.RegionsAsync(cancellationToken);
            var seen = project.Databases.Values.ToArray();
            // Copy placement before awaiting the provider; mutable record references are not a concurrency snapshot.
            var observed = seen.ToDictionary(record => record.Id, record =>
                (record.Name, record.ProjectId, record.Region, Regions: Regions(record).ToArray(), record.AllRegions, record.PlacementRevision));
            var homes = seen.Select(database => database.Region).Prepend(project.Region)
                .Select(code => DatabaseRegion.Resolve(catalog, code)).OfType<DatabaseRegion>().DistinctBy(region => region.Id).ToArray();
            if (homes.Length == 0 && !context.Organization!.ContainsMember(context.UserId)) homes = catalog.ToArray();
            var listings = await Task.WhenAll(homes.Select(async home =>
            {
                try { return (home, Listed: await backend.ListAsync(scope, home, cancellationToken)); }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is HttpRequestException or JsonException || DatabaseResults.KnownFailure(error)))
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
                    if (!observed.TryGetValue(record.Id, out var before)) imported.Add(record);
                    // Existing identity and immutable home must match; refresh configuration, never inferred health/status.
                    else if (record.Name == before.Name && record.ProjectId == before.ProjectId && record.Region.Equals(before.Region, StringComparison.OrdinalIgnoreCase) &&
                        (record.AllRegions != before.AllRegions || !Regions(record).SequenceEqual(before.Regions, StringComparer.OrdinalIgnoreCase)))
                        refreshed.Add(record);
                }
                catch (HttpRequestException) { }
            }
            var renamed = remote.Where(database => observed.TryGetValue(database.Id, out var before) && before.Name != database.Name).ToArray();
            if (gone.Count == 0 && imported.Count == 0 && refreshed.Count == 0 && renamed.Length == 0) return;
            bool Unchanged(DatabaseRecord record) => observed.TryGetValue(record.Id, out var before) && record.Name == before.Name && record.ProjectId == before.ProjectId &&
                record.PlacementRevision == before.PlacementRevision && record.Region == before.Region && record.AllRegions == before.AllRegions && Regions(record).SequenceEqual(before.Regions, StringComparer.OrdinalIgnoreCase);
            void Apply(ProjectRecord target)
            {
                // Rechecked on every storage CAS attempt: a newer dashboard placement must survive an older listing.
                foreach (var id in gone)
                    if (target.Databases.TryGetValue(id, out var current) && Unchanged(current)) target.Databases.Remove(id);
                foreach (var record in refreshed)
                    if (target.Databases.TryGetValue(record.Id, out var current) && Unchanged(current))
                    {
                        current.Regions = record.Regions.ToList();
                        current.AllRegions = record.AllRegions;
                        current.Endpoint = record.Endpoint;
                        current.PlacementRevision = checked(current.PlacementRevision + 1);
                    }
                foreach (var database in renamed)
                    if (target.Databases.GetValueOrDefault(database.Id) is { } current && current.Name == observed[database.Id].Name)
                        current.Name = database.Name;
                foreach (var record in imported.Where(record => !target.Databases.ContainsKey(record.Id) && target.Databases.Values.All(database => database.Name != record.Name)))
                    target.Databases[record.Id] = record;
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
            if (saved is not null) project.Databases = saved.Databases;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is HttpRequestException or JsonException || DatabaseResults.KnownFailure(error))) { }
    }

    public static string ValidName(string name)
    {
        name = name.Trim();
        if (!NamePattern().IsMatch(name)) throw new AccountAccessException(NameHint, 422);
        return name;
    }

    public const string NameInputPattern = @"(?!(?:con|prn|aux|nul|com[0-9]|lpt[0-9])$)[a-z](?:[a-z0-9\-]{0,61}[a-z0-9])?";
    public const string NameHint = "Database names must start with a lowercase letter and use 1–63 lowercase letters, digits, and inner hyphens. Reserved names: con, prn, aux, nul, com0–com9, lpt0–lpt9.";

    public static string PreviewName(string name) => name[..Math.Min(name.Length, 48)].TrimEnd('-') + "-preview-branch";
    public static string Path(string id, string tab = "overview", string collection = "") => "/databases/" + Uri.EscapeDataString(id) + (tab == "overview" ? "" : "/" + tab) + (collection.Length == 0 ? "" : "?collection=" + Uri.EscapeDataString(collection));
    public static IReadOnlyList<string> Regions(DatabaseRecord record) => record.Regions.Count > 0 ? record.Regions : string.IsNullOrEmpty(record.Region) ? [] : [record.Region];
    public static IReadOnlyList<string> ServingRegions(DatabaseRecord record, IReadOnlyList<DatabaseRegion> catalog) => record.AllRegions
        ? Regions(record).Concat(catalog.Select(region => region.Code)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : Regions(record);
    internal static void DemandName(ProjectRecord project, string name)
    {
        if (project.Databases.Values.Any(database => database.Name == name)) throw new AccountAccessException("A database with this name already exists.", 409);
    }
    internal static void DemandProvision(OrganizationRecord org, string userId)
    {
        if (!org.CanProvision(userId)) throw new AccountAccessException("Provisioning access and active billing are required.");
    }
    public static int DatabaseLimit(OrganizationRecord org) => org.InternalBilling is not null
        ? PlanCatalog.ResourceLimits(org).Databases is > 0 and var limit ? limit : int.MaxValue
        : PlanCatalog.For(org)?.Resources.Databases
            ?? throw new AccountAccessException("The organization plan is unavailable.", 503);
    internal static void DemandRoom(OrganizationRecord org)
    {
        if (org.Projects.Values.Sum(project => project.Databases.Count) >= DatabaseLimit(org))
            throw new AccountAccessException("Your organization has reached its plan's database limit. Upgrade the plan or delete a database.", 409);
    }
    internal Task<AccountContext> CurrentAsync(AccountContext context, CancellationToken cancellationToken) =>
        AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
    internal static DatabaseRecord Record(ManagedDatabase remote, IReadOnlyList<DatabaseRegion> placement, string status) => new()
    {
        Id = remote.Id,
        ProjectId = remote.ProjectId,
        Name = remote.Name,
        CreatedAt = remote.CreatedAt,
        BranchedFrom = remote.BranchedFrom,
        Region = placement[0].Code,
        Regions = placement.Select(region => region.Code).ToList(),
        AllRegions = remote.AllRegions,
        Endpoint = placement[0].Api.ToString(),
        Status = status
    };
    internal async Task<IReadOnlyList<DatabaseRegion>> RemotePlacementAsync(ManagedDatabase remote, CancellationToken cancellationToken, IReadOnlyList<DatabaseRegion>? knownCatalog = null)
    {
        if (string.IsNullOrWhiteSpace(remote.HomeRegion) || remote.ReplicaRegions is null ||
            remote.ReplicaRegions.Any(string.IsNullOrWhiteSpace)) throw new HttpRequestException("The database service did not return its canonical placement.");
        var ids = remote.ReplicaRegions.Prepend(remote.HomeRegion).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new HttpRequestException("The database service returned ambiguous placement.");
        var catalog = knownCatalog ?? await backend.RegionsAsync(cancellationToken);
        return ids.Select(id => (catalog.SingleOrDefault(region => region.Id == id) ?? DatabaseRegion.FromId(id))
            ?? throw new HttpRequestException("The database service returned an invalid region.")).ToArray();
    }
    private static bool SameReplicas(IReadOnlyList<DatabaseRegion> left, IReadOnlyList<DatabaseRegion> right) =>
        left.Skip(1).Select(region => region.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(right.Skip(1).Select(region => region.Id).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    [GeneratedRegex(@"\A" + NameInputPattern + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
