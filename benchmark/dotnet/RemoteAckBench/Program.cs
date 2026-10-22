using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

var label = args.Length == 0 ? "candidate" : args[0];
var incarnation = Ulid.Parse("00000000000000000000000001");
var otherIncarnation = Ulid.Parse("00000000000000000000000002");
await CheckBoundsAsync();
Console.WriteLine($"# RemoteAckWaiter {label}; host={Environment.MachineName}; framework={Environment.Version}");
Console.WriteLine("Delay is simulated scheduling delay before ACK delivery, not actual network latency.");
Console.WriteLine("CPU and allocations cover registration, ACK delivery, continuations and release; divided by ACK calls.");
Console.WriteLine("| delay | workload | repeat | max pending | ACK calls | p50 ms | p99 ms | CPU us/ACK | bytes/ACK |");
Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
// Warm the async machinery and both acknowledgement shapes outside the reported repetitions.
await RunAsync(1, false, false, 0, 0, report: false);
await RunAsync(1, true, true, 0, 0, report: false);
foreach (var delay in new[] { 0, 100 })
{
    foreach (var (databases, batches, noise) in new[] { (1, false, false), (1, true, false), (1, false, true), (16, false, true) })
    {
        for (var repeat = 1; repeat <= 5; repeat++)
        {
            await RunAsync(databases, batches, noise, delay, repeat, report: true);
        }
    }
}

async Task RunAsync(int databases, bool batches, bool noise, int delay, int repeat, bool report)
{
    var waiter = new RemoteAckWaiter(TimeProvider.System);
    var tenants = Enumerable.Range(0, databases).Select(Tenant).ToArray();
    var leases = new List<RemoteLease>();
    var tasks = new List<Task<double>>();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var process = Process.GetCurrentProcess();
    var allocated = GC.GetTotalAllocatedBytes(precise: true);
    var cpu = process.TotalProcessorTime;
    var ackCalls = 0;
    try
    {
        foreach (var tenant in tenants)
        {
            for (var position = 1; position <= RemoteAckWaiter.MaxPerDatabase; position++)
            {
                leases.Add(Admit(waiter, tenant));
                tasks.Add(ObserveAsync(waiter, tenant, position, deadline.Token));
            }
        }

        if (delay != 0)
        {
            await Task.Delay(delay, deadline.Token);
        }

        var width = batches ? 16 : 1;
        for (var position = 1; position <= RemoteAckWaiter.MaxPerDatabase; position += width)
        {
            for (var database = 0; database < tenants.Length; database++)
            {
                var tenant = tenants[database];
                if (noise)
                {
                    waiter.Acked(tenant, otherIncarnation, 10000, 10001);
                    ackCalls++;
                    await Task.Yield();
                }

                var end = position + width - 1;
                waiter.Acked(tenant, incarnation, position - 1, end);
                ackCalls++;
                // Wait for this ACK's observer continuations before delivering the next one.
                for (var confirmed = position; confirmed <= end; confirmed++)
                {
                    _ = await tasks[(database * RemoteAckWaiter.MaxPerDatabase) + confirmed - 1];
                }

                if (noise)
                {
                    waiter.Acked(tenant, incarnation, position - 1, end);
                    ackCalls++;
                    await Task.Yield();
                }
            }
        }

        var latencies = await Task.WhenAll(tasks);
        foreach (var lease in leases)
        {
            lease.Dispose();
        }

        var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var cpuMicroseconds = (process.TotalProcessorTime - cpu).TotalMicroseconds;
        Array.Sort(latencies);
        if (report)
        {
            var profile = delay == 0 ? "in-process/no injected delay" : "in-process/initial 100 ms delay";
            var workload = $"{databases} DB; {(batches ? "batch16" : "individual")}; noise={noise}";
            Console.WriteLine(FormattableString.Invariant($"| {profile} | {workload} | {repeat} | {tasks.Count} | {ackCalls} | {Percentile(latencies, 0.50):F3} | {Percentile(latencies, 0.99):F3} | {cpuMicroseconds / ackCalls:F3} | {(double)bytes / ackCalls:F1} |"));
        }
    }
    finally
    {
        await deadline.CancelAsync();
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }
}

async Task<double> ObserveAsync(RemoteAckWaiter waiter, TenantContext tenant, int position, CancellationToken token)
{
    var start = Stopwatch.GetTimestamp();
    return await waiter.WaitAsync(tenant, new CommitMarker(incarnation, position), static () => true, token)
        ? Stopwatch.GetElapsedTime(start).TotalMilliseconds
        : throw new InvalidOperationException("Eligible ACK did not confirm its waiter.");
}

async Task CheckBoundsAsync()
{
    var waiter = new RemoteAckWaiter(TimeProvider.System);
    var leases = new ConcurrentBag<RemoteLease>();
    try
    {
        Parallel.For(0, 10000, attempt =>
        {
            var result = waiter.TryAdmit(Tenant(attempt / RemoteAckWaiter.MaxPerDatabase));
            if (result is RemoteLease lease)
            {
                leases.Add(lease);
            }
        });

        if (leases.Count != RemoteAckWaiter.MaxPerMachine)
        {
            throw new InvalidOperationException("10,000 admissions did not enforce the 4,096 machine bound.");
        }

        if (waiter.TryAdmit(Tenant(0)) is RemoteLease unexpected)
        {
            unexpected.Dispose();
            throw new InvalidOperationException("Full admission admitted another waiter.");
        }
    }
    finally
    {
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }

    using var cancellation = new CancellationTokenSource();
    using (var lease = Admit(waiter, Tenant(0)))
    {
        var pending = waiter.WaitAsync(Tenant(0), new CommitMarker(incarnation, 1), static () => true, cancellation.Token);
        await cancellation.CancelAsync();
        try
        {
            _ = await pending;
            throw new InvalidOperationException("Cancellation did not stop the waiter.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    // Fill the same database again: cancellation plus lease disposal must release every slot.
    leases.Clear();
    try
    {
        for (var i = 0; i < RemoteAckWaiter.MaxPerDatabase; i++)
        {
            leases.Add(Admit(waiter, Tenant(0)));
        }

        if (waiter.TryAdmit(Tenant(0)) is RemoteLease excess)
        {
            excess.Dispose();
            throw new InvalidOperationException("Full database admitted another waiter below the machine bound.");
        }
    }
    finally
    {
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }

    Console.WriteLine("Self-check PASS: 10,000 parallel attempts admitted exactly 4,096; cancellation plus lease disposal allowed refilling 256 database slots; the database bound rejected slot 257.");
}

static RemoteLease Admit(RemoteAckWaiter waiter, TenantContext tenant)
{
    return waiter.TryAdmit(tenant) is RemoteLease lease
        ? lease : throw new InvalidOperationException("Expected a free admission slot.");
}

static TenantContext Tenant(int index)
{
    return new("00000000000000000000000001", PlanTier.Hobby,
        "00000000000000000000000002", (index + 10).ToString("D26", CultureInfo.InvariantCulture));
}

static double Percentile(double[] values, double fraction)
{
    return values[(int)Math.Ceiling(values.Length * fraction) - 1];
}
