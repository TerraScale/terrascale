using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Primitives;

namespace TerraBase.Testing;

internal sealed class ProjectApi : IAsyncDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-project-").FullName;
    private readonly string _machine = "project-" + EntityIds.New().ToString().ToLowerInvariant();
    private ManagementApi? _api;
    private HttpClient? _client;

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public ManagementApi Api => _api ?? throw new InvalidOperationException("server is stopped");
    public HttpClient Client => _client ?? throw new InvalidOperationException("server is stopped");
    public ManagedDatabaseRef Database { get; private set; } = null!;

    public static Task<ProjectApi> StartAsync(CancellationToken token) => StartAsync(null, token);

    public static async Task<ProjectApi> StartAsync(Action<IServiceCollection>? services, CancellationToken token)
    {
        var fixture = new ProjectApi();
        try
        {
            await fixture.StartNodeAsync(fixture._machine, services, token);
            fixture.Database = await fixture.Api.CreateManagedDatabaseAsync();
            await fixture.Api.VerifyProjectBindingAsync(fixture.Database.Organization, fixture.Database.Project);
            fixture._client = fixture.ProjectClient();
            await ManagementApi.CreateCollectionAsync(fixture.Client, fixture.Database, "items", token);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public HttpClient ProjectClient(string scopes = "documents:read documents:write", string? project = null, string? database = null) =>
        Api.WithToken(Api.Issuer.Mint(Api.User, descriptor =>
        {
            descriptor.Claims["project_id"] = project ?? Database.Project;
            descriptor.Claims["scope"] = scopes;
            if (database is not null)
            {
                descriptor.Claims["database"] = database;
            }
        }));

    public async Task StopAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_api is { } api)
        {
            _api = null;
            await api.DisposeAsync();
        }
    }

    private async Task StartNodeAsync(string machine, Action<IServiceCollection>? configure, CancellationToken token) =>
        _api = await ManagementApi.StartAsync(settings =>
        {
            settings["data-directory"] = _directory;
            settings["node-name"] = machine;
            settings["region"] = "gru";
            settings["auth-issuer"] = TestIssuer.Issuer;
        }, configure, Clock, cancellationToken: token);

    public async Task<JsonElement> JsonAsync(string path, CancellationToken token)
    {
        using var response = await Client.GetAsync(ManagementApi.Uri(path), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
