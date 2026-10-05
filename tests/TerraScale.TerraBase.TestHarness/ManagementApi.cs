using System.Net.Http.Headers;
using System.Text.Json;

namespace TerraScale.TerraBase.Testing;

/// <summary>
/// A server with the client listener that trusts a <see cref="TestIssuer"/>, and clients that call it like ts-dashboard
/// forwarding a signed-in user's ts-auth access token.
/// </summary>
public sealed class ManagementApi : IAsyncDisposable
{
    private ManagementApi(TerraBaseTestServer server, ClientTls tls, TestIssuer issuer, string user)
    {
        Server = server;
        Tls = tls;
        Issuer = issuer;
        User = user;
        Client = As(user);
    }

    public TerraBaseTestServer Server { get; }

    public ClientTls Tls { get; }

    /// <summary>The ts-auth stand-in whose tokens the server accepts.</summary>
    public TestIssuer Issuer { get; }

    public string User { get; }

    /// <summary>A client acting as <see cref="User"/>.</summary>
    public HttpClient Client { get; }

    /// <summary>Starts a server; <paramref name="services"/> replaces services as in <see cref="TerraBaseTestServer.StartAsync"/>.</summary>
    public static async Task<ManagementApi> StartAsync(
        Action<Dictionary<string, string>>? configure = null, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null)
    {
        var tls = new ClientTls();
        var issuer = new TestIssuer();
        try
        {
            var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
                configure?.Invoke(settings);
            }, collection =>
            {
                issuer.Route(collection);
                services?.Invoke(collection);
            });
            // Startup waited for the issuer's keys, so tokens work from the first request.
            return new ManagementApi(server, tls, issuer, Guid.NewGuid().ToString());
        }
        catch
        {
            try
            {
                tls.Dispose();
            }
            finally
            {
                issuer.Dispose();
            }

            throw;
        }
    }

    /// <summary>A client acting as <paramref name="user"/>, with <paramref name="email"/> as its verified email when set.</summary>
    public HttpClient As(string user, string? email = null) =>
        Authorized(() => email is null ? Issuer.Mint(user) : Issuer.MintWithEmail(user, email));

    /// <summary>A client that sends <paramref name="token"/> as its bearer token, or no token when it is null.</summary>
    public HttpClient WithToken(string? token) => Tls.CreateClient(Server.ClientAddress!, token);

    /// <summary>A data API client for the client listener that sends <paramref name="apiKey"/>, if any.</summary>
    public HttpClient DataClient(string? apiKey = null) => Tls.CreateClient(Server.ClientAddress!, apiKey);

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
                try
                {
                    Tls.Dispose();
                }
                finally
                {
                    Issuer.Dispose();
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

    // A fresh token on every request, so a long test never sends an expired one.
    private HttpClient Authorized(Func<string> token) =>
        new(new BearerToken(token) { InnerHandler = Tls.CreateHandler() })
        {
            BaseAddress = Server.ClientAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };

    private sealed class BearerToken(Func<string> token) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token());
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
