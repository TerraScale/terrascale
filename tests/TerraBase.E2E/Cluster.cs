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
/// One database served by two or three regions (gru, sjc, fra), with a client pinned to each. Without
/// <c>TERRABASE_E2E_FLY_URL</c> the regions are in-process nodes, each with a Fly identity
/// (region and machine ID) and one shared directory standing in for Fly's <c>vms.ts-terrabase.internal</c> record, which
/// lists every machine. With it they are the Fly app's machines, reached through the public proxy and pinned by
/// <see cref="RegionPin"/>. Each test owns one collection of the database. The fault calls (<see cref="Partition"/>,
/// <see cref="StopAsync"/>, a clock skew) act on the local nodes; on Fly only stopping and starting a machine exists.
/// </summary>
public sealed class Cluster : IAsyncDisposable
{
    public const string Gru = "gru";
    public const string Sjc = "sjc";
    public const string Fra = "fra";
    public const string FlyUrlVariable = "TERRABASE_E2E_FLY_URL";

    // A ts-auth project token's scopes: an API key cannot hold databases:*, and E12 and E13 create databases.
    private const string Scopes = "databases:read databases:write documents:read documents:write";

    private readonly Dictionary<string, HttpClient> _clients;
    private readonly Local? _local;
    private readonly Func<Task> _release;

    private Cluster(string projectPath, string databasePath, string collection, Dictionary<string, HttpClient> clients, Local? local, Func<Task> release)
    {
        ProjectPath = projectPath;
        DatabasePath = databasePath;
        Collection = collection;
        _clients = clients;
        _local = local;
        _release = release;
    }

    /// <summary>The project's path, <c>/v1/projects/{project}</c>.</summary>
    public string ProjectPath { get; }

    /// <summary>The database's path, <c>/v1/projects/{project}/databases/{database}</c>.</summary>
    public string DatabasePath { get; }

    /// <summary>This test's collection in the database.</summary>
    public string Collection { get; }

    /// <summary>The regions this cluster serves the database from, home region first.</summary>
    public IReadOnlyList<string> Regions => [.. _clients.Keys];

    /// <summary>Whether <c>TERRABASE_E2E_FLY_URL</c> is set, so the regions are Fly's.</summary>
    public static bool FlyRequested => Environment.GetEnvironmentVariable(FlyUrlVariable) is { Length: > 0 };

    /// <summary>The client of <paramref name="region"/>: pinned on Fly, the node itself locally.</summary>
    public HttpClient this[string region] => _clients.TryGetValue(region, out var client)
        ? client
        : throw new ArgumentOutOfRangeException(nameof(region), region, "not a region of this cluster");

    /// <summary>The document with <paramref name="id"/> in this test's collection.</summary>
    public Uri Document(string id) => new($"{DatabasePath}/collections/{Collection}/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);

    /// <summary>
    /// Starts <paramref name="regions"/> (gru and sjc when none are named; gru is always the home region) where the
    /// environment selects: Fly when <c>TERRABASE_E2E_FLY_URL</c> is set, otherwise local nodes.
    /// </summary>
    public static Task<Cluster> StartAsync(params string[] regions) => StartAsync(regions, skews: null);

    /// <summary>Starts <paramref name="regions"/> with <paramref name="skews"/> setting a local node's clock ahead (positive) or behind its peers.</summary>
    public static Task<Cluster> StartAsync(string[] regions, IReadOnlyDictionary<string, TimeSpan>? skews)
    {
        string[] chosen = regions.Length == 0 ? [Gru, Sjc] : [Gru, .. regions.Where(region => region != Gru).Distinct()];
        if (chosen.Any(region => region is not (Gru or Sjc or Fra)))
        {
            throw new ArgumentOutOfRangeException(nameof(regions), string.Join(',', regions), "the regions are gru, sjc and fra");
        }

        return FlyRequested
            ? skews is { Count: > 0 } ? throw new NotSupportedException("a clock skew needs local nodes") : StartFlyAsync(chosen)
            : StartLocalAsync(chosen, skews ?? new Dictionary<string, TimeSpan>());
    }

    /// <summary>
    /// Local nodes only: requests between <paramref name="a"/> and <paramref name="b"/> find no answer until
    /// <see cref="Heal"/>; each side keeps accepting writes. Fly has no partition switch, so the caller skips there.
    /// </summary>
    public void Partition(string a, string b) => RequireLocal().Partition(a, b);

    /// <summary>Local nodes only: every region reaches every other again.</summary>
    public void Heal() => RequireLocal().Heal();

    /// <summary>
    /// Takes <paramref name="region"/> down: the local node stops and keeps its data directory, or Fly stops the region's
    /// machine. Requests to the region fail until <see cref="StartAgainAsync"/>.
    /// </summary>
    public async Task StopAsync(string region)
    {
        if (_local is null)
        {
            await Flyctl.RunAsync("machine", "stop", await FlyMachineAsync(region), "--app", FlyApp);
            return;
        }

        await _local.StopAsync(region);
    }

    /// <summary>Brings <paramref name="region"/> back on the same data (the same volume on Fly) after <see cref="StopAsync"/>.</summary>
    public async Task StartAgainAsync(string region)
    {
        if (_local is null)
        {
            await Flyctl.RunAsync("machine", "start", await FlyMachineAsync(region), "--app", FlyApp);
            return;
        }

        await _local.StartAgainAsync(region);
    }

    public async ValueTask DisposeAsync()
    {
        await _release();
    }

    private Local RequireLocal() => _local ?? throw new NotSupportedException("this fault needs local nodes");

    private static string FlyApp => Environment.GetEnvironmentVariable("TERRABASE_E2E_FLY_APP") ?? "ts-terrabase";

    private static Task<string> FlyMachineAsync(string region) => Flyctl.MachineInRegionAsync(FlyApp, region);

    private static async Task<Cluster> StartLocalAsync(string[] regions, IReadOnlyDictionary<string, TimeSpan> skews)
    {
        var local = new Local(regions, skews);
        try
        {
            await local.StartAsync();
            var clients = regions.ToDictionary(region => region, local.Client, StringComparer.Ordinal);
            var collection = NewCollection();
            await CreateCollectionAsync(clients, local.DatabasePath, collection);
            return new Cluster(local.ProjectPath, local.DatabasePath, collection, clients, local, async () =>
            {
                foreach (var client in clients.Values)
                {
                    client.Dispose();
                }

                await local.DisposeAsync();
            });
        }
        catch
        {
            await local.DisposeAsync();
            throw;
        }
    }

    // The Fly database is shared: this test creates and deletes only its own collection.
    private static async Task<Cluster> StartFlyAsync(string[] regions)
    {
        var url = new Uri(Required(FlyUrlVariable), UriKind.Absolute);
        var key = Required("TERRABASE_E2E_FLY_API_KEY");
        var projectPath = $"/v1/projects/{Required("TERRABASE_E2E_FLY_PROJECT")}";
        var path = $"{projectPath}/databases/{Required("TERRABASE_E2E_FLY_DATABASE")}";
        var clients = regions.ToDictionary(region => region, region => FlyClient(url, key, region), StringComparer.Ordinal);
        var collection = NewCollection();
        try
        {
            await CreateCollectionAsync(clients, path, collection);
        }
        catch (Exception error)
        {
            try
            {
                await DeleteCollectionAsync(clients[Gru], path, collection);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(error, cleanup);
            }
            finally
            {
                foreach (var client in clients.Values)
                {
                    client.Dispose();
                }
            }

            throw;
        }

        return new Cluster(projectPath, path, collection, clients, null, async () =>
        {
            try
            {
                await DeleteCollectionAsync(clients[Gru], path, collection);
            }
            finally
            {
                foreach (var client in clients.Values)
                {
                    client.Dispose();
                }
            }
        });
    }

    // Creates the collection in gru and waits until every other region serves it, so every test starts from a replicated database.
    private static async Task CreateCollectionAsync(Dictionary<string, HttpClient> clients, string path, string collection)
    {
        using var created = await clients[Gru].PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = collection });
        await Docs.EnsureSuccessAsync(created);
        var uri = new Uri($"{path}/collections/{collection}", UriKind.Relative);
        foreach (var (region, client) in clients.Where(pair => pair.Key != Gru))
        {
            _ = await Converge.UntilAsync(() => Docs.StatusAsync(client, uri), status => status == HttpStatusCode.OK, $"collection on {region}");
        }
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

    private static HttpClient FlyClient(Uri url, string key, string region)
    {
        var client = new HttpClient(new RegionPin(region) { InnerHandler = new SocketsHttpHandler() }) { BaseAddress = url, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} must be set to run the suite against Fly");

    /// <summary>
    /// The local nodes: one per region, sharing one machine directory. Each node keeps its data directory
    /// and machine ID across a stop, so a start brings the same replica back, as a Fly machine restarts on its volume.
    /// </summary>
    private sealed class Local(string[] regions, IReadOnlyDictionary<string, TimeSpan> skews) : IAsyncDisposable
    {
        private readonly FakeMemberStore _machines = new();
        private readonly Dictionary<string, Node> _nodes = [with(StringComparer.Ordinal)];
        private readonly string _organization = EntityIds.New().ToString();
        private readonly string _project = EntityIds.New().ToString();

        // R2a-1 deleted the region bus the local nodes replicated over, and peer streams (R4a) are not in yet, so there is
        // no link between local nodes to cut. R4a cuts its peer streams here; until then the partition scenarios fail
        // loudly and are listed in Gaps.cs.
        public void Partition(string a, string b) =>
            throw new NotSupportedException($"no local link between {a} and {b} to cut: the local nodes have no cross-region transport until peer streams (R4a)");

        public void Heal() =>
            throw new NotSupportedException("no local link to heal: the local nodes have no cross-region transport until peer streams (R4a)");

        public string ProjectPath => $"/v1/projects/{_project}";

        public string DatabasePath => $"{ProjectPath}/databases/canary";

        public async Task StartAsync()
        {
            foreach (var region in regions)
            {
                var node = new Node(region, Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14],
                    Directory.CreateTempSubdirectory($"terrabase-e2e-{region}-").FullName, skews.GetValueOrDefault(region));
                _nodes[region] = node;
                await StartNodeAsync(node);
            }

            // The database lives in gru with the other regions as replicas; each node registers it.
            var database = EntityIds.New().ToString();
            foreach (var node in _nodes.Values)
            {
                await CommitAsync(node.Api!, Registration(_organization, _project, node.Region == Gru ? database : null, "canary", [.. regions.Skip(1)]));
            }

            foreach (var node in _nodes.Values)
            {
                await AuthorizeAsync(node, restart: false);
            }
        }

        // A region's client through the local stand-in for Fly's proxy; it finds the region's current node per request.
        public HttpClient Client(string region) =>
            new(new LocalFlyProxy(region, target => _nodes.TryGetValue(target, out var node) && node.Project is { } project
                ? project
                : throw new HttpRequestException($"{target} is down", null, HttpStatusCode.ServiceUnavailable)))
            {
                BaseAddress = new Uri("https://localhost/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };

        public async Task StopAsync(string region)
        {
            var node = _nodes[region];
            var (api, project) = (node.Api, node.Project);
            (node.Api, node.Project) = (null, null);
            project?.Dispose();
            if (api is not null)
            {
                await api.DisposeAsync();
            }
        }

        public async Task StartAgainAsync(string region)
        {
            var node = _nodes[region];
            if (node.Api is not null)
            {
                throw new InvalidOperationException($"{region} is already running");
            }

            await StartNodeAsync(node);
            try
            {
                await AuthorizeAsync(node, restart: true);
            }
            catch
            {
                await StopAsync(region);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception> errors = [];
            foreach (var node in _nodes.Values)
            {
                try
                {
                    await StopAsync(node.Region);
                    Directory.Delete(node.DataDirectory, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
                {
                    errors.Add(error);
                }
            }

            if (errors.Count > 0)
            {
                throw new AggregateException("the local nodes did not shut down cleanly", errors);
            }
        }

        // FLY_REGION and FLY_MACHINE_ID come from the harness's region and node-name keys; the machine ID has Fly's shape.
        private async Task StartNodeAsync(Node node)
        {
            node.Api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = node.Region;
                settings["node-name"] = node.MachineId;
                settings["data-directory"] = node.DataDirectory;
            }, services =>
            {
                services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
                services.AddSingleton<IMemberDirectory>(_machines);
            }, time: node.Skew == TimeSpan.Zero ? null : new SkewedTime(node.Skew));
        }

        // Each start is a new ts-auth stand-in, so the project binding and the project token are set again, for the
        // user who registered the organization at the first start; the plan stays in the node's catalog.
        private async Task AuthorizeAsync(Node node, bool restart)
        {
            var api = node.Api!;
            node.User ??= api.User;
            if (!restart)
            {
                // The enterprise plan lifts the organization's request budget, as the cross-region suite does. Fly's plan is its own.
                await api.SetPlanAsync(node.User, _organization, "enterprise");
            }

            // Project tokens act on the project only once ts-auth's binding of it to the organization is verified.
            using (var owner = api.As(node.User))
            {
                await api.VerifyProjectBindingAsync(_organization, _project, owner);
            }

            node.Project = api.WithToken(api.Issuer.Mint(node.User, token =>
            {
                token.Claims["project_id"] = _project;
                token.Claims["scope"] = Scopes;
                // A benchmark host serves for hours; the e2e tests end well within the default five minutes.
                token.Expires = DateTime.UtcNow.AddHours(12);
            }));
        }

        private static async Task CommitAsync(ManagementApi api, string commit)
        {
            using var service = api.Service(api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Docs.EnsureSuccessAsync(response);
        }

        // An organization, project and database with chosen ids, in one commit.
        private static string Registration(string organization, string project, string? database, string name, string[] replicas) => JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object?[]
            {
                new { op = "create_organization", id = organization, name = $"e2e-{organization}" },
                new { op = "create_project", organization, id = project, name = "e2e" },
                database is null ? null : new { op = "create_database", organization, project, name, id = database, replica_regions = replicas },
            }.OfType<object>(),
        });
    }

    private sealed class Node(string region, string machineId, string dataDirectory, TimeSpan skew)
    {
        public string Region { get; } = region;

        public string MachineId { get; } = machineId;

        public string DataDirectory { get; } = dataDirectory;

        public TimeSpan Skew { get; } = skew;

        public ManagementApi? Api { get; set; }

        public HttpClient? Project { get; set; }

        /// <summary>The user who registered the organization on this node, kept across restarts.</summary>
        public string? User { get; set; }
    }

    // A node's wall clock, off by a fixed amount: its stamps lead or trail its peers'.
    private sealed class SkewedTime(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + skew;
    }
}
