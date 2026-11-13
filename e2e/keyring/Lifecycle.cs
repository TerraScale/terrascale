using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Features.Catalog;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace Journey.Keyring;

internal static class Lifecycle
{
    private static async Task Main(string[] args)
    {
        if (args is ["previous", var file])
        {
            PreviousBuild.Open(file);
            return;
        }
        if (args is ["rows"] or ["seed-rows"] or ["rows", _] or ["seed-rows", _])
        {
            Records.Rows(args[0]);
            return;
        }
        await RunAsync(args);
    }

    private static async Task RunAsync(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var cancellationToken = deadline.Token;
        var built = new TaskCompletionSource<IHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? hostEvents = null;
        using var listeners = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name == "Microsoft.Extensions.Hosting")
            {
                hostEvents = listener.Subscribe(new Observer<KeyValuePair<string, object?>>(entry =>
                {
                    if (entry is { Key: "HostBuilt", Value: IHost host })
                    {
                        built.TrySetResult(host);
                    }
                }));
            }
        }));
        var entryPoint = typeof(TerraBaseOptions).Assembly.GetType("Program")!.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.ReturnType == typeof(Task<int>) && method.GetParameters() is [{ ParameterType: var type }] && type == typeof(string[]));
        var running = (Task<int>)(entryPoint.Invoke(null, [Array.Empty<string>()]) ?? throw new InvalidOperationException("service did not start"));
        var app = await built.Task.WaitAsync(cancellationToken);
        using var eventSubscription = hostEvents;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(cancellationToken);
        try
        {
            if (args[0] is "credentials" or "rows" or "seed-rows")
            {
                await Records.RunAsync(app.Services, args, cancellationToken);
                return;
            }
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(args[0], cancellationToken));
            var root = request.RootElement;
            var database = new TenantContext(root.GetProperty("organization").GetString()!, PlanTier.Pro,
                root.GetProperty("project").GetString()!, root.GetProperty("database").GetString()!);
            if (root.TryGetProperty("mode", out var mode) && mode.GetString() == "published")
            {
                await ReadPublishedAsync(app.Services, database, cancellationToken);
                return;
            }
            var replicas = app.Services.GetRequiredService<IDatabaseReplicas>();
            await app.Services.GetRequiredService<RegionReplication>().SuspendAsync(database);
            using var http = new HttpClient(new SocketsHttpHandler { EnableMultipleHttp2Connections = true });
            http.DefaultRequestVersion = new Version(2, 0);
            http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            http.DefaultRequestHeaders.Add("x-terrabase-peer-credential", Environment.GetEnvironmentVariable("TERRABASE_PEER_SECRET"));
            var source = new Uri("http://127.0.0.1:18481/v1/sync/organizations/" + database.OrganizationId
                + "/projects/" + database.ProjectId + "/databases/" + database.DatabaseId);
            await replicas.OpenAsync(database, source, http, cancellationToken);
            await Console.Out.WriteLineAsync("keyring-driver:replica-ready");
            Require(await Console.In.ReadLineAsync(cancellationToken) == "detach", "detach command missing");
            await using (var writer = await replicas.BeginWriteAsync(database, cancellationToken)
                ?? throw new InvalidOperationException("replica writer missing"))
            {
                await writer.ExecuteAsync(new SqlStatement("UPDATE keyring_rows SET value=42", [], new Dictionary<string, object?>(),
                    10, 1024, TimeSpan.FromSeconds(5)), cancellationToken);
                await writer.CommitAsync(cancellationToken);
            }
            var detached = await replicas.DetachAsync(database, cancellationToken)
                ?? throw new InvalidOperationException("replica was not detached");
            await Console.Out.WriteLineAsync("keyring-driver:detached");
            Require(await Console.In.ReadLineAsync(cancellationToken) == "pass", "maintenance command missing");
            await RunPassAsync(app.Services, app.Services.GetServices<IWrappedKeyFiles>(), app.Services.GetRequiredService<RegionalOwners>(), cancellationToken);
            Require(File.Exists(detached) && File.Exists(detached + ".key"), "maintenance removed an active promotion");
            await Console.Out.WriteLineAsync("keyring-driver:survived");
            var options = app.Services.GetRequiredService<TerraBaseOptions>();
            var owners = new RegionalOwners(options with { NatsUrl = new Uri("nats://127.0.0.1:4222"), PeersFile = "journey" });
            Require(!owners.Writer, "role must begin as non-writer");
            await RunPassAsync(app.Services, ChangeRole(app.Services, owners, options), owners, cancellationToken, unknown: true);
            Require(owners.Writer, "role did not become writer during the pass");
            await Console.Out.WriteLineAsync("keyring-driver:nonwriter wrapped=-1 snapshot_age=-1 last_complete=0; became writer during local scan");
            var command = await Console.In.ReadLineAsync(cancellationToken);
            Require(command is "import" or "publish" or "fail", "import command missing");
            try
            {
                if (command == "fail")
                {
                    using var cancelled = new CancellationTokenSource();
                    await cancelled.CancelAsync();
                    try
                    {
                        await app.Services.GetRequiredService<IWalBackup>().ImportDatabaseAsync(database, detached, cancelled.Token);
                        throw new InvalidOperationException("cancelled import succeeded");
                    }
                    catch (OperationCanceledException)
                    {
                        await Console.Out.WriteLineAsync("keyring-driver:import-failed");
                    }
                }
                else
                {
                    await app.Services.GetRequiredService<IWalBackup>().ImportDatabaseAsync(database, detached, cancellationToken);
                    await Console.Out.WriteLineAsync("keyring-driver:published");
                    if (command == "publish")
                    {
                        Require(await Console.In.ReadLineAsync(cancellationToken) == "complete", "completion command missing");
                    }
                }
            }
            finally
            {
                replicas.CompleteDetach(detached);
            }
            if (command == "fail")
            {
                await replicas.OpenAsync(database, source, http, cancellationToken);
                await using var reader = await replicas.BeginReadAsync(database, cancellationToken)
                    ?? throw new InvalidOperationException("put-back replica missing");
                var rows = await reader.ExecuteAsync(new SqlStatement("SELECT value FROM keyring_rows", [], new Dictionary<string, object?>(),
                    10, 1024, TimeSpan.FromSeconds(5)), cancellationToken);
                Require(rows.Rows is [var held] && held is [long number] && number == 42, "failed import lost its replica row");
                await Console.Out.WriteLineAsync("keyring-driver:put-back row=42");
                return;
            }
            await ReadPublishedAsync(app.Services, database, cancellationToken);
            Require(!File.Exists(detached) && !File.Exists(detached + ".key"), "completed promotion retained detached files");
            await Console.Out.WriteLineAsync("keyring-driver:imported row=42");
        }
        finally
        {
            app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            Require(await running.WaitAsync(cancellationToken) == 0, "service did not stop cleanly");
        }
    }

    private static async Task ReadPublishedAsync(IServiceProvider services, TenantContext database, CancellationToken cancellationToken)
    {
            var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, cancellationToken)
                ?? throw new InvalidOperationException("promoted database missing");
            await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken);
            var result = await transaction.ExecuteStatementAsync(
                new SqlStatement("SELECT value FROM keyring_rows", [], new Dictionary<string, object?>(), 10, 1024, TimeSpan.FromSeconds(5)), cancellationToken);
            Require(result.Rows is [var row] && row is [long value] && value == 42, "promoted data did not survive");
            await Console.Out.WriteLineAsync("keyring-driver:opened row=42".AsMemory(), cancellationToken);
    }

    private static IEnumerable<IWrappedKeyFiles> ChangeRole(IServiceProvider services, RegionalOwners owners, TerraBaseOptions options)
    {
        owners.Observe(new RegionalOwner(options.Region, options.MachineId, 1));
        owners.Renew(TimeProvider.System.GetTimestamp() + 60 * TimeProvider.System.TimestampFrequency);
        foreach (var source in services.GetServices<IWrappedKeyFiles>())
        {
            yield return source;
        }
    }

    private static async Task RunPassAsync(IServiceProvider services, IEnumerable<IWrappedKeyFiles> files, RegionalOwners owners,
        CancellationToken cancellationToken, bool unknown = false)
    {
        var samples = new Dictionary<string, double>(StringComparer.Ordinal);
        var instruments = new HashSet<Instrument>();
        var collecting = false;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (collecting && instrument.Name.StartsWith("terrabase.keys.", StringComparison.Ordinal))
                {
                    instruments.Add(instrument);
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };
        void Measured(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "status" && Equals(tag.Value, "unreadable"))
                {
                    return;
                }
            }
            if (instruments.Contains(instrument))
            {
                samples[instrument.Name] = value;
            }
        }
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Measured(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Measured(instrument, value, tags));
        listener.Start();
        using var pass = new KeyRewrap(services.GetRequiredService<IKeyring>(), files,
            services.GetRequiredService<CredentialKeyRewrap>(), services.GetRequiredService<KeySnapshotInventory>(),
            services.GetRequiredService<IMeterFactory>(), TimeProvider.System, services.GetRequiredService<ILogger<KeyRewrap>>(),
            services.GetRequiredService<ICatalogGate>(), owners, services.GetRequiredService<KeyRewrapFaults>(),
            services.GetService<KeyCustody>());
        collecting = true;
        await pass.StartAsync(cancellationToken);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            do
            {
                listener.RecordObservableInstruments();
                if (samples.TryGetValue("terrabase.keys.wrapped", out var count)
                    && samples.TryGetValue("terrabase.keys.last_complete", out var complete)
                    && samples.TryGetValue("terrabase.keys.oldest_snapshot_age", out var age)
                    && (unknown ? count == -1 && complete == 0 && age == -1 : complete > 0))
                {
                    return;
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
            throw new InvalidOperationException("maintenance pass did not publish its inventory");
        }
        finally
        {
            await pass.StopAsync(cancellationToken);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => throw new InvalidOperationException("host diagnostics failed", error);
    }
}
