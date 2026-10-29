using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.E2E.WalFaults;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using TerraScale.Endpoints;

namespace TerraBase.E2E;

/// <summary>
/// Regions as separate operating-system processes, so a node can be killed with SIGKILL and started again on its data
/// while the others keep replicating. Each process runs <see cref="RegionProcessHost"/> (this test binary, one node of the
/// real <c>Program</c>), which finds its peers in a shared fake test directory standing in for the ts-orch peers file:
/// every node lists itself there with its internal listener, and a kill removes the entry. The harness drives each node
/// over its internal listener as the platform operator (h2c on loopback, every scope on a project's data routes), so no
/// per-process TLS or token is needed. Local only.
/// </summary>
internal sealed class RegionProcesses : IAsyncDisposable
{
    private const string Collection = "e2e";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);
    private readonly string _root = Directory.CreateTempSubdirectory("terrabase-regions-").FullName;
    private readonly Dictionary<string, Region> _regions = [with(StringComparer.Ordinal)];
    private string _organization = EntityIds.New().ToString();
    private string _project = EntityIds.New().ToString();
    private string _database = EntityIds.New().ToString();

    // Every region's process loads the write-ahead log fault shim (StartWithWalFaultsAsync).
    private readonly bool _walFaults;

    private RegionProcesses(bool walFaults)
    {
        _walFaults = walFaults;
    }

    /// <summary>The database's path on every region, <c>/v1/projects/{project}/databases/canary</c>.</summary>
    public string DatabasePath => $"/v1/projects/{_project}/databases/canary";

    public IReadOnlyList<string> Regions => [.. _regions.Keys];

    /// <summary>The database's identity, as every region's engine opens it.</summary>
    public TenantContext Tenant => new(_organization, PlanTier.Hobby, _project, _database);

    /// <summary>The harness's collection.</summary>
    public static string CollectionName => Collection;

    /// <summary>The region's internal listener as the platform operator; a new client after each start.</summary>
    public HttpClient this[string region] => _regions[region].Client ?? throw new InvalidOperationException($"{region} is down");

    /// <summary>The document with <paramref name="id"/> in the harness's collection.</summary>
    public Uri Document(string id) => new($"{DatabasePath}/collections/{Collection}/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);

    /// <summary>Starts one process per region, registers the database placed in all of them, and waits until each serves it.</summary>
    public static Task<RegionProcesses> StartAsync(params string[] regions) => StartAsync(walFaults: false, regions);

    /// <summary>As <see cref="StartAsync(string[])"/>, with the write-ahead log fault shim in every region's process.</summary>
    public static Task<RegionProcesses> StartWithWalFaultsAsync(params string[] regions) => StartAsync(walFaults: true, regions);

    private static async Task<RegionProcesses> StartAsync(bool walFaults, string[] regions)
    {
        var cluster = new RegionProcesses(walFaults);
        try
        {
            Directory.CreateDirectory(Path.Combine(cluster._root, "members"));
            foreach (var region in regions)
            {
                var node = new Region(region, EntityIds.New().ToString().ToLowerInvariant(), Path.Combine(cluster._root, region));
                Directory.CreateDirectory(node.DataDirectory);
                cluster._regions[region] = node;
                await cluster.LaunchAsync(node);
            }

            // Bootstrap once at home; the other regions wait for its replicated catalog instead of racing duplicate commits.
            var owner = EntityIds.New().ToString();
            await cluster.RegisterAsync(cluster._regions[regions[0]], owner, [.. regions.Skip(1)]);

            foreach (var node in cluster._regions.Values)
            {
                _ = await Converge.UntilAsync(() => Docs.StatusAsync(cluster[node.Name], new Uri($"{cluster.DatabasePath}/collections", UriKind.Relative)),
                    status => status == HttpStatusCode.OK, $"the database on {node.Name}");
            }

            // Created in gru, served everywhere once its metadata document replicated.
            await Cluster.CreateCollectionAsync(cluster._regions.ToDictionary(pair => pair.Key, pair => cluster[pair.Key], StringComparer.Ordinal), cluster.DatabasePath, Collection);

            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    /// <summary>SIGKILL: the region's process ends at once, without shutdown or flushes, and leaves the machine list.</summary>
    public async Task KillAsync(string region)
    {
        var node = _regions[region];
        if (node.Process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        File.Delete(MemberFile(node));
        node.Client?.Dispose();
        node.Client = null;
    }

    /// <summary>
    /// The region refuses every batch and repair of the database from now on, before applying it, and records no origin's
    /// frontier for it: its peers' writes stay unacknowledged while their streams stay connected and every other database,
    /// the catalog included, replicates. Lasts for the process lifetimes of this harness.
    /// </summary>
    public void HoldReplication(string region)
    {
        var hold = HoldFile(_regions[region]);
        // Written aside and moved, so a reader never sees half an id.
        File.WriteAllText(hold + ".partial", _database);
        File.Move(hold + ".partial", hold, overwrite: true);
    }

    /// <summary>A client of the region's public listener with a fresh API key of the database's organization holding <paramref name="scopes"/>.</summary>
    public async Task<HttpClient> DataClientAsync(string region, params string[] scopes)
    {
        using var response = await this[region].PostAsJsonAsync(new Uri("/admin/api-keys", UriKind.Relative),
            new { name = "e2e", scopes, organizationId = _organization });
        await Docs.EnsureSuccessAsync(response);
        var secret = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString();
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = _regions[region].ClientAddress };
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        client.DefaultRequestHeaders.Add("TB-Durability", "remote_durable");
        return client;
    }

    /// <summary>The region applies the database's batches again.</summary>
    public void ReleaseReplication(string region) => File.Delete(HoldFile(_regions[region]));

    /// <summary>The fault shim's control of the region's process (<see cref="StartWithWalFaultsAsync"/> only).</summary>
    public WalSyncFaults Faults(string region) => new(_regions[region].DataDirectory + ".walfaults");

    /// <summary>The harness database's write-ahead log on the region.</summary>
    public string TenantWal(string region) => Path.Combine(_regions[region].DataDirectory, _organization, _project, _database + ".db-wal");

    /// <summary>Kills the region and drops every log byte the shim did not observe successfully synced.</summary>
    public async Task PowerLossAsync(string region)
    {
        await KillAsync(region);
        Faults(region).DropUnsynced(_regions[region].DataDirectory);
    }

    /// <summary>
    /// A power loss at the sync the region's shim was armed to kill: waits for that SIGKILL, drops every unsynced byte of
    /// every log, then writes back the dropped frames of the database's log but the last, torn after
    /// <paramref name="tornBytes"/> (a torn tail). Answers the log's synced, unsynced and written-back lengths.
    /// </summary>
    public async Task<(long Synced, long Unsynced, long Retained)> PowerLossAtSyncAsync(string region, int tornBytes)
    {
        var node = _regions[region];
        using var armed = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await node.Process!.WaitForExitAsync(armed.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"the armed sync of {region} never ran");
        }

        await KillAsync(region);
        var (faults, wal) = (Faults(region), TenantWal(region));
        var written = await File.ReadAllBytesAsync(wal);
        var synced = faults.SyncedLength(wal);
        faults.DropUnsynced(node.DataDirectory);
        faults.Clear();
        var unsynced = written.LongLength - synced;
        var frame = BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(8, 4)) + 24L;
        var retained = unsynced < frame ? 0 : unsynced - frame + Math.Min(tornBytes, frame - 1);
        await using (var file = new FileStream(wal, FileMode.Append, FileAccess.Write))
        {
            await file.WriteAsync(written.AsMemory(checked((int)synced), checked((int)retained)));
        }

        return (synced, unsynced, retained);
    }

    /// <summary>Starts the region's process again on the same data directory and machine id.</summary>
    public Task StartAgainAsync(string region) =>
        _regions[region].Process is { HasExited: false } ? throw new InvalidOperationException($"{region} is still running") : LaunchAsync(_regions[region]);

    public async ValueTask DisposeAsync()
    {
        foreach (var region in _regions.Keys)
        {
            await KillAsync(region);
            _regions[region].Process?.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a killed process still held: the temporary directory is left for the OS to clean.
        }
    }

    private string MemberFile(Region node) => Path.Combine(_root, "members", node.MachineId);

    private string HoldFile(Region node) => Path.Combine(_root, $"{node.Name}.hold");

    private async Task LaunchAsync(Region node)
    {
        var info = Path.Combine(_root, $"{node.Name}.info");
        File.Delete(info);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerraBase.E2E.dll"));
        start.ArgumentList.Add("--treenode-filter");
        start.ArgumentList.Add($"/*/*/{nameof(RegionProcessHost)}/*");
        start.ArgumentList.Add("--results-directory");
        start.ArgumentList.Add(Path.Combine(_root, $"{node.Name}.results"));
        // The child is a node, not a test run of this suite: none of the suite's own settings reach it.
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name.StartsWith("TS_", StringComparison.Ordinal)).ToList())
        {
            _ = start.Environment.Remove(name);
        }

        start.Environment[PeerTls.TestSecretVariable] = PeerTls.Secret;
        start.Environment[RegionProcessHost.InfoVariable] = info;
        start.Environment[RegionProcessHost.RegionVariable] = node.Name;
        start.Environment[RegionProcessHost.MachineVariable] = node.MachineId;
        start.Environment[RegionProcessHost.DataVariable] = node.DataDirectory;
        start.Environment[RegionProcessHost.MembersVariable] = Path.Combine(_root, "members");
        start.Environment[RegionProcessHost.HoldVariable] = HoldFile(node);
        if (_walFaults)
        {
            Faults(node.Name).AddTo(start.Environment);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"the {node.Name} process did not start");
        process.OutputDataReceived += (_, line) => node.Append(line.Data);
        process.ErrorDataReceived += (_, line) => node.Append(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        node.Process = process;

        var clock = Stopwatch.StartNew();
        while (!File.Exists(info))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"the {node.Name} process exited with {process.ExitCode}:\n{node.Output}");
            }

            if (clock.Elapsed > StartTimeout)
            {
                throw new TimeoutException($"the {node.Name} process did not start within {StartTimeout.TotalSeconds:F0} s:\n{node.Output}");
            }

            await Task.Delay(100);
        }

        var listeners = JsonNode.Parse(await File.ReadAllTextAsync(info))!;
        var address = new Uri(listeners["internal"]!.GetValue<string>());
        node.ClientAddress = new Uri(listeners["client"]!.GetValue<string>());
        node.Client = new HttpClient(PeerTls.Credential.Wrap(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }))
        {
            BaseAddress = address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30),
        };
        // These processes qualify the multi-region guarantee: writes ask for another region's durable ack (decision 0013e).
        node.Client.DefaultRequestHeaders.Add("TB-Durability", "remote_durable");
    }

    private async Task RegisterAsync(Region node, string owner, string[] replicas)
    {
        var database = await CatalogApi.CreateAsync(this[node.Name], owner, "canary", replicas);
        _organization = database.Organization;
        _project = database.Project;
        _database = database.Id;
    }

    private sealed class Region(string name, string machineId, string dataDirectory)
    {
        private readonly StringBuilder _output = new();

        public string Name { get; } = name;

        public string MachineId { get; } = machineId;

        public string DataDirectory { get; } = dataDirectory;

        public Process? Process { get; set; }

        public HttpClient? Client { get; set; }

        public Uri? ClientAddress { get; set; }

        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        public void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_output)
            {
                // The tail is what a failure needs.
                if (_output.Length > 64 * 1024)
                {
                    _ = _output.Remove(0, _output.Length - (32 * 1024));
                }

                _ = _output.AppendLine(line);
            }
        }
    }
}

/// <summary>
/// One region of <see cref="RegionProcesses"/>: hosts a node of the real <c>Program</c> in this process until the process is
/// killed. Without its variables it skips: it is a host, not a scenario.
/// </summary>
[Category("region-process-host")]
public sealed class RegionProcessHost
{
    internal const string InfoVariable = "TERRABASE_E2E_REGION_HOST";
    internal const string RegionVariable = "TERRABASE_E2E_REGION_NAME";
    internal const string MachineVariable = "TERRABASE_E2E_REGION_MACHINE";
    internal const string DataVariable = "TERRABASE_E2E_REGION_DATA";
    internal const string MembersVariable = "TERRABASE_E2E_REGION_MEMBERS";
    internal const string HoldVariable = "TERRABASE_E2E_REGION_HOLD";

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    [Timeout(6 * 3600 * 1000)]
    public async Task Hosts_one_region_until_its_process_is_killed(CancellationToken cancellationToken)
    {
        var info = Environment.GetEnvironmentVariable(InfoVariable);
        Skip.When(string.IsNullOrEmpty(info), "a region of RegionProcesses: started by the harness in a process of its own");
        var (region, machine, members) = (Environment.GetEnvironmentVariable(RegionVariable)!, Environment.GetEnvironmentVariable(MachineVariable)!,
            Environment.GetEnvironmentVariable(MembersVariable)!);
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = machine;
            settings["data-directory"] = Environment.GetEnvironmentVariable(DataVariable)!;
        }, services =>
        {
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
            services.AddSingleton<IMemberDirectory>(new DirectoryOfMembers(members));
            services.AddSingleton<StorageChangeGroupSink>();
            services.AddSingleton<IChangeGroupSink>(provider => new HeldSink(provider.GetRequiredService<StorageChangeGroupSink>(),
                Environment.GetEnvironmentVariable(HoldVariable)!));
            services.AddTerraScaleEndpoints([EndpointRegistration.Create<ShapeSealProbeEndpoint>()]);
        });

        // Listed, then announced: the harness drives the node only once its peers can find it.
        // Written aside and moved, so a reader never sees half an entry (names starting with '.' are not machines).
        await File.WriteAllTextAsync(Path.Combine(members, "." + machine), $"{region}\n{api.Server.PeerAddress}", cancellationToken);
        File.Move(Path.Combine(members, "." + machine), Path.Combine(members, machine), overwrite: true);
        await File.WriteAllTextAsync(info + ".partial", new JsonObject
        {
            ["internal"] = api.Server.BaseAddress.ToString(),
            ["client"] = api.Server.ClientAddress?.ToString(),
        }.ToJsonString(), cancellationToken);
        File.Move(info + ".partial", info!, overwrite: true);
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    // Refuses the held database's batches and repairs before they apply, as a failed apply is refused: the stream stays.
    private sealed class HeldSink(IChangeGroupSink inner, string hold) : IChangeGroupSink
    {
        public ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null) =>
            Held(database) ? throw new IOException("the harness holds this database's replication") : inner.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);

        public ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            Held(database) ? throw new IOException("the harness holds this database's replication") : inner.RepairAsync(database, incarnation, states, cancellationToken);

        public ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            Held(database) ? ValueTask.FromResult<Ulid?>(null) : inner.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);

        private bool Held(TenantContext database)
        {
            try
            {
                return File.ReadAllText(hold) == database.DatabaseId;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }
    }

    // The machine list: one file per started machine, named by its id, holding its region and internal listener.
    private sealed class DirectoryOfMembers(string directory) : IMemberDirectory
    {
        public Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken)
        {
            List<ClusterMember> members = [];
            foreach (var file in Directory.EnumerateFiles(directory).Where(file => !Path.GetFileName(file).StartsWith('.')))
            {
                try
                {
                    if (File.ReadAllText(file).Split('\n') is [var region, var address])
                    {
                        members.Add(new ClusterMember(Path.GetFileName(file), address, region, string.Empty));
                    }
                }
                catch (FileNotFoundException)
                {
                    // A machine leaving while the list is read.
                }
            }

            return Task.FromResult<ImmutableArray<ClusterMember>>([.. members.OrderBy(member => member.Name, StringComparer.Ordinal)]);
        }
    }
}
