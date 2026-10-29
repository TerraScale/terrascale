using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using TerraBase.E2E;
using TerraBase.E2E.WalFaults;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

/// <summary>Isolated processes with genuine peer partitions, lossy transport, WAL delays and clock skew.</summary>
internal sealed partial class FaultSoakCluster : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("terrabase-fault-soak-").FullName;
    private readonly Dictionary<string, Node> _nodes = [with(StringComparer.Ordinal)];
    private readonly Dictionary<(string From, string To), PeerLink> _links = [];
    private readonly List<LossyLink> _lossy = [];
    private readonly List<HttpClient> _clients = [];
    private readonly Dictionary<(string From, string To), NetworkFault> _networkFaults = [];
    private readonly HashSet<(string From, string To)> _cuts = [];
    private readonly string _organization = EntityIds.New().ToString();
    private readonly string _project = EntityIds.New().ToString();
    private readonly string _database = EntityIds.New().ToString();
    private readonly string _owner = EntityIds.New().ToString();
    private readonly int _seed;

    private FaultSoakCluster(int seed, string[] regions)
    {
        _seed = seed;
        Regions = regions;
    }

    public string[] Regions { get; }
    public string Collection { get; } = "soak";
    public string DatabasePath => $"/v1/projects/{_project}/databases/soak";
    public string Diagnostics => string.Join("\n", _nodes.Select(node => node.Key + ": " + node.Value.Output[^Math.Min(node.Value.Output.Length, 8192)..]));
    public TenantContext Tenant => new(_organization, PlanTier.Enterprise, _project, _database);
    public HttpClient this[string region] => _nodes[region].Client ?? throw new HttpRequestException($"{region} is down");
    public HttpClient Management(string region) => _nodes[region].Management!;
    public Uri Document(string id, string? collection = null) =>
        new($"{DatabasePath}/collections/{collection ?? Collection}/documents/by-key?id={Uri.EscapeDataString(id)}", UriKind.Relative);

    public async Task<Ulid> IncarnationAsync(string region, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_nodes[region].Client is not null)
            {
                using var channel = PeerChannel.Create(_nodes[region].Peer!, PeerTls.Credential);
                try
                {
                    var answer = await new TerraBase.Features.Replication.V1.Replication.ReplicationClient(channel)
                        .DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(Tenant, null) }, cancellationToken: token);
                    if (answer.Held)
                    {
                        return new Ulid(answer.Incarnation.Span);
                    }
                }
                catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
                {
                    // A feed probes during intentional kills; the test thread checks unexpected exits.
                }
            }

            await Task.Delay(100, token);
        }
    }

    public static Task<FaultSoakCluster> StartAsync(int seed, CancellationToken cancellationToken) =>
        StartAsync(seed, ["gru", "sjc", "fra"], cancellationToken);

    private static async Task<FaultSoakCluster> StartAsync(int seed, string[] regions, CancellationToken cancellationToken)
    {
        var cluster = new FaultSoakCluster(seed, regions);
        try
        {
            var random = new Random(seed);
            foreach (var region in cluster.Regions)
            {
                var node = new Node(region, Path.Combine(cluster._root, region), random.Next(-90_000, 90_001));
                cluster._nodes.Add(region, node);
                Directory.CreateDirectory(node.Data);
                Directory.CreateDirectory(node.Members);
                await cluster.LaunchAsync(node, cancellationToken);
            }

            await cluster.RebuildLinksAsync();
            await cluster.ReadyAsync(new Uri($"{cluster.DatabasePath}/collections", UriKind.Relative), cancellationToken);
            await cluster.UntilAsync(async token =>
            {
                using var response = await cluster["gru"].PostAsJsonAsync(new Uri($"{cluster.DatabasePath}/collections", UriKind.Relative),
                    new { name = cluster.Collection }, token);
                return await AcceptedAsync(response, token);
            }, cancellationToken);
            await cluster.ReadyAsync(new Uri($"{cluster.DatabasePath}/collections/{cluster.Collection}", UriKind.Relative), cancellationToken);
            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    public async Task KillAsync(string region)
    {
        var node = _nodes[region];
        if (node.Process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        node.Client = null;
        foreach (var source in _nodes.Values)
        {
            File.Delete(Path.Combine(source.Members, node.Machine));
        }
    }

    public async Task RestartAsync(string region, CancellationToken cancellationToken = default)
    {
        var node = _nodes[region];
        if (node.Process is { HasExited: false })
        {
            throw new InvalidOperationException($"{region} is still running");
        }

        node.Process?.Dispose();
        node.Process = null;
        await LaunchAsync(node, cancellationToken);
        await RebuildLinksAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Task.WhenAll(_nodes.Keys.Select(KillAsync));
        }
        finally
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            foreach (var link in _links.Values)
            {
                await link.DisposeAsync();
            }

            foreach (var link in _lossy)
            {
                await link.DisposeAsync();
            }

            var stopped = _nodes.Values.All(node => node.Process is null || node.Process.HasExited);
            foreach (var node in _nodes.Values)
            {
                node.Process?.Dispose();
            }

            if (stopped)
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private WalSyncFaults Faults(string region) => new(_nodes[region].Data + ".walfaults");

    private async Task RebuildLinksAsync()
    {
        // Keep healthy routes and proxy ports stable while one process changes its listener port.
        foreach (var source in _nodes.Values)
        {
            foreach (var target in _nodes.Values.Where(node => node.Client is not null))
            {
                var address = target.Peer!;
                if (source != target)
                {
                    var edge = (source.Region, target.Region);
                    var endpoint = new IPEndPoint(IPAddress.Loopback, address.Port);
                    var changed = !_links.TryGetValue(edge, out var link);
                    if (link is null)
                    {
                        link = new PeerLink(endpoint);
                        _links.Add(edge, link);
                    }
                    else if (!link.Target.Equals(endpoint))
                    {
                        link.Target = endpoint;
                        changed = true;
                    }

                    link.Cut = _cuts.Contains(edge);
                    if (changed && _networkFaults.TryGetValue(edge, out var fault))
                    {
                        var lossy = new LossyLink(link.Target, fault.Delay, fault.Rate, fault.Resets, unchecked(_seed + _lossy.Count)).Start();
                        _lossy.Add(lossy);
                        link.Via = lossy.Endpoint;
                    }

                    if (changed)
                    {
                        link.Reset();
                    }

                    address = new Uri(link.Address);
                }

                var file = Path.Combine(source.Members, target.Machine);
                await File.WriteAllTextAsync(Path.Combine(source.Members, "." + target.Machine), $"{target.Region}\n{address}");
                File.Move(Path.Combine(source.Members, "." + target.Machine), file, overwrite: true);
            }
        }
    }

    private async Task LaunchAsync(Node node, CancellationToken cancellationToken)
    {
        var info = Path.Combine(_root, node.Region + ".info");
        File.Delete(info);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "TerraBase.Slow.dll"), "--treenode-filter",
            $"/*/*/{nameof(FaultSoakNode)}/*", "--results-directory", Path.Combine(_root, node.Region + ".results") })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TERRABASE_", StringComparison.Ordinal)
            || name.StartsWith("TS_", StringComparison.Ordinal)).ToArray())
        {
            start.Environment.Remove(name);
        }

        start.Environment[PeerTls.TestSecretVariable] = PeerTls.Secret;
        start.Environment["TMPDIR"] = Directory.CreateDirectory(Path.Combine(_root, node.Region, "tmp")).FullName;
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["INFO"] = info,
            ["REGION"] = node.Region,
            ["MACHINE"] = node.Machine,
            ["DATA"] = node.Data,
            ["MEMBERS"] = node.Members,
            ["SKEW"] = node.Skew.ToString(CultureInfo.InvariantCulture),
            ["LOCAL"] = Regions.Length == 1 ? "1" : "0",
            ["OWNER"] = _owner,
            ["ORGANIZATION"] = _organization,
            ["PROJECT"] = _project,
            ["DATABASE"] = _database,
        })
        {
            start.Environment[FaultSoakNode.Prefix + key] = value;
        }

        Faults(node.Region).AddTo(start.Environment);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {node.Region}");
        node.Process = process;
        process.OutputDataReceived += (_, line) => node.Append(line.Data);
        process.ErrorDataReceived += (_, line) => node.Append(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await UntilAsync(_ => process.HasExited
            ? throw new InvalidOperationException($"{node.Region} exited with {process.ExitCode}:\n{node.Output}")
            : Task.FromResult(File.Exists(info)), cancellationToken);
        var listeners = JsonNode.Parse(await File.ReadAllTextAsync(info, cancellationToken))!;
        node.Peer = new Uri(listeners["peer"]!.GetValue<string>());
        node.Client = PeerTls.CreateClient(new Uri(listeners["internal"]!.GetValue<string>()));
        // The soak qualifies the multi-region guarantee: writes ask for another region's durable ack (decision 0013e).
        node.Client.DefaultRequestHeaders.Add("TB-Durability", "remote_durable");
        node.Client.Timeout = TimeSpan.FromSeconds(30);
        _clients.Add(node.Client);
        node.Management = new HttpClient { BaseAddress = new Uri(listeners["client"]!.GetValue<string>()) };
        node.Management.DefaultRequestHeaders.Authorization = new("Bearer", listeners["projectToken"]!.GetValue<string>());
        _clients.Add(node.Management);
    }

    private async Task ReadyAsync(Uri uri, CancellationToken cancellationToken)
    {
        foreach (var region in Regions)
        {
            await UntilAsync(async token =>
            {
                using var response = await this[region].GetAsync(uri, token);
                return response.IsSuccessStatusCode;
            }, cancellationToken);
        }
    }

    private async Task UntilAsync(Func<CancellationToken, Task<bool>> condition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            foreach (var node in _nodes.Values.Where(node => node.Process is { HasExited: true } && node.Client is not null))
            {
                throw new InvalidOperationException($"{node.Region} exited:\n{node.Output}");
            }

            if (await condition(deadline.Token))
            {
                return;
            }

            await Task.Delay(100, deadline.Token);
        }
    }

    private static async Task<bool> AcceptedAsync(HttpResponseMessage response, CancellationToken cancellationToken, bool allowTaken = false)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable && (text.Contains("\"remote_region_unavailable\"", StringComparison.Ordinal)
            || text.Contains("\"catalog_unverified\"", StringComparison.Ordinal) || text.Contains("\"catalog_anchor_unavailable\"", StringComparison.Ordinal)))
        {
            return false;
        }

        if (allowTaken && response.StatusCode == HttpStatusCode.Conflict && (text.Contains("\"id_taken\"", StringComparison.Ordinal)
            || text.Contains("\"name_taken\"", StringComparison.Ordinal)))
        {
            return true;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private sealed class Node(string region, string root, int skew)
    {
        private readonly StringBuilder _output = new();
        public string Region { get; } = region;
        public string Machine { get; } = EntityIds.New().ToString().ToLowerInvariant();
        public string Data { get; } = Path.Combine(root, "data");
        public string Members { get; } = Path.Combine(root, "members");
        public int Skew { get; } = skew;
        public Process? Process { get; set; }
        public HttpClient? Client { get; set; }
        public HttpClient? Management { get; set; }
        public Uri? Peer { get; set; }
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
            lock (_output)
            {
                if (_output.Length > 64 * 1024)
                {
                    _output.Remove(0, 32 * 1024);
                }

                _output.AppendLine(line);
            }
        }
    }

    private sealed record NetworkFault(TimeSpan Delay, int Rate, double Resets);
}
