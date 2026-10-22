using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Documents;
using TerraBase.Tenancy;

internal static class FeedReadersBenchmark
{
    public static async Task RunAsync()
    {
        const int changes = 200, warmup = 10;
        var root = Directory.CreateTempSubdirectory("feed-readers-").FullName;
        Console.WriteLine($"# Feed readers: {DateTimeOffset.UtcNow:O}\n");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; host: {Environment.MachineName}; CPUs: {Environment.ProcessorCount}; server GC: {System.Runtime.GCSettings.IsServerGC}.");
        Console.WriteLine($"Build: {typeof(IDatabaseChanges).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}; benchmark: {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}.");
        Console.WriteLine("Shared-process CPU/allocation includes real engine, producer, SSE JSON consumer and synchronization; excludes admission, setup, warmup and cancellation. Direct service: no HTTP/auth/replication/network. Latency starts before each write and ends when each sink validates its change.\n");
        Console.WriteLine("| requested | admitted | rejected | repeat | delivered | all-path SELECTs | all-path SELECTs/write | feed SELECTs | feed SELECTs/delivery | CPU ms | CPU us/delivery | bytes/delivery | p50 ms | p99 ms |");
        Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        try
        {
            foreach (var requested in new[] { 1, 10, 100 })
            {
                for (var repeat = 1; repeat <= 3; repeat++)
                {
                    await RunAsync(requested, repeat).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        async Task RunAsync(int requested, int repeat)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = deadline.Token;
            using var lifetime = new Lifetime();
            var options = new TerraBaseOptions { DataDirectory = Path.Combine(root, Ulid.NewUlid().ToString()), MemoryBudget = 128 * 1024 * 1024, StorageThreads = 2, StorageQueue = 1024 };
            await using var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddSingleton<IHostApplicationLifetime>(lifetime)
                .AddTerraBaseEngine(options).AddTerraBaseDocuments().BuildServiceProvider();
            var engine = services.GetRequiredService<IStorageEngine>();
            var tenant = new TenantContext(Ulid.NewUlid().ToString(), PlanTier.Hobby, Ulid.NewUlid().ToString(), Ulid.NewUlid().ToString());
            if (!await engine.CreateDatabaseAsync(tenant, token).ConfigureAwait(false)) { throw new InvalidOperationException("Database creation failed."); }
            var store = await engine.OpenDatabaseAsync(tenant, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Database missing.");
            await using (var setup = await store.BeginTransactionAsync(write: true, token).ConfigureAwait(false))
            {
                _ = await setup.CreateCollectionAsync(new CollectionInfo("items", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ulid.NewUlid().ToString()), token).ConfigureAwait(false);
                _ = await setup.CommitAsync(token).ConfigureAwait(false);
            }

            long reads = 0, allReads = 0;
            using var meter = new MeterListener();
            meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "TerraScale.TerraBase" && instrument.Name == "terrabase.storage.reads") { listener.EnableMeasurementEvents(instrument); }
            };
            meter.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                _ = Interlocked.Add(ref allReads, value);
                foreach (var tag in tags)
                {
                    if (tag.Key == "path" && tag.Value is "feed") { _ = Interlocked.Add(ref reads, value); }
                }
            });
            meter.Start();
            var feed = services.GetRequiredService<IDatabaseChanges>();
            var clock = new Clock();
            var id = Ulid.NewUlid().ToString();
            var payloads = Enumerable.Range(0, warmup + changes).Select(n => Encoding.UTF8.GetBytes(FormattableString.Invariant($"{{\"n\":{n}}}"))).ToArray();
            var sinks = new List<Sink>();
            var tasks = new List<Task<IResult>>();
            var readers = new List<CancellationTokenSource>();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                for (var i = 0; i < requested; i++)
                {
                    var sink = new Sink(id, clock, warmup + changes);
                    var context = new DefaultHttpContext { RequestServices = services };
                    context.Response.Body = sink;
                    var reader = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    var task = feed.StreamAsync(context, tenant, null, head: true, new HashSet<string>(StringComparer.Ordinal) { "items" }, reader.Token).AsTask();
                    if (await Task.WhenAny(sink.Ready.Task, task).WaitAsync(token).ConfigureAwait(false) == task)
                    {
                        context.Response.Body = new MemoryStream();
                        await using var body = context.Response.Body;
                        await (await task.ConfigureAwait(false)).ExecuteAsync(context).ConfigureAwait(false);
                        if (context.Response.StatusCode != 429) { throw new InvalidOperationException($"Unexpected admission status {context.Response.StatusCode}."); }
                        reader.Dispose();
                        sink.Dispose();
                        continue;
                    }
                    sinks.Add(sink);
                    tasks.Add(task);
                    readers.Add(reader);
                }
                if (sinks.Count == 0) { throw new InvalidOperationException("No stream admitted."); }
                var waits = new Task[sinks.Count];
                async Task AppendAsync(int n)
                {
                    Volatile.Write(ref clock.Start, Stopwatch.GetTimestamp());
                    await using (var write = await store.BeginTransactionAsync(write: true, token).ConfigureAwait(false))
                    {
                        _ = await write.PutAsync("items", id, payloads[n], token).ConfigureAwait(false);
                        _ = await write.CommitAsync(token).ConfigureAwait(false);
                    }
                    for (var i = 0; i < sinks.Count; i++) { waits[i] = sinks[i].Next.WaitAsync(token); }
                    await Task.WhenAll(waits).ConfigureAwait(false);
                }
                for (var n = 0; n < warmup; n++) { await AppendAsync(n).ConfigureAwait(false); }
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var before = Interlocked.Read(ref reads);
                var allBefore = Interlocked.Read(ref allReads);
                for (var n = warmup; n < warmup + changes; n++) { await AppendAsync(n).ConfigureAwait(false); }
                var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                process.Refresh();
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var selects = Interlocked.Read(ref reads) - before;
                var allSelects = Interlocked.Read(ref allReads) - allBefore;
                var delivered = changes * sinks.Count;
                var latencies = sinks.SelectMany(s => s.Latencies.Skip(warmup)).Order().ToArray();
                if (selects == 0 || sinks.Any(s => s.Count != warmup + changes)) { throw new InvalidOperationException("Missing feed reads or deliveries."); }
                await readers[0].CancelAsync().ConfigureAwait(false);
                await tasks[0].WaitAsync(token).ConfigureAwait(false);
                if (tasks.Skip(1).Any(t => t.IsCompleted)) { throw new InvalidOperationException("One reader's cancellation stopped another reader."); }
                await stop.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(tasks).WaitAsync(token).ConfigureAwait(false);
                if (sinks.Any(s => s.Count != warmup + changes)) { throw new InvalidOperationException("Unexpected delivery during cancellation."); }
                Console.WriteLine(FormattableString.Invariant($"| {requested} | {sinks.Count} | {requested - sinks.Count} | {repeat} | {delivered} | {allSelects} | {(double)allSelects / changes:F3} | {selects} | {(double)selects / delivered:F3} | {cpuMs:F3} | {cpuMs * 1000 / delivered:F3} | {(double)bytes / delivered:F1} | {latencies[(int)Math.Ceiling(latencies.Length * .50) - 1]:F3} | {latencies[(int)Math.Ceiling(latencies.Length * .99) - 1]:F3} |"));
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                foreach (var reader in readers) { reader.Dispose(); }
                foreach (var sink in sinks) { sink.Dispose(); }
            }
        }
    }
}

internal sealed class Clock { internal long Start; }

internal sealed class Sink(string id, Clock clock, int capacity) : Stream
{
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal SemaphoreSlim Next { get; } = new(0, 1);
    internal double[] Latencies { get; } = new double[capacity];
    internal int Count { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) { return Task.CompletedTask; }
    public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = Encoding.UTF8.GetString(buffer.Span);
        if (!text.EndsWith("\n\n", StringComparison.Ordinal)) { throw new InvalidOperationException("Incomplete SSE frame."); }
        foreach (var frame in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (frame.StartsWith("event: cursor\n", StringComparison.Ordinal)) { Ready.TrySetResult(); continue; }
            if (!frame.StartsWith("event: change\n", StringComparison.Ordinal)) { throw new InvalidOperationException("Unexpected SSE event."); }
            var data = frame.IndexOf("\ndata: ", StringComparison.Ordinal);
            if (data < 0) { throw new InvalidOperationException("Missing SSE data."); }
            using var json = JsonDocument.Parse(frame[(data + 7)..]);
            var change = json.RootElement;
            if (Count == capacity || change.GetProperty("id").GetString() != id || change.GetProperty("collection").GetString() != "items"
                || change.GetProperty("op").GetString() != "upsert" || change.GetProperty("document").GetProperty("n").GetInt32() != Count)
            {
                throw new InvalidOperationException("Missing, duplicate, out-of-order or incorrect change.");
            }
            Latencies[Count++] = Stopwatch.GetElapsedTime(Volatile.Read(ref clock.Start)).TotalMilliseconds;
            Next.Release();
        }
        return ValueTask.CompletedTask;
    }
    protected override void Dispose(bool disposing) { if (disposing) { Next.Dispose(); } base.Dispose(disposing); }
}

internal sealed class Lifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stop.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { _stop.Cancel(); }
    public void Dispose() { _stop.Dispose(); }
}
