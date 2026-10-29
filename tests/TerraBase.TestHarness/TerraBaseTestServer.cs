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
using Turso.Data.Sqlite;
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
using TerraBase.Features.Replication;
using TerraBase.Hosting;

namespace TerraBase.Testing;

/// <summary>
/// Runs the real TerraBase <c>Program</c> on real Kestrel listeners bound to ephemeral ports,
/// with its own temporary data directory. Linked into both suites as source; deliberately not
/// a third test project.
/// </summary>
public sealed class TerraBaseTestServer : IAsyncDisposable
{
    /// <summary>Harness setting that runs the platform seed (<c>"true"</c>); without it a test node seeds nothing.</summary>
    public const string PlatformSeedKey = "platform-seed";

    private readonly Factory _factory;

    // The temporary directory this harness created, deleted on dispose even when a test chose another data directory.
    private readonly string _temporaryDirectory;

    private TerraBaseTestServer(
        Factory factory, string temporaryDirectory, IReadOnlyList<Uri> listeners, IReadOnlyDictionary<string, string> settings)
    {
        _factory = factory;
        _temporaryDirectory = temporaryDirectory;
        DataDirectory = Path.GetFullPath(settings["data-directory"]);
        // Kestrel reports listeners in bind order: internal, public, metrics, postgres. The last three bind every interface outside Development.
        Listeners = listeners;
        BaseAddress = listeners[0];
        ClientAddress = new UriBuilder(listeners[1]) { Host = "127.0.0.1" }.Uri;
        MetricsAddress = new UriBuilder(listeners[2]) { Host = "127.0.0.1" }.Uri;
        PostgresPort = listeners[3].Port;
        // The effective registration, so a test that replaced the credential gets a client that sends its secret.
        Credential = factory.Services.GetRequiredService<PeerCredential>();
        Client = PeerTls.CreateClient(BaseAddress, credential: Credential);
    }

    /// <summary>
    /// The server's data directory: its own temporary one, deleted on dispose, unless the test set <c>data-directory</c>
    /// (which the test then owns).
    /// </summary>
    public string DataDirectory { get; }

    /// <summary>The addresses Kestrel bound, in bind order: internal, public, metrics, postgres.</summary>
    public IReadOnlyList<Uri> Listeners { get; }

    /// <summary>The loopback origin of the internal listener (HTTP/2 only).</summary>
    public Uri BaseAddress { get; }

    /// <summary>The loopback origin of the public listener.</summary>
    public Uri? ClientAddress { get; }

    /// <summary>The internal listener, where other members reach this one (see <see cref="PeerTls"/>).</summary>
    public Uri? PeerAddress => BaseAddress;

    /// <summary>The loopback origin of the metrics listener.</summary>
    public Uri MetricsAddress { get; }

    /// <summary>The loopback port of the PostgreSQL wire listener.</summary>
    public int PostgresPort { get; }

    /// <summary>An HTTP/2 client for the internal listener that never follows redirects and never stores cookies.</summary>
    public HttpClient Client { get; }

    /// <summary>The peer credential this server accepts and <see cref="Client"/> sends: the process secret unless a test replaced it.</summary>
    internal PeerCredential Credential { get; }

    /// <summary>The running server's services, for checks no listener exposes (for example engine transactions or its endpoints).</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>
    /// Whether a process (this one included) owns <paramref name="dataDirectory"/>: tries the exclusive lock an engine
    /// holds on its <c>.terrabase.lock</c>, and gives it back at once.
    /// </summary>
    public static bool IsDataDirectoryOwned(string dataDirectory)
    {
        try
        {
            using var attempt = new FileStream(Path.Combine(dataDirectory, ".terrabase.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException error) when (TursoStorageEngine.IsLockConflict(error))
        {
            // Only the lock being held, the same test the engine applies. Every other failure (a missing directory, a
            // full disk, a broken lock file) propagates, so a broken probe never reads as an owned directory.
            return true;
        }
    }

    /// <summary>
    /// Records this build's storage layout and replica identity without starting a host or creating SYSTEM, so a test
    /// can seed files before real-server startup performs recovery.
    /// </summary>
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

    /// <summary>
    /// Starts a server. <paramref name="settings"/> edits the <c>--name=value</c> settings and the harness's
    /// <see cref="TestTopology"/> keys (default region <c>test-region</c>);
    /// <paramref name="services"/> replaces services after the application registered its own
    /// (for example a <see cref="TimeProvider"/> that tests move forward).
    /// <paramref name="environmentName"/> selects the host environment without changing process-wide variables.
    /// <paramref name="cancellationToken"/> defaults to the test execution token and bounds the caller's waits.
    /// <paramref name="failedStartCleanup"/> receives a failed start's cleanup, which outlives the throw when that token
    /// ends the wait for it; owners of files the startup reads (certificates) join it before releasing them.
    /// </summary>
    public static async Task<TerraBaseTestServer> StartAsync(
        Action<Dictionary<string, string>>? settings = null, Action<IServiceCollection>? services = null, string environmentName = "Development",
        CancellationToken? cancellationToken = null, Action<Task>? failedStartCleanup = null)
    {
        InitializeProcessTimers();
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-test-").FullName;
        var testToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
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
                // Each server starts its read threads eagerly, one per core by default: on a 46-core host the parallel suite
                // held about 7,400 threads and exhausted the user's process limit. Production nodes resolve this to 1.
                ["storage-threads"] = "2",
                // The query budget's time half counts wall time while Turso runs, so a loaded runner that deschedules or
                // GC-pauses a tiny list fails it (TS-742). Tests of that budget pin it with UseProductionQueryBudget.
                ["query-budget-seconds"] = "120",
            };
            settings?.Invoke(values);
            var topology = TestTopology.Take(values);
            // Harness key, not a TerraBase setting: production always seeds the platform databases (PlatformSeed). Most tests
            // assert the exact catalog and files of an empty node, so the harness leaves the seed out unless a test opts in;
            // PlatformDatabasesTests do, and ts-auth's suites run the real binary, which seeds.
            var platformSeed = values.Remove(PlatformSeedKey, out var seedValue) && seedValue == "true";

            // UseSetting values reach Program as --name=value entry-point arguments. Every other
            // setting is passed empty, which clears it, so ambient TERRABASE_* variables of the
            // test process never reach a test server.
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
                // Factory startup blocks waiting for the host; keep parallel starts off request workers.
                factory.Startup = Task.Factory.StartNew(factory.StartServer, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await factory.Startup.WaitAsync(testCancellation);
                addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                    ?? throw new InvalidOperationException("the server did not report its listening addresses");
            }
            catch (ObjectDisposedException error) when (string.Equals(error.ObjectName, "IServiceProvider", StringComparison.Ordinal))
            {
                // Program disposes the host before returning on a failed startup, releasing its directory lock.
                throw new InvalidOperationException("the server exited before it listened; see its error output", error);
            }

            if (addresses.Addresses.Count == 0)
            {
                // Program returned (for example with exit code 2 and a message on stderr) instead of running.
                throw new InvalidOperationException("the server exited before it listened; see its error output");
            }

            var server = new TerraBaseTestServer(factory, dataDirectory, [.. addresses.Addresses.Select(address => new Uri(address))], values);
            topology.Started(server.PeerAddress);
            return server;
        }
        catch (Exception startupError)
        {
            // Failed starts have no caller to dispose them. Keep startup, callback and cleanup failures.
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
        // Process-wide Turso and SDK diagnostics workers must not capture a fixture's startup context.
        if (ExecutionContext.IsFlowSuppressed())
        {
            Initialize();
            return;
        }

        using var flow = ExecutionContext.SuppressFlow();
        Initialize();

        static void Initialize()
        {
            // Setting a connection string initializes the provider factory without opening a database.
            using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            _ = Sdk.CreateMeterProviderBuilder();
        }
    }

    /// <summary>
    /// Starts two servers that name each other in their settings, for example in <c>peer-addresses</c>:
    /// <paramref name="settings"/> gets each server's settings, its own peer port and the other's, both from
    /// <see cref="FreePort"/>.
    /// </summary>
    public static async Task<(TerraBaseTestServer A, TerraBaseTestServer B)> StartPairAsync(Action<Dictionary<string, string>, int, int> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (portA, portB) = (FreePort(), FreePort());
        var a = await StartAsync(values => settings(values, portA, portB));
        try
        {
            return (a, await StartAsync(values => settings(values, portB, portA)));
        }
        catch
        {
            await a.DisposeAsync();
            throw;
        }
    }

    // Ports below the kernel's ephemeral range, which port-0 listeners and outgoing connections draw from: no other
    // socket takes one of these between FreePort and the server's bind. Each process starts at its own offset.
    private const int FirstPort = 10_000;
    private static readonly int PortCount = EphemeralPortStart() - FirstPort;
    private static readonly Lock PortGate = new();
    private static readonly List<FileStream> PortClaims = [];
    private static int _nextPort = Environment.ProcessId;

    /// <summary>
    /// A port for a server that must be named before it starts, such as a peer list or a restart on the same port.
    /// No other caller in this or another test process gets it while this process runs, and no port-0 listener
    /// or outgoing connection takes it.
    /// </summary>
    /// <remarks>
    /// ponytail: ports are never handed back, so a process gets at most the ports below the ephemeral range
    /// (about 22000 on Linux); a suite needs a few dozen.
    /// </remarks>
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

    // A lock file keeps other test processes off the port for this process's lifetime; a probe bind skips a port
    // something else already listens on.
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

    // Linux reads its range from procfs; elsewhere the IANA dynamic range, which Windows and macOS use by default.
    private static int EphemeralPortStart()
    {
        const string range = "/proc/sys/net/ipv4/ip_local_port_range";
        return File.Exists(range)
            ? int.Parse(File.ReadAllText(range).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture)
            : 49_152;
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds, failing after <paramref name="timeout"/> (default: the test's own timeout).
    /// The condition gets the deadline's token and must pass it to its I/O, so a check still running at the
    /// deadline is cancelled instead of outliving the test.
    /// </summary>
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

    /// <summary>
    /// The replication completion barrier: waits until, on every node in <paramref name="nodes"/>, every change
    /// group committed so far is durably acknowledged by every peer (the senders' durable lag, journal head
    /// minus acknowledged prefix, is zero). Each sender must finish measuring every existing journal head at
    /// startup first, so commits from before a node's startup count even when its peers are unavailable.
    /// </summary>
    public static Task ReplicatedAsync(params TerraBaseTestServer[] nodes) =>
        EventuallyAsync(async token =>
        {
            foreach (var node in nodes)
            {
                if (await node.MetricAsync("terrabase_replication_sender_initialized", "", token) != 1
                    || await node.MetricAsync("terrabase_replication_sender_durable_lag", "", token) != 0)
                {
                    return false;
                }
            }

            return true;
        });

    /// <summary>
    /// The sum of the Prometheus samples of <paramref name="name"/> on this server whose labels contain
    /// <paramref name="label"/> (for example <c>result="applied"</c>). Throws when no matching sample exists.
    /// </summary>
    public async Task<double> MetricAsync(string name, string label = "", CancellationToken cancellationToken = default)
    {
        var text = await Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), cancellationToken);
        var samples = text.Split('\n')
            .Where(line => (line.StartsWith(name + "{", StringComparison.Ordinal) || line.StartsWith(name + " ", StringComparison.Ordinal))
                && line.Contains(label, StringComparison.Ordinal))
            .Select(line =>
            {
                // name{labels} value [timestamp]
                var start = line.Contains('}', StringComparison.Ordinal) ? line.LastIndexOf('}') + 1 : name.Length;
                return double.Parse(line[start..].Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            }).ToArray();
        return samples.Length == 0
            ? throw new InvalidOperationException(
                $"metric '{name}' with labels containing '{label}' has no samples; the scrape {(text.Contains($"# TYPE {name} ", StringComparison.Ordinal) ? "declares" : "does not declare")} it")
            : samples.Sum();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.CleanupAsync(_temporaryDirectory);
    }

    // Resource owners can retain the actual cleanup after a bounded DisposeAsync wait is canceled.
    internal Task CleanupCompletion => _factory.BeginCleanupAsync(_temporaryDirectory);

    internal static async Task WaitForCleanupAsync(Task cleanup, Action<AggregateException> reportFailure, CancellationToken cancellationToken)
    {
        try
        {
            await cleanup.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The test can finish before its cleanup owner. Observe and report every eventual leaf failure.
            _ = cleanup.ContinueWith(task => reportFailure(task.Exception!.Flatten()), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>Pins the production query time budget, for tests that cross it with a faked deadline clock.</summary>
    public static void UseProductionQueryBudget(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["query-budget-seconds"] = TerraBaseOptions.DefaultQueryBudgetSeconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Captures connection and TLS closure reasons for an individual fixture.</summary>
    public static void EnableConnectionDiagnostics(IServiceCollection services) => services.AddLogging(logging => logging
        .AddFilter("Microsoft.AspNetCore.Server.Kestrel.Connections", LogLevel.Debug)
        .AddFilter("Microsoft.AspNetCore.Server.Kestrel.Https", LogLevel.Debug)
        .AddFilter("Microsoft.AspNetCore.Server.Kestrel.BadRequests", LogLevel.Debug));

    /// <summary>
    /// Delivers a test provider's Debug records. The production logging setup sets its default filters in PostConfigure,
    /// which overrides a plain Configure, so the provider-scoped rule is added after it, matched by full type name.
    /// </summary>
    public static void CaptureDebugLogs(IServiceCollection services, ILoggerProvider logs)
    {
        services.AddSingleton(logs);
        services.PostConfigure<LoggerFilterOptions>(filter => filter.Rules.Add(new LoggerFilterRule(logs.GetType().FullName, null, LogLevel.Debug, null)));
    }

    internal static void ReportCleanupFailure(AggregateException error)
    {
        // Bypass per-test console capture: this test's report may already have been emitted.
        using var standardError = Console.OpenStandardError();
        using var diagnostics = new StreamWriter(standardError, leaveOpen: true);
        diagnostics.WriteLine($"Test cleanup failed after caller cancellation: {error}");
    }

    private sealed class Factory(
        IReadOnlyDictionary<string, string> settings, TestTopology topology, Action<IServiceCollection>? services, string environmentName,
        PeerCredential peerCredential, CancellationTokenSource? linkedCancellation, CancellationToken cancellationToken) : WebApplicationFactory<Program>
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
                // A canceled wait replaces the test body's failure. Record whether cleanup started late or hung.
                TUnit.Core.TestContext.Current?.OutputWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Test cleanup wait canceled: suite_elapsed_s={suiteElapsed.TotalSeconds:F3} cleanup_wait_s={Stopwatch.GetElapsedTime(entered).TotalSeconds:F3} canceled_at_entry={canceledAtEntry} cleanup_status={cleanup.Status}"));
                throw;
            }
        }

        public Task BeginCleanupAsync(string directory)
        {
            lock (_cleanupGate)
            {
                // Bound the caller, not cleanup ownership. A timed-out test must not dispose live dependencies.
                return _cleanup ??= CleanupCoreAsync(directory);
            }
        }

        private async Task CleanupCoreAsync(string directory)
        {
            // A stopping machine leaves the test member directory.
            topology.Stopping();
            using var cleanupLifetime = linkedCancellation;
            var errors = new List<Exception>();
            try
            {
                // Startup may publish a host after the caller cancels. Keep responsibility for that host.
                await Startup;
            }
            catch (Exception error)
            {
                // A normal failed start is reported by StartAsync. After cancellation, retain late failures here.
                if (cancellationToken.IsCancellationRequested && error is not OperationCanceledException)
                {
                    errors.Add(error);
                }
            }

            // Program owns StopAsync and service disposal. Attempt every join even if a previous cleanup failed.
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

            // Capture can precede adapter construction; even an early startup failure must join native ownership.
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
            // StartServer already runs on the harness's dedicated startup thread, as in the factory default.
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
                // Storage and backup fakes are independent; storage restarts retain this process's test-only master.
                values.Replace(ServiceDescriptor.KeyedSingleton<IMasterKey, TestMasterKey>(EngineExtensions.DatabaseMasterKey));

                // Replaces Program's registration (empty: records peers as unmatched and cannot send) before the test's callbacks, which may replace it again.
                values.Replace(ServiceDescriptor.Singleton(peerCredential));
                // Fixtures hold more databases than Hobby allows; plan allowance tests register the plans' allowances again.
                values.Replace(ServiceDescriptor.Singleton(new DatabaseAllowance(_ => long.MaxValue)));
                // Completion barriers must observe commits after the previous scrape, not its cached zero.
                values.Configure<PrometheusAspNetCoreOptions>(options => options.ScrapeResponseCacheDurationMilliseconds = 0);
                topology.ApplyIdentity(values);
                services?.Invoke(values);
                topology.ApplyMembership(values);
                // Fixture teardown, rather than expiry timers, owns every outgoing HTTP pipeline.
                values.PostConfigureAll<HttpClientFactoryOptions>(options =>
                {
                    options.HandlerLifetime = Timeout.InfiniteTimeSpan;
                    options.SuppressHandlerScope = true;
                });
                // Run after all existing filters configure the pipeline, including logging and instrumentation.
                values.Insert(0, ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter>(_httpHandlers));
                // Capture when Program validates the directory, before service activation/StartingAsync can fail.
                var native = values.Last(value => value.ServiceType == typeof(TursoStorageEngine));
                if (native.ImplementationInstance is TursoStorageEngine instance)
                {
                    // Keep instance registrations externally owned, as DI does.
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

    // Calling the factory's normal StopAsync races Program.RunAsync's own StopAsync and disposes flush dependencies
    // while another shutdown flush still uses them. Only Program stops/disposes the real host.
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
            // Program.RunAsync (and its await using) owns service disposal, including failed starts.
            _cancellation.Dispose();
        }
    }
}
