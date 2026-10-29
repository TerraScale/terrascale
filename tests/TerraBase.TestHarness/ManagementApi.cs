using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Primitives;

namespace TerraBase.Testing;

public sealed class ManagementApi : IAsyncDisposable
{
    private static readonly HttpRequestOptionsKey<(string Organization, string Project)> BindingAuthority = new("test-project-binding-authority");

    private readonly Lazy<Task> _cleanup;

    private ManagementApi(TerraBaseTestServer server, ClientTls tls, TestIssuer issuer, string user)
    {
        Server = server;
        Tls = tls;
        Issuer = issuer;
        User = user;
        _cleanup = new(() => CleanupAssetsAsync(server.CleanupCompletion, tls, issuer));
        Client = As(user);
    }

    public TerraBaseTestServer Server { get; }

    public ClientTls Tls { get; }
    public TestIssuer Issuer { get; }

    public string User { get; }
    public HttpClient Client { get; }

    internal Task CleanupCompletion => _cleanup.Value;
    public static async Task<ManagementApi> StartAsync(
        Action<Dictionary<string, string>>? configure = null, Action<IServiceCollection>? services = null, TimeProvider? time = null,
        CancellationToken? cancellationToken = null, Action<Task>? failedStartCleanup = null, string? userId = null)
    {
        var tls = new ClientTls();
        var issuer = new TestIssuer();
        TerraBaseTestServer? server = null;
        var failedCleanup = Task.CompletedTask;
        try
        {
            server = await TerraBaseTestServer.StartAsync(settings =>
            {
                TestIssuer.Configure(settings);
                configure?.Invoke(settings);
            }, collection =>
            {
                issuer.Route(collection);
                if (time is not null)
                {
                    collection.AddSingleton(time);
                }

                services?.Invoke(collection);
                collection.TryAddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
            }, cancellationToken: cancellationToken, failedStartCleanup: task => failedCleanup = task);
            return new ManagementApi(server, tls, issuer, userId ?? EntityIds.New().ToString());
        }
        catch (Exception startupError)
        {
            server?.Client.Dispose();
            var cleanup = CleanupAssetsAsync(server?.CleanupCompletion ?? failedCleanup, tls, issuer);
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

    public HttpClient As(string user, string? email = null) =>
        Authorized(() => email is null ? Issuer.Mint(user) : Issuer.MintWithEmail(user, email));

    public HttpClient Service(string user)
    {
        var client = PeerTls.CreateClient(Server.BaseAddress, credential: Server.Credential);
        client.DefaultRequestHeaders.Add("X-TerraBase-User-Id", user);
        return client;
    }

    public async Task SetPlanAsync(string user, string organization, string plan, CancellationToken cancellationToken = default)
    {
        using var service = Service(user);
        using var response = await service.PutAsJsonAsync(Uri($"/v1/management/organizations/{organization}/plan"), new { plan }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public HttpClient WithToken(string? token) => Tls.CreateClient(Server.ClientAddress!, token);
    public HttpClient DataClient(string? apiKey = null) => Tls.CreateClient(Server.ClientAddress!, apiKey);
    public async Task<HttpClient> CreateClientAsync(string organization, params string[] scopes) =>
        DataClient(await Server.IssueAsync(organization, scopes));
    public static Uri Uri(string path) => new(path, UriKind.Relative);

    public async Task<string> CreateOrganizationAsync(string name, HttpClient? client = null) =>
        await CreateAsync(client, "/v1/management/organizations", name);

    public async Task<string> CreateProjectAsync(string organization, string name, HttpClient? client = null) =>
        await CreateAsync(client, $"/v1/management/organizations/{organization}/projects", name);
    public async Task VerifyProjectBindingAsync(string organization, string project, HttpClient? client = null)
    {
        client ??= Client;
        if (client.DefaultRequestHeaders.Authorization is { Scheme: "Bearer", Parameter: { } token })
        {
            Issuer.GrantProject(token, organization, project);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Uri($"/v1/management/organizations/{organization}/projects/{project}/binding"))
        {
            Content = JsonContent.Create(new { organization }),
        };
        request.Options.Set(BindingAuthority, (organization, project));
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> CreateDatabaseAsync(string organization, string project, string name, HttpClient? client = null) =>
        await CreateAsync(client, $"/v1/management/organizations/{organization}/projects/{project}/databases", name);
    public async Task<ManagedDatabaseRef> CreateManagedDatabaseAsync(string name = "main")
    {
        var organization = await CreateOrganizationAsync("Org " + EntityIds.New().ToString());
        var project = await CreateProjectAsync(organization, "Project");
        return await CreateManagedDatabaseAsync(organization, project, name);
    }

    public async Task<ManagedDatabaseRef> CreateManagedDatabaseAsync(string organization, string project, string name) =>
        new(organization, project, await CreateDatabaseAsync(organization, project, name), name);
    public static string DocumentId(string name)
    {
        if (EntityIds.IsValid(name))
        {
            return name;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(Encoding.UTF8.GetByteCount(name), 16);
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        Encoding.UTF8.GetBytes(name, bytes);
        return new Ulid(bytes).ToString();
    }

    public static string DocumentName(string id) => Encoding.UTF8.GetString(Ulid.Parse(id).ToByteArray()).TrimEnd('\0');
    public static string DocumentPath(ManagedDatabaseRef database, string collection, string name) =>
        $"{database.Data}/collections/{collection}/documents/by-key?id={System.Uri.EscapeDataString(DocumentId(name))}";
    public static async Task CreateCollectionAsync(HttpClient data, ManagedDatabaseRef database, string collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(database);
        using var response = await data.PostAsJsonAsync(Uri($"{database.Data}/collections"), new { name = collection }, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }
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

    public static async Task ReplaceStoredBodyAsync(string dataDirectory, ManagedDatabaseRef database, string collection, string name, string json)
    {
        var changed = await ExecuteStoredAsync(
            dataDirectory,
            database,
            $"UPDATE {TerraBase.Engine.Turso.TursoStorageTransaction.Table(collection)} SET doc = $body WHERE id = $id;",
            ("$body", json),
            ("$id", DocumentId(name)));
        if (changed != 1)
        {
            throw new InvalidOperationException("the document to replace does not exist");
        }
    }

    public async Task PutStoredAsync(HttpClient data, ManagedDatabaseRef database, string collection, string name, string json)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var put = await data.PutAsync(Uri(DocumentPath(database, collection, name)), content);
        put.EnsureSuccessStatusCode();
        await ReplaceStoredBodyAsync(Server.DataDirectory, database, collection, name, json);
    }

    public async Task<Tenant> CreateTenantAsync(params string[] scopes)
    {
        var user = EntityIds.New().ToString();
        using var owner = As(user);
        var organization = await CreateOrganizationAsync("Tenant " + EntityIds.New().ToString(), owner);
        var project = await CreateProjectAsync(organization, "Main", owner);
        return new Tenant(user, organization, project, DataClient(await Server.IssueAsync(organization, scopes)));
    }

    public async Task<string> AddMemberAsync(HttpClient inviter, string organization, string role)
    {
        ArgumentNullException.ThrowIfNull(inviter);
        var user = EntityIds.New().ToString();
        var email = $"{EntityIds.New()}@example.com";
        using var invited = await inviter.PostAsJsonAsync(
            new Uri($"/v1/management/organizations/{organization}/invitations", UriKind.Relative), new { email, role });
        invited.EnsureSuccessStatusCode();
        var token = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var member = As(user, email);
        using var accepted = await member.PostAsJsonAsync(new Uri("/v1/management/invitations/accept", UriKind.Relative), new { token });
        accepted.EnsureSuccessStatusCode();
        return user;
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
                await Server.DisposeAsync();
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

    private static async Task CleanupAssetsAsync(Task completion, ClientTls tls, TestIssuer issuer)
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
                issuer.Dispose();
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
        new(new BearerToken(token, Issuer) { InnerHandler = Tls.CreateHandler() })
        {
            BaseAddress = Server.ClientAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private sealed class BearerToken(Func<string> token, TestIssuer issuer) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bearer = token();
            if (request.Options.TryGetValue(BindingAuthority, out var authority))
            {
                issuer.GrantProject(bearer, authority.Organization, authority.Project);
            }

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
    public string Path => $"{ProjectPath}/databases/{Id}";
    public string Data => $"/v1/projects/{Project}/databases/{Name}";
}
