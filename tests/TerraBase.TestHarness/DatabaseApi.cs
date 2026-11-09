using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Management.Usage;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

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

    public string DatabaseId { get; }

    public ManagementApi Management { get; }

    public Tenant Tenant { get; }

    public TerraBaseTestServer Server => Management.Server;

    public async Task<double> ReservedBytesAsync(CancellationToken cancellationToken = default)
    {
        await Server.Services.GetRequiredService<UsageMeter>().FlushAsync(cancellationToken);
        return await Server.MetricAsync("terrabase_memory_reserved_bytes", cancellationToken: cancellationToken);
    }

    public HttpClient Client => Tenant.Data;

    public string Project => $"/v1/projects/{Tenant.ProjectId}";

    public static Task<DatabaseApi> StartAsync(
        Action<Dictionary<string, string>>? configure = null, Action<IServiceCollection>? services = null,
        TimeProvider? time = null, CancellationToken cancellationToken = default) =>
        StartCoreAsync(configure, services, time, ownsServer: configure is not null || services is not null || time is not null, cancellationToken);

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
            tenant?.Dispose();
            if (ownsServer)
            {
                await management.DisposeAsync();
            }
            throw;
        }
    }

    public string DatabasePath(string name = Database) => $"{Project}/databases/{name}";

    public TenantContext TenantOf() => new(Tenant.OrganizationId, PlanTier.Hobby, Tenant.ProjectId, DatabaseId);

    public static string NewId() => EntityIds.New().ToString();

    public static string ConnectionString(string path, bool readOnly = false) => TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    });

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
    public static void ShouldBe(this HttpStatusCode actual, HttpStatusCode expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"expected {expected}, got {actual}");
        }
    }
}
