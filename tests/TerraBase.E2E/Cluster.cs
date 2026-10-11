using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.E2E;

/// <summary>
/// One database served by two regions, gru and sjc, with a client pinned to each. Without <c>TERRABASE_E2E_FLY_URL</c>
/// the regions are two in-process nodes on the harness's region bus. With it they are the Fly app's machines, reached
/// through the public proxy and pinned by <see cref="RegionPin"/>. Each test owns one collection of the database.
/// </summary>
public sealed class Cluster : IAsyncDisposable
{
    public const string Gru = "gru";
    public const string Sjc = "sjc";
    public const string FlyUrlVariable = "TERRABASE_E2E_FLY_URL";

    private static readonly string[] Scopes = ["documents:read", "documents:write"];


    private readonly HttpClient _gru;
    private readonly HttpClient _sjc;
    private readonly Func<Task> _release;

    private Cluster(string databasePath, string collection, HttpClient gru, HttpClient sjc, Func<Task> release)
    {
        DatabasePath = databasePath;
        Collection = collection;
        _gru = gru;
        _sjc = sjc;
        _release = release;
    }

    /// <summary>The database's path, <c>/v1/projects/{project}/databases/{database}</c>.</summary>
    public string DatabasePath { get; }

    /// <summary>This test's collection in the database.</summary>
    public string Collection { get; }

    /// <summary>Whether <c>TERRABASE_E2E_FLY_URL</c> is set, so the regions are Fly's.</summary>
    public static bool FlyRequested => Environment.GetEnvironmentVariable(FlyUrlVariable) is { Length: > 0 };

    /// <summary>The client of <paramref name="region"/>: pinned on Fly, the node itself locally.</summary>
    public HttpClient this[string region] => region switch
    {
        Gru => _gru,
        Sjc => _sjc,
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "unknown region"),
    };

    /// <summary>The document with <paramref name="id"/> in this test's collection.</summary>
    public Uri Document(string id) => new($"{DatabasePath}/collections/{Collection}/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);

    /// <summary>Starts the regions the environment selects: Fly when <c>TERRABASE_E2E_FLY_URL</c> is set, otherwise two local nodes.</summary>
    public static Task<Cluster> StartAsync() => FlyRequested ? StartFlyAsync() : StartLocalAsync();

    public async ValueTask DisposeAsync()
    {
        await _release();
    }

    private static async Task<Cluster> StartLocalAsync()
    {
        var network = new FakeRegionNetwork();
        ManagementApi? gruApi = null;
        ManagementApi? sjcApi = null;
        HttpClient? gru = null;
        HttpClient? sjc = null;
        try
        {
            gruApi = await StartNodeAsync(network, Gru);
            sjcApi = await StartNodeAsync(network, Sjc);
            var organization = EntityIds.New().ToString();
            var project = EntityIds.New().ToString();
            var database = EntityIds.New().ToString();
            // The database lives in gru with sjc as a replica; sjc learns it over the region bus.
            await CommitAsync(gruApi, Registration(organization, project, database, "canary", Sjc));
            await CommitAsync(sjcApi, Registration(organization, project, null, "canary"));
            // The enterprise plan lifts the organization's request budget, as the cross-region suite does. Fly's plan is its own.
            await gruApi.SetPlanAsync(gruApi.User, organization, "enterprise");
            await sjcApi.SetPlanAsync(sjcApi.User, organization, "enterprise");
            gru = await gruApi.CreateClientAsync(organization, Scopes);
            sjc = await sjcApi.CreateClientAsync(organization, Scopes);
            var path = $"/v1/projects/{project}/databases/canary";
            var collection = NewCollection();
            await CreateCollectionAsync(gru, sjc, path, collection);
            return new Cluster(path, collection, gru, sjc, async () =>
            {
                gru.Dispose();
                sjc.Dispose();
                await gruApi.DisposeAsync();
                await sjcApi.DisposeAsync();
            });
        }
        catch
        {
            gru?.Dispose();
            sjc?.Dispose();
            if (sjcApi is not null)
            {
                await sjcApi.DisposeAsync();
            }

            if (gruApi is not null)
            {
                await gruApi.DisposeAsync();
            }

            throw;
        }
    }

    // The Fly database is shared: this test creates and deletes only its own collection.
    private static async Task<Cluster> StartFlyAsync()
    {
        var url = new Uri(Required(FlyUrlVariable), UriKind.Absolute);
        var key = Required("TERRABASE_E2E_FLY_API_KEY");
        var path = $"/v1/projects/{Required("TERRABASE_E2E_FLY_PROJECT")}/databases/{Required("TERRABASE_E2E_FLY_DATABASE")}";
        var gru = FlyClient(url, key, Gru);
        var sjc = FlyClient(url, key, Sjc);
        var collection = NewCollection();
        try
        {
            await CreateCollectionAsync(gru, sjc, path, collection);
        }
        catch (Exception error)
        {
            try
            {
                await DeleteCollectionAsync(gru, path, collection);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(error, cleanup);
            }
            finally
            {
                gru.Dispose();
                sjc.Dispose();
            }

            throw;
        }

        return new Cluster(path, collection, gru, sjc, async () =>
        {
            try
            {
                await DeleteCollectionAsync(gru, path, collection);
            }
            finally
            {
                gru.Dispose();
                sjc.Dispose();
            }
        });
    }

    // Creates the collection in gru and waits until sjc serves it, so every test starts from a replicated database.
    private static async Task CreateCollectionAsync(HttpClient gru, HttpClient sjc, string path, string collection)
    {
        using var created = await gru.PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = collection });
        await Docs.EnsureSuccessAsync(created);
        var uri = new Uri($"{path}/collections/{collection}", UriKind.Relative);
        _ = await Converge.UntilAsync(() => Docs.StatusAsync(sjc, uri), status => status == HttpStatusCode.OK, "collection on sjc");
    }

    // A missing collection is already gone, so only the answers that leave data behind fail the cleanup.
    private static async Task DeleteCollectionAsync(HttpClient gru, string path, string collection)
    {
        using var deleted = await gru.DeleteAsync(new Uri($"{path}/collections/{collection}", UriKind.Relative));
        if (deleted.StatusCode != HttpStatusCode.NotFound)
        {
            await Docs.EnsureSuccessAsync(deleted);
        }
    }

    private static string NewCollection() => $"e2e-{Guid.NewGuid():N}";

    private static Task<ManagementApi> StartNodeAsync(FakeRegionNetwork network, string region) =>
        ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = $"{region}-member";
            settings["wan-regions"] = "gru=gru,sjc=sjc";
        }, services =>
        {
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
            services.AddSingleton(network.Join(region));
            services.AddSingleton<IMemberDirectory>(new FakeMemberStore());
        }, managementCredential: true);

    private static async Task CommitAsync(ManagementApi api, string commit)
    {
        using var service = api.Service(api.User);
        using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
        await Docs.EnsureSuccessAsync(response);
    }

    // An organization, project and database with chosen ids, in one commit.
    private static string Registration(string organization, string project, string? database, string name, params string[] replicas) => JsonSerializer.Serialize(new
    {
        id = EntityIds.New().ToString(),
        ops = new object?[]
        {
            new { op = "create_organization", id = organization, name = $"e2e-{organization}" },
            new { op = "create_project", organization, id = project, name = "e2e" },
            database is null ? null : new { op = "create_database", organization, project, name, id = database, replica_regions = replicas },
        }.OfType<object>(),
    });

    private static HttpClient FlyClient(Uri url, string key, string region)
    {
        var client = new HttpClient(new RegionPin(region) { InnerHandler = new SocketsHttpHandler() }) { BaseAddress = url, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} must be set to run the suite against Fly");
}
