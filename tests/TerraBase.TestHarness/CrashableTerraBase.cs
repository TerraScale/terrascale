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
    private Task<int> _exitCleanup = Task.FromResult(0);
    private Task<string> _stdout = Task.FromResult(string.Empty);
    private Task<string> _stderr = Task.FromResult(string.Empty);
    private bool _disposed;
    private OutputTail _stdoutTail = new();
    private OutputTail _stderrTail = new();

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

    // The real exit/pipe owner outlives a bounded cleanup wait or a failed kill.
    public Task CleanupCompletion => _exitCleanup;

    public static async Task<CrashableTerraBase> StartAsync(IReadOnlyDictionary<string, string> settings, CancellationToken token, Action<Task>? failedStartCleanup = null)
    {
        var server = new CrashableTerraBase(settings);
        try
        {
            await server.RestartAsync(token);
            return server;
        }
        catch (Exception startupError)
        {
            try
            {
                failedStartCleanup?.Invoke(server.CleanupCompletion);
                await server.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(startupError, cleanupError).Flatten();
            }

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
        _stdoutTail = new();
        _stderrTail = new();
        try
        {
            // No shell or wrapper: the production child inherits the suite's nice priority.
            _process = Process.Start(start) ?? throw new InvalidOperationException("the TerraBase process did not start");
            var drainStop = new CancellationTokenSource();
            _stdout = CaptureAsync(_process.StandardOutput, _stdoutTail, drainStop.Token);
            _stderr = CaptureAsync(_process.StandardError, _stderrTail, drainStop.Token);
            _exitCleanup = CompleteExitAsync(_process, _stdout, _stderr, drainStop);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(StartupTimeout);
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (_exitCleanup.IsCompleted)
                {
                    throw new InvalidOperationException($"TerraBase exited before readiness with code {await _exitCleanup}");
                }

                using var probe = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                probe.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    using var response = await Client.GetAsync(new Uri("/readyz", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, probe.Token);
                    if (response.IsSuccessStatusCode && !_exitCleanup.IsCompleted)
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
            Exception? cleanupError = null;
            try
            {
                await StopAsync();
            }
            catch (Exception failure)
            {
                cleanupError = failure;
            }

            if (error is OperationCanceledException && token.IsCancellationRequested && cleanupError is null)
            {
                throw;
            }

            var startupError = new InvalidOperationException(
                $"TerraBase startup failed: {error.Message}\nstdout:\n{_stdoutTail.Text}\nstderr:\n{_stderrTail.Text}", error);
            throw cleanupError is null ? startupError : new AggregateException(startupError, cleanupError).Flatten();
        }
    }

    public async Task CrashAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var process = _process ?? throw new InvalidOperationException("TerraBase is not running");
        if (_exitCleanup.IsCompleted)
        {
            var exitCode = await _exitCleanup;
            await StopAsync();
            throw new InvalidOperationException($"TerraBase exited with code {exitCode} before the requested crash\nstdout:\n{await _stdout}\nstderr:\n{await _stderr}");
        }

        try
        {
            // Success requires an actual abrupt kill, never a graceful stop or an already-exited process.
            process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            var exitCode = await _exitCleanup.WaitAsync(cleanup.Token);
            // Unix reports 128 + SIGKILL (9); Windows TerminateProcess uses -1.
            var killedExitCode = OperatingSystem.IsWindows() ? -1 : 137;
            if (exitCode != killedExitCode)
            {
                throw new InvalidOperationException($"TerraBase exited with code {exitCode} instead of the forced-kill code {killedExitCode}");
            }
        }
        finally
        {
            await StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed && _process is null)
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
        try
        {
            if (!_exitCleanup.IsCompleted)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Only the exit owner unbinds this process; join it even before its task publishes completion.
        }

        await _exitCleanup.WaitAsync(cleanup.Token);
        _process = null;
    }

    private static async Task<int> CompleteExitAsync(Process process, Task<string> stdout, Task<string> stderr, CancellationTokenSource drainStop)
    {
        // Do not dispose the handle or end pipe ownership before the child actually exits.
        await process.WaitForExitAsync();
        var exitCode = process.ExitCode;
        drainStop.CancelAfter(CleanupTimeout);
        try
        {
            await Task.WhenAll(stdout, stderr);
        }
        finally
        {
            process.Dispose();
            drainStop.Dispose();
        }

        return exitCode;
    }

    private static async Task<string> CaptureAsync(StreamReader reader, OutputTail output, CancellationToken token)
    {
        var chunk = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(chunk.AsMemory(), token)) != 0)
            {
                output.Append(chunk, read);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Retain bounded diagnostics even if a pipe outlives cleanup's deadline.
        }

        return output.Text;
    }

    private sealed class OutputTail
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _text = new(MaxOutputCharacters, MaxOutputCharacters);

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        public void Append(char[] chunk, int count)
        {
            lock (_gate)
            {
                var excess = _text.Length + count - MaxOutputCharacters;
                if (excess > 0)
                {
                    _text.Remove(0, excess);
                }

                _text.Append(chunk, 0, count);
            }
        }
    }
}
