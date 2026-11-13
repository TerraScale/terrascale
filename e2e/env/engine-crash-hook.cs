using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Turso.Raw.Public;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

public sealed class StartupHook : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private static readonly List<IStorageTransaction> Held = [];
    private static readonly List<SqliteConnection> Replay = [];
    private static readonly ConcurrentQueue<string> Events = new();
    private static readonly List<IDisposable> Kept = [];
    private static readonly List<Task> Running = [];
    private static TaskCompletionSource RecordingSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Observe(string step, TenantContext database)
    {
        if (step is "storage.fault.recording" or "storage.fault.recorded")
        {
            Events.Enqueue(step["storage.fault.".Length..]);
            if (step == "storage.fault.recording")
            {
                RecordingSeen.TrySetResult();
            }
        }
    }

    private static object Ledger(IStorageEngine engine) => engine.GetType().GetProperty("Ledger", Hidden)!.GetValue(engine)!;

    private static void Arm(IStorageEngine engine)
    {
        Events.Clear();
        RecordingSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var field = engine.GetType().GetField("_checkpoint", Hidden)!;
        field.SetValue(engine, Delegate.CreateDelegate(field.FieldType,
            typeof(StartupHook).GetMethod(nameof(Observe), BindingFlags.Static | BindingFlags.NonPublic)!));
    }

    private static async Task Record(object ledger, TenantContext tenant, Ulid incarnation, string reason)
    {
        await (ValueTask)ledger.GetType().GetMethod("RecordAsync", Hidden)!.Invoke(ledger, [tenant, incarnation, reason])!;
        await Published(ledger);
    }

    private static Task Published(object ledger) =>
        ledger.GetType().GetField("_publications", Hidden)?.GetValue(ledger) is IDictionary publications
            ? Task.WhenAll(publications.Keys.Cast<Task>().ToArray())
            : Task.CompletedTask;

    private static readonly List<Task> Faults = [];
    private static readonly Dictionary<string, SemaphoreSlim> HeldWriters = [];

    private static SemaphoreSlim Writer(object store) => (SemaphoreSlim)store.GetType().GetField("_writer", Hidden)!.GetValue(store)!;

    private static bool Waiting(SemaphoreSlim semaphore) => typeof(SemaphoreSlim).GetField("m_asyncHead", Hidden)!.GetValue(semaphore) is not null;

    private static async Task Until(Func<bool> condition, string what, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException(what);
            }
            await Task.Delay(5, cancellationToken);
        }
    }

    private static async Task<object> NamedStoreAsync(IStorageEngine engine, string name, CancellationToken cancellationToken) =>
        name == "journal"
            ? engine.GetType().GetProperty("IntentStore", Hidden)!.GetValue(engine)!
            : await engine.OpenSystemDatabaseAsync(cancellationToken);

    private static void ReleaseWriter(string name)
    {
        lock (HeldWriters)
        {
            if (HeldWriters.Remove(name, out var held))
            {
                held.Release();
            }
        }
    }

    private static async Task<Ulid> IncarnationAsync(IStorageEngine engine, TenantContext tenant, CancellationToken cancellationToken) =>
        (await engine.OpenDatabaseAsync(tenant, cancellationToken))!.Incarnation;

    private static async Task CreateAndReadAsync(IStorageEngine engine, TenantContext tenant, CancellationToken cancellationToken)
    {
        _ = await engine.CreateDatabaseAsync(tenant, cancellationToken);
        var created = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
        await using var read = await created.BeginTransactionAsync(false, cancellationToken);
        _ = await read.GetOffsetAsync(cancellationToken);
    }

    private static async Task DeleteWithQueuedFaultAsync(IStorageEngine engine, TenantContext tenant, bool busy, CancellationToken cancellationToken)
    {
        var incarnation = await IncarnationAsync(engine, tenant, cancellationToken);
        var ledger = Ledger(engine);
        var writer = Writer(await engine.OpenSystemDatabaseAsync(cancellationToken));
        var directory = Path.Combine(Environment.GetEnvironmentVariable("TERRABASE_DATA_DIRECTORY")!, tenant.OrganizationId, tenant.ProjectId!);
        await writer.WaitAsync(cancellationToken);
        var held = true;
        try
        {
            var deletion = engine.DeleteDatabaseAsync(tenant, cancellationToken).AsTask();
            if (busy)
            {
                try
                {
                    _ = await deletion;
                    throw new InvalidOperationException("the deletion forgot its ledger records while the system writer was held");
                }
                catch (Exception error) when (error.GetType().Name == "StorageBusyException")
                {
                    Console.WriteLine("deletion failed busy while forgetting; marker kept: " + File.Exists(Path.Combine(directory, tenant.DatabaseId + ".db.deleting")));
                }
                writer.Release();
                held = false;
                await Record(ledger, tenant, incarnation, "damage reported after a deletion that failed busy");
                return;
            }
            var gate = (SemaphoreSlim)ledger.GetType().GetField("_gate", Hidden)!.GetValue(ledger)!;
            await Until(() => gate.CurrentCount == 0 && Waiting(writer), "the deletion did not hold the ledger gate while forgetting", cancellationToken);
            var fault = Record(ledger, tenant, incarnation, "damage reported while the deletion forgot its ledger records");
            await Until(() => Waiting(gate), "the fault record did not queue on the ledger gate", cancellationToken);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            writer.Release();
            held = false;
            try
            {
                _ = await deletion;
                throw new InvalidOperationException("the deletion removed its marker from a read-only directory");
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("deletion forgot its ledger records; the marker stays while the queued fault record runs");
            }
            await fault;
        }
        finally
        {
            if (held)
            {
                writer.Release();
            }
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task<IDisposable> HoldBarrierAsync(object ledger, TenantContext tenant, CancellationToken cancellationToken)
    {
        var barrier = ledger.GetType().GetMethod("Mutations", Hidden)!.Invoke(ledger, [tenant])!;
        var entering = (Task)barrier.GetType().GetMethod("EnterAsync", Hidden)!.Invoke(barrier, [cancellationToken])!;
        await entering;
        return (IDisposable)entering.GetType().GetProperty("Result")!.GetValue(entering)!;
    }

    private static async Task<Task> FaultBehindBarrierAsync(IStorageEngine engine, TenantContext tenant, Ulid incarnation, string reason, CancellationToken cancellationToken)
    {
        var ledger = Ledger(engine);
        Arm(engine);
        Kept.Add(await HoldBarrierAsync(ledger, tenant, cancellationToken));
        var fault = Record(ledger, tenant, incarnation, reason);
        await RecordingSeen.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        if (fault.IsCompleted)
        {
            throw new InvalidOperationException("fault publication did not wait for the copy barrier");
        }
        return fault;
    }

    private static async Task DeleteRaceAsync(IStorageEngine engine, TenantContext tenant, CancellationToken cancellationToken)
    {
        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
        var incarnation = store.Incarnation;
        ((IDisposable)store).Dispose();
        ((IDictionary)engine.GetType().GetField("_stores", Hidden)!.GetValue(engine)!).Remove((tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId!));
        var fault = await FaultBehindBarrierAsync(engine, tenant, incarnation, "damage seen before an explicit deletion", cancellationToken);
        try
        {
            if (!await engine.DeleteDatabaseAsync(tenant, cancellationToken))
            {
                throw new InvalidOperationException("the faulting copy was not explicitly deleted");
            }
        }
        finally
        {
            foreach (var lease in Kept)
            {
                lease.Dispose();
            }
            Kept.Clear();
        }
        await fault;
        if (Events.Contains("recorded"))
        {
            throw new InvalidOperationException("a fault row was published for a deleted database");
        }
        _ = await engine.CreateDatabaseAsync(tenant, cancellationToken);
        var recreated = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
        await using var read = await recreated.BeginTransactionAsync(false, cancellationToken);
        _ = await read.GetOffsetAsync(cancellationToken);
        Console.WriteLine("deletion during a waiting fault publication: no fault row; same-key creation healthy");
    }

    private static readonly List<IMemoryReservation> Saturation = [];

    private static void Saturate(IMemoryBudget budget, string project)
    {
        for (var index = 0; index < 4000; index++)
        {
            var tenant = new TenantContext(Ulid.NewUlid().ToString(), PlanTier.Enterprise, project, Ulid.NewUlid().ToString());
            for (var bytes = 1L << 40; bytes >= 1; bytes /= 2)
            {
                while (budget.TryReserve(bytes, tenant) is { } reservation)
                {
                    Saturation.Add(reservation);
                }
            }
            if (budget.TryReserve(1, tenant: null) is not { } spare)
            {
                Console.WriteLine(FormattableString.Invariant($"memory budget saturated by {index + 1} tenants with {Saturation.Count} reservations"));
                return;
            }
            spare.Dispose();
        }
        throw new InvalidOperationException("the memory budget could not be saturated");
    }

    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("ENGINE_INTEGRITY_PROXY") is { } proxy)
        {
            NativeLibrary.SetDllImportResolver(typeof(TursoBindings).Assembly,
                (name, assembly, path) => name == "turso_sdk_kit" ? NativeLibrary.Load(proxy) : IntPtr.Zero);
        }
        DiagnosticListener.AllListeners.Subscribe(new StartupHook());
    }

    private static void FaultBoundary(string step, TenantContext database)
    {
        if (step == "storage.fault.recording")
        {
            Console.WriteLine("fault recording marker flushed");
            _ = kill(Environment.ProcessId, 19);
        }
    }

    [DllImport("libc")]
    private static extern int kill(int process, int signal);

    private static readonly Dictionary<string, IWalTap> Taps = [];

    private static async Task ProbeTapAsync(IStorageEngine engine, TenantContext tenant, string operation, CancellationToken cancellationToken)
    {
        var database = tenant.DatabaseId ?? throw new InvalidOperationException("a tap requires a database id");
        if (operation == "tap-open")
        {
            Taps[database] = (await ((IWalBackup)engine).OpenTapAsync(tenant, cancellationToken))!;
            return;
        }
        var tap = Taps[database];
        using var content = new MemoryStream();
        try
        {
            _ = await tap.ReadAsync(content, cancellationToken);
            throw new InvalidOperationException("a faulted tap returned data");
        }
        catch (Exception error) when (error.GetType().Name == "StorageCopyFaultedException")
        {
            if (content.Length != 0 || !((CancellationToken)tap.GetType().GetProperty("Refused")!.GetValue(tap)!).IsCancellationRequested)
            {
                throw new InvalidOperationException("a faulted tap emitted data or did not cancel shipping");
            }
        }
        try
        {
            try
            {
                tap.GetType().GetMethod("ThrowIfUnavailable")!.Invoke(tap, null);
            }
            catch (TargetInvocationException error) when (error.InnerException?.GetType().Name == "StorageCopyFaultedException")
            {
                throw error.InnerException!;
            }
            throw new InvalidOperationException("a faulted tap admitted a buffered upload");
        }
        catch (Exception error) when (error.GetType().Name == "StorageCopyFaultedException")
        {
            Console.WriteLine("pre-opened tap refused: read bytes 0; buffered shipping refused; shipping token cancelled");
        }
        try
        {
            _ = await tap.SnapshotAsync(content, Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), cancellationToken);
            throw new InvalidOperationException("a faulted tap returned a snapshot");
        }
        catch (Exception error) when (error.GetType().Name == "StorageCopyFaultedException")
        {
            Console.WriteLine("pre-opened tap snapshot refused");
        }
        try
        {
            await (ValueTask)tap.GetType().GetMethod("AcknowledgeAsync")!.Invoke(tap, [0L, cancellationToken])!;
            throw new InvalidOperationException("a faulted tap acknowledged CDC");
        }
        catch (Exception error) when (error.GetType().Name == "StorageCopyFaultedException")
        {
            Console.WriteLine("pre-opened tap acknowledgement refused");
        }
        try
        {
            _ = await ((IWalBackup)engine).OpenTapAsync(tenant, cancellationToken);
            throw new InvalidOperationException("a faulted copy opened another tap");
        }
        catch (Exception error) when (error.GetType().Name == "StorageCopyFaultedException")
        {
            Console.WriteLine("new tap on faulted copy refused");
        }
        await tap.DisposeAsync();
        Taps.Remove(database);
    }

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static async Task FirstOpenAsync(IStorageEngine engine, TenantContext tenant, string code, CancellationToken cancellationToken)
    {
        var stores = (IDictionary)engine.GetType().GetField("_stores", Hidden)!.GetValue(engine)!;
        var key = (tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId!);
        File.WriteAllText(Environment.GetEnvironmentVariable("ENGINE_INTEGRITY_CONTROL")!, "open" + code + " " + tenant.DatabaseId);
        if (stores[key] is IDisposable previous)
        {
            previous.Dispose();
            stores.Remove(key);
        }
        try
        {
            _ = await engine.OpenDatabaseAsync(tenant, cancellationToken);
            throw new InvalidOperationException("the first session did not throw its injected engine error");
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 10 or 11 or 26)
        {
            Console.WriteLine("first session open threw " + error.SqliteErrorCode);
        }
        var ledger = engine.GetType().GetProperty("Ledger", Hidden)!.GetValue(engine)!;
        var path = Path.Combine(Environment.GetEnvironmentVariable("TERRABASE_DATA_DIRECTORY")!, tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId + ".db");
        var candidate = engine.GetType().GetMethod("CreateTenantStore", Hidden)!.Invoke(engine, [key, path])!;
        candidate.GetType().GetProperty("Settled", Hidden)!.SetValue(candidate, true);
        candidate.GetType().GetMethod("Rewritten", Hidden)!.Invoke(candidate, [Ulid.NewUlid()]);
        try
        {
            await (Task)ledger.GetType().GetMethod("RecordIntentAsync", Hidden)!.Invoke(ledger, [candidate, cancellationToken])!;
            throw new InvalidOperationException("a different incarnation overwrote the identity-free fault marker");
        }
        catch (Exception error) when (error.GetType().Name == "StorageCopyFaultedException")
        {
            Console.WriteLine("identity-free intent refused a different incarnation write");
        }
        ((IDisposable)candidate).Dispose();
    }

    private static async Task CheckpointRaceAsync(IStorageEngine engine, TenantContext tenant, bool close, CancellationToken cancellationToken)
    {
        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
        await using (var write = await store.BeginDurableTransactionAsync(cancellationToken))
        {
            _ = await write.CommitAsync(cancellationToken);
        }
        var preparation = store.GetType().GetMethod("PrepareCopyAsync", Hidden)!.Invoke(store, [cancellationToken])!;
        var preparing = (Task)preparation.GetType().GetMethod("AsTask")!.Invoke(preparation, null)!;
        await preparing;
        var operation = (IAsyncDisposable)preparing.GetType().GetProperty("Result")!.GetValue(preparing)!;
        var lane = store.GetType().GetProperty("WriteLane", Hidden)!.GetValue(store)!;
        var enqueue = lane.GetType().GetMethods().Single(method => method.Name == "RunAsync" && method.IsGenericMethod).MakeGenericMethod(typeof(bool));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var blocking = (Task<bool>)enqueue.Invoke(lane, [new Func<bool>(() =>
        {
            entered.SetResult();
            release.Wait(cancellationToken);
            return true;
        }), cancellationToken])!;
        await entered.Task;
        var checkpoint = (Task)store.GetType().GetMethod(close ? "CheckpointOnCloseAsync" : "EvictWriterAsync", Hidden)!.Invoke(store, null)!;
        if ((int)lane.GetType().GetProperty("Queued", Hidden)!.GetValue(lane)! == 0)
        {
            throw new InvalidOperationException("checkpoint was not queued behind the native blocker");
        }
        var path = (string)store.GetType().GetProperty("FilePath", Hidden)!.GetValue(store)!;
        var before = File.ReadAllBytes(path + "-wal");
        try
        {
            var ledger = engine.GetType().GetProperty("Ledger", Hidden)!.GetValue(engine)!;
            await Record(ledger, tenant, store.Incarnation, "the checkpoint race recorded proven damage");
        }
        finally
        {
            release.Set();
        }
        await blocking;
        try
        {
            await checkpoint;
        }
        catch (OperationCanceledException) when (store.Refused.IsCancellationRequested)
        {
            Console.WriteLine("queued checkpoint cancelled before native work");
        }
        operation.GetType().GetMethod("Complete")!.Invoke(operation, null);
        await operation.DisposeAsync();
        if (!before.AsSpan().SequenceEqual(File.ReadAllBytes(path + "-wal")))
        {
            throw new InvalidOperationException("a queued checkpoint changed the faulted WAL");
        }
        Console.WriteLine((close ? "close" : "eviction") + " race: WAL byte-identical after concurrent fault");
    }

    private static async Task LatencyAsync(IStorageEngine engine, TenantContext tenant, CancellationToken cancellationToken)
    {
        var stores = (IDictionary)engine.GetType().GetField("_stores", Hidden)!.GetValue(engine)!;
        var key = (tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId!);
        if (stores[key] is IDisposable previous)
        {
            previous.Dispose();
            stores.Remove(key);
        }
        var started = Stopwatch.GetTimestamp();
        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
        var cold = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        for (var index = 0; index < 1000; index++)
        {
            _ = await engine.OpenDatabaseAsync(tenant, cancellationToken);
        }
        var opens = Stopwatch.GetElapsedTime(started).TotalMilliseconds / 1000;
        started = Stopwatch.GetTimestamp();
        var ledger = engine.GetType().GetProperty("Ledger", Hidden)!.GetValue(engine)!;
        var refused = ledger.GetType().GetMethod("RefusedAsync")!.CreateDelegate<Func<TenantContext, Ulid, CancellationToken, ValueTask<bool>>>(ledger);
        for (var index = 0; index < 1000; index++)
        {
            if (await refused(tenant, store.Incarnation, cancellationToken))
            {
                throw new InvalidOperationException("a healthy repeated open was refused");
            }
        }
        Console.WriteLine(FormattableString.Invariant($"open latency: cold {cold:F4} ms; cached {opens:F4} ms/open; admission {Stopwatch.GetElapsedTime(started).TotalMilliseconds / 1000:F4} ms/open; 1000 repetitions"));
    }

    private static async Task NativeCheckpointRaceAsync(IStorageEngine engine, TenantContext tenant, bool timeout, CancellationToken cancellationToken)
    {
        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
        var preparation = store.GetType().GetMethod("PrepareCopyAsync", Hidden)!.Invoke(store, [cancellationToken])!;
        var preparing = (Task)preparation.GetType().GetMethod("AsTask")!.Invoke(preparation, null)!;
        await preparing;
        var operation = (IAsyncDisposable)preparing.GetType().GetProperty("Result")!.GetValue(preparing)!;
        var control = Environment.GetEnvironmentVariable("ENGINE_CHECKPOINT_CONTROL")!;
        File.Delete(control + ".entered");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(control)!);
        watcher.Created += (_, change) =>
        {
            if (change.FullPath == control + ".entered") entered.TrySetResult();
        };
        watcher.EnableRaisingEvents = true;
        File.WriteAllText(control, "checkpoint");
        var checkpoint = (Task)store.GetType().GetMethod("CheckpointOnCloseAsync", Hidden)!.Invoke(store, null)!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Arm(engine);
        var fault = Record(Ledger(engine), tenant, store.Incarnation, "another reader detected damage during checkpoint backfill");
        await RecordingSeen.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        if (timeout)
        {
            if (fault.IsCompleted)
            {
                throw new InvalidOperationException("fault publication did not wait for the held checkpoint");
            }
            Running.Add(fault);
            Running.Add(checkpoint);
            Console.WriteLine("native checkpoint held with the durable fault intent written");
            return;
        }
        try
        {
            if (await Task.WhenAny(fault, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)) == fault
                || store.GetType().GetProperty("FaultRow", Hidden)!.GetValue(store) is not null)
            {
                throw new InvalidOperationException("fault publication overtook native checkpoint backfill");
            }
            if (!store.Refused.IsCancellationRequested)
            {
                throw new InvalidOperationException("the copy was not refused while its fault publication waited");
            }
            Console.WriteLine("fault publication waits inside native checkpoint backfill");
        }
        finally
        {
            Events.Enqueue("released");
            using var release = new FileStream(control + ".release", FileMode.Open, FileAccess.Write);
            release.WriteByte(1);
        }
        try
        {
            await checkpoint;
        }
        catch (Exception error) when (error.GetType().Name == "StorageCopyFaultedException")
        {
        }
        await fault;
        operation.GetType().GetMethod("Complete")!.Invoke(operation, null);
        await operation.DisposeAsync();
        var path = (string)store.GetType().GetProperty("FilePath", Hidden)!.GetValue(store)!;
        var main = File.ReadAllBytes(path);
        var log = File.ReadAllBytes(path + "-wal");
        await (Task)store.GetType().GetMethod("CheckpointOnCloseAsync", Hidden)!.Invoke(store, null)!;
        if (!main.AsSpan().SequenceEqual(File.ReadAllBytes(path)) || !log.AsSpan().SequenceEqual(File.ReadAllBytes(path + "-wal")))
        {
            throw new InvalidOperationException("a later checkpoint modified the recorded fault");
        }
        if (string.Join(",", Events) != "recording,released,recorded")
        {
            throw new InvalidOperationException("fault publication order: " + string.Join(",", Events));
        }
        Console.WriteLine("native checkpoint finished before fault publication; later checkpoint never started");
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.Extensions.Hosting")
        {
            listener.Subscribe(this);
        }
    }

    public void OnNext(KeyValuePair<string, object?> entry)
    {
        if (entry is { Key: "HostBuilt", Value: IHost host })
        {
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
                _ = ServeAsync(host.Services, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        }
    }

    public void OnError(Exception error) => throw error;

    public void OnCompleted() => Held.Clear();

    private static async Task ServeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var path = Environment.GetEnvironmentVariable("ENGINE_CRASH_SOCKET")!;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            Console.WriteLine("engine crash hook ready");
            while (!cancellationToken.IsCancellationRequested)
            {
                using var socket = await listener.AcceptAsync(cancellationToken);
                using var stream = new NetworkStream(socket);
                using var reader = new StreamReader(stream);
                await using var writer = new StreamWriter(stream) { AutoFlush = true };
                var parts = (await reader.ReadLineAsync(cancellationToken))!.Split(' ');
                try
                {
                    var engine = services.GetRequiredService<IStorageEngine>();
                    var tenant = new TenantContext(parts[1], PlanTier.Hobby, parts[2], parts[3]);
                    if (parts[0] == "latency")
                    {
                        await LatencyAsync(engine, tenant, cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "native-race")
                    {
                        await NativeCheckpointRaceAsync(engine, tenant, parts.Length > 4 && parts[4] == "timeout", cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "hold-fault")
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        Running.Add(await FaultBehindBarrierAsync(engine, tenant, store.Incarnation, "damage seen while a reader session opened", cancellationToken));
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "delete-race")
                    {
                        await DeleteRaceAsync(engine, tenant, cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] is "delete-queued" or "delete-busy")
                    {
                        await DeleteWithQueuedFaultAsync(engine, tenant, parts[0] == "delete-busy", cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "finish-delete")
                    {
                        _ = await engine.DeleteDatabaseAsync(tenant, cancellationToken);
                        await CreateAndReadAsync(engine, tenant, cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "create")
                    {
                        await CreateAndReadAsync(engine, tenant, cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "hold-writer")
                    {
                        var held = Writer(await NamedStoreAsync(engine, parts[3], cancellationToken));
                        await held.WaitAsync(cancellationToken);
                        lock (HeldWriters)
                        {
                            HeldWriters.Add(parts[3], held);
                        }
                        var name = parts[3];
                        _ = cancellationToken.Register(() => ReleaseWriter(name));
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "release-writer")
                    {
                        ReleaseWriter(parts[3]);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] is "fault-async" or "fault-recorded")
                    {
                        var incarnation = await IncarnationAsync(engine, tenant, cancellationToken);
                        Arm(engine);
                        Faults.Add(Record(Ledger(engine), tenant, incarnation, "damage reported while the copy was in use"));
                        if (parts[0] == "fault-recorded")
                        {
                            await RecordingSeen.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                        }
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "published")
                    {
                        await Task.WhenAll(Faults).WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
                        Faults.Clear();
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "stop-retries")
                    {
                        var ledger = Ledger(engine);
                        ((CancellationTokenSource)ledger.GetType().GetField("_retryStopping", Hidden)!.GetValue(ledger)!).Cancel();
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] is "poison-proof" or "unpoison-proof")
                    {
                        var ledger = Ledger(engine);
                        var pending = (IDictionary)ledger.GetType().GetField("_pending", Hidden)!.GetValue(ledger)!;
                        var key = Activator.CreateInstance(ledger.GetType().GetNestedType("Key", BindingFlags.NonPublic)!, [parts[1], parts[2], "unusable-database-id"])!;
                        if (parts[0] == "poison-proof")
                        {
                            var incarnation = Ulid.NewUlid();
                            pending[key] = Activator.CreateInstance(ledger.GetType().GetNestedType("PendingProof", BindingFlags.NonPublic)!,
                                [long.MaxValue, key, incarnation, incarnation, 0, 0L])!;
                        }
                        else
                        {
                            pending.Remove(key);
                        }
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "close-idle")
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var pool = engine.GetType().GetProperty("Pool", Hidden)!.GetValue(engine)!;
                        pool.GetType().GetMethod("CloseIdleOf", Hidden)!.Invoke(pool, [store]);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "delete")
                    {
                        _ = await engine.DeleteDatabaseAsync(tenant, cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "saturate")
                    {
                        Saturate(services.GetRequiredService<IMemoryBudget>(), parts[2]);
                        if (services.GetRequiredService<IMemoryBudget>().TryReserve(4096, tenant: null) is not null)
                        {
                            throw new InvalidOperationException("the ledger's bookkeeping share was still available");
                        }
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "unsaturate")
                    {
                        foreach (var reservation in Saturation)
                        {
                            reservation.Dispose();
                        }
                        Saturation.Clear();
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "refused")
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var ledger = Ledger(engine);
                        var refused = await ledger.GetType().GetMethod("RefusedAsync")!
                            .CreateDelegate<Func<TenantContext, Ulid, CancellationToken, ValueTask<bool>>>(ledger)(tenant, store.Incarnation, cancellationToken);
                        await writer.WriteLineAsync((refused ? "1" : "0").AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "many")
                    {
                        for (var index = 0; index < int.Parse(parts[4], CultureInfo.InvariantCulture); index++)
                        {
                            _ = await engine.CreateDatabaseAsync(new TenantContext(parts[1], PlanTier.Hobby, parts[2], Ulid.NewUlid().ToString()), cancellationToken);
                        }
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "idle-pass")
                    {
                        var maintenance = services.GetServices<IHostedService>().First(service => service.GetType().Name == "CdcMaintenance");
                        var passing = (Task)maintenance.GetType().GetMethod("PassAsync", Hidden)!.Invoke(maintenance, [cancellationToken])!;
                        await passing;
                        var pool = engine.GetType().GetProperty("Pool", Hidden)!.GetValue(engine)!;
                        var summary = passing.GetType().GetProperty("Result")!.GetValue(passing)!.ToString()
                            + FormattableString.Invariant($"; open connections {pool.GetType().GetProperty("OpenCount")!.GetValue(pool)}, managed heap {GC.GetTotalMemory(true) >> 20} MiB");
                        await writer.WriteLineAsync(summary.AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "migration-lease")
                    {
                        var ledger = engine.GetType().GetProperty("Ledger", Hidden)!.GetValue(engine)!;
                        var pending = (Task)ledger.GetType().GetMethod("BeginReadAsync", Hidden)!.Invoke(ledger, [tenant, cancellationToken])!;
                        await pending;
                        await ((IAsyncDisposable)pending.GetType().GetProperty("Result")!.GetValue(pending)!).DisposeAsync();
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "recreate")
                    {
                        if (!await engine.DeleteDatabaseAsync(tenant, cancellationToken))
                        {
                            throw new InvalidOperationException("the faulted copy was not explicitly deleted");
                        }
                        _ = await engine.CreateDatabaseAsync(tenant, cancellationToken);
                        var recreated = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        await using var read = await recreated.BeginTransactionAsync(false, cancellationToken);
                        _ = await read.GetOffsetAsync(cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "first-open")
                    {
                        await FirstOpenAsync(engine, tenant, parts[4], cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] is "eviction-race" or "close-race")
                    {
                        await CheckpointRaceAsync(engine, tenant, parts[0] == "close-race", cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0].StartsWith("tap-", StringComparison.Ordinal))
                    {
                        await ProbeTapAsync(engine, tenant, parts[0], cancellationToken);
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "arm-recording")
                    {
                        var field = engine.GetType().GetField("_checkpoint", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        field.SetValue(engine, Delegate.CreateDelegate(field.FieldType,
                            typeof(StartupHook).GetMethod(nameof(FaultBoundary), BindingFlags.Static | BindingFlags.NonPublic)!));
                        await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "migration-refused")
                    {
                        var file = Path.Combine(Environment.GetEnvironmentVariable("TERRABASE_DATA_DIRECTORY")!, parts[1], parts[2], parts[3] + ".db");
                        try
                        {
                            await (Task<bool>)engine.GetType().GetMethod("MigrateTenantAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(engine, [tenant, file, cancellationToken])!;
                            throw new InvalidOperationException("a refused copy admitted migration");
                        }
                        catch (Exception error) when (error.GetType().Name is "StorageCopyFaultedException" or "StorageProofPendingException")
                        {
                            await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                        }
                        continue;
                    }
                    if (parts[0] == "pending")
                    {
                        var ledger = engine.GetType().GetProperty("Ledger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        var count = ledger.GetType().GetProperty("PendingCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ledger);
                        await writer.WriteLineAsync(count!.ToString().AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] == "ledger")
                    {
                        var system = await engine.OpenSystemDatabaseAsync(cancellationToken);
                        var journal = engine.GetType().GetProperty("IntentStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        var io = engine.GetType().GetProperty("Io", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        var counts = new List<long>();
                        foreach (var (store, table) in new[] { (system, "_tb_fault"), ((IDatabaseStore)journal, "_tb_write_intent") })
                        {
                            var connectionString = await (Task<string>)store.GetType().GetMethod("ConnectionStringAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(store, [io, cancellationToken])!;
                            using var connection = new SqliteConnection(connectionString);
                            connection.Open();
                            using var command = connection.CreateCommand();
                            command.CommandText = "SELECT count(*) FROM " + table + " WHERE database = $database OR $database = '*';";
                            command.Parameters.AddWithValue("$database", parts[3]);
                            counts.Add((long)command.ExecuteScalar()!);
                        }
                        await writer.WriteLineAsync(string.Join(" ", counts).AsMemory(), cancellationToken);
                        continue;
                    }
                    if (parts[0] is "fault" or "unopened-fault")
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        if (parts[0] == "unopened-fault")
                        {
                            ((IDisposable)store).Dispose();
                            var stores = (IDictionary)engine.GetType().GetField("_stores", Hidden)!.GetValue(engine)!;
                            stores.Remove((tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId!));
                        }
                        var ledger = engine.GetType().GetProperty("Ledger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        IAsyncDisposable? read = null;
                        if (ledger.GetType().GetMethod("BeginReadAsync", BindingFlags.Instance | BindingFlags.NonPublic) is { } begin)
                        {
                            var pending = (Task)begin.Invoke(ledger, [tenant, cancellationToken])!;
                            await pending;
                            read = (IAsyncDisposable)pending.GetType().GetProperty("Result")!.GetValue(pending)!;
                        }
                        try
                        {
                            await Record(ledger, tenant, parts[0] == "unopened-fault" ? Ulid.Empty : store.Incarnation, "the crash probe recorded proven damage");
                            if (read is not null && !((CancellationToken)read.GetType().GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .GetValue(read)!).IsCancellationRequested)
                            {
                                throw new InvalidOperationException("a fault did not cancel the direct copy reader");
                            }
                        }
                        finally
                        {
                            if (read is not null)
                            {
                                await read.DisposeAsync();
                            }
                        }
                    }
                    else if (parts[0] is "populate" or "backlog")
                    {
                        _ = await engine.CreateDatabaseAsync(tenant, cancellationToken);
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        await using (var initialized = await store.BeginDurableTransactionAsync(cancellationToken))
                        {
                            _ = await initialized.CommitAsync(cancellationToken);
                        }
                        var io = engine.GetType().GetProperty("Io", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        var connectionString = await (Task<string>)store.GetType()
                            .GetMethod("ConnectionStringAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(store, [io, cancellationToken])!;
                        using var connection = new SqliteConnection(connectionString);
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
                        command.ExecuteNonQuery();
                        if (parts[0] == "backlog")
                        {
                            command.CommandText = """
                                PRAGMA capture_changes = 'off';
                                WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 60000)
                                INSERT INTO turso_cdc(change_type, table_name, id, before, after)
                                SELECT CASE WHEN n % 10 = 0 THEN 2 ELSE 1 END, 'idle', n, NULL, NULL FROM rows;
                                """;
                            command.ExecuteNonQuery();
                        }
                        else
                        {
                            command.CommandText = "CREATE TABLE payload (id INTEGER PRIMARY KEY, body BLOB);";
                            command.ExecuteNonQuery();
                            var count = int.Parse(parts[4], CultureInfo.InvariantCulture);
                            for (var index = 0; index < count; index++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                command.CommandText = "INSERT INTO payload(body) VALUES (randomblob(1048576));";
                                command.ExecuteNonQuery();
                            }
                            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                            command.ExecuteNonQuery();
                            command.CommandText = "UPDATE payload SET body = randomblob(1048576) WHERE id <= 16;";
                            command.ExecuteNonQuery();
                        }
                    }
                    else if (parts[0] == "replay")
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var io = engine.GetType().GetProperty("Io", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        var connectionString = await (Task<string>)store.GetType()
                            .GetMethod("ConnectionStringAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(store, [io, cancellationToken])!;
                        var connection = new SqliteConnection(connectionString);
                        Replay.Add(connection);
                        connection.Open();
                        connection.UseManualWal();
                        using var command = connection.CreateCommand();
                        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; UPDATE payload SET body = randomblob(1048576) WHERE id <= 16;";
                        command.ExecuteNonQuery();
                    }
                    else if (parts[0] == "hold")
                    {
                        _ = await engine.CreateDatabaseAsync(tenant, cancellationToken);
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var held = await store.BeginDurableTransactionAsync(cancellationToken);
                        Held.Add(held);
                    }
                    else if (parts[0] == "check")
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        await using var read = await store.BeginTransactionAsync(false, cancellationToken);
                        if (await read.GetOffsetAsync(cancellationToken) < 0)
                        {
                            throw new InvalidDataException("the recovered offset is invalid");
                        }
                    }
                    else
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var ledger = engine.GetType().GetProperty("Ledger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        await (Task)ledger.GetType().GetMethod("RecordIntentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(ledger, [store, cancellationToken])!;
                        if (parts[0] == "rewrite")
                        {
                            var journal = engine.GetType().GetProperty("IntentStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                            var io = engine.GetType().GetProperty("Io", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                            var connectionString = await (Task<string>)journal.GetType()
                                .GetMethod("ConnectionStringAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(journal, [io, cancellationToken])!;
                            var incarnation = store.GetType().GetProperty(nameof(IDatabaseStore.Incarnation))!.GetValue(store)!.ToString();
                            store.GetType().GetProperty("RewriteBeforeWriter", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .SetValue(store, (Action)(() =>
                                {
                                    using var connection = new SqliteConnection(connectionString);
                                    connection.Open();
                                    using var command = connection.CreateCommand();
                                    command.CommandText = """
                                        PRAGMA synchronous = FULL;
                                        INSERT INTO _tb_write_intent (organization, project, database, incarnation, run)
                                        VALUES ($organization, $project, $database, $incarnation, 1)
                                        ON CONFLICT (organization, project, database) DO UPDATE SET incarnation = excluded.incarnation, run = excluded.run;
                                        """;
                                    command.Parameters.AddWithValue("$organization", parts[1]);
                                    command.Parameters.AddWithValue("$project", parts[2]);
                                    command.Parameters.AddWithValue("$database", parts[3]);
                                    command.Parameters.AddWithValue("$incarnation", incarnation);
                                    command.ExecuteNonQuery();
                                }));
                            var rewritten = engine.GetType().GetMethod("RewriteAsync")!.Invoke(engine, [tenant, 0L, cancellationToken])!;
                            await (Task)rewritten.GetType().GetMethod("AsTask")!.Invoke(rewritten, null)!;
                        }
                    }
                    await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                }
                catch (Exception error)
                {
                    await writer.WriteLineAsync(("error " + error).AsMemory(), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("engine crash hook stopped");
        }
        catch (Exception error)
        {
            Console.WriteLine("engine crash hook failed: " + error);
        }
    }
}
