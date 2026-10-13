using System.Text.RegularExpressions;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Shared.Databases;

public sealed partial class DatabaseService(AccountService accounts, IDatabaseBackend backend)
{
    public static DatabaseScope Scope(AccountContext context) => new(context.UserId, context.Session.AccessToken,
        context.Organization?.Id ?? throw new AccountAccessException("Select an organization.", 404), context.Organization.Name,
        context.Project?.Id ?? throw new AccountAccessException("Select a project.", 404), context.Project.Name);

    public static DatabaseRecord Database(AccountContext context, string id) =>
        context.Project?.Databases.GetValueOrDefault(id) is { } database && database.ProjectId == context.Project.Id
            ? database : throw new AccountAccessException("Database is unavailable.", 404);

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

    public async Task<DatabaseRecord> CreateAsync(AccountContext context, string name, IEnumerable<string> regions, CancellationToken cancellationToken)
    {
        name = ValidName(name);
        context = await CurrentAsync(context, cancellationToken);
        var scope = Scope(context);
        DemandProvision(context.Organization!, context.UserId);
        DemandName(context.Project!, name);
        DemandRoom(context.Organization!);
        var placement = await PlacementAsync(regions, cancellationToken);
        ManagedDatabase remote;
        var recovered = false;
        try { remote = await backend.CreateAsync(scope, name, placement, cancellationToken); }
        catch (DatabaseProviderException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict)
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
        if (!recovered && (actualPlacement[0].Id != placement[0].Id || !SameReplicas(actualPlacement, placement)))
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

    public async Task SetRegionsAsync(AccountContext context, string id, IEnumerable<string> regions, CancellationToken cancellationToken)
    {
        context = await CurrentAsync(context, cancellationToken);
        var database = Database(context, id);
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        var scope = Scope(context);
        var home = await HomeAsync(context, database, cancellationToken);
        var placement = (await PlacementAsync(regions, cancellationToken, Regions(database))).ToList();
        if (!placement.Remove(home)) throw new AccountAccessException("The home region must stay selected.", 422);
        placement.Insert(0, home);
        var remote = await backend.SetRegionsAsync(scope, home, id, placement, cancellationToken);
        if (remote.Id != id || remote.ProjectId != scope.ProjectId || remote.Name != database.Name || remote.HomeRegion != home.Id)
            throw new HttpRequestException("The placement update returned an invalid database identity.");
        var actualPlacement = await RemotePlacementAsync(remote, cancellationToken);
        if (!SameReplicas(actualPlacement, placement)) throw new HttpRequestException("The placement update returned a different replica set.");
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            if (!org.CanManage(context.UserId)) throw new AccountAccessException("Organization management access is required.");
            var current = org.Projects.GetValueOrDefault(scope.ProjectId)?.Databases.GetValueOrDefault(id) ?? throw new AccountAccessException("Database is unavailable.", 404);
            current.Region = home.Code;
            current.Regions = placement.Select(region => region.Code).ToList();
        }, cancellationToken);
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
            project.Databases.Remove(id);
        }, cancellationToken);
    }

    public static string ValidName(string name)
    {
        name = name.Trim();
        if (!NamePattern().IsMatch(name)) throw new AccountAccessException("Database names use 1–63 lowercase letters, digits, and inner hyphens.", 422);
        return name;
    }

    public static string PreviewName(string name) => name[..Math.Min(name.Length, 48)].TrimEnd('-') + "-preview-branch";
    public static string Path(string id, string tab = "overview", string collection = "") => "/databases/" + Uri.EscapeDataString(id) + (tab == "overview" ? "" : "/" + tab) + (collection.Length == 0 ? "" : "?collection=" + Uri.EscapeDataString(collection));
    public static IReadOnlyList<string> Regions(DatabaseRecord record) => record.Regions.Count > 0 ? record.Regions : string.IsNullOrEmpty(record.Region) ? [] : [record.Region];
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
        : PlanCatalog.Plans.SingleOrDefault(plan => plan.Id == org.PlanId)?.Key switch
        {
            "free" => 1,
            "starter" => 5,
            "pro" => 25,
            "business" => 100,
            _ => throw new AccountAccessException("The organization plan is unavailable.", 503)
        };
    internal static void DemandRoom(OrganizationRecord org)
    {
        if (org.Projects.Values.Sum(project => project.Databases.Count) >= DatabaseLimit(org))
            throw new AccountAccessException("Your organization has reached its plan's database limit. Upgrade the plan or delete a database.", 409);
    }
    internal async Task<AccountContext> CurrentAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var current = await accounts.GetContextAsync(context.Session, cancellationToken);
        var organization = current.Organizations.FirstOrDefault(item => item.Id == context.Organization?.Id)
            ?? throw new AccountAccessException("Organization is unavailable.", 404);
        var project = organization.Projects.GetValueOrDefault(context.Project?.Id ?? "")
            ?? throw new AccountAccessException("Project is unavailable.", 404);
        if (project.OrganizationId != organization.Id) throw new AccountAccessException("Project belongs to another organization.");
        return new(current.Session, current.Account, current.Organizations, organization, project);
    }
    internal static DatabaseRecord Record(ManagedDatabase remote, IReadOnlyList<DatabaseRegion> placement, string status) => new()
    {
        Id = remote.Id,
        ProjectId = remote.ProjectId,
        Name = remote.Name,
        CreatedAt = remote.CreatedAt,
        BranchedFrom = remote.BranchedFrom,
        Region = placement[0].Code,
        Regions = placement.Select(region => region.Code).ToList(),
        Endpoint = placement[0].Api.ToString(),
        Status = status
    };
    internal async Task<IReadOnlyList<DatabaseRegion>> RemotePlacementAsync(ManagedDatabase remote, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remote.HomeRegion) || remote.ReplicaRegions is null ||
            remote.ReplicaRegions.Any(string.IsNullOrWhiteSpace)) throw new HttpRequestException("The database service did not return its canonical placement.");
        var ids = remote.ReplicaRegions.Prepend(remote.HomeRegion).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new HttpRequestException("The database service returned ambiguous placement.");
        var catalog = await backend.RegionsAsync(cancellationToken);
        return ids.Select(id => (catalog.SingleOrDefault(region => region.Id == id) ?? DatabaseRegion.FromId(id))
            ?? throw new HttpRequestException("The database service returned an invalid region.")).ToArray();
    }
    private static bool SameReplicas(IReadOnlyList<DatabaseRegion> left, IReadOnlyList<DatabaseRegion> right) =>
        left.Skip(1).Select(region => region.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(right.Skip(1).Select(region => region.Id).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
