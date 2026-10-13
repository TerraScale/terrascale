using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace TerraBase.E2E.WalFaults;

// shortcut: one node of RegionProcessHost started here with the shim, until RegionProcesses loads it (hookup on #108);
// then these scenarios move onto RegionProcesses.PowerLossAsync.
/// <summary>
/// One TerraBase node in a process of its own with the write-ahead log fault shim loaded, driven over its internal
/// listener as the platform operator; <see cref="PowerLossAsync"/> drops every unsynced byte of every log.
/// </summary>
internal sealed class PowerLossNode : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("terrabase-power-").FullName;
    private readonly string _machine = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
    private Process? _process;

    // The child's last lines of error output, for a startup failure's message.
    private readonly Queue<string> _errors = new();

    private PowerLossNode()
    {
        Faults = new WalSyncFaults(Data + ".walfaults");
    }

    public HttpClient Client { get; private set; } = new();

    /// <summary>The internal listener, which also serves the peer streams.</summary>
    public Uri Address => Client.BaseAddress!;

    private string Data => Path.Combine(_root, Cluster.Gru);

    private WalSyncFaults Faults { get; }

    public static async Task<PowerLossNode> StartAsync()
    {
        var node = new PowerLossNode();
        try
        {
            _ = Directory.CreateDirectory(node.Data);
            _ = Directory.CreateDirectory(Path.Combine(node._root, "members"));
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
        start.ArgumentList.Add($"/*/*/{nameof(RegionProcessHost)}/*");
        start.ArgumentList.Add("--results-directory");
        start.ArgumentList.Add(Path.Combine(_root, "results"));
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name.StartsWith("FLY_", StringComparison.Ordinal)).ToList())
        {
            _ = start.Environment.Remove(name);
        }

        start.Environment[RegionProcessHost.InfoVariable] = info;
        start.Environment[RegionProcessHost.RegionVariable] = Cluster.Gru;
        start.Environment[RegionProcessHost.MachineVariable] = _machine;
        start.Environment[RegionProcessHost.DataVariable] = Data;
        start.Environment[RegionProcessHost.MembersVariable] = Path.Combine(_root, "members");
        // The child's own temporary files (its test server's directories) stay under this node's root, which a SIGKILL leaves
        // behind and DisposeAsync deletes.
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
        Client = new HttpClient(new SocketsHttpHandler { UseCookies = false })
        {
            BaseAddress = new Uri(JsonNode.Parse(await File.ReadAllTextAsync(info))!["internal"]!.GetValue<string>()),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task CommitAsync(string user, JsonObject commit)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/management/commits", UriKind.Relative))
        {
            Content = JsonContent.Create(commit),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Add("X-TerraBase-User-Id", user);
        using var response = await Client.SendAsync(request);
        await Docs.EnsureSuccessAsync(response);
    }

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

    /// <summary>Every log sync of the node waits <paramref name="delay"/> first, from now on.</summary>
    public void DelaySyncs(TimeSpan delay) => Faults.Delay(delay);

    /// <summary>
    /// From now on the next log sync ends the process before it syncs anything: a power loss at this instant, whatever the
    /// node does next. Call <see cref="PowerLossAsync"/> after it.
    /// </summary>
    public void KillAtNextSync() => Faults.KillAtSync(1);

    /// <summary>Log syncs run normally again.</summary>
    public void ClearFaults() => Faults.Clear();

    // SIGKILL, then every write-ahead log back to its length at its last sync.
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
