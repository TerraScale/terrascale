using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Shared.Databases;

public sealed record DatabaseScope(string UserId, string AccessToken, string OrganizationId, string OrganizationName, string ProjectId, string ProjectName)
{
    public SessionRecord? Session { get; init; }
    public override string ToString() => $"Database scope {OrganizationId}/{ProjectId} [REDACTED]";
}

public sealed record DatabaseRegion(string Code, string Id, string Name, string Country, Uri Api, bool Healthy)
{
    // TerraBase's regional contract (terrascale docs/terrabase/region-identity.md): a region id is a lowercase letter,
    // then lowercase letters, digits or hyphens, at most 63 characters, not ending in a hyphen, and
    // https://<id>.api.terrascale.tech/ serves only that region. Anything else derives no origin.
    public static DatabaseRegion? FromId(string? id) =>
        id is { Length: >= 1 and <= 63 } && id[0] is >= 'a' and <= 'z' && id[^1] != '-' &&
        id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            ? new(id.ToUpperInvariant(), id, DatabaseFormat.Region(id), "", new($"https://{id}.api.terrascale.tech/"), true)
            : null;

    // A stored or producer-named region: the catalog's entry, else the region its id names. A home or replica outside
    // the catalog keeps its own region instead of being dropped or moved.
    public static DatabaseRegion? Resolve(IReadOnlyList<DatabaseRegion> catalog, string? code) =>
        catalog.FirstOrDefault(region => region.Code == code || region.Id == code) ?? FromId(code?.ToLowerInvariant());
}
public sealed record ManagedDatabase(string Id, string ProjectId, string Name, DateTimeOffset CreatedAt, string? BranchedFrom = null, string HomeRegion = "", IReadOnlyList<string>? ReplicaRegions = null, bool AllRegions = false);
public sealed record CollectionStats(string Name, long Documents, long Bytes);
public sealed record DatabaseSqlRow(string Id, JsonElement Value);
public sealed record DatabaseSqlResult(int Matched, bool Truncated, IReadOnlyList<DatabaseSqlRow> Rows);
public sealed record DatabaseIndex(string Name, string Collection, IReadOnlyList<string> Fields, string State);
public sealed record ProjectCredential(string Id, string ProjectId, string Name, IReadOnlyList<string> Scopes, string? DatabaseId, string LastFour, DateTimeOffset CreatedAt)
{
    public string? DatabaseName { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; init; } = true;
}
public sealed record CreatedProjectCredential(ProjectCredential Key, string Secret)
{
    public override string ToString() => $"Project credential {Key.Id} [REDACTED]";
}

public interface IDatabaseBackend
{
    Task<IReadOnlyList<DatabaseRegion>> RegionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ManagedDatabase>> ListAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
    Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken);
    Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken);
    Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
    Task<ManagedDatabase> SetRegionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken);
    Task<IReadOnlyList<CollectionStats>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
    Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, CancellationToken cancellationToken);
    Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
    Task<DatabaseIndex> CreateIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string collection, IReadOnlyList<string> fields, CancellationToken cancellationToken);
    Task DropIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken);

}

// Canonical data operations use the official SDK with current action-scoped authority.
public sealed class DatabaseBackend(IHttpClientFactory factory, IProjectAccessTokens tokens) : IDatabaseBackend
{
    // No producer publishes a region catalog: Atlas /v1/discovery was never served and is gone, and TerraBase has no catalog
    // route yet. The one verified region is the one answering api.terrascale.tech, named by both its /healthz body and its
    // X-TerraBase-Region header. It is not necessarily the nearest, a database's home, or every serving region.
    public async Task<IReadOnlyList<DatabaseRegion>> RegionsAsync(CancellationToken cancellationToken)
    {
        using var response = await factory.CreateClient("platform").GetAsync(new Uri(TerraBaseDiscovery.Origin, "/healthz"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        string? region;
        try
        {
            using var document = await PlatformJson.ReadAsync(response.Content, cancellationToken);
            region = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("region", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException error) { throw new HttpRequestException("TerraBase returned an invalid health answer.", error); }
        if (!response.Headers.TryGetValues("X-TerraBase-Region", out var named) || named.Count() != 1 || named.Single() != region ||
            DatabaseRegion.FromId(region) is not { } serving)
            throw new HttpRequestException("TerraBase did not name a valid serving region.");
        return [serving];
    }

    public async Task<IReadOnlyList<ManagedDatabase>> ListAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
    {
        var sdk = await ClientAsync(scope, home, "databases:read", cancellationToken);
        var result = new List<ManagedDatabase>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        var pages = 0;
        do
        {
            var page = await RunAsync(() => sdk.Project(scope.ProjectId).ListDatabasesAsync(next, 100, cancellationToken));
            foreach (var item in page.Items)
            {
                if (item.ProjectId != scope.ProjectId || !TerraScaleIds.IsValid(item.Id) ||
                    result.Any(existing => existing.Id == item.Id || existing.Name == item.Name))
                    throw new HttpRequestException("Database listing returned an invalid identity.");
                result.Add(Map(item));
            }
            next = page.Next;
            if (!string.IsNullOrEmpty(next) && (!seen.Add(next) || ++pages >= 100))
                throw new HttpRequestException("Database pagination did not advance within its bounds.");
        } while (!string.IsNullOrEmpty(next));
        return result;
    }

    public async Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken)
    {
        var sdk = await ClientAsync(scope, placement[0], "databases:write", cancellationToken);
        // TerraBase refuses allRegions together with any replicaRegions list, even an empty one.
        return Map(await RunAsync(() => sdk.Project(scope.ProjectId).CreateDatabaseAsync(name, placement[0].Id,
            allRegions ? null : placement.Skip(1).Select(region => region.Id).ToArray(), allRegions ? true : null, cancellationToken)));
    }

    public async Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(scope, home, databaseId, "databases:write", cancellationToken);
        return Map(await RunAsync(() => database.BranchAsync(name, cancellationToken)));
    }

    public async Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
    {
        var name = await NameAsync(scope, home, databaseId, cancellationToken);
        var sdk = await ClientAsync(scope, home, "databases:write", cancellationToken);
        await RunAsync(() => sdk.Project(scope.ProjectId).DeleteDatabaseAsync(name, cancellationToken));
    }

    public async Task<ManagedDatabase> SetRegionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken)
    {
        var name = await NameAsync(scope, home, databaseId, cancellationToken);
        var project = (await ClientAsync(scope, home, "databases:write", cancellationToken)).Project(scope.ProjectId);
        return Map(await RunAsync(() => allRegions ? project.SetDatabaseAllRegionsAsync(name, cancellationToken)
            : project.SetDatabaseRegionsAsync(name, placement.Skip(1).Select(region => region.Id).ToArray(), cancellationToken)));
    }

    public async Task<IReadOnlyList<CollectionStats>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(scope, home, databaseId, "databases:read", cancellationToken);
        return (await RunAsync(() => database.ListCollectionStatsAsync(cancellationToken))).Items
            .Select(item => new CollectionStats(item.Name, item.Documents, item.Bytes)).ToArray();
    }

    public async Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(scope, home, databaseId, "databases:read", cancellationToken);
        var result = await RunAsync(() => database.RunSqlAsync(statement, cancellationToken));
        return new(result.Matched, result.Truncated, result.Rows.Select(row => new DatabaseSqlRow(row.Id, row.Value)).ToArray());
    }

    public async Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(scope, home, databaseId, "databases:read", cancellationToken);
        return (await RunAsync(() => database.ListIndexesAsync(cancellationToken: cancellationToken))).Items.Select(Map).ToArray();
    }

    public async Task<DatabaseIndex> CreateIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string collection, IReadOnlyList<string> fields, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(scope, home, databaseId, "databases:write", cancellationToken);
        return Map(await RunAsync(() => database.CreateIndexAsync(collection, fields, cancellationToken: cancellationToken)));
    }

    public async Task DropIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(scope, home, databaseId, "databases:write", cancellationToken);
        await RunAsync(() => database.DropIndexAsync(name, cancellationToken));
    }

    private async Task<TerraBaseDocumentsClient> ClientAsync(DatabaseScope scope, DatabaseRegion home, string permission, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatform(scope.OrganizationId)) throw PlatformDatabases.ReadOnly();
        var session = scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
        var token = await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, [permission], cancellationToken);
        return new(factory.CreateClient("platform"), new TerraBaseDocumentsClientOptions
        { BaseAddress = home.Api, Region = home.Id, AccessTokenProvider = _ => ValueTask.FromResult(token) });
    }

    // UI URLs use database IDs; the authoritative SDK list resolves those IDs to canonical route names.
    private async Task<string> NameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken) =>
        (await ListAsync(scope, home, cancellationToken)).SingleOrDefault(item => item.Id == databaseId)?.Name
            ?? throw new DatabaseProviderException(HttpStatusCode.NotFound);

    private async Task<TerraBaseDatabase> DatabaseAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string permission, CancellationToken cancellationToken)
    {
        var name = await NameAsync(scope, home, databaseId, cancellationToken);
        return (await ClientAsync(scope, home, permission, cancellationToken)).Database(scope.ProjectId, name);
    }

    private static ManagedDatabase Map(TerraBaseManagedDatabase item) => new(item.Id, item.ProjectId, item.Name, item.CreatedAt,
        item.BranchedFrom, item.HomeRegion, item.ReplicaRegions, item.AllRegions);
    private static DatabaseIndex Map(TerraBaseIndex item) => new(item.Name, item.Collection, item.Fields, item.State);
    private static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (TerraBaseWrongRegionException error) { throw new DatabaseWrongRegionException(error.Region, error.HomeRegion, error.Regions); }
    }
    private static async Task RunAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (TerraBaseWrongRegionException error) { throw new DatabaseWrongRegionException(error.Region, error.HomeRegion, error.Regions); }
    }


}

public class DatabaseProviderException(HttpStatusCode status, string message = "The database service could not complete the request.") : HttpRequestException(message, null, status);

// TerraBase's 421 wrong_region, as ts-dotnet 0.0.2's TerraBaseWrongRegionException reads it: the answering region
// (X-TerraBase-Region) and the database's home and serving regions (X-TerraBase-Database-Home/-Regions). Nothing is
// retried or redirected; malformed region names are dropped.
public sealed class DatabaseWrongRegionException(string? region, string? homeRegion, IReadOnlyList<string> regions)
    : DatabaseProviderException(HttpStatusCode.MisdirectedRequest, homeRegion is null
        ? "The database is served in another region. Nothing was changed."
        : $"The database is managed in {DatabaseFormat.Region(homeRegion)}. Nothing was changed.")
{
    public string? Region { get; } = region;
    public string? HomeRegion { get; } = homeRegion;
    public IReadOnlyList<string> Regions { get; } = regions;

    public static DatabaseWrongRegionException From(HttpResponseMessage response)
    {
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) && values.Count() == 1 ? values.Single() : null;
        string? Valid(string? id) => DatabaseRegion.FromId(id)?.Id;
        return new(Valid(Header("X-TerraBase-Region")), Valid(Header("X-TerraBase-Database-Home")),
            (Header("X-TerraBase-Database-Regions") ?? "").Split(',').Select(Valid).OfType<string>().ToArray());
    }
}
