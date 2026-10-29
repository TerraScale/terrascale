using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TerraBase.Primitives;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.E2E;

/// <summary>
/// One TerraBase node as its own operating-system process: the production entry point (<c>terrabase.dll</c> of this
/// build), its own data directory and fixed loopback ports. It can be killed with SIGKILL and started again on the same
/// data, which an in-process node cannot. This is a harness for the current architecture: it drives the node only over
/// its listeners, as the platform operator on the internal listener (loopback locally, h2c, generated test credential) and as a
/// client on the public one. It is not the deleted CrashableTerraBase: it takes no peer, TLS or catalog-primary
/// settings, because design C discovers peers itself.
/// </summary>
internal sealed class NodeProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private readonly int _port = FreePort();
    private readonly int _internalPort = FreePort();
    private readonly int _metricsPort = FreePort();
    private readonly StringBuilder _output = new();
    private Process? _process;

    private NodeProcess()
    {
        Internal = new HttpClient(PeerTls.Credential.Wrap(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }))
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_internalPort}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>The internal listener as the platform operator: on a project's data routes it holds every scope.</summary>
    public HttpClient Internal { get; }

    /// <summary>The node's data directory, kept across kills.</summary>
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("terrabase-process-").FullName;

    /// <summary>The process output so far, for failure messages.</summary>
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

    /// <summary>Starts a node process on a fresh data directory and waits until it answers.</summary>
    public static async Task<NodeProcess> StartAsync()
    {
        var node = new NodeProcess();
        try
        {
            await node.StartAgainAsync();
            return node;
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts the process again on the same data directory and ports, after <see cref="KillAsync"/>.</summary>
    public async Task StartAgainAsync()
    {
        if (_process is { HasExited: false })
        {
            throw new InvalidOperationException("the node process is still running");
        }

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Binary());
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["data-directory"] = DataDirectory,
            ["port"] = $"{_port}",
            ["internal-port"] = $"{_internalPort}",
            ["metrics-port"] = $"{_metricsPort}",
            ["postgres-port"] = "0",
            ["wire-port"] = "0",
            ["storage-threads"] = "2",
        })
        {
            start.ArgumentList.Add($"--{name}={value}");
        }

        // Local node: no ambient ts-orch identity or TerraBase settings of the test process reach the node.
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TS_", StringComparison.Ordinal)
            || name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name == "PRIMARY_REGION").ToList())
        {
            _ = start.Environment.Remove(name);
        }

        // Real child binaries use only the synthetic fixture master, which survives every restart.
        TestMasterKey.ConfigureProcess(start);
        start.Environment[PeerCredential.EnvironmentVariable] = PeerTls.Secret;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        var process = Process.Start(start) ?? throw new InvalidOperationException("the node process did not start");
        process.OutputDataReceived += (_, line) => Append(line.Data);
        process.ErrorDataReceived += (_, line) => Append(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"the node process exited with {process.ExitCode}:\n{Output}");
            }

            try
            {
                using var answer = await health.GetAsync(new Uri($"http://127.0.0.1:{_port}/healthz"));
                if (answer.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            if (clock.Elapsed > StartTimeout)
            {
                throw new TimeoutException($"the node process did not answer /healthz within {StartTimeout.TotalSeconds:F0} s:\n{Output}");
            }

            await Task.Delay(100);
        }
    }

    /// <summary>SIGKILL: the process ends at once, without shutdown, flushes or disposal.</summary>
    public async Task KillAsync()
    {
        if (_process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    /// <summary>Creates an organization, project and database (in this node's region) as the operator; answers the database path.</summary>
    public async Task<string> CreateTenantAsync()
    {
        var database = await CatalogApi.CreateAsync(Internal, EntityIds.New().ToString(), "canary", []);
        return database.Data;
    }

    public async ValueTask DisposeAsync()
    {
        await KillAsync();
        _process?.Dispose();
        Internal.Dispose();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file the killed process still held: the temporary directory is left for the OS to clean.
        }
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_output)
        {
            // The tail is what a failure needs; a long run would otherwise keep every log line.
            if (_output.Length > 64 * 1024)
            {
                _ = _output.Remove(0, _output.Length - (32 * 1024));
            }

            _ = _output.AppendLine(line);
        }
    }

    // tests/TerraBase.E2E/bin/<Configuration>/<tfm>/ -> src/TerraBase/bin/<Configuration>/<tfm>/terrabase.dll
    private static string Binary()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var (tfm, configuration) = (bin.Name, bin.Parent!.Name);
        var root = bin.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var path = Path.Combine(root, "src", "TerraBase", "bin", configuration, tfm, "terrabase.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException("build src/TerraBase first (make terrabase-build)", path);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
