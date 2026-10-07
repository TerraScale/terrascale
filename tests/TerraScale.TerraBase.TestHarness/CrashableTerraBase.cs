using System.Diagnostics;
using System.Globalization;
using System.Text;
using TerraBase.Configuration;

namespace TerraBase.Testing;

// The test owns its directory, certificates and ports from TerraBaseTestServer.FreePort.
// Await lifecycle operations in order; HTTP requests may run concurrently with a crash.
internal sealed class CrashableTerraBase : IAsyncDisposable
{
    private const int MaxOutputCharacters = 8 * 1024; // 16 KiB of UTF-16 per stream.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, string> _settings;
    private readonly string _workingDirectory = Environment.CurrentDirectory;
    private Process? _process;
    private CancellationTokenSource? _drainStop;
    private Task<string> _stdout = Task.FromResult(string.Empty);
    private Task<string> _stderr = Task.FromResult(string.Empty);
    private bool _disposed;

    private CrashableTerraBase(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = new Dictionary<string, string>(settings, StringComparer.Ordinal);
        foreach (var name in _settings.Keys)
        {
            if (!TerraBaseOptions.SettingNames.Contains(name))
            {
                throw new ArgumentException($"unknown TerraBase setting: {name}", nameof(settings));
            }
        }

        if (!_settings.TryGetValue("data-directory", out var directory) || string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("data-directory must be supplied and owned by the test", nameof(settings));
        }

        var adminPort = RequirePort("admin-port");
        ClientAddress = new UriBuilder(Uri.UriSchemeHttps, "localhost", RequirePort("client-port")).Uri;
        PeerAddress = new UriBuilder(Uri.UriSchemeHttps, "localhost", RequirePort("peer-port")).Uri;
        Client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", adminPort).Uri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public HttpClient Client { get; }
    public Uri ClientAddress { get; }
    public Uri PeerAddress { get; }

    public static async Task<CrashableTerraBase> StartAsync(IReadOnlyDictionary<string, string> settings, CancellationToken token)
    {
        var server = new CrashableTerraBase(settings);
        try
        {
            await server.RestartAsync(token);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async Task RestartAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("CrashAsync must finish before restarting TerraBase");
        }

        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _workingDirectory,
        };
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
        foreach (var name in TerraBaseOptions.SettingNames)
        {
            start.ArgumentList.Add($"--{name}={_settings.GetValueOrDefault(name, string.Empty)}");
        }

        _stdout = Task.FromResult(string.Empty);
        _stderr = Task.FromResult(string.Empty);
        try
        {
            // No shell or wrapper: the production child inherits the suite's nice priority.
            _process = Process.Start(start) ?? throw new InvalidOperationException("the TerraBase process did not start");
            _drainStop = new CancellationTokenSource();
            _stdout = CaptureAsync(_process.StandardOutput, _drainStop.Token);
            _stderr = CaptureAsync(_process.StandardError, _drainStop.Token);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(StartupTimeout);
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"TerraBase exited before readiness with code {_process.ExitCode}");
                }

                using var probe = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                probe.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    using var response = await Client.GetAsync(new Uri("/readyz", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, probe.Token);
                    if (response.IsSuccessStatusCode && !_process.HasExited)
                    {
                        startup.Token.ThrowIfCancellationRequested();
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Connection refusal is expected while Kestrel starts.
                }
                catch (OperationCanceledException) when (!startup.IsCancellationRequested)
                {
                    // A stalled readiness request must not hide a child that exited.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), startup.Token);
            }
        }
        catch (Exception error)
        {
            await StopAsync();
            if (error is OperationCanceledException && token.IsCancellationRequested)
            {
                throw;
            }

            throw new InvalidOperationException($"TerraBase startup failed: {error.Message}\nstdout:\n{await _stdout}\nstderr:\n{await _stderr}", error);
        }
    }

    public async Task CrashAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var process = _process ?? throw new InvalidOperationException("TerraBase is not running");
        if (process.HasExited)
        {
            var exitCode = process.ExitCode;
            await StopAsync();
            throw new InvalidOperationException($"TerraBase exited with code {exitCode} before the requested crash\nstdout:\n{await _stdout}\nstderr:\n{await _stderr}");
        }

        try
        {
            // Success requires an actual abrupt kill, never a graceful stop or an already-exited process.
            process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            await process.WaitForExitAsync(cleanup.Token);
            // Unix reports 128 + SIGKILL (9); Windows TerminateProcess uses -1.
            var killedExitCode = OperatingSystem.IsWindows() ? -1 : 137;
            if (process.ExitCode != killedExitCode)
            {
                throw new InvalidOperationException($"TerraBase exited with code {process.ExitCode} instead of the forced-kill code {killedExitCode}");
            }
        }
        finally
        {
            await StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync();
        }
        finally
        {
            Client.Dispose();
        }
    }

    private int RequirePort(string name)
    {
        if (!_settings.TryGetValue(name, out var value)
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentException($"{name} must be a positive test-owned port from TerraBaseTestServer.FreePort");
        }

        return port;
    }

    private async Task StopAsync()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        _drainStop?.CancelAfter(CleanupTimeout);
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The child may exit between the check and the cleanup kill.
                }
            }

            await process.WaitForExitAsync(cleanup.Token);
        }
        finally
        {
            try
            {
                await Task.WhenAll(_stdout, _stderr);
            }
            finally
            {
                process.Dispose();
                _process = null;
                _drainStop?.Dispose();
                _drainStop = null;
            }
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder(MaxOutputCharacters, MaxOutputCharacters);
        var chunk = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(chunk.AsMemory(), token)) != 0)
            {
                var excess = output.Length + read - MaxOutputCharacters;
                if (excess > 0)
                {
                    output.Remove(0, excess);
                }

                output.Append(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Retain bounded diagnostics even if a pipe outlives cleanup's deadline.
        }

        return output.ToString();
    }
}
