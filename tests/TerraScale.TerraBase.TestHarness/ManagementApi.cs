using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TerraScale.TerraBase.Testing;

/// <summary>
/// A server with the client listener and the dashboard credential, and clients that send it like
/// ts-dashboard: the acting user on every request and, on organization routes, the organization from the path.
/// </summary>
public sealed class ManagementApi : IAsyncDisposable
{
    public const string Credential = "test-dashboard-credential-0123456789abcdef";
    public const string UserHeader = "X-TerraBase-User-Id";
    public const string OrganizationHeader = "X-TerraBase-Organization-Id";

    private readonly string _directory;

    private ManagementApi(TerraBaseTestServer server, ClientTls tls, string directory, string user)
    {
        Server = server;
        Tls = tls;
        _directory = directory;
        User = user;
        Client = As(user);
    }

    public TerraBaseTestServer Server { get; }

    public ClientTls Tls { get; }

    public string User { get; }

    /// <summary>A dashboard client acting as <see cref="User"/>.</summary>
    public HttpClient Client { get; }

    /// <summary>Starts a server; <paramref name="services"/> replaces services as in <see cref="TerraBaseTestServer.StartAsync"/>.</summary>
    public static async Task<ManagementApi> StartAsync(
        Action<Dictionary<string, string>>? configure = null, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-dashboard-").FullName;
        var tls = new ClientTls();
        try
        {
            var credentialFile = Path.Combine(directory, "credential");
            await File.WriteAllTextAsync(credentialFile, Credential + "\n");
            var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["dashboard-credential-file"] = credentialFile;
                configure?.Invoke(settings);
            }, services);
            return new ManagementApi(server, tls, directory, Guid.NewGuid().ToString());
        }
        catch
        {
            try
            {
                tls.Dispose();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }

            throw;
        }
    }

    /// <summary>A dashboard client acting as <paramref name="user"/>.</summary>
    public HttpClient As(string user)
    {
        var client = new HttpClient(new DashboardHeaders(user) { InnerHandler = Tls.CreateHandler() })
        {
            BaseAddress = Server.ClientAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
        return client;
    }

    /// <summary>A data API client for the client listener that sends <paramref name="apiKey"/>, if any.</summary>
    public HttpClient DataClient(string? apiKey = null) => Tls.CreateClient(Server.ClientAddress!, apiKey);

    /// <summary>Sends a request after <paramref name="edit"/> changes its final headers.</summary>
    public async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, Action<HttpRequestHeaders> edit)
    {
        using var client = new HttpClient(new DashboardHeaders(User, edit) { InnerHandler = Tls.CreateHandler() }) { BaseAddress = Server.ClientAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    public async Task<string> CreateOrganizationAsync(string name, HttpClient? client = null) =>
        await CreateAsync(client, "/v1/management/organizations", name);

    public async Task<string> CreateProjectAsync(string organization, string name, HttpClient? client = null) =>
        await CreateAsync(client, $"/v1/management/organizations/{organization}/projects", name);

    public async Task<string> CreateDatabaseAsync(string organization, string project, string name, HttpClient? client = null) =>
        await CreateAsync(client, $"/v1/management/organizations/{organization}/projects/{project}/databases", name);

    /// <summary>
    /// Creates an organization with one project, as a new user, and a data client whose API key has
    /// <paramref name="scopes"/> and reaches the whole organization.
    /// </summary>
    public async Task<Tenant> CreateTenantAsync(params string[] scopes)
    {
        var user = Guid.NewGuid().ToString();
        using var owner = As(user);
        var organization = await CreateOrganizationAsync("Tenant " + Guid.NewGuid(), owner);
        var project = await CreateProjectAsync(organization, "Main", owner);
        return new Tenant(user, organization, project, DataClient(await Server.IssueAsync(organization, scopes)));
    }

    /// <summary>Has a new user join <paramref name="organization"/> as <paramref name="role"/> through an invitation from <paramref name="inviter"/>; returns its id.</summary>
    public async Task<string> AddMemberAsync(HttpClient inviter, string organization, string role)
    {
        ArgumentNullException.ThrowIfNull(inviter);
        var user = Guid.NewGuid().ToString();
        var email = $"{Guid.NewGuid():N}@example.com";
        using var invited = await inviter.PostAsJsonAsync(
            new Uri($"/v1/management/organizations/{organization}/invitations", UriKind.Relative), new { email, role });
        invited.EnsureSuccessStatusCode();
        var token = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var member = As(user);
        using var accepted = await member.PostAsJsonAsync(new Uri("/v1/management/invitations/accept", UriKind.Relative), new { token, email });
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
                try
                {
                    Tls.Dispose();
                }
                finally
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
        }
    }

    private async Task<string> CreateAsync(HttpClient? client, string path, string name)
    {
        using var response = await (client ?? Client).PostAsJsonAsync(new Uri(path, UriKind.Relative), new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private sealed class DashboardHeaders(string user, Action<HttpRequestHeaders>? edit = null) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(UserHeader, user);
            var segments = request.RequestUri!.AbsolutePath.Split('/');
            if (segments.Length > 4 && segments[3] == "organizations")
            {
                request.Headers.Add(OrganizationHeader, segments[4]);
            }

            edit?.Invoke(request.Headers);
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
