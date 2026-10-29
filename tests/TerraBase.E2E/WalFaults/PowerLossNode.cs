using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.E2E.WalFaults;

internal sealed class PowerLossNode : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("terrabase-power-").FullName;
    private readonly string _machine = EntityIds.New().ToString().ToLowerInvariant();
    private Process? _process;
    private readonly Queue<string> _errors = new();
    private readonly string _host;

    private PowerLossNode(string host)
    {
        _host = host;
        Faults = new WalSyncFaults(Data + ".walfaults");
    }

    public string PathOf(string name) => Path.Combine(_root, name);

    public HttpClient Client { get; private set; } = new();
    public Uri Address => Client.BaseAddress!;

    private string Data => Path.Combine(_root, Cluster.Gru);

    private WalSyncFaults Faults { get; }

    public static async Task<PowerLossNode> StartAsync(string host = nameof(RegionProcessHost))
    {
        var node = new PowerLossNode(host);
        try
        {
            _ = Directory.CreateDirectory(node.Data);
            await node.StartAgainAsync();
            return node;
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
    }

    public async Task StartAgainAsync()
    {
        var info = Path.Combine(_root, "gru.info");
        File.Delete(info);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerraBase.E2E.dll"));
        start.ArgumentList.Add("--treenode-filter");
        start.ArgumentList.Add($"/*/*/{_host}/*");
        start.ArgumentList.Add("--results-directory");
        start.ArgumentList.Add(Path.Combine(_root, "results"));
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name.StartsWith("TS_", StringComparison.Ordinal)).ToList())
        {
            _ = start.Environment.Remove(name);
        }

        start.Environment[PeerTls.TestSecretVariable] = PeerTls.Secret;
        start.Environment[RegionProcessHost.InfoVariable] = info;
        start.Environment[RegionProcessHost.RegionVariable] = Cluster.Gru;
        start.Environment[RegionProcessHost.MachineVariable] = _machine;
        start.Environment[RegionProcessHost.DataVariable] = Data;
        start.Environment["TMPDIR"] = Directory.CreateDirectory(Path.Combine(_root, "tmp")).FullName;
        Faults.AddTo(start.Environment);
        _process?.Dispose();
        _process = Process.Start(start) ?? throw new InvalidOperationException("the node process did not start");
        _process.OutputDataReceived += static (_, _) => { };
        _process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { } text)
            {
                lock (_errors)
                {
                    _errors.Enqueue(text);
                    while (_errors.Count > 20)
                    {
                        _ = _errors.Dequeue();
                    }
                }
            }
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        var clock = Stopwatch.StartNew();
        while (!File.Exists(info))
        {
            if (_process.HasExited || clock.Elapsed > TimeSpan.FromSeconds(120))
            {
                string errors;
                lock (_errors)
                {
                    errors = string.Join('\n', _errors);
                }

                throw new InvalidOperationException($"the node did not start (exited: {_process.HasExited}):\n{errors}");
            }

            await Task.Delay(100);
        }

        Client.Dispose();
        Client = PeerTls.CreateClient(new Uri(JsonNode.Parse(await File.ReadAllTextAsync(info))!["internal"]!.GetValue<string>()));
        Client.Timeout = TimeSpan.FromSeconds(30);
    }

    public Task<ManagedDatabaseRef> CreateDatabaseAsync(string user, IReadOnlyList<string>? replicas = null) =>
        CatalogApi.CreateAsync(Client, user, "canary", replicas ?? []);

    public async Task<HttpStatusCode> StatusAsync(Uri uri, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await Client.SendAsync(request);
        return response.StatusCode;
    }

    public void DelaySyncs(TimeSpan delay) => Faults.Delay(delay);
    public void KillAtNextSync() => Faults.KillAtSync(1);
    public void ClearFaults() => Faults.Clear();
    public async Task PowerLossAsync()
    {
        if (!_process!.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync();
        Faults.DropUnsynced(Data);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        _process?.Dispose();
        Client.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
