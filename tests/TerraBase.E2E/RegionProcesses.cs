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
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.E2E;

/// <summary>
/// Regions as separate operating-system processes, so a node can be killed with SIGKILL and started again on its data
/// while the others keep replicating. Each process runs <see cref="RegionProcessHost"/> (this test binary, one node of the
/// real <c>Program</c>), which finds its peers in a shared directory standing in for Fly's <c>vms.&lt;app&gt;.internal</c>:
/// every node lists itself there with its internal listener, and a kill removes the entry, as Fly's DNS drops a stopped
/// machine. The harness drives each node over its internal listener as the platform operator (h2c on loopback, every
/// scope on a project's data routes), so no per-process TLS or token is needed. Local only.
/// </summary>
internal sealed class RegionProcesses : IAsyncDisposable
{
    private const string Collection = "e2e";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);
    private readonly string _root = Directory.CreateTempSubdirectory("terrabase-regions-").FullName;
    private readonly Dictionary<string, Region> _regions = [with(StringComparer.Ordinal)];
    private readonly string _organization = EntityIds.New().ToString();
    private readonly string _project = EntityIds.New().ToString();
    private readonly string _database = EntityIds.New().ToString();

    private RegionProcesses()
    {
    }

    /// <summary>The database's path on every region, <c>/v1/projects/{project}/databases/canary</c>.</summary>
    public string DatabasePath => $"/v1/projects/{_project}/databases/canary";

    public IReadOnlyList<string> Regions => [.. _regions.Keys];

    /// <summary>The region's internal listener as the platform operator; a new client after each start.</summary>
    public HttpClient this[string region] => _regions[region].Client ?? throw new InvalidOperationException($"{region} is down");

    /// <summary>The document with <paramref name="id"/> in the harness's collection.</summary>
    public Uri Document(string id) => new($"{DatabasePath}/collections/{Collection}/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);

    /// <summary>Starts one process per region, registers the database placed in all of them, and waits until each serves it.</summary>
    public static async Task<RegionProcesses> StartAsync(params string[] regions)
    {
        var cluster = new RegionProcesses();
        try
        {
            Directory.CreateDirectory(Path.Combine(cluster._root, "members"));
            foreach (var region in regions)
            {
                var node = new Region(region, Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14], Path.Combine(cluster._root, region));
                Directory.CreateDirectory(node.DataDirectory);
                cluster._regions[region] = node;
                await cluster.LaunchAsync(node);
            }

            // Each node keeps its own catalog until the catalog replicates (R10a): every node registers the database under
            // the same id, placed in every region.
            var owner = EntityIds.New().ToString();
            foreach (var node in cluster._regions.Values)
            {
                await cluster.RegisterAsync(node, owner, [.. regions.Where(other => other != node.Name)]);
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
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name.StartsWith("FLY_", StringComparison.Ordinal)).ToList())
        {
            _ = start.Environment.Remove(name);
        }

        start.Environment[RegionProcessHost.InfoVariable] = info;
        start.Environment[RegionProcessHost.RegionVariable] = node.Name;
        start.Environment[RegionProcessHost.MachineVariable] = node.MachineId;
        start.Environment[RegionProcessHost.DataVariable] = node.DataDirectory;
        start.Environment[RegionProcessHost.MembersVariable] = Path.Combine(_root, "members");
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

        var address = new Uri(JsonNode.Parse(await File.ReadAllTextAsync(info))!["internal"]!.GetValue<string>());
        node.Client = new HttpClient(new SocketsHttpHandler { UseCookies = false })
        {
            BaseAddress = address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private async Task RegisterAsync(Region node, string owner, string[] replicas)
    {
        var commit = JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object[]
            {
                new { op = "create_organization", id = _organization, name = $"regions-{_organization}" },
                new { op = "set_plan", organization = _organization, plan = "enterprise" },
                new { op = "create_project", organization = _organization, id = _project, name = "regions" },
                new { op = "create_database", organization = _organization, project = _project, name = "canary", id = _database, replica_regions = replicas },
            },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/management/commits", UriKind.Relative))
        {
            Content = new StringContent(commit, Encoding.UTF8, "application/json"),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Add("X-TerraBase-User-Id", owner);
        using var response = await this[node.Name].SendAsync(request);
        await Docs.EnsureSuccessAsync(response);
    }

    private sealed class Region(string name, string machineId, string dataDirectory)
    {
        private readonly StringBuilder _output = new();

        public string Name { get; } = name;

        public string MachineId { get; } = machineId;

        public string DataDirectory { get; } = dataDirectory;

        public Process? Process { get; set; }

        public HttpClient? Client { get; set; }

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
        });

        // Listed, then announced: the harness drives the node only once its peers can find it.
        // Written aside and moved, so a reader never sees half an entry (names starting with '.' are not machines).
        await File.WriteAllTextAsync(Path.Combine(members, "." + machine), $"{region}\n{api.Server.PeerAddress}", cancellationToken);
        File.Move(Path.Combine(members, "." + machine), Path.Combine(members, machine), overwrite: true);
        await File.WriteAllTextAsync(info + ".partial", new JsonObject { ["internal"] = api.Server.BaseAddress.ToString() }.ToJsonString(), cancellationToken);
        File.Move(info + ".partial", info!, overwrite: true);
        await Task.Delay(Timeout.Infinite, cancellationToken);
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
