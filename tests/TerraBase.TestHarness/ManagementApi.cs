using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Primitives;

namespace TerraBase.Testing;

/// <summary>
/// A server with the client listener that trusts a <see cref="TestIssuer"/>, and clients that call it like ts-dashboard
/// forwarding a signed-in user's ts-auth access token.
/// </summary>
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

    /// <summary>The ts-auth stand-in whose tokens the server accepts.</summary>
    public TestIssuer Issuer { get; }

    public string User { get; }

    /// <summary>A client acting as <see cref="User"/>.</summary>
    public HttpClient Client { get; }

    internal Task CleanupCompletion => _cleanup.Value;

    /// <summary>
    /// Starts a server; <paramref name="configure"/> edits its settings after the issuer is configured, <paramref name="time"/>
    /// replaces its clock and <paramref name="services"/> its other services.
    /// </summary>
    public static async Task<ManagementApi> StartAsync(
        Action<Dictionary<string, string>>? configure = null, Action<IServiceCollection>? services = null, TimeProvider? time = null,
        CancellationToken? cancellationToken = null, Action<Task>? failedStartCleanup = null)
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
            }, cancellationToken: cancellationToken, failedStartCleanup: task => failedCleanup = task);
            // Startup waited for the issuer's keys, so tokens work from the first request.
            return new ManagementApi(server, tls, issuer, EntityIds.New().ToString());
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

    /// <summary>A client acting as <paramref name="user"/>, with <paramref name="email"/> as its verified email when set.</summary>
    public HttpClient As(string user, string? email = null) =>
        Authorized(() => email is null ? Issuer.Mint(user) : Issuer.MintWithEmail(user, email));

    /// <summary>A client acting as <paramref name="user"/> through the platform operator on the internal listener.</summary>
    public HttpClient Service(string user)
    {
        var client = PeerTls.CreateClient(Server.BaseAddress);
        client.DefaultRequestHeaders.Add("X-TerraBase-User-Id", user);
        return client;
    }

    /// <summary>Publishes a resource plan through the trusted service listener, retaining owner authorization.</summary>
    public async Task SetPlanAsync(string user, string organization, string plan, CancellationToken cancellationToken = default)
    {
        using var service = Service(user);
        using var response = await service.PutAsJsonAsync(Uri($"/v1/management/organizations/{organization}/plan"), new { plan }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>A client that sends <paramref name="token"/> as its bearer token, or no token when it is null.</summary>
    public HttpClient WithToken(string? token) => Tls.CreateClient(Server.ClientAddress!, token);

    /// <summary>A data API client for the client listener that sends <paramref name="apiKey"/>, if any.</summary>
    public HttpClient DataClient(string? apiKey = null) => Tls.CreateClient(Server.ClientAddress!, apiKey);

    /// <summary>A data API client with a fresh API key of <paramref name="organization"/> holding <paramref name="scopes"/>.</summary>
    public async Task<HttpClient> CreateClientAsync(string organization, params string[] scopes) =>
        DataClient(await Server.IssueAsync(organization, scopes));

    /// <summary>A relative URI.</summary>
    public static Uri Uri(string path) => new(path, UriKind.Relative);

    public async Task<string> CreateOrganizationAsync(string name, HttpClient? client = null) =>
        await CreateAsync(client, "/v1/management/organizations", name);

    public async Task<string> CreateProjectAsync(string organization, string name, HttpClient? client = null) =>
        await CreateAsync(client, $"/v1/management/organizations/{organization}/projects", name);

    /// <summary>Explicitly proves the sign-in caller's ts-auth project authority and verifies an existing legacy binding.</summary>
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

    /// <summary>Creates an organization, a project and a database in it, as <see cref="User"/>.</summary>
    public async Task<ManagedDatabaseRef> CreateManagedDatabaseAsync(string name = "main")
    {
        var organization = await CreateOrganizationAsync("Org " + EntityIds.New().ToString());
        var project = await CreateProjectAsync(organization, "Project");
        return await CreateManagedDatabaseAsync(organization, project, name);
    }

    /// <summary>Creates a database in an existing project, as <see cref="User"/>.</summary>
    public async Task<ManagedDatabaseRef> CreateManagedDatabaseAsync(string organization, string project, string name) =>
        new(organization, project, await CreateDatabaseAsync(organization, project, name), name);

    /// <summary>A caller-supplied document key, ordered by its readable fixture name.</summary>
    public static string DocumentId(string name) => name;

    /// <summary>The fixture name is its document key.</summary>
    public static string DocumentName(string id) => id;

    /// <summary>The data API route of document <see cref="DocumentId"/>(<paramref name="name"/>) of a collection of <paramref name="database"/>.</summary>
    public static string DocumentPath(ManagedDatabaseRef database, string collection, string name) =>
        $"{database.Data}/collections/{collection}/documents/by-key?id={System.Uri.EscapeDataString(DocumentId(name))}";

    /// <summary>Creates <paramref name="collection"/> through the data API, unless it exists.</summary>
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

    /// <summary>
    /// Runs one statement straight on the file of <paramref name="database"/> and returns the rows it changed: for stored
    /// data the data API no longer accepts (written by an earlier build), such as duplicate members or legacy ids.
    /// </summary>
    public static async Task<int> ExecuteStoredAsync(string dataDirectory, ManagedDatabaseRef database, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(database);
        var path = Path.Combine(dataDirectory, database.Organization, database.Project, database.Id + ".db");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        StoredRegisters.Register(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Replaces the stored JSON of the live document <paramref name="name"/> (see <see cref="ExecuteStoredAsync"/>).</summary>
    public static async Task ReplaceStoredBodyAsync(string dataDirectory, ManagedDatabaseRef database, string collection, string name, string json)
    {
        var changed = await ExecuteStoredAsync(
            dataDirectory,
            database,
            "UPDATE documents SET body = $body WHERE collection = $collection AND id = $id AND body IS NOT NULL;",
            ("$body", json),
            ("$collection", collection),
            ("$id", DocumentId(name)));
        if (changed != 1)
        {
            throw new InvalidOperationException("the document to replace does not exist");
        }
    }

    /// <summary>
    /// Creates document <paramref name="name"/> as <c>{}</c> through the data API, then stores <paramref name="json"/> as its
    /// body straight in the file: for bodies the data API refuses, and for megabytes of seed data a test would otherwise upload.
    /// </summary>
    public async Task PutStoredAsync(HttpClient data, ManagedDatabaseRef database, string collection, string name, string json)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var put = await data.PutAsync(Uri(DocumentPath(database, collection, name)), content);
        put.EnsureSuccessStatusCode();
        await ReplaceStoredBodyAsync(Server.DataDirectory, database, collection, name, json);
    }

    /// <summary>
    /// Creates an organization with one project, as a new user, and a data client whose API key has
    /// <paramref name="scopes"/> and reaches the whole organization.
    /// </summary>
    public async Task<Tenant> CreateTenantAsync(params string[] scopes)
    {
        var user = EntityIds.New().ToString();
        using var owner = As(user);
        var organization = await CreateOrganizationAsync("Tenant " + EntityIds.New().ToString(), owner);
        var project = await CreateProjectAsync(organization, "Main", owner);
        return new Tenant(user, organization, project, DataClient(await Server.IssueAsync(organization, scopes)));
    }

    /// <summary>Has a new user join <paramref name="organization"/> as <paramref name="role"/> through an invitation from <paramref name="inviter"/>; returns its id.</summary>
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
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    // A fresh token on every request, so a long test never sends an expired one.
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

/// <summary>An organization with one project, its only member, and a data API client whose key reaches the whole organization.</summary>
public sealed class Tenant(string user, string organizationId, string projectId, HttpClient data) : IDisposable
{
    /// <summary>The organization's only member, its creator.</summary>
    public string User { get; } = user;

    public string OrganizationId { get; } = organizationId;

    public string ProjectId { get; } = projectId;

    public HttpClient Data { get; } = data;

    /// <summary>The data API route of the database named <paramref name="name"/> in <see cref="ProjectId"/>.</summary>
    public Uri Database(string name, string path = "") => new($"/v1/projects/{ProjectId}/databases/{name}{path}", UriKind.Relative);

    public void Dispose() => Data.Dispose();
}

/// <summary>A managed database created by a test.</summary>
/// <param name="Organization">Organization id.</param>
/// <param name="Project">Project id.</param>
/// <param name="Id">Database id.</param>
/// <param name="Name">Database name, which the data API addresses it by in its project.</param>
public sealed record ManagedDatabaseRef(string Organization, string Project, string Id, string Name)
{
    /// <summary><c>/v1/management/organizations/{organization}/projects/{project}</c>.</summary>
    public string ProjectPath => $"/v1/management/organizations/{Organization}/projects/{Project}";

    /// <summary><c>…/projects/{project}/databases/{id}</c>.</summary>
    public string Path => $"{ProjectPath}/databases/{Id}";

    /// <summary>The data API route of the database: <c>/v1/projects/{project}/databases/{name}</c>.</summary>
    public string Data => $"/v1/projects/{Project}/databases/{Name}";
}
