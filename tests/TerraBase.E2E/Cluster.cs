using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.E2E;

/// <summary>
/// One database served by two or three regions (gru, sjc, fra), with a client entering each region. Without
/// <c>TERRABASE_E2E_ORCH_NODES</c> the regions are in-process nodes, each with a region and node name and one shared
/// directory standing in for the peers ts-orch lists. With it they are the disposable ts-orch service's nodes, each
/// reached directly over NetBird (<see cref="Orch"/>) through the neutral replay edge. Each test owns one collection
/// of the database. The fault calls (<see cref="Partition"/>, <see cref="StopAsync"/>, a clock skew) act on the local
/// nodes; on ts-orch only stopping and starting a region exists.
/// </summary>
public sealed class Cluster : IAsyncDisposable
{
    public const string Gru = "gru";
    public const string Sjc = "sjc";
    public const string Fra = "fra";

    /// <summary>The commit every ts-orch node must run; set, each node's identity is checked before the first write and after the last.</summary>
    public const string RevisionVariable = "TERRABASE_E2E_ORCH_REVISION";

    // A ts-auth project token's scopes: an API key cannot hold databases:*, and E12 and E13 create databases.
    // The e2e project's shared database on ts-orch, the one CI's e2e project has always used.
    private const string SharedDatabase = "ci-e2e";

    private const string Scopes = "databases:read databases:write documents:read documents:write";

    private readonly Dictionary<string, HttpClient> _clients;
    private readonly Local? _local;
    private readonly Dictionary<string, string>? _identities;
    private readonly Func<Task> _release;
    private readonly Func<string, string, Task<HttpClient>> _via;
    private readonly Func<string, HttpClient> _sql;
    private readonly Dictionary<string, HttpClient>? _nodes;

    private Cluster(
        string projectPath, string databasePath, string collection, Dictionary<string, HttpClient> clients, Local? local, Dictionary<string, string>? identities,
        Func<Task> release, Func<string, string, Task<HttpClient>> via, Func<string, HttpClient> sql,
        Dictionary<string, HttpClient>? nodes = null, string? organization = null)
    {
        OrganizationId = local?.OrganizationId ?? organization;
        ProjectPath = projectPath;
        DatabasePath = databasePath;
        Collection = collection;
        _clients = clients;
        _local = local;
        _identities = identities;
        _release = release;
        _via = via;
        _sql = sql;
        _nodes = nodes;
    }

    /// <summary>
    /// The region's identity for a run's record, labelled with how it was reached: <c>local:gru</c> (a node of this
    /// process), <c>orch:gru/&lt;node&gt;</c> (the ts-orch node) or <c>orch-pinned:gru/&lt;node&gt;@&lt;revision&gt;</c>
    /// (its identity checked before the first write and after the last).
    /// </summary>
    public string Identity(string region) => _identities?.GetValueOrDefault(region) ?? $"local:{region}";

    /// <summary>The project's path, <c>/v1/projects/{project}</c>.</summary>
    public string ProjectPath { get; }

    internal string? OrganizationId { get; }

    internal bool IsLive => _local is null;

    /// <summary>The database's path, <c>/v1/projects/{project}/databases/{database}</c>.</summary>
    public string DatabasePath { get; }

    /// <summary>This test's collection in the database.</summary>
    public string Collection { get; }

    /// <summary>The regions this cluster serves the database from, home region first, including stopped nodes.</summary>
    public IReadOnlyList<string> Regions => _local?.Regions ?? [.. _clients.Keys];

    /// <summary>Whether <c>TERRABASE_E2E_ORCH_NODES</c> is set, so the regions are the ts-orch service's nodes.</summary>
    public static bool Live => Environment.GetEnvironmentVariable(Orch.NodesVariable) is { Length: > 0 };

    /// <summary>
    /// Whether a test may stop ts-orch regions: only in a pinned outage run (<c>TERRABASE_E2E_ORCH_OUTAGE=1</c> with
    /// <c>TERRABASE_E2E_ORCH_REVISION</c>), and only of the disposable service <see cref="Orch.Service"/>.
    /// </summary>
    public static bool LiveOutage => Environment.GetEnvironmentVariable("TERRABASE_E2E_ORCH_OUTAGE") == "1" && LivePinned;

    /// <summary>Why an outage test skips outside an outage run.</summary>
    public const string OutageRun = "ts-orch outage test: TERRABASE_E2E_ORCH_NODES, TERRABASE_E2E_ORCH_REVISION and TERRABASE_E2E_ORCH_OUTAGE=1";

    /// <summary>Whether this is a revision-pinned ts-orch run (<c>TERRABASE_E2E_ORCH_REVISION</c>): acceptance evidence.</summary>
    public static bool LivePinned => Live && Environment.GetEnvironmentVariable(RevisionVariable) is { Length: > 0 };

    // The cluster qualifies the multi-region guarantee: its region clients ask every write to wait for another region's
    // durable ack (TB-Durability, decision 0013e), as every write did before that default changed.
    private static void AskRemoteDurable(HttpClient client) => client.DefaultRequestHeaders.Add("TB-Durability", "remote_durable");

    /// <summary>The running region's borrowed edge client, owned by the cluster; do not dispose it.</summary>
    public HttpClient this[string region] => _local is not null
        ? _local.Client(region)
        : _clients.TryGetValue(region, out var client)
            ? client
            : throw new ArgumentOutOfRangeException(nameof(region), region, "not a region of this cluster");

    /// <summary>
    /// A caller-owned client entering <paramref name="edge"/> that accepts only <paramref name="served"/>'s answer.
    /// A healthy cursor issuer is reached through the edge's neutral region replay; a stopped issuer falls back locally.
    /// </summary>
    public Task<HttpClient> ViaAsync(string edge, string served) => _via(edge, served);

    /// <summary>A new project-token client for <c>/sql</c> on the running region. The caller disposes it.</summary>
    public HttpClient Sql(string region) => _sql(region);

    /// <summary>Local nodes only: every neutral replay the local edge followed, in order.</summary>
    internal IReadOnlyCollection<LocalEdge.Replay> Replays => RequireLocal().Replays;

    /// <summary>The document with <paramref name="id"/> in this test's collection.</summary>
    public Uri Document(string id) => new($"{DatabasePath}/collections/{Collection}/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);

    /// <summary>
    /// Starts <paramref name="regions"/> (gru and sjc when none are named; gru is always the home region) where the
    /// environment selects: ts-orch when <c>TERRABASE_E2E_ORCH_NODES</c> is set, otherwise local nodes.
    /// </summary>
    public static Task<Cluster> StartAsync(params string[] regions) => StartAsync(regions, skews: null);

    /// <summary>Starts local nodes even when the environment names a live ts-orch service.</summary>
    internal static Task<Cluster> StartLocalAsync(params string[] regions) => StartLocalAsync(regions, skews: null);

    /// <summary>Starts local nodes with each region's requested clock skew, independently of the live environment.</summary>
    internal static Task<Cluster> StartLocalAsync(string[] regions, IReadOnlyDictionary<string, TimeSpan>? skews) =>
        StartLocalCoreAsync(ChooseRegions(regions), skews ?? new Dictionary<string, TimeSpan>());

    /// <summary>Local <paramref name="regions"/> whose backups and shape mirrors use a real S3-compatible bucket (the shape benchmark's T1 and T2).</summary>
    internal static Task<Cluster> StartBucketAsync(string[] regions, Action<Dictionary<string, string>> bucket) =>
        StartLocalCoreAsync(ChooseRegions(regions), new Dictionary<string, TimeSpan>(), bucket: bucket);

    /// <summary>Starts local nodes of <paramref name="regions"/> that share <paramref name="bucket"/> as their platform bucket.</summary>
    internal static Task<Cluster> StartLocalAsync(string[] regions, FakeS3 bucket) =>
        StartLocalCoreAsync(ChooseRegions(regions), new Dictionary<string, TimeSpan>(), backups: bucket);

    // Backup qualification owns a whole database: snapshots never include other parallel tests' collections.
    internal static Task<Cluster> StartBackupAsync() =>
        StartLocalCoreAsync([Gru, Sjc, Fra], new Dictionary<string, TimeSpan>(), new FakeS3());

    internal Task ShipBackupAsync(CancellationToken token) => RequireLocal().ShipBackupAsync(token);

    /// <summary>
    /// gru, sjc and fra whose shapes mirror to one shared fake object-store bucket (FR-22). As in production, each machine
    /// anchors its catalog head in that bucket, so a region restarted while another is down opens from a majority.
    /// </summary>
    internal static Task<Cluster> StartMirroredAsync() =>
        StartLocalCoreAsync([Gru, Sjc, Fra], new Dictionary<string, TimeSpan>(), new FakeS3());

    /// <summary>
    /// A client of the object store behind presigned shape URLs, carrying no TerraBase credential, through the region's
    /// route to its fake bucket. The caller disposes it.
    /// </summary>
    internal HttpClient ObjectClient(string region) => RequireLocal().ObjectClient(region);

    /// <summary>Starts <paramref name="regions"/> with <paramref name="skews"/> setting a local node's clock ahead (positive) or behind its peers.</summary>
    public static Task<Cluster> StartAsync(string[] regions, IReadOnlyDictionary<string, TimeSpan>? skews)
    {
        var chosen = ChooseRegions(regions);
        return Live
            ? skews is { Count: > 0 } ? throw new NotSupportedException("a clock skew needs local nodes") : StartOrchAsync(chosen)
            : StartLocalCoreAsync(chosen, skews ?? new Dictionary<string, TimeSpan>());
    }

    private static string[] ChooseRegions(string[] regions)
    {
        string[] chosen = regions.Length == 0 ? [Gru, Sjc] : [Gru, .. regions.Where(region => region != Gru).Distinct()];
        return chosen.Any(region => region is not (Gru or Sjc or Fra))
            ? throw new ArgumentOutOfRangeException(nameof(regions), string.Join(',', regions), "the regions are gru, sjc and fra")
            : chosen;
    }

    /// <summary>
    /// Local nodes only: requests between <paramref name="a"/> and <paramref name="b"/> find no answer until
    /// <see cref="Heal"/>; each side keeps accepting writes. Callers start owned local nodes for this fault.
    /// </summary>
    public void Partition(string a, string b) => RequireLocal().Partition(a, b);

    /// <summary>Local nodes only: every region reaches every other again.</summary>
    public void Heal() => RequireLocal().Heal();

    /// <summary>
    /// Local nodes only: stalls <paramref name="region"/>'s storage for <paramref name="stall"/>. The database's single writer
    /// is held, so its commits queue (and may time out) until it is released. Turso allows one writer per file, so a second
    /// connection cannot stand in for a stalled disk.
    /// </summary>
    public Task StallStorageAsync(string region, TimeSpan stall) => RequireLocal().StallStorageAsync(region, stall);

    /// <summary>
    /// Takes <paramref name="region"/> down: the local node stops and keeps its data directory, or ts-orch removes the region
    /// from the disposable service (an outage run only). Requests to the region fail until <see cref="StartAgainAsync"/>. The
    /// token cancels the ts-orch call; a local stop is not canceled once begun.
    /// </summary>
    public async Task StopAsync(string region, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_local is null)
        {
            await Orch.StopAsync(region, OutageNode(region), token);
            return;
        }

        await _local.StopAsync(region);
    }

    /// <summary>Brings <paramref name="region"/> back on the same data (the same host directory on ts-orch) after <see cref="StopAsync"/>.</summary>
    public async Task StartAgainAsync(string region, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_local is null)
        {
            await Orch.StartAsync(region, OutageNode(region), token);
            return;
        }

        await _local.StartAgainAsync(region);
    }

    private HttpClient OutageNode(string region) => LiveOutage
        ? _nodes![region]
        : throw new InvalidOperationException($"stopping a ts-orch region needs an outage run (TERRABASE_E2E_ORCH_OUTAGE=1 and {RevisionVariable})");

    /// <summary>Local nodes only: cuts <paramref name="region"/> off from <paramref name="from"/> (every other region when empty).</summary>
    public Task IsolateAsync(string region, params string[] from)
    {
        var local = RequireLocal();
        foreach (var other in from.Length > 0 ? from : Regions.Where(other => other != region))
        {
            local.Partition(region, other);
        }

        return Task.CompletedTask;
    }

    /// <summary>Restores local links after every <see cref="IsolateAsync"/> and <see cref="Partition"/>.</summary>
    public Task HealAllAsync()
    {
        RequireLocal().Heal();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _release();
    }

    /// <summary>
    /// Local nodes only: how a client outside this process reaches <paramref name="region"/>'s node: its public listener
    /// (plain HTTP), the project token and the node's data directory.
    /// </summary>
    public (Uri Address, string Token, string DataDirectory) Endpoint(string region) => RequireLocal().Endpoint(region);

    /// <summary>Local nodes only: <paramref name="region"/>'s data directory, also while the node is stopped.</summary>
    public string DataDirectory(string region) => RequireLocal().DataDirectory(region);

    /// <summary>Local nodes only: the Turso file of this cluster's database on <paramref name="region"/>'s node.</summary>
    public string DatabaseFile(string region) => RequireLocal().DatabaseFile(region);

    /// <summary>Local nodes only: the Prometheus listener of <paramref name="region"/>'s node.</summary>
    public Uri MetricsAddress(string region) => RequireLocal().MetricsAddress(region);

    /// <summary>Local nodes only: connected outbound peer streams reported by the internal listener.</summary>
    public Task<JsonElement> ReplicationAsync(string region, CancellationToken token = default) => RequireLocal().ReplicationAsync(region, token);

    /// <summary>
    /// Local nodes only: sends the peer traffic between <paramref name="a"/> and <paramref name="b"/> through the link
    /// <paramref name="link"/> builds for <paramref name="b"/>'s peer endpoint, and answers it. Peer streams (R4a) publish
    /// the peer addresses this needs; until then it throws.
    /// </summary>
    public LossyLink RouteThrough(string a, string b, Func<IPEndPoint, LossyLink> link) => RequireLocal().RouteThrough(a, b, link);

    /// <summary>
    /// Local nodes only: every byte between <paramref name="a"/> and <paramref name="b"/> takes <paramref name="oneWay"/> each
    /// way, from now on; open peer connections reconnect to take it up.
    /// </summary>
    public void Delay(string a, string b, TimeSpan oneWay) => RequireLocal().Delay(a, b, oneWay);

    /// <summary>Local nodes only: <paramref name="region"/>'s services and this database's tenant, for in-process benchmarks.</summary>
    internal (IServiceProvider Services, TenantContext Database) Engine(string region) => RequireLocal().Engine(region);

    private Local RequireLocal() => _local ?? throw new NotSupportedException("this fault needs local nodes");

    private static async Task<Cluster> StartLocalCoreAsync(string[] regions, IReadOnlyDictionary<string, TimeSpan> skews, FakeS3? backups = null,
        Action<Dictionary<string, string>>? bucket = null)
    {
        var local = new Local(regions, skews, backups, bucket);
        try
        {
            await local.StartAsync();
            var clients = regions.ToDictionary(region => region, local.Client, StringComparer.Ordinal);
            var collection = NewCollection();
            await CreateCollectionAsync(clients, local.DatabasePath, collection);
            return new Cluster(local.ProjectPath, local.DatabasePath, collection, clients, local, null,
                async () => await local.DisposeAsync(), (edge, served) => Task.FromResult(local.Via(edge, served)), local.Sql);
        }
        catch
        {
            await local.DisposeAsync();
            throw;
        }
    }

    // Each ts-orch scenario owns one collection of the shared e2e database.
    private static async Task<Cluster> StartOrchAsync(string[] regions)
    {
        // Check each direct node's region, identity and requested revision before the first write and after the last.
        var revision = Environment.GetEnvironmentVariable(RevisionVariable) is { Length: > 0 } expected ? expected : null;
        var addresses = Orch.Nodes(Environment.GetEnvironmentVariable, regions);
        var nodes = regions.ToDictionary(region => region, region => Orch.Node(addresses[region]), StringComparer.Ordinal);
        // Only the internal listener names the build revision, so a pinned run reads it there with the peer credential.
        var builds = revision is null ? null : Orch.Nodes(Environment.GetEnvironmentVariable, regions, Orch.InternalNodesVariable).ToDictionary(
            entry => entry.Key, entry => Orch.Internal(entry.Value, Environment.GetEnvironmentVariable(Orch.PeerSecretVariable) is { Length: > 0 } secret
                ? secret : throw new InvalidOperationException($"a pinned run needs {Orch.PeerSecretVariable}")), StringComparer.Ordinal);
        var clients = regions.ToDictionary(region => region, region => Client(region), StringComparer.Ordinal);
        var collection = NewCollection();
        string? path = null;
        try
        {
            var (project, organization) = await Orch.ClaimsAsync();
            var projectPath = $"/v1/projects/{project}";
            var names = await IdentifyAsync(nodes, revision, builds);
            path = await SharedDatabaseAsync(clients[Gru], projectPath);
            await CreateCollectionAsync(clients, path, collection);
            return new Cluster(projectPath, path, collection, clients, null, regions.ToDictionary(
                region => region, region => revision is null ? $"orch:{region}/{names[region]}" : $"orch-pinned:{region}/{names[region]}@{revision}", StringComparer.Ordinal), async () =>
            {
                try
                {
                    try
                    {
                        if (!(await IdentifyAsync(nodes, revision, builds)).OrderBy(name => name.Key).SequenceEqual(names.OrderBy(name => name.Key)))
                        {
                            throw new InvalidOperationException($"the ts-orch nodes changed during the run: {string.Join(", ", names)} before");
                        }
                    }
                    finally
                    {
                        await DeleteCollectionAsync(clients[Gru], path, collection);
                    }
                }
                finally
                {
                    DisposeClients();
                }
            }, (edge, served) => Task.FromResult(Client(edge, served)), region => Client(region), nodes, organization);
        }
        catch (Exception error)
        {
            try
            {
                if (path is not null)
                {
                    await DeleteCollectionAsync(clients[Gru], path, collection);
                }
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(error, cleanup);
            }
            finally
            {
                DisposeClients();
            }

            throw;
        }

        HttpClient Client(string edge, string? served = null)
        {
            var client = new HttpClient(new RegionPin(edge, served)
            {
                InnerHandler = new LocalEdge(edge, target => nodes.TryGetValue(target, out var node)
                    ? node
                    : throw new HttpRequestException($"{target} is not a region of this run", null, HttpStatusCode.ServiceUnavailable)),
            })
            {
                BaseAddress = new Uri("http://orch.invalid/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            AskRemoteDurable(client);
            return client;
        }

        void DisposeClients()
        {
            foreach (var client in clients.Values.Concat(nodes.Values))
            {
                client.Dispose();
            }
        }
    }

    // The e2e project's one shared database, placed in every region (home gru), created on first use; each test owns a collection in it.
    private static async Task<string> SharedDatabaseAsync(HttpClient gru, string projectPath)
    {
        var path = $"{projectPath}/databases/{SharedDatabase}";
        using var created = await Docs.AdmittedAsync(() => gru.PostAsJsonAsync(new Uri($"{projectPath}/databases", UriKind.Relative),
            new { name = SharedDatabase, homeRegion = Gru, replicaRegions = new[] { Sjc, Fra } }));
        if (!await TakenAsync(created))
        {
            await Docs.EnsureSuccessAsync(created);
        }

        using var read = await Docs.AdmittedAsync(() => gru.GetAsync(new Uri(path, UriKind.Relative)));
        await Docs.EnsureSuccessAsync(read);
        var record = await read.Content.ReadFromJsonAsync<JsonElement>();
        var replicas = record.GetProperty("replicaRegions").EnumerateArray().Select(region => region.GetString()).Order(StringComparer.Ordinal);
        return record.GetProperty("homeRegion").GetString() == Gru && replicas.SequenceEqual([Fra, Sjc])
            ? path
            : throw new InvalidOperationException($"{path} must be homed in gru with replicas fra and sjc; it is {record}");
    }

    // Creates the collection in gru and waits until every other region serves it, so every test starts from a replicated database.
    // Whether a commit's answer says its ids or names are already taken: a replicated catalog holds them.
    internal static async Task<bool> TakenAsync(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Conflict && await response.Content.ReadAsStringAsync() is var body
        && (body.Contains("\"id_taken\"", StringComparison.Ordinal) || body.Contains("\"name_taken\"", StringComparison.Ordinal));

    internal static async Task CreateCollectionAsync(Dictionary<string, HttpClient> clients, string path, string collection)
    {
        using var created = await Docs.AdmittedAsync(() => clients[Gru].PostAsJsonAsync(new Uri($"{path}/collections", UriKind.Relative), new { name = collection }));
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
        using var deleted = await Docs.AdmittedAsync(() => gru.DeleteAsync(new Uri($"{path}/collections/{collection}", UriKind.Relative)));
        if (deleted.StatusCode != HttpStatusCode.NotFound)
        {
            await Docs.EnsureSuccessAsync(deleted);
        }
    }

    // Deletes a database this run created, through its home after dropping its replica regions: DELETE answers 421
    // wrong_region elsewhere and 409 database_replicated while it has any (a default placement or a restore always does).
    // The home is read through via, the region the run created it in, since a move or a same-name twin may have changed it.
    // Its own one-minute bound, so cleanup still runs after the scenario's deadline or the test token fired; 404 is gone.
    internal static async Task DeleteDatabaseAsync(Func<string, HttpClient> regions, string via, string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var uri = new Uri(path, UriKind.Relative);
        using var existing = await Docs.AdmittedAsync(() => regions(via).GetAsync(uri, deadline.Token), deadline.Token);
        if (existing.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await Docs.EnsureSuccessAsync(existing);
        var record = await existing.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        var home = regions(record.GetProperty("homeRegion").GetString()!);
        if (record.GetProperty("replicaRegions").GetArrayLength() > 0)
        {
            using var unreplicated = await Docs.AdmittedAsync(
                () => home.PatchAsJsonAsync(uri, new { replicaRegions = Array.Empty<string>() }, deadline.Token), deadline.Token);
            if (unreplicated.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            await Docs.EnsureSuccessAsync(unreplicated);
        }

        using var deleted = await Docs.AdmittedAsync(() => home.DeleteAsync(uri, deadline.Token), deadline.Token);
        if (deleted.StatusCode != HttpStatusCode.NotFound)
        {
            await Docs.EnsureSuccessAsync(deleted);
        }
    }

    /// <summary>
    /// Runs <paramref name="scenario"/>, which adds each database it creates (the region it creates it through, its name)
    /// before creating it, then deletes every one of them (<see cref="DeleteDatabaseAsync"/>). Every deletion is tried, and
    /// a cleanup failure joins the scenario's own failure instead of replacing it.
    /// </summary>
    internal async Task OwningDatabasesAsync(Func<List<(string Region, string Name)>, Task> scenario)
    {
        var created = new List<(string Region, string Name)>();
        var errors = new List<Exception>();
        try
        {
            await scenario(created);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        foreach (var (region, name) in created)
        {
            try
            {
                await DeleteDatabaseAsync(other => this[other], region, $"{ProjectPath}/databases/{name}");
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count == 1)
        {
            // The scenario's own failure keeps its type and stack: Gaps matches it against its signature.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(errors[0]);
        }

        if (errors.Count > 1)
        {
            throw new AggregateException(errors);
        }
    }

    private static string NewCollection() => $"e2e-{Guid.NewGuid():N}";

    // Each region's node answers /healthz as itself (its region, a node name in X-TerraBase-Machine and, pinned, the commit under
    // test on its internal listener in `builds`), or the run stops before any write: an answer from another region or build
    // proves nothing about this one. Answers each node's name.
    internal static async Task<Dictionary<string, string>> IdentifyAsync(
        Dictionary<string, HttpClient> nodes, string? revision, IReadOnlyDictionary<string, HttpClient>? builds = null)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (region, node) in nodes)
        {
            using var health = await node.GetAsync(new Uri("/healthz", UriKind.Relative));
            await Docs.EnsureSuccessAsync(health);
            using var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            var (served, name) = (Text(body, "region"), health.Headers.TryGetValues("X-TerraBase-Machine", out var machine) ? machine.Single() : null);
            string? built = null;
            if (revision is not null)
            {
                using var build = await (builds ?? throw new InvalidOperationException("a pinned run needs each node's internal listener"))[region]
                    .GetAsync(new Uri("/healthz", UriKind.Relative));
                await Docs.EnsureSuccessAsync(build);
                using var detail = JsonDocument.Parse(await build.Content.ReadAsStringAsync());
                built = Text(detail, "revision");
            }

            names[region] = served == region && name is { Length: > 0 } && (revision is null || built == revision)
                ? name
                : throw new InvalidOperationException(
                    $"{region}'s node must answer as {region}{(revision is null ? "" : $" running {revision}")}; /healthz answered region {served ?? "(none)"}, node {name ?? "(none)"}, revision {built ?? "(none)"}");
        }

        return names;
    }

    private static string? Text(JsonDocument body, string name) =>
        body.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;

    /// <summary>
    /// The local nodes: one per region, sharing one machine directory. Each node keeps its data directory
    /// and machine ID across a stop, so a start brings the same replica back on its data directory.
    /// </summary>
    private sealed class Local(string[] regions, IReadOnlyDictionary<string, TimeSpan> skews, FakeS3? backups,
        Action<Dictionary<string, string>>? bucket = null) : IAsyncDisposable
    {
        private readonly FakeMemberStore _machines = new();
        private readonly Dictionary<string, Node> _nodes = [with(StringComparer.Ordinal)];
        private readonly System.Collections.Concurrent.ConcurrentQueue<LocalEdge.Replay> _replays = new();
        public IReadOnlyCollection<LocalEdge.Replay> Replays => _replays;
        public string OrganizationId { get; } = EntityIds.New().ToString();
        private readonly string _user = EntityIds.New().ToString();
        private readonly string _project = EntityIds.New().ToString();
        private readonly string _database = EntityIds.New().ToString();

        // Every machine reaches each peer through a PeerLink of its own: its view of the shared directory names the link, not
        // the peer's listener. A partition cuts the links between two regions both ways, on the real peer path, so the CDC
        // groups and acks stop at once; links made later (a restart, a new view) start cut until the heal.
        private readonly Lock _linksGate = new();
        private readonly Dictionary<(string From, string To, string Address), PeerLink> _links = [];
        private readonly HashSet<(string, string)> _cuts = [];
        private readonly Dictionary<(string, string), TimeSpan> _delays = [];

        public void Delay(string a, string b, TimeSpan oneWay)
        {
            lock (_linksGate)
            {
                (_delays[(a, b)], _delays[(b, a)]) = (oneWay, oneWay);
                foreach (var ((from, to, _), link) in _links)
                {
                    if ((from, to) == (a, b) || (from, to) == (b, a))
                    {
                        link.Delay = oneWay;
                        link.Reset();
                    }
                }
            }
        }

        // Machine id -> region, for the directory views; filled before a node starts, read by running nodes.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _regions = new(StringComparer.Ordinal);

        public void Partition(string a, string b)
        {
            lock (_linksGate)
            {
                _cuts.Add((a, b));
                _cuts.Add((b, a));
                foreach (var ((from, to, _), link) in _links)
                {
                    if ((from, to) == (a, b) || (from, to) == (b, a))
                    {
                        link.Cut = true;
                    }
                }
            }
        }

        public void Heal()
        {
            lock (_linksGate)
            {
                _cuts.Clear();
                foreach (var link in _links.Values)
                {
                    link.Cut = false;
                }
            }
        }

        // a's connections to b go through the link `make` builds on b's listener; returns it.
        public LossyLink RouteThrough(string a, string b, Func<IPEndPoint, LossyLink> make)
        {
            lock (_linksGate)
            {
                var link = _links.Where(entry => entry.Key.From == a && entry.Key.To == b).Select(entry => entry.Value).LastOrDefault()
                    ?? throw new InvalidOperationException($"{a} has no route to {b} yet");
                var lossy = make(link.Target);
                link.Via = lossy.Endpoint;
                link.Reset();
                return lossy;
            }
        }

        // How the machine `reader` sees `member`: through its own link to that member's current listener.
        private ClusterMember Route(string reader, ClusterMember member)
        {
            if (Region(reader) is not { } from || !Uri.TryCreate(member.PeerAddress, UriKind.Absolute, out var address))
            {
                return member;
            }

            lock (_linksGate)
            {
                var key = (from, member.Region, member.PeerAddress);
                if (!_links.TryGetValue(key, out var link))
                {
                    // The peer listens on every address of this host: its loopback reaches it.
                    link = _links[key] = new PeerLink(new IPEndPoint(IPAddress.Loopback, address.Port))
                    {
                        Cut = _cuts.Contains((from, member.Region)),
                        Delay = _delays.GetValueOrDefault((from, member.Region)),
                    };
                }

                return member with { PeerAddress = link.Address };
            }
        }

        private string? Region(string machine) => _regions.GetValueOrDefault(machine);

        public async Task StallStorageAsync(string region, TimeSpan stall)
        {
            var api = _nodes[region].Api ?? throw new InvalidOperationException($"{region} is down");
            var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
            var database = new TenantContext(OrganizationId, PlanTier.Hobby, _project, _database);
            var store = await engine.OpenDatabaseAsync(database, CancellationToken.None) as TursoDatabaseStore
                ?? throw new InvalidOperationException($"{region} does not hold the database");
            _ = await store.WithWritersHeldAsync(async () =>
            {
                await Task.Delay(stall);
                return true;
            }, CancellationToken.None, "an e2e storage stall");
        }

        public (IServiceProvider, TenantContext) Engine(string region) =>
            ((_nodes[region].Api ?? throw new InvalidOperationException($"{region} is down")).Server.Services,
                new TenantContext(OrganizationId, PlanTier.Enterprise, _project, _database));

        public HttpClient ObjectClient(string region) =>
            (_nodes[region].Api ?? throw new InvalidOperationException($"{region} is down")).Server.Services
                .GetRequiredService<IHttpClientFactory>().CreateClient(S3ObjectStore.HttpClientName);

        public string DataDirectory(string region) => _nodes[region].DataDirectory;

        public string DatabaseFile(string region)
        {
            var files = Directory.EnumerateFiles(_nodes[region].DataDirectory, "*.db", SearchOption.AllDirectories)
                .Where(path => path.Contains(_database, StringComparison.Ordinal)).ToList();
            return files is [var file] ? file : throw new InvalidOperationException($"{region} holds {files.Count} files of database {_database}");
        }

        public Uri MetricsAddress(string region) =>
            (_nodes[region].Api ?? throw new InvalidOperationException($"{region} is down")).Server.MetricsAddress;

        public async Task<JsonElement> ReplicationAsync(string region, CancellationToken token)
        {
            var client = (_nodes[region].Api ?? throw new InvalidOperationException($"{region} is down")).Server.Client;
            using var response = await client.GetAsync(new Uri("/replication", UriKind.Relative), token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>(token);
        }

        public string ProjectPath => $"/v1/projects/{_project}";

        public string DatabasePath => $"{ProjectPath}/databases/canary";

        public Task ShipBackupAsync(CancellationToken token) => Task.WhenAll(_nodes.Values.Select(node => node.Api!.Server.Services
            .GetRequiredService<IContinuousBackup>().RunOnceAsync(token)));

        public async Task StartAsync()
        {
            _machines.Map = Route;
            foreach (var region in regions)
            {
                var node = new Node(region, EntityIds.New().ToString().ToLowerInvariant(),
                    Directory.CreateTempSubdirectory($"terrabase-e2e-{region}-").FullName, skews.GetValueOrDefault(region));
                _nodes[region] = node;
                _regions[node.MachineId] = region;
                await StartNodeAsync(node);
            }

            // Registered once in the first region; every other node registers the same ids too, which a replicated catalog
            // (R10a) answers with 409 id_taken or name_taken: it already holds them. Until then each node keeps its own catalog.
            foreach (var node in _nodes.Values)
            {
                await CatalogFixture.RegisterAsync(node.Api!, node.Api!.User,
                    new TenantContext(OrganizationId, PlanTier.Hobby, _project, _database), "canary", [.. regions.Where(region => region != node.Region)]);
            }

            foreach (var node in _nodes.Values)
            {
                await AuthorizeAsync(node, restart: false);
                _ = await Converge.UntilAsync(() => Docs.StatusAsync(node.Project!, new Uri(DatabasePath, UriKind.Relative)), status => status == HttpStatusCode.OK,
                    $"the database on {node.Region}");
            }
        }

        public IReadOnlyList<string> Regions => [.. _nodes.Keys];

        public HttpClient Client(string region) => RunningNode(region).Edge!;

        public HttpClient Via(string edge, string served) =>
            new(new RegionPin(edge, served) { InnerHandler = Proxy(edge) }) { BaseAddress = new Uri("http://local.invalid/"), Timeout = Timeout.InfiniteTimeSpan };

        private LocalEdge Proxy(string region) =>
            new(region, target => _nodes.TryGetValue(target, out var node) && node.Project is { } project
                ? project
                : throw new HttpRequestException($"{target} is down", null, HttpStatusCode.ServiceUnavailable), _replays.Enqueue);

        public HttpClient Sql(string region)
        {
            _ = RunningNode(region);
            return new HttpClient(Proxy(region)) { BaseAddress = new Uri("http://local.invalid/"), Timeout = Timeout.InfiniteTimeSpan };
        }

        private Node RunningNode(string region)
        {
            if (!_nodes.TryGetValue(region, out var node))
            {
                throw new ArgumentOutOfRangeException(nameof(region), region, "not a region of this cluster");
            }

            return node.Api is not null && node.Project is not null
                ? node
                : throw new InvalidOperationException($"{region} is down");
        }

        public (Uri Address, string Token, string DataDirectory) Endpoint(string region)
        {
            var node = _nodes[region];
            var api = node.Api ?? throw new InvalidOperationException($"{region} is down");
            return (api.Server.ClientAddress!, node.Token!, node.DataDirectory);
        }

        public async Task StopAsync(string region)
        {
            var node = _nodes[region];
            var (api, project, edge) = (node.Api, node.Project, node.Edge);
            (node.Api, node.Project, node.Edge) = (null, null, null);
            edge?.Dispose();
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
                // Nodes restarted together answer 503 catalog_unverified until their gates verify each other's catalogs;
                // ready means the persisted binding serves the database to the new project token.
                _ = await Converge.UntilAsync(() => Docs.StatusAsync(node.Project!, new Uri(DatabasePath, UriKind.Relative)), status => status == HttpStatusCode.OK,
                    $"the database on restarted {node.Region}");
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

            foreach (var link in _links.Values)
            {
                await link.DisposeAsync();
            }

            if (errors.Count > 0)
            {
                throw new AggregateException("the local nodes did not shut down cleanly", errors);
            }
        }

        // TS_REGION and TS_NODE come from the harness's region and node-name keys.
        private async Task StartNodeAsync(Node node)
        {
            node.Api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = node.Region;
                settings["node-name"] = node.MachineId;
                settings["data-directory"] = node.DataDirectory;
                backups?.Configure(settings);
                bucket?.Invoke(settings);
            }, services =>
            {
                services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
                services.AddSingleton<IMemberDirectory>(_machines);
                if (backups is not null)
                {
                    backups.Route(services);
                    ManualBackupHosting.Configure(services);
                }
            }, time: node.Skew == TimeSpan.Zero ? null : new SkewedTime(node.Skew), userId: _user);
        }

        private async Task AuthorizeAsync(Node node, bool restart)
        {
            var api = node.Api!;
            node.User ??= api.User;
            if (!restart)
            {
                // The enterprise plan lifts the organization's request budget for the cross-region suite.
                await api.SetPlanAsync(node.User, OrganizationId, "enterprise");
            }

            node.Token = api.Issuer.Mint(node.User, token =>
            {
                token.Claims["project_id"] = _project;
                token.Claims["org_id"] = OrganizationId;
                token.Claims["scope"] = Scopes;
                // A benchmark host serves for hours; the e2e tests end well within the default five minutes.
                token.Expires = DateTime.UtcNow.AddHours(12);
            });
            node.Project = api.WithToken(node.Token);
            node.Edge = new HttpClient(Proxy(node.Region)) { BaseAddress = new Uri("http://local.invalid/"), Timeout = Timeout.InfiniteTimeSpan };
            AskRemoteDurable(node.Edge);
        }

    }

    private sealed class Node(string region, string machineId, string dataDirectory, TimeSpan skew)
    {
        public string Region { get; } = region;

        public string MachineId { get; } = machineId;

        public string DataDirectory { get; } = dataDirectory;

        public TimeSpan Skew { get; } = skew;

        public ManagementApi? Api { get; set; }

        public HttpClient? Project { get; set; }

        public HttpClient? Edge { get; set; }

        /// <summary>The project token of the running node.</summary>
        public string? Token { get; set; }

        /// <summary>The user who registered the organization on this node, kept across restarts.</summary>
        public string? User { get; set; }
    }

    // A node's wall clock, off by a fixed amount: its stamps lead or trail its peers'.
    private sealed class SkewedTime(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + skew;
    }
}
