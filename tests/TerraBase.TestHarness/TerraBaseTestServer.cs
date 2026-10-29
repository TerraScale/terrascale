using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Hosting;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

public sealed class TerraBaseTestServer : IAsyncDisposable
{
    public const string PlatformSeedKey = "platform-seed";

    private readonly Factory _factory;
    private readonly string _temporaryDirectory;

    private TerraBaseTestServer(
        Factory factory, string temporaryDirectory, IReadOnlyList<Uri> listeners, IReadOnlyDictionary<string, string> settings)
    {
        _factory = factory;
        _temporaryDirectory = temporaryDirectory;
        DataDirectory = Path.GetFullPath(settings["data-directory"]);
        Listeners = listeners;
        BaseAddress = listeners[0];
        ClientAddress = new UriBuilder(listeners[1]) { Host = "127.0.0.1" }.Uri;
        MetricsAddress = new UriBuilder(listeners[2]) { Host = "127.0.0.1" }.Uri;
        PostgresPort = listeners[3].Port;
        Credential = factory.Services.GetRequiredService<InternalCredential>();
        Client = PeerTls.CreateClient(BaseAddress, credential: Credential);
    }

    public string DataDirectory { get; }
    public IReadOnlyList<Uri> Listeners { get; }
    public Uri BaseAddress { get; }
    public Uri? ClientAddress { get; }
    public Uri? PeerAddress => BaseAddress;
    public Uri MetricsAddress { get; }
    public int PostgresPort { get; }
    public HttpClient Client { get; }
    internal InternalCredential Credential { get; }
    public IServiceProvider Services => _factory.Services;
    public static bool IsDataDirectoryOwned(string dataDirectory)
    {
        try
        {
            using var attempt = new FileStream(Path.Combine(dataDirectory, ".terrabase.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException error) when (TursoStorageEngine.IsLockConflict(error))
        {
            return true;
        }
    }

    public static async Task InitializeDataDirectoryAsync(string dataDirectory)
    {
        InitializeProcessTimers();
        var options = new TerraBaseOptions { DataDirectory = dataDirectory, MemoryBudget = 64 * 1024 * 1024, StorageThreads = 1, StorageQueue = 16 };
        var services = new ServiceCollection();
        services.AddTerraBaseEngine(options);
        services.AddSingleton<INativeTemporaryStorage, TestNativeTemporaryStorage>();
        services.AddKeyedSingleton<IMasterKey, TestMasterKey>(EngineExtensions.DatabaseMasterKey);
        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IStorageEngine>();
    }

    public static async Task<TerraBaseTestServer> StartAsync(
        Action<Dictionary<string, string>>? settings = null, Action<IServiceCollection>? services = null, string environmentName = "Development",
        CancellationToken? cancellationToken = null, Action<Task>? failedStartCleanup = null, CancellationToken? lifetimeCancellationToken = null)
    {
        InitializeProcessTimers();
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-test-").FullName;
        var testToken = lifetimeCancellationToken ?? TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var linkedCancellation = cancellationToken is { } callerCancellation
            ? CancellationTokenSource.CreateLinkedTokenSource(testToken, callerCancellation)
            : null;
        var testCancellation = linkedCancellation?.Token ?? testToken;
        Factory? factory = null;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["internal-port"] = "0",
                ["port"] = "0",
                ["metrics-port"] = "0",
                ["postgres-port"] = "0",
                ["data-directory"] = dataDirectory,
                ["region"] = "test-region",
                ["storage-threads"] = "2",
                ["query-budget-seconds"] = "120",
            };
            settings?.Invoke(values);
            var topology = TestTopology.Take(values);
            var platformSeed = values.Remove(PlatformSeedKey, out var seedValue) && seedValue == "true";
            var arguments = TerraBaseOptions.SettingNames.ToDictionary(name => name, _ => string.Empty, StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                arguments[key] = value;
            }

            var withoutSeed = platformSeed ? services : collection =>
            {
                foreach (var seed in collection.Where(descriptor => descriptor.ImplementationType == typeof(PlatformSeed)).ToList())
                {
                    collection.Remove(seed);
                }

                services?.Invoke(collection);
            };
            factory = new Factory(arguments, topology, withoutSeed, environmentName, PeerTls.Credential, linkedCancellation, testCancellation);
            factory.UseKestrel();
            IServerAddressesFeature addresses;
            try
            {
                factory.Startup = Task.Factory.StartNew(factory.StartServer, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await factory.Startup.WaitAsync(testCancellation);
                addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                    ?? throw new InvalidOperationException("the server did not report its listening addresses");
            }
            catch (ObjectDisposedException error) when (string.Equals(error.ObjectName, "IServiceProvider", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the server exited before it listened; see its error output", error);
            }

            if (addresses.Addresses.Count == 0)
            {
                throw new InvalidOperationException("the server exited before it listened; see its error output");
            }

            var server = new TerraBaseTestServer(factory, dataDirectory, [.. addresses.Addresses.Select(address => new Uri(address))], values);
            return server;
        }
        catch (Exception startupError)
        {
            var failures = new List<Exception> { startupError };
            try
            {
                if (factory is not null)
                {
                    try
                    {
                        failedStartCleanup?.Invoke(factory.BeginCleanupAsync(dataDirectory));
                    }
                    catch (Exception callbackError)
                    {
                        failures.Add(callbackError);
                    }

                    await factory.CleanupAsync(dataDirectory);
                }
                else
                {
                    linkedCancellation?.Dispose();
                    Directory.Delete(dataDirectory, recursive: true);
                }
            }
            catch (Exception cleanupError)
            {
                failures.Add(cleanupError);
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(failures).Flatten();
            }

            throw;
        }
    }

    private static void InitializeProcessTimers()
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            Initialize();
            return;
        }

        using var flow = ExecutionContext.SuppressFlow();
        Initialize();

        static void Initialize()
        {
            using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            _ = Sdk.CreateMeterProviderBuilder();
        }
    }

    private const int FirstPort = 10_000;
    private static readonly int PortCount = EphemeralPortStart() - FirstPort;
    private static readonly Lock PortGate = new();
    private static readonly List<FileStream> PortClaims = [];
    private static int _nextPort = Environment.ProcessId;
    public static int FreePort()
    {
        lock (PortGate)
        {
            for (var tried = 0; tried < PortCount; tried++)
            {
                var port = FirstPort + (_nextPort++ % PortCount);
                if (TryClaim(port))
                {
                    return port;
                }
            }
        }

        throw new InvalidOperationException("no free port below the ephemeral port range");
    }

    private static bool TryClaim(int port)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "terrabase-test-ports")).FullName;
        FileStream claim;
        try
        {
            claim = new FileStream(Path.Combine(directory, port.ToString(CultureInfo.InvariantCulture)), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (TursoStorageEngine.IsLockConflict(error))
        {
            return false;
        }

        try
        {
            var ipv6 = Socket.OSSupportsIPv6;
            using var probe = new Socket(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (ipv6)
            {
                probe.DualMode = true;
            }

            probe.Bind(new IPEndPoint(ipv6 ? IPAddress.IPv6Any : IPAddress.Any, port));
        }
        catch (SocketException)
        {
            claim.Dispose();
            return false;
        }

        PortClaims.Add(claim);
        return true;
    }

    private static int EphemeralPortStart()
    {
        const string range = "/proc/sys/net/ipv4/ip_local_port_range";
        return File.Exists(range)
            ? int.Parse(File.ReadAllText(range).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture)
            : 49_152;
    }

    public static async Task EventuallyAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        time ??= TimeProvider.System;
        using var deadline = timeout is { } limit
            ? new CancellationTokenSource(limit, time)
            : CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        while (!await condition(deadline.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), time, deadline.Token);
        }

        deadline.Token.ThrowIfCancellationRequested();
    }

    public async Task<double> MetricAsync(string name, string label = "", CancellationToken cancellationToken = default)
    {
        var text = await Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), cancellationToken);
        var samples = text.Split('\n')
            .Where(line => (line.StartsWith(name + "{", StringComparison.Ordinal) || line.StartsWith(name + " ", StringComparison.Ordinal))
                && line.Contains(label, StringComparison.Ordinal))
            .Select(line =>
            {
                var start = line.Contains('}', StringComparison.Ordinal) ? line.LastIndexOf('}') + 1 : name.Length;
                return double.Parse(line[start..].Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            }).ToArray();
        return samples.Length == 0
            ? throw new InvalidOperationException(
                $"metric '{name}' with labels containing '{label}' has no samples; the scrape {(text.Contains($"# TYPE {name} ", StringComparison.Ordinal) ? "declares" : "does not declare")} it")
            : samples.Sum();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.CleanupAsync(_temporaryDirectory);
    }

    internal Task CleanupCompletion => _factory.BeginCleanupAsync(_temporaryDirectory);

    internal static async Task WaitForCleanupAsync(Task cleanup, Action<AggregateException> reportFailure, CancellationToken cancellationToken)
    {
        try
        {
            await cleanup.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = cleanup.ContinueWith(task => reportFailure(task.Exception!.Flatten()), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    public static void UseProductionQueryBudget(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["query-budget-seconds"] = TerraBaseOptions.DefaultQueryBudgetSeconds.ToString(CultureInfo.InvariantCulture);
    }

    public static void EnableConnectionDiagnostics(IServiceCollection services) => services.AddLogging(logging => logging
        .AddFilter("Microsoft.AspNetCore.Server.Kestrel.Connections", LogLevel.Debug)
        .AddFilter("Microsoft.AspNetCore.Server.Kestrel.Https", LogLevel.Debug)
        .AddFilter("Microsoft.AspNetCore.Server.Kestrel.BadRequests", LogLevel.Debug));
    public static void CaptureDebugLogs(IServiceCollection services, ILoggerProvider logs)
    {
        services.AddSingleton(logs);
        services.PostConfigure<LoggerFilterOptions>(filter => filter.Rules.Add(new LoggerFilterRule(logs.GetType().FullName, null, LogLevel.Debug, null)));
    }

    internal static void ReportCleanupFailure(AggregateException error)
    {
        using var standardError = Console.OpenStandardError();
        using var diagnostics = new StreamWriter(standardError, leaveOpen: true);
        diagnostics.WriteLine($"Test cleanup failed after caller cancellation: {error}");
    }

    private sealed class Factory(
        IReadOnlyDictionary<string, string> settings, TestTopology topology, Action<IServiceCollection>? services, string environmentName,
        InternalCredential internalCredential, CancellationTokenSource? linkedCancellation, CancellationToken cancellationToken) : WebApplicationFactory<Program>
    {
        private ShutdownHost? _host;
        private TursoStorageEngine? _nativeEngine;
        private readonly FixtureHttpHandlers _httpHandlers = new();

        public Task Startup { get; set; } = Task.CompletedTask;

        private readonly Lock _cleanupGate = new();
        private Task? _cleanup;

        public async Task CleanupAsync(string directory)
        {
            var entered = Stopwatch.GetTimestamp();
            var suiteElapsed = SuiteDiagnostics.Elapsed;
            var canceledAtEntry = cancellationToken.IsCancellationRequested;
            var cleanup = BeginCleanupAsync(directory);
            try
            {
                await WaitForCleanupAsync(cleanup, ReportCleanupFailure, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TUnit.Core.TestContext.Current?.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Test cleanup wait canceled: suite_elapsed_s={suiteElapsed.TotalSeconds:F3} cleanup_wait_s={Stopwatch.GetElapsedTime(entered).TotalSeconds:F3} canceled_at_entry={canceledAtEntry} cleanup_status={cleanup.Status}"));
                throw;
            }
        }

        public Task BeginCleanupAsync(string directory)
        {
            lock (_cleanupGate)
            {
                return _cleanup ??= CleanupCoreAsync(directory);
            }
        }

        private async Task CleanupCoreAsync(string directory)
        {
            using var cleanupLifetime = linkedCancellation;
            var errors = new List<Exception>();
            try
            {
                await Startup;
            }
            catch (Exception error)
            {
                if (cancellationToken.IsCancellationRequested && error is not OperationCanceledException)
                {
                    errors.Add(error);
                }
            }
            if (_host is not null && Startup.IsCompletedSuccessfully)
            {
                _host.RequestStop();
            }

            await ObserveAsync(() => DisposeAsync().AsTask());
            if (_host is not null)
            {
                if (Startup.IsCompletedSuccessfully)
                {
                    await ObserveAsync(() => _host.RunCompletion);
                }
            }
            await ObserveAsync(() => _nativeEngine?.CleanupCompletion ?? Task.CompletedTask);
            await ObserveAsync(() =>
            {
                _httpHandlers.Dispose();
                return Task.CompletedTask;
            });
            _host?.Dispose();

            var failures = new AggregateException(errors).Flatten().InnerExceptions.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
            if (failures.Length == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            else if (failures.Length > 1)
            {
                throw new AggregateException(failures);
            }

            async Task ObserveAsync(Func<Task> cleanup)
            {
                try
                {
                    await cleanup();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }

            Directory.Delete(directory, recursive: true);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            _host = new ShutdownHost(builder.Build(), cancellationToken);
            _host.Start();
            return _host;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(values =>
            {
                values.Replace(ServiceDescriptor.Singleton<INativeTemporaryStorage, TestNativeTemporaryStorage>());
                values.Replace(ServiceDescriptor.KeyedSingleton<IMasterKey, TestMasterKey>(EngineExtensions.DatabaseMasterKey));
                values.Replace(ServiceDescriptor.Singleton(internalCredential));
                values.Replace(ServiceDescriptor.Singleton(new DatabaseAllowance(_ => long.MaxValue)));
                values.Configure<PrometheusAspNetCoreOptions>(options => options.ScrapeResponseCacheDurationMilliseconds = 0);
                topology.ApplyIdentity(values);
                services?.Invoke(values);
                values.PostConfigureAll<HttpClientFactoryOptions>(options =>
                {
                    options.HandlerLifetime = Timeout.InfiniteTimeSpan;
                    options.SuppressHandlerScope = true;
                });
                values.Insert(0, ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter>(_httpHandlers));
                var native = values.Last(value => value.ServiceType == typeof(TursoStorageEngine));
                if (native.ImplementationInstance is TursoStorageEngine instance)
                {
                    _nativeEngine = instance;
                }
                else
                {
                    values[values.IndexOf(native)] = ServiceDescriptor.Describe(typeof(TursoStorageEngine),
                        provider => _nativeEngine = (TursoStorageEngine)(native.ImplementationFactory is { } create
                            ? create(provider)
                            : ActivatorUtilities.CreateInstance(provider, native.ImplementationType!)), native.Lifetime);
                }
            });
        }
    }

    private sealed class ShutdownHost : IHost
    {
        private readonly IHost _inner;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly CancellationTokenRegistration _cancellation;

        public ShutdownHost(IHost inner, CancellationToken testCancellation)
        {
            _inner = inner;
            _lifetime = inner.Services.GetRequiredService<IHostApplicationLifetime>();
            RunCompletion = inner.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
            _cancellation = testCancellation.Register(_lifetime.StopApplication);
        }

        public IServiceProvider Services => _inner.Services;

        public Task RunCompletion { get; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            RequestStop();
            await RunCompletion.WaitAsync(cancellationToken);
        }

        public void RequestStop()
        {
            if (!RunCompletion.IsCompleted)
            {
                _lifetime.StopApplication();
            }
        }

        public void Dispose()
        {
            _cancellation.Dispose();
        }
    }
}
