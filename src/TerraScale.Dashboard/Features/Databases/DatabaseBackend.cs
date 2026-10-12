using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Databases;

public sealed record DatabaseScope(string UserId, string AccessToken, string OrganizationId, string OrganizationName, string ProjectId, string ProjectName)
{
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
            ? new(id.ToUpperInvariant(), id, DatabasesPage.RegionDisplay(id), "", new($"https://{id}.api.terrascale.tech/"), true)
            : null;

    // A stored or producer-named region: the catalog's entry, else the region its id names. A home or replica outside
    // the catalog keeps its own region instead of being dropped or moved.
    public static DatabaseRegion? Resolve(IReadOnlyList<DatabaseRegion> catalog, string? code) =>
        catalog.FirstOrDefault(region => region.Code == code || region.Id == code) ?? FromId(code?.ToLowerInvariant());
}
public sealed record ManagedDatabase(string Id, string ProjectId, string Name, DateTimeOffset CreatedAt, string? BranchedFrom = null, string HomeRegion = "", IReadOnlyList<string>? ReplicaRegions = null);
public sealed record CollectionStats(string Name, long Documents, long Bytes);
public sealed record DatabaseSqlRow(string Id, JsonElement Value);
public sealed record DatabaseSqlResult(int Matched, bool Truncated, IReadOnlyList<DatabaseSqlRow> Rows);
public sealed record DatabaseIndex(string Name, string Collection, IReadOnlyList<string> Fields, string State);
public sealed record ProjectCredential(string Id, string ProjectId, string Name, IReadOnlyList<string> Scopes, string? DatabaseId, string LastFour, DateTimeOffset CreatedAt);
public sealed record CreatedProjectCredential(ProjectCredential Key, string Secret)
{
    public override string ToString() => $"Project credential {Key.Id} [REDACTED]";
}

public interface IDatabaseBackend
{
    Task<IReadOnlyList<DatabaseRegion>> RegionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ManagedDatabase>> ListAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
    Task RegisterAsync(DatabaseScope scope, DatabaseRegion region, CancellationToken cancellationToken);
    Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, IReadOnlyList<DatabaseRegion> placement, CancellationToken cancellationToken);
    Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken);
    Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
    Task<ManagedDatabase> SetRegionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<DatabaseRegion> placement, CancellationToken cancellationToken);
    Task<IReadOnlyList<CollectionStats>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
    Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, CancellationToken cancellationToken);
    Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
    Task<DatabaseIndex> CreateIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string collection, IReadOnlyList<string> fields, CancellationToken cancellationToken);
    Task DropIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectCredential>> CredentialsAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
    Task<CreatedProjectCredential> CreateCredentialAsync(DatabaseScope scope, DatabaseRegion home, string name, IReadOnlyList<string> scopes, string? databaseId, CancellationToken cancellationToken);
    Task RevokeCredentialAsync(DatabaseScope scope, DatabaseRegion home, string keyId, CancellationToken cancellationToken);
}

// The official SDK handles the supported lifecycle methods. The narrow HTTP
// methods below preserve producer operations already used by ts-go/v1/management.go
// while their ts-dotnet equivalents are pending. They always use the user's token.
public sealed class DatabaseBackend(IHttpClientFactory factory) : IDatabaseBackend
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

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
        var result = new List<ManagedDatabase>();
        string? next = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            // The pinned official SDK DTO drops the producer's canonical
            // homeRegion/replicaRegions fields, which recovery must preserve.
            var page = await SendAsync<DatabasePage>(scope, home, HttpMethod.Get, ProjectPath(scope) + "/databases?limit=100" +
                (next is null ? "" : "&after=" + Segment(next)), null, cancellationToken);
            result.AddRange(page.Items);
            next = page.Next;
            if (next is not null && !seen.Add(next)) throw new HttpRequestException("Database pagination did not advance.");
        } while (next is not null);
        return result;
    }

    public async Task RegisterAsync(DatabaseScope scope, DatabaseRegion region, CancellationToken cancellationToken)
    {
        // Management routes are forwarded to the catalog primary by the producer.
        // A missing regional catalog cannot be repaired using a user's token:
        // recreating the canonical project with its chosen ID is operator-only.
        var client = Client(scope, region);
        var organization = await client.GetOrganizationAsync(scope.UserId, scope.OrganizationId, cancellationToken);
        if (organization.Id != scope.OrganizationId)
            throw new HttpRequestException("The regional catalog returned a different organization identity.");
        var project = await client.GetProjectAsync(scope.UserId, scope.OrganizationId, scope.ProjectId, cancellationToken);
        if (project.Id != scope.ProjectId || project.OrganizationId != scope.OrganizationId)
            throw new HttpRequestException("The regional catalog returned a different project scope.");
    }

    public async Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, IReadOnlyList<DatabaseRegion> placement, CancellationToken cancellationToken)
    {
        foreach (var region in placement) await RegisterAsync(scope, region, cancellationToken);
        return await SendAsync<ManagedDatabase>(scope, placement[0], HttpMethod.Post, ProjectPath(scope) + "/databases", new { name, homeRegion = placement[0].Id, replicaRegions = placement.Skip(1).Select(region => region.Id).ToArray() }, cancellationToken);
    }

    public async Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken)
    {
        var item = await Client(scope, home).BranchDatabaseAsync(scope.UserId, scope.OrganizationId, scope.ProjectId, databaseId, name, cancellationToken);
        var confirmed = await SendAsync<ManagedDatabase>(scope, home, HttpMethod.Get, DatabasePath(scope, item.Id), null, cancellationToken);
        if (confirmed.Id != item.Id || confirmed.ProjectId != item.ProjectId || confirmed.Name != item.Name ||
            confirmed.CreatedAt != item.CreatedAt || confirmed.BranchedFrom != item.BranchedFrom)
            throw new HttpRequestException("The branch confirmation did not match the official SDK creation response.");
        return confirmed;
    }

    public Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken) =>
        Client(scope, home).DeleteDatabaseAsync(scope.UserId, scope.OrganizationId, scope.ProjectId, databaseId, cancellationToken);

    public async Task<ManagedDatabase> SetRegionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<DatabaseRegion> placement, CancellationToken cancellationToken)
    {
        foreach (var region in placement.Skip(1)) await RegisterAsync(scope, region, cancellationToken);
        return await SendAsync<ManagedDatabase>(scope, home, HttpMethod.Patch, DatabasePath(scope, databaseId), new { replicaRegions = placement.Skip(1).Select(region => region.Id).ToArray() }, cancellationToken);
    }

    public async Task<IReadOnlyList<CollectionStats>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken) =>
        (await SendAsync<Items<CollectionStats>>(scope, home, HttpMethod.Get, DatabasePath(scope, databaseId) + "/collections", null, cancellationToken)).ItemsValue;

    public Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, CancellationToken cancellationToken) =>
        SendAsync<DatabaseSqlResult>(scope, home, HttpMethod.Post, DatabasePath(scope, databaseId) + "/sql", new { sql = statement }, cancellationToken);

    public async Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken) =>
        (await SendAsync<Items<DatabaseIndex>>(scope, home, HttpMethod.Get, DatabasePath(scope, databaseId) + "/indexes", null, cancellationToken)).ItemsValue;

    public Task<DatabaseIndex> CreateIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string collection, IReadOnlyList<string> fields, CancellationToken cancellationToken) =>
        SendAsync<DatabaseIndex>(scope, home, HttpMethod.Post, DatabasePath(scope, databaseId) + "/indexes", new { collection, fields }, cancellationToken);

    public async Task DropIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken) =>
        await SendAsync<JsonElement>(scope, home, HttpMethod.Delete, DatabasePath(scope, databaseId) + "/indexes/" + Segment(name), null, cancellationToken);

    public async Task<IReadOnlyList<ProjectCredential>> CredentialsAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
    {
        var result = new List<ProjectCredential>();
        string? next = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await SendAsync<CredentialPage>(scope, home, HttpMethod.Get, ProjectPath(scope) + "/api-keys?limit=100" + (string.IsNullOrEmpty(next) ? "" : "&after=" + Segment(next)), null, cancellationToken);
            result.AddRange(page.Items);
            next = page.Next;
            if (!string.IsNullOrEmpty(next) && !seen.Add(next)) throw new HttpRequestException("Credential pagination did not advance.");
        } while (!string.IsNullOrEmpty(next));
        return result;
    }

    public Task<CreatedProjectCredential> CreateCredentialAsync(DatabaseScope scope, DatabaseRegion home, string name, IReadOnlyList<string> scopes, string? databaseId, CancellationToken cancellationToken) =>
        SendAsync<CreatedProjectCredential>(scope, home, HttpMethod.Post, ProjectPath(scope) + "/api-keys", new { name, scopes, databaseId = databaseId ?? "" }, cancellationToken);

    public async Task RevokeCredentialAsync(DatabaseScope scope, DatabaseRegion home, string keyId, CancellationToken cancellationToken) =>
        await SendAsync<JsonElement>(scope, home, HttpMethod.Delete, ProjectPath(scope) + "/api-keys/" + Segment(keyId), null, cancellationToken);

    private TerraBaseManagementClient Client(DatabaseScope scope, DatabaseRegion region) => new(factory.CreateClient("platform"), new TerraBaseManagementClientOptions
    {
        BaseAddress = region.Api,
        AccessTokenProvider = _ => ValueTask.FromResult(scope.AccessToken)
    });

    private async Task<T> SendAsync<T>(DatabaseScope scope, DatabaseRegion region, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, new Uri(region.Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scope.AccessToken);
        request.Headers.Add("X-TerraBase-Organization-Id", scope.OrganizationId);
        request.Headers.Add("X-TerraBase-User-Id", scope.UserId);
        request.Headers.Add(TerraBaseRegionPin, region.Id);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await factory.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.MisdirectedRequest) throw DatabaseWrongRegionException.From(response);
        if (!response.IsSuccessStatusCode) throw new DatabaseProviderException(response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        const int maxBytes = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new HttpRequestException("Database response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var read = await input.ReadAsync(buffer, deadline.Token);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw new HttpRequestException("Database response is too large.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0 && typeof(T) == typeof(JsonElement)) return default!;
        try
        {
            return JsonSerializer.Deserialize<T>(output.GetBuffer().AsSpan(0, (int)output.Length), Json) ?? throw new HttpRequestException("Database returned an invalid response.");
        }
        catch (JsonException error) { throw new HttpRequestException("Database returned an invalid response.", error); }
    }

    // TerraBase.Region.PinHeader in ts-dotnet 0.0.2: the region that must answer, or 421.
    private const string TerraBaseRegionPin = "X-TerraScale-Region-Pin";
    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static string ProjectPath(DatabaseScope scope) => "/v1/management/organizations/" + Segment(scope.OrganizationId) + "/projects/" + Segment(scope.ProjectId);
    private static string DatabasePath(DatabaseScope scope, string databaseId) => ProjectPath(scope) + "/databases/" + Segment(databaseId);
    private sealed record Items<T>([property: System.Text.Json.Serialization.JsonPropertyName("items")] IReadOnlyList<T> ItemsValue);
    private sealed record CredentialPage(IReadOnlyList<ProjectCredential> Items, string? Next);
    private sealed record DatabasePage(IReadOnlyList<ManagedDatabase> Items, string? Next);
}

public class DatabaseProviderException(HttpStatusCode status, string message = "The database service could not complete the request.") : HttpRequestException(message, null, status);

// TerraBase's 421 wrong_region, as ts-dotnet 0.0.2's TerraBaseWrongRegionException reads it: the answering region
// (X-TerraBase-Region) and the database's home and serving regions (X-TerraBase-Database-Home/-Regions). Nothing is
// retried or redirected; malformed region names are dropped.
public sealed class DatabaseWrongRegionException(string? region, string? homeRegion, IReadOnlyList<string> regions)
    : DatabaseProviderException(HttpStatusCode.MisdirectedRequest, homeRegion is null
        ? "The database is served in another region. Nothing was changed."
        : $"The database is managed in {DatabasesPage.RegionDisplay(homeRegion)}. Nothing was changed.")
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
