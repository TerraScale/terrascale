using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;
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

    /// <summary>The commit every Fly machine must run; set, it pins each region to its one machine (a disposable app's run).</summary>
    public const string FlyRevisionVariable = "TERRABASE_E2E_FLY_REVISION";

    // A ts-auth project token's scopes: an API key cannot hold databases:*, and E12 and E13 create databases.
    private const string Scopes = "databases:read databases:write documents:read documents:write";

    private readonly Dictionary<string, HttpClient> _clients;
    private readonly Local? _local;
    private readonly Dictionary<string, string>? _identities;
    private readonly Func<Task> _release;
    private readonly Func<string, string, Task<HttpClient>> _via;
    private readonly Func<string, HttpClient> _sql;
    private DisposableTarget.Target? FlyTarget { get; }

    private Cluster(
        string projectPath, string databasePath, string collection, Dictionary<string, HttpClient> clients, Local? local, Dictionary<string, string>? identities,
        Func<Task> release, Func<string, string, Task<HttpClient>> via, Func<string, HttpClient> sql, DisposableTarget.Target? flyTarget = null, string? organization = null)
    {
        _via = via;
        _sql = sql;
        FlyTarget = flyTarget;
        OrganizationId = local?.OrganizationId ?? organization;
        ProjectPath = projectPath;
        DatabasePath = databasePath;
        Collection = collection;
        _clients = clients;
        _local = local;
        _identities = identities;
        _release = release;
    }

    /// <summary>
    /// The region's identity for a run's record, labelled with how it was reached: <c>local:gru</c> (a node of this
    /// process), <c>fly-unpinned:gru</c> (Fly, pinned by region only) or <c>fly-pinned:gru/&lt;machine&gt;@&lt;revision&gt;</c>
    /// (one machine, its identity checked before the first write and after the last).
    /// </summary>
    public string Identity(string region) => _identities?.GetValueOrDefault(region) ?? $"local:{region}";

    /// <summary>The project's path, <c>/v1/projects/{project}</c>.</summary>
    public string ProjectPath { get; }

    internal string? OrganizationId { get; }

    /// <summary>The database's path, <c>/v1/projects/{project}/databases/{database}</c>.</summary>
    public string DatabasePath { get; }

    /// <summary>This test's collection in the database.</summary>
    public string Collection { get; }

    /// <summary>The regions this cluster serves the database from, home region first.</summary>
    public IReadOnlyList<string> Regions => [.. _clients.Keys];

    /// <summary>Whether <c>TERRABASE_E2E_FLY_URL</c> is set, so the regions are Fly's.</summary>
    public static bool FlyRequested => Environment.GetEnvironmentVariable(FlyUrlVariable) is { Length: > 0 };

    /// <summary>
    /// Whether a test may stop Fly machines: only in a pinned disposable app's outage run (<c>TERRABASE_E2E_FLY_OUTAGE=1</c>
    /// with <c>TERRABASE_E2E_FLY_REVISION</c>), never against production.
    /// </summary>
    public static bool FlyMachineFaults => Environment.GetEnvironmentVariable("TERRABASE_E2E_FLY_OUTAGE") == "1" && FlyPinned;

    /// <summary>Whether this is a machine-pinned run of a disposable app (<c>TERRABASE_E2E_FLY_REVISION</c>): acceptance evidence.</summary>
    public static bool FlyPinned => FlyRequested && Environment.GetEnvironmentVariable(FlyRevisionVariable) is { Length: > 0 };

    /// <summary>The client of <paramref name="region"/>: pinned on Fly, the node itself locally.</summary>
    public HttpClient this[string region] => _clients.TryGetValue(region, out var client)
        ? client
        : throw new ArgumentOutOfRangeException(nameof(region), region, "not a region of this cluster");

    /// <summary>
    /// A client entering at <paramref name="edge"/> whose answers must come from <paramref name="served"/>: a changes-feed
    /// resume, which TerraBase replays to its cursor's issuing machine, or serves itself when that machine is down
    /// (docs/terrabase/multi-region-replication.md, issuer routing). On Fly the request is forced onto the edge's machine (the
    /// pinned one, or the one that answers the edge's <c>/healthz</c> as itself), so Fly cannot fall back to another region and
    /// pass a direct answer off as a replay; the answer must name <paramref name="served"/> (and its pinned machine). The
    /// caller disposes it.
    /// </summary>
    public Task<HttpClient> ViaAsync(string edge, string served) => _via(edge, served);

    /// <summary>
    /// A client of <paramref name="region"/> for <c>/sql</c>, which takes a ts-auth project token, never an API key: locally the
    /// nodes' project token; on Fly the existing <c>TERRABASE_E2E_FLY_PROJECT_TOKEN</c>, required (nothing is minted). Pinned
    /// like <see cref="this[string]"/>; the caller disposes it.
    /// </summary>
    public HttpClient Sql(string region) => _sql(region);

    /// <summary>Local nodes only: every <c>fly-replay</c> the local stand-in for Fly's proxy followed, in order.</summary>
    internal IReadOnlyCollection<LocalFlyProxy.Replay> Replays => RequireLocal().Replays;

    /// <summary>The document with <paramref name="id"/> in this test's collection.</summary>
    public Uri Document(string id) => new($"{DatabasePath}/collections/{Collection}/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);

    /// <summary>
    /// Starts <paramref name="regions"/> (gru and sjc when none are named; gru is always the home region) where the
    /// environment selects: Fly when <c>TERRABASE_E2E_FLY_URL</c> is set, otherwise local nodes.
    /// </summary>
    public static Task<Cluster> StartAsync(params string[] regions) => StartAsync(regions, skews: null);

    // Backup qualification owns a whole database: snapshots never include other parallel tests' collections.
    internal static Task<Cluster> StartBackupAsync()
    {
        if (!FlyRequested)
        {
            return StartLocalAsync([Gru, Sjc, Fra], new Dictionary<string, TimeSpan>(), new FakeS3());
        }

        if (!FlyPinned || Environment.GetEnvironmentVariable(DisposableTarget.AppVariable) != DisposableTarget.ApprovedApp
            || Environment.GetEnvironmentVariable(DisposableTarget.OrgVariable) != DisposableTarget.ApprovedOrg)
        {
            throw new InvalidOperationException($"backup qualification on Fly requires pinned {DisposableTarget.ApprovedApp} in {DisposableTarget.ApprovedOrg}");
        }

        return StartFlyAsync([Gru, Sjc, Fra], backup: true);
    }

    internal Task ShipBackupAsync(CancellationToken token) => _local is null ? Task.CompletedTask : _local.ShipBackupAsync(token);

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
    /// Local nodes only: stalls <paramref name="region"/>'s storage for <paramref name="stall"/>. The database's single writer
    /// is held, so its commits queue (and may time out) until it is released. Turso allows one writer per file, so a second
    /// connection cannot stand in for a stalled disk.
    /// </summary>
    public Task StallStorageAsync(string region, TimeSpan stall) => RequireLocal().StallStorageAsync(region, stall);

    /// <summary>
    /// Takes <paramref name="region"/> down: the local node stops and keeps its data directory, or Fly stops the region's
    /// machine. Requests to the region fail until <see cref="StartAgainAsync"/>. The token cancels flyctl; a local stop is
    /// not canceled once begun.
    /// </summary>
    public async Task StopAsync(string region, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_local is null)
        {
            await Flyctl.RunAsync(["machine", "stop", Disposable.Machines[region], "--app", Disposable.App], token);
            return;
        }

        await _local.StopAsync(region);
    }

    // Machine faults run only on a pinned disposable app: an unpinned run has no app to stop machines of.
    private DisposableTarget.Target Disposable => FlyTarget ?? throw new InvalidOperationException(
        $"machine faults on Fly need a pinned disposable app ({FlyRevisionVariable} and {DisposableTarget.AppVariable})");

    /// <summary>Brings <paramref name="region"/> back on the same data (the same volume on Fly) after <see cref="StopAsync"/>.</summary>
    public async Task StartAgainAsync(string region, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_local is null)
        {
            await Flyctl.StartStoppedAsync(Disposable.App, Disposable.Machines[region], token: token);
            return;
        }

        await _local.StartAgainAsync(region);
    }

    /// <summary>
    /// Cuts <paramref name="region"/> off from <paramref name="from"/> (every other region when empty). Local nodes drop the
    /// peer links (<see cref="Partition"/>). On a pinned disposable app's fault run each machine pair drops the other's 6PN
    /// packets both ways (<c>ip6tables</c> over <c>flyctl ssh</c>; the test-only faults image), while Fly Proxy still
    /// reaches every machine.
    /// </summary>
    public async Task IsolateAsync(string region, params string[] from)
    {
        var others = from.Length > 0 ? from : [.. Regions.Where(other => other != region)];
        if (_local is not null)
        {
            foreach (var other in others)
            {
                _local.Partition(region, other);
            }

            return;
        }

        using var listed = JsonDocument.Parse(await Flyctl.RunAsync("machine", "list", "--app", Disposable.App, "--json"));
        var ips = listed.RootElement.EnumerateArray().ToDictionary(machine => machine.GetProperty("id").GetString()!, machine => machine.GetProperty("private_ip").GetString()!);
        foreach (var other in others)
        {
            await DropAsync(region, ips[Disposable.Machines[other]]);
            await DropAsync(other, ips[Disposable.Machines[region]]);
        }

        Task DropAsync(string on, string peer)
        {
            return FlySshAsync(on, $"ip6tables -A INPUT -s {peer} -j DROP && ip6tables -A OUTPUT -d {peer} -j DROP");
        }
    }

    /// <summary>Undoes every <see cref="IsolateAsync"/> and <see cref="Partition"/>: local links come back, Fly rules are flushed.</summary>
    public async Task HealAllAsync()
    {
        if (_local is not null)
        {
            _local.Heal();
            return;
        }

        foreach (var region in Regions)
        {
            await FlySshAsync(region, "ip6tables -F INPUT && ip6tables -F OUTPUT");
        }
    }

    /// <summary>Fly only: SIGKILLs <paramref name="region"/>'s machine (<c>flyctl machine kill</c>); <see cref="StartAgainAsync"/> brings it back.</summary>
    public Task KillAsync(string region) => _local is null
        ? Flyctl.RunAsync("machine", "kill", Disposable.Machines[region], "--app", Disposable.App)
        : throw new NotSupportedException("a local SIGKILL needs separate processes (RegionProcesses)");

    /// <summary>
    /// Fly only: steps <paramref name="region"/>'s machine wall clock by <paramref name="skew"/> (<c>date -s</c> as root; the
    /// VM runs no NTP daemon). Step back by the negative to restore; each step lags by the command's run time (about 1 ms).
    /// </summary>
    public Task<string> StepClockAsync(string region, TimeSpan skew) => FlyScriptAsync(region, $"""
        now=$(date +%s%N); new=$((now + {(long)skew.TotalMilliseconds} * 1000000))
        date -s "@$((new / 1000000000)).$(printf %09d $((new % 1000000000)))" > /dev/null
        echo "stepped {(long)skew.TotalMilliseconds} ms: $now -> $(date +%s%N)"
        """);

    /// <summary>
    /// Fly only: makes <paramref name="region"/> lose the acknowledged document <paramref name="id"/>, as a lost apply would.
    /// The running terrabase holds its database files locked, so the machine is first updated to idle
    /// (<c>sleep infinity</c>; terrabase stops on SIGTERM), then the id's rows are deleted from every table keyed by
    /// <c>id</c> in each database file (sqlite3, installed into the test-only image's ephemeral rootfs), and terrabase is
    /// started again. Returns the edit's output once the region is ready again.
    /// </summary>
    public async Task<string> LoseDocumentAsync(string region, string id)
    {
        var machine = Disposable.Machines[region];
        _ = await Flyctl.RunAsync("machine", "update", machine, "--app", Disposable.App, "--entrypoint", "sleep infinity", "--skip-health-checks", "--yes");
        try
        {
            var output = await FlyScriptAsync(region, $$"""
                apt-get update -qq > /dev/null 2>&1; apt-get install -y -qq sqlite3 > /dev/null 2>&1
                for f in /data/data/*/*/*.db; do
                  for t in $(sqlite3 "$f" "SELECT m.name FROM sqlite_master m JOIN pragma_table_info(m.name) c WHERE m.type = 'table' AND c.name = 'id'" 2>&1); do
                    n=$(sqlite3 "$f" "DELETE FROM \"$t\" WHERE id = '{{id}}'; SELECT changes();" 2>&1)
                    [ "$n" = 0 ] || echo "deleted $n from $t in $f"
                  done
                done
                """);
            await RestoreEntrypointAsync();
            _ = await Converge.UntilAsync(async () =>
            {
                try
                {
                    return await Docs.StatusAsync(this[region], new Uri("/readyz", UriKind.Relative));
                }
                catch (Exception error) when (error is HttpRequestException or RegionMismatchException)
                {
                    return HttpStatusCode.ServiceUnavailable;
                }
            }, status => status == HttpStatusCode.OK, $"{region} ready on its edited volume", TimeSpan.FromMinutes(4));
            return output;
        }
        catch
        {
            await RestoreEntrypointAsync();
            throw;
        }

        // The image's own entrypoint: a merged --machine-config cannot clear the override.
        Task<string> RestoreEntrypointAsync()
        {
            return Flyctl.RunAsync("machine", "update", machine, "--app", Disposable.App, "--entrypoint", "/app/terrabase", "--skip-health-checks", "--yes");
        }
    }
    // The script travels base64-encoded, so its quotes survive flyctl's command line and sh -c.
    private Task<string> FlyScriptAsync(string region, string script) =>
        FlySshAsync(region, $"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(script))} | base64 -d | sh");

    private Task<string> FlySshAsync(string region, string command) =>
        Flyctl.RunAsync("ssh", "console", "--app", Disposable.App, "--machine", Disposable.Machines[region], "-C", $"sh -c '{command}'");

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

    /// <summary>Local nodes only: the SQLite file of this cluster's database on <paramref name="region"/>'s node.</summary>
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

    private Local RequireLocal() => _local ?? throw new NotSupportedException("this fault needs local nodes");

    // An unpinned Fly run (the production e2e project's suite: its own collection, no machine faults) needs no app; a
    // machine-pinned run names its whole disposable target explicitly (DisposableTarget).

    private static async Task<Cluster> StartLocalAsync(string[] regions, IReadOnlyDictionary<string, TimeSpan> skews, FakeS3? backups = null)
    {
        var local = new Local(regions, skews, backups);
        try
        {
            await local.StartAsync();
            var clients = regions.ToDictionary(region => region, local.Client, StringComparer.Ordinal);
            var collection = NewCollection();
            await CreateCollectionAsync(clients, local.DatabasePath, collection);
            return new Cluster(local.ProjectPath, local.DatabasePath, collection, clients, local, null, async () =>
            {
                foreach (var client in clients.Values)
                {
                    client.Dispose();
                }

                await local.DisposeAsync();
            }, (edge, served) => Task.FromResult(local.Via(edge, served)), local.Client);
        }
        catch
        {
            await local.DisposeAsync();
            throw;
        }
    }

    // Ordinary Fly scenarios own a collection; backup qualification owns and deletes an isolated database.
    private static async Task<Cluster> StartFlyAsync(string[] regions, bool backup = false)
    {
        // With TERRABASE_E2E_FLY_REVISION (a disposable app's run) every request goes to the region's one machine, and each
        // machine must answer as itself, in its region, running exactly that commit, before the first write and after the
        // last. The target is checked first: no flyctl call or request happens with a missing or production target. Then,
        // read-only, Fly must show the app in its org and network with exactly the named machine in each region.
        var revision = Environment.GetEnvironmentVariable(FlyRevisionVariable) is { Length: > 0 } expected ? expected : null;
        var target = revision is null ? null : DisposableTarget.Resolve(Environment.GetEnvironmentVariable, regions);
        var url = new Uri(Required(FlyUrlVariable), UriKind.Absolute);
        // The documents client's bearer: in CI the bound e2e project's 15-minute ts-auth project token
        // (scripts/e2e/ci-oidc-credential.sh), the same project as /sql's TERRABASE_E2E_FLY_PROJECT_TOKEN.
        var organization = backup ? Required("TERRABASE_E2E_FLY_ORGANIZATION") : null;
        var key = Required(backup ? "TERRABASE_E2E_FLY_PROJECT_TOKEN" : "TERRABASE_E2E_FLY_API_KEY");
        var projectPath = $"/v1/projects/{Required("TERRABASE_E2E_FLY_PROJECT")}";
        var path = backup ? "" : $"{projectPath}/databases/{Required("TERRABASE_E2E_FLY_DATABASE")}";
        if (target is not null)
        {
            using var apps = JsonDocument.Parse(await Flyctl.RunAsync(["apps", "list", "--org", target.Org, "--json"]));
            DisposableTarget.CheckApp(target, apps.RootElement);
            using var listed = JsonDocument.Parse(await Flyctl.RunAsync(["machine", "list", "--app", target.App, "--json"]));
            DisposableTarget.CheckMachines(target, listed.RootElement);
        }

        var machines = regions.ToDictionary(region => region, region => target?.Machines[region], StringComparer.Ordinal);

        var clients = regions.ToDictionary(region => region, region => FlyClient(url, key, region, machines[region]), StringComparer.Ordinal);
        if (revision is not null)
        {
            await CheckIdentitiesAsync(clients, machines!, revision);
        }

        // ViaAsync's machines: the pinned ones, or on an unpinned run each region's machine identified once and reused, so a
        // cursor issued through ViaAsync(issuer, issuer) and its resume's expected issuer are the same exact machine.
        var identified = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task<string> ViaMachineAsync(string region)
        {
            if (machines[region] is { } pinned)
            {
                return pinned;
            }

            if (!identified.TryGetValue(region, out var machine))
            {
                identified[region] = machine = await EdgeMachineAsync(clients[region], region, TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            }

            return machine;
        }

        var collection = NewCollection();
        string? owned = null;
        try
        {
            if (backup)
            {
                owned = $"{projectPath}/databases/e2e-backup-{EntityIds.New().ToString().ToLowerInvariant()}";
                var database = new
                {
                    name = owned.Split('/')[^1],
                    homeRegion = Gru,
                    replicaRegions = regions.Where(region => region != Gru).ToArray(),
                };
                using var created = await Docs.AdmittedAsync(() => clients[Gru].PostAsJsonAsync(new Uri($"{projectPath}/databases", UriKind.Relative), database));
                await Docs.EnsureSuccessAsync(created);
                path = owned;
            }

            await CreateCollectionAsync(clients, path, collection);
        }
        catch (Exception error)
        {
            try
            {
                if (owned is not null)
                {
                    await DeleteDatabaseAsync(region => clients[region], Gru, owned);
                }
                else
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
                foreach (var client in clients.Values)
                {
                    client.Dispose();
                }
            }

            throw;
        }

        return new Cluster(projectPath, path, collection, clients, null, regions.ToDictionary(
            region => region, region => revision is null ? $"fly-unpinned:{region}" : $"fly-pinned:{region}/{machines[region]}@{revision}", StringComparer.Ordinal), async () =>
        {
            try
            {
                try
                {
                    // Every edge recorded as region/machine@revision held for the whole run: no machine was redeployed during it.
                    if (revision is not null)
                    {
                        await CheckIdentitiesAsync(clients, machines!, revision);
                    }
                }
                finally
                {
                    if (owned is not null)
                    {
                        await DeleteDatabaseAsync(region => clients[region], Gru, owned);
                    }
                    else
                    {
                        await DeleteCollectionAsync(clients[Gru], path, collection);
                    }
                }
            }
            finally
            {
                foreach (var client in clients.Values)
                {
                    client.Dispose();
                }
            }
        }, async (edge, served) => FlyClient(url, key, edge, await ViaMachineAsync(edge), served, await ViaMachineAsync(served)),
            region => FlyClient(url, Required("TERRABASE_E2E_FLY_PROJECT_TOKEN"), region, machines[region]), target, organization);
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

    // Each region's machine answers /healthz as itself (machine, region) running the commit under test, or the run stops
    // before any write: an answer from another machine, region or build proves nothing about this one.
    internal static async Task CheckIdentitiesAsync(Dictionary<string, HttpClient> clients, Dictionary<string, string> machines, string revision)
    {
        foreach (var (region, client) in clients)
        {
            using var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative));
            await Docs.EnsureSuccessAsync(health);
            using var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            var (served, machine, built) = (Text(body, "region"), Text(body, "machine"), Text(body, "revision"));
            if (served != region || machine != machines[region] || built != revision)
            {
                throw new InvalidOperationException(
                    $"{region}'s machine {machines[region]} must run {revision}; /healthz answered region {served ?? "(none)"}, machine {machine ?? "(none)"}, revision {built ?? "(none)"}");
            }
        }
    }

    // An unpinned run's edge machine for ViaAsync: the one that answers /healthz through the region's pinned client (RegionPin
    // refuses another region) naming the region and itself in both the body and X-TerraBase-Machine. Any other answer stops the
    // test; nothing is retried.
    internal static async Task<string> EdgeMachineAsync(HttpClient client, string region, CancellationToken cancellationToken = default)
    {
        using var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), cancellationToken);
        await Docs.EnsureSuccessAsync(health);
        using var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync(cancellationToken));
        var (served, machine) = (Text(body, "region"), Text(body, "machine"));
        var header = health.Headers.TryGetValues("X-TerraBase-Machine", out var values) ? string.Join(',', values) : null;
        return served == region && machine is { Length: > 0 } && machine == header
            ? machine
            : throw new InvalidOperationException(
                $"{region}'s /healthz must name {region} and the answering machine; it answered region {served ?? "(none)"}, machine {machine ?? "(none)"}, X-TerraBase-Machine {header ?? "(none)"}");
    }

    private static string? Text(JsonDocument body, string name) =>
        body.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static HttpClient FlyClient(Uri url, string key, string region, string? machine, string? served = null, string? servedMachine = null)
    {
        DelegatingHandler pin = machine is null
            ? new RegionPin(region)
            : new MachinePin(region, machine, served is null ? null : (served, servedMachine));
        pin.InnerHandler = new SocketsHttpHandler();
        var client = new HttpClient(pin) { BaseAddress = url, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} must be set to run the suite against Fly");

    /// <summary>
    /// The local nodes: one per region, sharing one machine directory. Each node keeps its data directory
    /// and machine ID across a stop, so a start brings the same replica back, as a Fly machine restarts on its volume.
    /// </summary>
    private sealed class Local(string[] regions, IReadOnlyDictionary<string, TimeSpan> skews, FakeS3? backups) : IAsyncDisposable
    {
        private readonly FakeMemberStore _machines = new();
        private readonly Dictionary<string, Node> _nodes = [with(StringComparer.Ordinal)];
        private readonly System.Collections.Concurrent.ConcurrentQueue<LocalFlyProxy.Replay> _replays = new();
        public IReadOnlyCollection<LocalFlyProxy.Replay> Replays => _replays;
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
                    link = _links[key] = new PeerLink(new IPEndPoint(IPAddress.Loopback, address.Port)) { Cut = _cuts.Contains((from, member.Region)) };
                }

                return member with { PeerAddress = link.Address };
            }
        }

        private string? Region(string machine) => _regions.GetValueOrDefault(machine);

        public async Task StallStorageAsync(string region, TimeSpan stall)
        {
            var api = _nodes[region].Api ?? throw new InvalidOperationException($"{region} is down");
            var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
            var database = new TenantContext(OrganizationId, PlanTier.Hobby, _project, _database);
            var store = await engine.OpenDatabaseAsync(database, CancellationToken.None) as SqliteDatabaseStore
                ?? throw new InvalidOperationException($"{region} does not hold the database");
            _ = await store.WithWritersHeldAsync(async () =>
            {
                await Task.Delay(stall);
                return true;
            }, CancellationToken.None, "an e2e storage stall");
        }

        public string DataDirectory(string region) => _nodes[region].DataDirectory;

        public string DatabaseFile(string region)
        {
            var files = Directory.EnumerateFiles(_nodes[region].DataDirectory, "*.db", SearchOption.AllDirectories)
                .Where(path => path.Contains(_database, StringComparison.Ordinal)).ToList();
            return files is [var file] ? file : throw new InvalidOperationException($"{region} holds {files.Count} files of database {_database}");
        }

        public Uri MetricsAddress(string region) =>
            (_nodes[region].Api ?? throw new InvalidOperationException($"{region} is down")).Server.MetricsAddress;

        public Task<JsonElement> ReplicationAsync(string region, CancellationToken token) =>
            (_nodes[region].Api ?? throw new InvalidOperationException($"{region} is down")).Server.Client
                .GetFromJsonAsync<JsonElement>(new Uri("/replication", UriKind.Relative), token);

        public string ProjectPath => $"/v1/projects/{_project}";

        public string DatabasePath => $"{ProjectPath}/databases/canary";

        public Task ShipBackupAsync(CancellationToken token) => Task.WhenAll(_nodes.Values.Select(node => node.Api!.Server.Services
            .GetRequiredService<IContinuousBackup>().RunOnceAsync(token)));

        public async Task StartAsync()
        {
            _machines.Map = Route;
            foreach (var region in regions)
            {
                var node = new Node(region, Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14],
                    Directory.CreateTempSubdirectory($"terrabase-e2e-{region}-").FullName, skews.GetValueOrDefault(region));
                _nodes[region] = node;
                _regions[node.MachineId] = region;
                await StartNodeAsync(node);
            }

            // Registered once in the first region; every other node registers the same ids too, which a replicated catalog
            // (R10a) answers with 409 id_taken or name_taken: it already holds them. Until then each node keeps its own catalog.
            foreach (var node in _nodes.Values)
            {
                await CommitAsync(node.Api!, Registration(OrganizationId, _project, _database, "canary", [.. regions.Where(region => region != node.Region)]),
                    alreadyHeld: node.Region != regions[0]);
            }

            foreach (var node in _nodes.Values)
            {
                await AuthorizeAsync(node, restart: false);
                _ = await Converge.UntilAsync(() => Docs.StatusAsync(node.Project!, new Uri(DatabasePath, UriKind.Relative)), status => status == HttpStatusCode.OK,
                    $"the database on {node.Region}");
            }
        }

        // A region's client through the local stand-in for Fly's proxy; it finds the region's current node per request.
        public HttpClient Client(string region) => new(Proxy(region)) { BaseAddress = new Uri("https://localhost/"), Timeout = Timeout.InfiniteTimeSpan };

        // Cluster.Via: the edge's proxy, refusing an answer from any node but served's.
        public HttpClient Via(string edge, string served) =>
            new(new RegionPin(edge, served) { InnerHandler = Proxy(edge) }) { BaseAddress = new Uri("https://localhost/"), Timeout = Timeout.InfiniteTimeSpan };

        private LocalFlyProxy Proxy(string region) =>
            new(region, target => _nodes.TryGetValue(target, out var node) && node.Project is { } project
                ? project
                : throw new HttpRequestException($"{target} is down", null, HttpStatusCode.ServiceUnavailable), Region, _replays.Enqueue);

        public (Uri Address, string Token, string DataDirectory) Endpoint(string region)
        {
            var node = _nodes[region];
            var api = node.Api ?? throw new InvalidOperationException($"{region} is down");
            return (api.Server.ClientAddress!, node.Token!, node.DataDirectory);
        }

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

        // FLY_REGION and FLY_MACHINE_ID come from the harness's region and node-name keys; the machine ID has Fly's shape.
        private async Task StartNodeAsync(Node node)
        {
            node.Api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = node.Region;
                settings["node-name"] = node.MachineId;
                settings["data-directory"] = node.DataDirectory;
                backups?.Configure(settings);
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

        // Each start is a new ts-auth stand-in, so the project token is minted again, for the user who registered the
        // organization at the first start; the plan and the verified binding stay in the node's catalog.
        private async Task AuthorizeAsync(Node node, bool restart)
        {
            var api = node.Api!;
            node.User ??= api.User;
            if (!restart)
            {
                // The enterprise plan lifts the organization's request budget, as the cross-region suite does. Fly's plan is its own.
                await api.SetPlanAsync(node.User, OrganizationId, "enterprise");
                // Project tokens act on the project only once ts-auth's binding of it to the organization is verified.
                using var owner = api.As(node.User);
                await api.VerifyProjectBindingAsync(OrganizationId, _project, owner);
            }

            node.Token = api.Issuer.Mint(node.User, token =>
            {
                token.Claims["project_id"] = _project;
                token.Claims["scope"] = Scopes;
                // A benchmark host serves for hours; the e2e tests end well within the default five minutes.
                token.Expires = DateTime.UtcNow.AddHours(12);
            });
            node.Project = api.WithToken(node.Token);
        }

        // With alreadyHeld, a 409 naming ids or names already taken counts as done: the catalog brought them.
        private static async Task CommitAsync(ManagementApi api, string commit, bool alreadyHeld = false)
        {
            using var service = api.Service(api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            if (!alreadyHeld || !await Cluster.TakenAsync(response))
            {
                await Docs.EnsureSuccessAsync(response);
            }
        }

        // An organization, project and database with chosen ids, in one commit.
        private static string Registration(string organization, string project, string database, string name, string[] replicas) => JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object[]
            {
                new { op = "create_organization", id = organization, name = $"e2e-{organization}" },
                new { op = "create_project", organization, id = project, name = "e2e" },
                new { op = "create_database", organization, project, name, id = database, replica_regions = replicas },
            },
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
