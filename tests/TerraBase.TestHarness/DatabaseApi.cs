using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Management.Usage;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

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

}

/// <summary>
/// One test's own tenant (an organization with one project), a key of that organization and database
/// <c>db</c>, plus helpers that send JSON and read whole responses.
/// </summary>
internal sealed class DatabaseApi : IAsyncDisposable
{
    public const string Database = "db";

    private readonly bool _ownsServer;

    private DatabaseApi(ManagementApi management, Tenant tenant, bool ownsServer, string databaseId)
    {
        Management = management;
        Tenant = tenant;
        _ownsServer = ownsServer;
        DatabaseId = databaseId;
    }

    /// <summary>The id of <c>db</c>.</summary>
    public string DatabaseId { get; }

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

    /// <summary>Creates <c>db</c> in a new tenant on the assembly's server; customized hosts remain test-owned.</summary>
    public static Task<DatabaseApi> StartAsync(
        Action<Dictionary<string, string>>? configure = null, Action<IServiceCollection>? services = null,
        TimeProvider? time = null, CancellationToken cancellationToken = default) =>
        StartCoreAsync(configure, services, time, ownsServer: configure is not null || services is not null || time is not null, cancellationToken);

    /// <summary>Owns a separate host for tests that stop it, mutate system storage or measure host-wide state.</summary>
    public static Task<DatabaseApi> StartIsolatedAsync() => StartCoreAsync(null, null, null, ownsServer: true, CancellationToken.None);

    private static async Task<DatabaseApi> StartCoreAsync(
        Action<Dictionary<string, string>>? configure, Action<IServiceCollection>? services,
        TimeProvider? time, bool ownsServer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var management = ownsServer
            ? await ManagementApi.StartAsync(configure, services, time)
            : await SharedServer.GetAsync(cancellationToken);
        Tenant? tenant = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            tenant = await management.CreateTenantAsync("sql:read", "sql:write");
            using var owner = management.As(tenant.User);
            var id = await management.CreateDatabaseAsync(tenant.OrganizationId, tenant.ProjectId, Database, owner);
            return new DatabaseApi(management, tenant, ownsServer, id);
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

    /// <summary>The tenant context of <c>db</c>, as the engine addresses it.</summary>
    public TenantContext TenantOf() => new(Tenant.OrganizationId, PlanTier.Hobby, Tenant.ProjectId, DatabaseId);

    /// <summary>A new ULID.</summary>
    public static string NewId() => EntityIds.New().ToString();

    /// <summary>A connection string for a file at <paramref name="path"/>, built rather than interpolated.</summary>
    public static string ConnectionString(string path, bool readOnly = false) => TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    });

    /// <summary>A client for the same server and tenant with other scopes.</summary>
    public async Task<HttpClient> ClientWithScopesAsync(params string[] scopes) =>
        Management.DataClient(await Server.IssueAsync(Tenant.OrganizationId, scopes));

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
