using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Management.Usage;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

/// <summary>A response read to the end: status, JSON body (Undefined when empty) and the headers tests check.</summary>
internal sealed record ApiResponse(HttpStatusCode Status, JsonElement Json, string? ETag, Uri? Location)
{
    public string? Code => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error)
        ? error.GetProperty("code").GetString()
        : null;

    public int? Op => Json.TryGetProperty("error", out var error) && error.TryGetProperty("op", out var op) ? op.GetInt32() : null;

    public string? Message => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
        ? message.GetString()
        : null;

    public string Version => Json.GetProperty("version").GetString()!;

    public IReadOnlyList<string> Ids => [.. Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)];
}

/// <summary>
/// One test's own tenant (an organization with one project), a key of that organization, database
/// <c>db</c> and collection <c>items</c>, plus helpers that send JSON and read whole responses.
/// </summary>
internal sealed class DocumentApi : IAsyncDisposable
{
    public const string Database = "db";
    public const string Collection = "items";

    private readonly bool _ownsServer;

    private DocumentApi(ManagementApi management, Tenant tenant, bool ownsServer)
    {
        Management = management;
        Tenant = tenant;
        _ownsServer = ownsServer;
    }

    public ManagementApi Management { get; }

    public Tenant Tenant { get; }

    public TerraBaseTestServer Server => Management.Server;

    /// <summary>
    /// Process memory reserved once pending usage observations are flushed: they hold admission until the next usage flush,
    /// so a request's own observations do not read as a leak.
    /// </summary>
    public async Task<double> ReservedBytesAsync(CancellationToken cancellationToken = default)
    {
        await Server.Services.GetRequiredService<UsageMeter>().FlushAsync(cancellationToken);
        return await Server.MetricAsync("terrabase_memory_reserved_bytes", cancellationToken: cancellationToken);
    }

    public HttpClient Client => Tenant.Data;

    /// <summary>The data API root of the tenant's project.</summary>
    public string Project => $"/v1/projects/{Tenant.ProjectId}";

    public string Collections => $"{DatabasePath()}/collections";

    public string Documents => $"{Collections}/{Collection}/documents";

    public string Query => $"{DatabasePath()}/collections/{Collection}/query";

    public string Transactions => $"{DatabasePath()}/transactions";

    /// <summary>Creates <c>db</c> and <c>items</c> in a new tenant on the assembly's server; customized hosts remain test-owned.</summary>
    public static Task<DocumentApi> StartAsync(
        Action<Dictionary<string, string>>? configure = null, Action<IServiceCollection>? services = null,
        TimeProvider? time = null, CancellationToken cancellationToken = default) =>
        StartCoreAsync(configure, services, time, ownsServer: configure is not null || services is not null || time is not null, cancellationToken);

    /// <summary>Owns a separate host for tests that stop it, mutate system storage or measure host-wide state.</summary>
    public static Task<DocumentApi> StartIsolatedAsync() => StartCoreAsync(null, null, null, ownsServer: true, CancellationToken.None);

    private static async Task<DocumentApi> StartCoreAsync(
        Action<Dictionary<string, string>>? configure, Action<IServiceCollection>? services,
        TimeProvider? time, bool ownsServer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var management = ownsServer
            ? await ManagementApi.StartAsync(configure, services, time)
            : await SharedDocumentServer.GetAsync(cancellationToken);
        Tenant? tenant = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            tenant = await management.CreateTenantAsync("documents:read", "documents:write");
            var api = new DocumentApi(management, tenant, ownsServer);
            (await api.SendAsync(HttpMethod.Put, api.DatabasePath(), cancellationToken: cancellationToken)).Status.ShouldBe(HttpStatusCode.Created);
            (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath()}/collections", new { name = Collection }, cancellationToken: cancellationToken)).Status.ShouldBe(HttpStatusCode.Created);
            return api;
        }
        catch
        {
            // The tenant's client is not the management API's to dispose.
            tenant?.Dispose();
            if (ownsServer)
            {
                await management.DisposeAsync();
            }
            throw;
        }
    }

    /// <summary>The data API route of the database named <paramref name="name"/> in the tenant's project.</summary>
    public string DatabasePath(string name = Database) => $"{Project}/databases/{name}";

    /// <summary>Deletes a database of the tenant's project through the Management API, as the tenant's owner.</summary>
    public async Task DeleteDatabaseAsync(string name = Database, CancellationToken cancellationToken = default)
    {
        // Creating an existing database is idempotent and returns its id.
        var database = await SendAsync(HttpMethod.Put, DatabasePath(name), cancellationToken: cancellationToken);
        database.Status.ShouldBe(HttpStatusCode.OK);
        using var owner = Management.As(Tenant.User);
        using var deleted = await owner.DeleteAsync(new Uri(
            $"/v1/management/organizations/{Tenant.OrganizationId}/projects/{Tenant.ProjectId}/databases/{database.Json.GetProperty("id").GetString()}",
            UriKind.Relative), cancellationToken);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// Registers database <paramref name="name"/> in the tenant's project, then has the engine forget its store and
    /// file and lets <paramref name="write"/> write a database file at its path directly (<see cref="DocumentFiles"/>);
    /// the next request opens that file. Returns the path.
    /// </summary>
    public async Task<string> ReplaceFileAsync(string name, Func<string, Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var created = await SendAsync(HttpMethod.Put, DatabasePath(name));
        var id = created.Json.GetProperty("id").GetString()!;
        var database = new TenantContext(Tenant.OrganizationId, PlanTier.Hobby, Tenant.ProjectId, id);
        await Server.Services.GetRequiredService<IStorageEngine>().DeleteDatabaseAsync(database, CancellationToken.None);
        var path = Path.Combine(Server.DataDirectory, Tenant.OrganizationId, Tenant.ProjectId, id + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await write(path);
        return path;
    }

    /// <summary>The tenant context of database <paramref name="name"/> of the tenant's project, as the engine addresses it.</summary>
    public async Task<TenantContext> TenantOfAsync(string name = Database, CancellationToken cancellationToken = default)
    {
        // Creating an existing database is idempotent and returns its id.
        var database = await SendAsync(HttpMethod.Put, DatabasePath(name), cancellationToken: cancellationToken);
        database.Status.ShouldBe(HttpStatusCode.OK);
        return new TenantContext(Tenant.OrganizationId, PlanTier.Hobby, Tenant.ProjectId, database.Json.GetProperty("id").GetString());
    }

    /// <summary>The file of database <paramref name="name"/> of the tenant's project, under its tenant's directory.</summary>
    public async Task<string> DatabaseFileAsync(string name = Database)
    {
        // Creating an existing database is idempotent and returns its id.
        var database = await SendAsync(HttpMethod.Put, DatabasePath(name));
        database.Status.ShouldBe(HttpStatusCode.OK);
        return Path.Combine(Server.DataDirectory, Tenant.OrganizationId, Tenant.ProjectId, database.Json.GetProperty("id").GetString() + ".db");
    }

    /// <summary>A connection string for a file at <paramref name="path"/>, built rather than interpolated.</summary>
    public static string ConnectionString(string path, bool readOnly = false) => TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    });

    /// <summary>A new document id.</summary>
    public static string NewId() => EntityIds.New().ToString();

    public string Document(string id) => $"{Documents}/by-key?id={Uri.EscapeDataString(id)}";

    /// <summary>A client for the same server and tenant with other scopes.</summary>
    public async Task<HttpClient> ClientWithScopesAsync(params string[] scopes) =>
        Management.DataClient(await Server.IssueAsync(Tenant.OrganizationId, scopes));

    public Task<ApiResponse> PutAsync(
        string id, object document, Action<HttpRequestMessage>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, Document(id), document, configure, cancellationToken);

    public Task<ApiResponse> GetAsync(string id, Action<HttpRequestMessage>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Document(id), configure: configure, cancellationToken: cancellationToken);

    public Task<ApiResponse> QueryAsync(object query, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Query, query, cancellationToken: cancellationToken);

    public async Task<(long Units, long Reads)> ReadUsageAsync()
    {
        using var owner = Management.As(Tenant.User);
        var usage = await owner.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(
            $"/v1/management/organizations/{Tenant.OrganizationId}/projects/{Tenant.ProjectId}/usage"));
        return (usage.GetProperty("month").GetProperty("readUnits").GetInt64(), usage.GetProperty("reads").GetInt64());
    }

    public Task<ApiResponse> TransactAsync(params object[] ops) => TransactAsync(ops, CancellationToken.None);

    public Task<ApiResponse> TransactAsync(object[] ops, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, Transactions, new { ops }, cancellationToken: cancellationToken);

    public Task<ApiResponse> SendAsync(
        HttpMethod method, string uri, object? body = null, Action<HttpRequestMessage>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(Client, method, uri, body is null ? null : JsonContent.Create(body), configure, cancellationToken);

    public Task<ApiResponse> SendRawAsync(
        HttpMethod method, string uri, string body, string mediaType, Action<HttpRequestMessage>? configure = null, CancellationToken cancellationToken = default) =>
        SendAsync(Client, method, uri, new StringContent(body, Encoding.UTF8, mediaType), configure, cancellationToken);

    public static async Task<ApiResponse> SendAsync(
        HttpClient client, HttpMethod method, string uri, HttpContent? content = null, Action<HttpRequestMessage>? configure = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, new Uri(uri, UriKind.Relative)) { Content = content };
        configure?.Invoke(request);
        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement json = default;
        if (text.Length > 0)
        {
            using var document = JsonDocument.Parse(text);
            json = document.RootElement.Clone();
        }

        return new ApiResponse(response.StatusCode, json, response.Headers.ETag?.Tag.Trim('"'), response.Headers.Location);
    }

    public static Action<HttpRequestMessage> IfMatch(string version) =>
        request => request.Headers.IfMatch.Add(version == "*" ? EntityTagHeaderValue.Any : new EntityTagHeaderValue($"\"{version}\""));

    public static Action<HttpRequestMessage> IfNoneMatch(string tag) =>
        request => request.Headers.IfNoneMatch.Add(tag == "*" ? EntityTagHeaderValue.Any : new EntityTagHeaderValue($"\"{tag}\""));

    public async ValueTask DisposeAsync()
    {
        Tenant.Dispose();
        if (_ownsServer)
        {
            await Management.DisposeAsync();
        }
    }
}

internal static class StatusAssertions
{
    /// <summary>Fails setup early when a precondition request does not return <paramref name="expected"/>.</summary>
    public static void ShouldBe(this HttpStatusCode actual, HttpStatusCode expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"expected {expected}, got {actual}");
        }
    }
}
