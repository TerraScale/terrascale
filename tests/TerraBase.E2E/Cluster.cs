using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.E2E;

public sealed class Cluster : IAsyncDisposable
{
    public const string Gru = "gru";
    private readonly ManagementApi _api;
    private readonly HttpClient _client;
    private readonly ManagedDatabaseRef _database;

    private Cluster(ManagementApi api, HttpClient client, ManagedDatabaseRef database)
    {
        _api = api;
        _client = client;
        _database = database;
    }

    public string OrganizationId => _database.Organization;

    public string ProjectPath => $"/v1/projects/{_database.Project}";

    public string DatabasePath => $"{ProjectPath}/databases/{_database.Name}";

    public string Collection { get; } = "e2e-" + EntityIds.New().ToString().ToLowerInvariant();

    public IReadOnlyList<string> Regions => [Gru];

    public string Identity(string region) => region == Gru ? "local:" + Gru : throw new ArgumentOutOfRangeException(nameof(region));

    public HttpClient this[string region] => region == Gru ? _client : throw new ArgumentOutOfRangeException(nameof(region));

    public Uri Document(string id) => new($"{DatabasePath}/collections/{Collection}/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);

    internal static Task<Cluster> StartBackupAsync() => StartAsync();

    internal static Task<Cluster> StartMirroredAsync() => StartAsync();

    public static async Task<Cluster> StartAsync()
    {
        var bucket = new FakeS3();
        var api = await ManagementApi.StartAsync(settings =>
        {
            bucket.Configure(settings);
            settings["region"] = Gru;
        }, services =>
        {
            bucket.Route(services);
            ManualBackupHosting.Configure(services);
        });
        HttpClient? client = null;
        try
        {
            var database = await api.CreateManagedDatabaseAsync("canary");
            await api.SetPlanAsync(api.User, database.Organization, "enterprise");
            client = api.WithToken(api.Issuer.Mint(api.User, token =>
            {
                token.Claims["project_id"] = database.Project;
                token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
            }));
            var cluster = new Cluster(api, client, database);
            await CreateCollectionAsync(new(StringComparer.Ordinal) { [Gru] = client }, cluster.DatabasePath, cluster.Collection);
            return cluster;
        }
        catch
        {
            client?.Dispose();
            await api.DisposeAsync();
            throw;
        }
    }

    internal Task ShipBackupAsync(CancellationToken token) => _api.Server.Services.GetRequiredService<IContinuousBackup>().RunOnceAsync(token);

    internal HttpClient ObjectClient(string region) => region == Gru
        ? _api.Server.Services.GetRequiredService<IHttpClientFactory>().CreateClient(S3ObjectStore.HttpClientName)
        : throw new ArgumentOutOfRangeException(nameof(region));

    internal static async Task CreateCollectionAsync(Dictionary<string, HttpClient> clients, string path, string collection)
    {
        foreach (var client in clients.Values)
        {
            using var response = await client.PostAsJsonAsync(new Uri(path + "/collections", UriKind.Relative), new { name = collection });
            await Docs.EnsureSuccessAsync(response);
        }
    }

    internal static async Task DeleteDatabaseAsync(Func<string, HttpClient> regions, string via, string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var response = await Docs.AdmittedAsync(() => regions(via).DeleteAsync(new Uri(path, UriKind.Relative), deadline.Token), deadline.Token);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await Docs.EnsureSuccessAsync(response);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _api.DisposeAsync();
    }
}
