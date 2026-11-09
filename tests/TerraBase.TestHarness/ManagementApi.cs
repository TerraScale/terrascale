using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Primitives;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

public sealed class ManagementApi : IAsyncDisposable
{

    private readonly Lazy<Task> _cleanup;
    private readonly bool _ownsServer;

    private ManagementApi(TerraBaseTestServer server, ClientTls tls, TestIssuer issuer, string user, bool ownsIssuer, bool ownsServer = true)
    {
        Server = server;
        Tls = tls;
        Issuer = issuer;
        User = user;
        _ownsServer = ownsServer;
        _cleanup = new(() => CleanupAssetsAsync(ownsServer ? server.CleanupCompletion : Task.CompletedTask, tls, ownsIssuer ? issuer : null));
        Client = As(user);
    }

    public TerraBaseTestServer Server { get; }

    public ClientTls Tls { get; }

    public TestIssuer Issuer { get; }

    public string User { get; }

    public HttpClient Client { get; }

    internal Task CleanupCompletion => _cleanup.Value;

    public static async Task<ManagementApi> StartSharedAsync()
    {
        var shared = await SharedServer.GetAsync(CancellationToken.None);
        return new(shared.Server, new ClientTls(), shared.Issuer, EntityIds.New().ToString(), ownsIssuer: false, ownsServer: false);
    }

    internal static async Task<ManagementApi> StartSharedAsAsync(string user)
    {
        var shared = await SharedServer.GetAsync(CancellationToken.None);
        return new(shared.Server, new ClientTls(), shared.Issuer, user, ownsIssuer: false, ownsServer: false);
    }

    public static async Task<ManagementApi> StartAsync(
        Action<Dictionary<string, string>>? configure = null, Action<IServiceCollection>? services = null, TimeProvider? time = null,
        CancellationToken? cancellationToken = null, Action<Task>? failedStartCleanup = null, string? userId = null,
        CancellationToken? lifetimeCancellationToken = null, TestIssuer? sharedIssuer = null)
    {
        var tls = new ClientTls();
        var issuer = sharedIssuer ?? new TestIssuer();
        TerraBaseTestServer? server = null;
        var failedCleanup = Task.CompletedTask;
        try
        {
            server = await TerraBaseTestServer.StartAsync(settings =>
            {
                TestIssuer.Configure(settings);
                configure?.Invoke(settings);
            }, registrations =>
            {
                issuer.Route(registrations);
                if (time is not null)
                {
                    registrations.AddSingleton(time);
                }

                services?.Invoke(registrations);
                registrations.TryAddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
            }, cancellationToken: cancellationToken, failedStartCleanup: task => failedCleanup = task, lifetimeCancellationToken: lifetimeCancellationToken);
            return new ManagementApi(server, tls, issuer, userId ?? EntityIds.New().ToString(), sharedIssuer is null);
        }
        catch (Exception startupError)
        {
            server?.Client.Dispose();
            var cleanup = CleanupAssetsAsync(server?.CleanupCompletion ?? failedCleanup, tls, sharedIssuer is null ? issuer : null);
            try
            {
                failedStartCleanup?.Invoke(cleanup);
                if (cleanup.IsCompleted)
                {
                    await cleanup;
                }
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(startupError, cleanupError);
            }
            finally
            {
                ObserveDeferredCleanup(cleanup);
            }

            throw;
        }
    }

    internal Task VerifyCatalogAsync(CancellationToken cancellationToken) =>
        Server.Services.GetRequiredService<ICatalogGate>().Opened.WaitAsync(cancellationToken);

    public HttpClient As(string user) => Authorized(() => Issuer.Mint(user));

    public HttpClient Service(string user)
    {
        var client = PeerTls.CreateClient(Server.BaseAddress);
        client.DefaultRequestHeaders.Add("X-TerraBase-User-Id", user);
        return client;
    }

    public async Task SetPlanAsync(string user, string organization, string plan, CancellationToken cancellationToken = default)
    {
        using var service = Service(user);
        using var response = await service.PutAsJsonAsync(Uri($"/v1/management/organizations/{organization}/plan"), new { plan }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public string ProjectToken(string project, string scopes) => Issuer.Mint(EntityIds.New().ToString(), token =>
    {
        token.Claims["project_id"] = project;
        token.Claims["scope"] = scopes;
    });

    public HttpClient WithToken(string? token) => Tls.CreateClient(Server.ClientAddress!, token);

    public HttpClient DataClient(string? apiKey = null) => Tls.CreateClient(Server.ClientAddress!, apiKey);

    public async Task<HttpClient> CreateClientAsync(string organization, params string[] scopes) =>
        DataClient(await Server.IssueAsync(organization, scopes));

    public static Uri Uri(string path) => new(path, UriKind.Relative);

    public async Task<string> CreateOrganizationAsync(string name, HttpClient? client = null) =>
        await CreateAsync(client, "/v1/management/organizations", name);

    public async Task<string> CreateProjectAsync(string organization, string name, HttpClient? client = null)
    {
        var project = await CreateAsync(client, $"/v1/management/organizations/{organization}/projects", name);
        Issuer.RegisterProject(project, organization);
        return project;
    }

    public async Task<string> CreateDatabaseAsync(string organization, string project, string name, HttpClient? client = null, IReadOnlyList<string>? replicaRegions = null)
    {
        using var owner = ProjectClient(project);
        using var response = await owner.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name, replicaRegions });
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Create failed: {response.StatusCode}: {await response.Content.ReadAsStringAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken)}");
        }
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    public HttpClient ProjectClient(string project) => WithToken(ProjectToken(project, "databases:read databases:write"));

    public void RegisterProject(string project, string organization) => Issuer.RegisterProject(project, organization);

    public async Task<ManagedDatabaseRef> CreateManagedDatabaseAsync(string name = "main")
    {
        var organization = await CreateOrganizationAsync("Org " + EntityIds.New().ToString());
        var project = await CreateProjectAsync(organization, "Project");
        return await CreateManagedDatabaseAsync(organization, project, name);
    }

    public async Task<ManagedDatabaseRef> CreateManagedDatabaseAsync(string organization, string project, string name) =>
        new(organization, project, await CreateDatabaseAsync(organization, project, name), name);

    public static Task<HttpResponseMessage> SqlAsync(HttpClient data, string databasePath, string sql, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.PostAsJsonAsync(Uri($"{databasePath}/sql/execute"), new { statements = new[] { new { sql } } }, cancellationToken);
    }

    public static async Task<JsonElement[]> RowsAsync(HttpClient data, string databasePath, string sql, CancellationToken cancellationToken = default)
    {
        using var response = await SqlAsync(data, databasePath, sql, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return [.. body.GetProperty("results")[0].GetProperty("rows").EnumerateArray()];
    }

    public static async Task<JsonElement> WaitForRestoreAsync(HttpClient management, string databasePath, JsonElement operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(management);
        var operationId = operation.GetProperty("operationId").GetString()!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            using var response = await management.GetAsync(Uri($"{databasePath}/restores/{operationId}"), deadline.Token);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(deadline.Token);
            using var document = JsonDocument.Parse(payload);
            var current = document.RootElement.Clone();
            var status = current.GetProperty("status").GetString();
            if (status == "completed")
            {
                return current;
            }

            if (status == "failed" || deadline.IsCancellationRequested)
            {
                throw new HttpRequestException($"Restore did not complete: {payload}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
        }
    }

    public static Task<HttpResponseMessage> SqlAsync(HttpClient data, ManagedDatabaseRef database, string sql, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        return SqlAsync(data, database.Data, sql, cancellationToken);
    }

    public static async Task<int> ExecuteStoredAsync(string dataDirectory, ManagedDatabaseRef database, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(database);
        var path = Path.Combine(dataDirectory, database.Organization, database.Project, database.Id + ".db");
        await using var connection = new SqliteConnection(
            TestMasterKey.ConnectionString(path));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    public async Task<Tenant> CreateTenantAsync(params string[] scopes)
    {
        var user = EntityIds.New().ToString();
        using var owner = As(user);
        var organization = await CreateOrganizationAsync("Tenant " + EntityIds.New().ToString(), owner);
        var project = await CreateProjectAsync(organization, "Main", owner);
        return new Tenant(user, organization, project, DataClient(await Server.IssueAsync(organization, scopes)));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Client.Dispose();
        }
        finally
        {
            try
            {
                if (_ownsServer)
                {
                    await Server.DisposeAsync();
                }
            }
            finally
            {
                var cleanup = CleanupCompletion;
                ObserveDeferredCleanup(cleanup);
                if (cleanup.IsCompleted)
                {
                    await cleanup;
                }
            }
        }
    }

    private static async Task CleanupAssetsAsync(Task completion, ClientTls tls, TestIssuer? issuer)
    {
        try
        {
            await completion;
        }
        finally
        {
            try
            {
                tls.Dispose();
            }
            finally
            {
                issuer?.Dispose();
            }
        }
    }

    private static void ObserveDeferredCleanup(Task cleanup)
    {
        if (!cleanup.IsCompleted)
        {
            _ = cleanup.ContinueWith(task => TerraBaseTestServer.ReportCleanupFailure(task.Exception!.Flatten()), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task<string> CreateAsync(HttpClient? client, string path, string name)
    {
        using var response = await (client ?? Client).PostAsJsonAsync(new Uri(path, UriKind.Relative), new { name });
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Create failed: {response.StatusCode}: {await response.Content.ReadAsStringAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken)}");
        }
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private HttpClient Authorized(Func<string> token) =>
        new(new BearerToken(token) { InnerHandler = Tls.CreateHandler() })
        {
            BaseAddress = Server.ClientAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private sealed class BearerToken(Func<string> token) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bearer = token();

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

public sealed class Tenant(string user, string organizationId, string projectId, HttpClient data) : IDisposable
{
    public string User { get; } = user;

    public string OrganizationId { get; } = organizationId;

    public string ProjectId { get; } = projectId;

    public HttpClient Data { get; } = data;

    public Uri Database(string name, string path = "") => new($"/v1/projects/{ProjectId}/databases/{name}{path}", UriKind.Relative);

    public void Dispose() => Data.Dispose();
}

public sealed record ManagedDatabaseRef(string Organization, string Project, string Id, string Name)
{
    public string ProjectPath => $"/v1/management/organizations/{Organization}/projects/{Project}";

    public string Path => $"/v1/projects/{Project}/databases/{Id}";

    public string Data => $"/v1/projects/{Project}/databases/{Name}";
}
