// Measures what releasing abandoned Turso statements costs (#115): write throughput and latency, concurrent read latency,
// the server's CPU, memory and GC, with 0 % to 50 % of writes failing. --mode system instead measures system database
// under the --wrap command (default: taskset to CPUs 0,1), records the limits that process actually has, and drives the
// load from this process, which should run on other CPUs. See benchmark/bench/2026-10-12-statement-drain.md.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var options = args.Chunk(2).ToDictionary(pair => pair[0].TrimStart('-'), pair => pair[1]);
var binary = options["server"];
var label = options["label"];
var wrap = options.GetValueOrDefault("wrap", "taskset -c 0,1").Split(' ', StringSplitOptions.RemoveEmptyEntries);
var seconds = int.Parse(options.GetValueOrDefault("seconds", "20"), CultureInfo.InvariantCulture);
var writers = int.Parse(options.GetValueOrDefault("writers", "16"), CultureInfo.InvariantCulture);
var readers = int.Parse(options.GetValueOrDefault("readers", "4"), CultureInfo.InvariantCulture);
var mode = options.GetValueOrDefault("mode", "drain");

var peerSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var data = Directory.CreateTempSubdirectory("drain-bench-").FullName;
var (port, internalPort, metricsPort) = (FreePort(), FreePort(), FreePort());
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 64 })
{
    BaseAddress = new Uri($"http://127.0.0.1:{internalPort}/"),
    DefaultRequestVersion = HttpVersion.Version20,
    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
    Timeout = TimeSpan.FromSeconds(60),
};
http.DefaultRequestHeaders.Add("X-TerraBase-Peer-Credential", peerSecret);
using var scrape = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{metricsPort}/") };

// Set once the server serves the database; the local functions below read them.
var (database, pid, allowed) = ("", 0, 0.0);
var server = await StartAsync();
// Whatever happens, the server stops and its data directory goes: a failed run must not leave a server behind.
try
{
    var (owner, organization, project, databaseId, path) = await CreateTenantAsync();
    database = path;
    await SendAsync(HttpMethod.Post, $"{database}/collections", new { name = "items" }, HttpStatusCode.Created);
    await SendAsync(HttpMethod.Put, Document("existing"), new { n = 0 }, HttpStatusCode.Created);
    await SendAsync(HttpMethod.Put, Document("seed-fail"), new { fail = 1 }, HttpStatusCode.Created);
    await StopAsync(server);

    // A write of a document marked "fail" then fails its INSERT on this UNIQUE index: a failed statement, as a stopped read or
    // an aborted transaction fails one. Added while the server is down: one writer per file.
    var file = Path.Combine(data, organization, project, databaseId + ".db");
    using (var connection = new Turso.TursoConnection($"Data Source={file}"))
    {
        connection.Open();
        connection.ExecuteNonQuery("CREATE UNIQUE INDEX bench_fail ON \"items\" (json_extract(doc, '$.fail'));");
        connection.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    server = await StartAsync();
    pid = ServerPid();
    allowed = await LimitsAsync(pid);
    if (mode == "system")
    {
        Console.WriteLine($"# {label}: system database writes, {seconds} s per phase, wrap: {string.Join(' ', wrap)}");
        Console.WriteLine("| variant | write | writers | writes/s | p50 / p95 / p99 ms | CPU % of allowed | unexpected |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var (kind, count) in new[] { ("api key", 1), ("api key", 16), ("organization create", 1), ("organization create", 16), ("tenant put", 1), ("tenant put", 16) })
        {
            await SystemPhaseAsync(kind, count, owner, organization, project, TimeSpan.FromSeconds(3), record: false);
            await SystemPhaseAsync(kind, count, owner, organization, project, TimeSpan.FromSeconds(seconds), record: true);
        }

        return;
    }

    Console.WriteLine($"# {label}: {writers} writers, {readers} readers, {seconds} s per phase, wrap: {string.Join(' ', wrap)}");
    Console.WriteLine("| variant | failing writes | kind | valid writes/s | write p50 / p95 / p99 ms | failing p50 / p99 ms | read p50 / p95 / p99 ms | reads/s | CPU s (user+sys) | % of allowed CPU | peak RSS MB | max GC heap / committed MB | GC/s (gen2/s) | GC pause ms/s | unexpected |");
    Console.WriteLine("| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    foreach (var (rate, kind) in new[] { (0.0, "none"), (0.01, "statement"), (0.10, "statement"), (0.50, "statement"), (0.10, "conflict 412"), (0.50, "conflict 412") })
    {
        await PhaseAsync(rate, kind, TimeSpan.FromSeconds(3), record: false);
        await PhaseAsync(rate, kind, TimeSpan.FromSeconds(seconds), record: true);
    }

}
finally
{
    if (!server.HasExited)
    {
        await StopAsync(server);
    }

    Directory.Delete(data, recursive: true);
}

async Task PhaseAsync(double rate, string kind, TimeSpan duration, bool record)
{
    var (gen2Before, pauseBefore, allBefore) = await GcAsync();
    var cpuBefore = CpuSeconds(pid);
    // Resets the peak RSS (VmHWM) to the current RSS.
    await File.WriteAllTextAsync($"/proc/{pid}/clear_refs", "5");
    var (maxHeap, maxCommitted) = (0.0, 0.0);
    var valid = new List<double>();
    var failing = new List<double>();
    var reads = new List<double>();
    var unexpected = 0;
    using var stop = new CancellationTokenSource(duration);
    var clock = Stopwatch.StartNew();
    var writeTasks = Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
    {
        var random = new Random(writer);
        var (mine, bad, n) = (new List<double>(), new List<double>(), 0);
        while (!stop.IsCancellationRequested)
        {
            var fails = random.NextDouble() < rate;
            // A conflict is If-None-Match: * on a document that exists: TerraBase rejects it itself (412, like a 409 unique
            // violation), no statement fails.
            object body = !fails ? new { n = n++ } : kind == "statement" ? new { fail = 1 } : new { n = 0 };
            var expected = !fails ? HttpStatusCode.Created : kind == "statement" ? HttpStatusCode.InternalServerError : HttpStatusCode.PreconditionFailed;
            var started = Stopwatch.GetTimestamp();
            var status = await StatusAsync(HttpMethod.Put, Document(fails && kind != "statement" ? "existing" : Guid.NewGuid().ToString("N")), body,
                conditional: fails && kind != "statement");
            (fails ? bad : mine).Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (status != expected)
            {
                Interlocked.Increment(ref unexpected);
            }
        }

        lock (valid)
        {
            valid.AddRange(mine);
            failing.AddRange(bad);
        }
    })).ToArray();
    var readTasks = Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
    {
        var mine = new List<double>();
        while (!stop.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            if (await StatusAsync(HttpMethod.Get, Document("existing"), null) != HttpStatusCode.OK)
            {
                Interlocked.Increment(ref unexpected);
            }

            mine.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        lock (reads)
        {
            reads.AddRange(mine);
        }
    })).ToArray();
    var sampler = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            var (heap, committed) = await HeapAsync();
            (maxHeap, maxCommitted) = (Math.Max(maxHeap, heap), Math.Max(maxCommitted, committed));
            await Task.Delay(250);
        }
    });
    await Task.WhenAll(writeTasks.Concat(readTasks).Append(sampler));
    var elapsed = clock.Elapsed.TotalSeconds;
    var cpu = CpuSeconds(pid) - cpuBefore;
    var peakRss = Status(pid, "VmHWM") / 1024.0;
    var (gen2After, pauseAfter, allAfter) = await GcAsync();
    if (!record)
    {
        return;
    }

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"| {label} | {rate:P0} | {kind} | {valid.Count / elapsed:F0} | {P(valid, 50):F1} / {P(valid, 95):F1} / {P(valid, 99):F1} | " +
        $"{(failing.Count == 0 ? "-" : $"{P(failing, 50):F1} / {P(failing, 99):F1}")} | {P(reads, 50):F1} / {P(reads, 95):F1} / {P(reads, 99):F1} | " +
        $"{reads.Count / elapsed:F0} | {cpu:F1} | {cpu / (elapsed * allowed) * 100:F0} % | {peakRss:F0} | {maxHeap / 1048576:F0} / {maxCommitted / 1048576:F0} | " +
        $"{(allAfter - allBefore) / elapsed:F1} ({(gen2After - gen2Before) / elapsed:F2}) | {(pauseAfter - pauseBefore) * 1000 / elapsed:F1} | {unexpected} |"));
}

// organization) commits to the system database; a tenant put to the tenant database, the control.
async Task SystemPhaseAsync(string kind, int count, string owner, string organization, string project, TimeSpan duration, bool record)
{
    var cpuBefore = CpuSeconds(pid);
    var latencies = new List<double>();
    var unexpected = 0;
    using var stop = new CancellationTokenSource(duration);
    var clock = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
    {
        var mine = new List<double>();
        while (!stop.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            var status = kind switch
            {
                "api key" => await StatusAsync(HttpMethod.Post, "/admin/api-keys",
                    new { name = "bench", scopes = new[] { "documents:read" }, organizationId = organization, projectId = project }),
                "organization create" => await StatusAsync(HttpMethod.Post, "/v1/management/organizations",
                    new { name = $"bench-{Ulid()}" }, user: owner),
                _ => await StatusAsync(HttpMethod.Put, Document(Guid.NewGuid().ToString("N")), new { n = 1 }),
            };
            mine.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if ((int)status is < 200 or > 299)
            {
                Interlocked.Increment(ref unexpected);
            }
        }

        lock (latencies)
        {
            latencies.AddRange(mine);
        }
    })));
    var elapsed = clock.Elapsed.TotalSeconds;
    var cpu = CpuSeconds(pid) - cpuBefore;
    if (record)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"| {label} | {kind} | {count} | {latencies.Count / elapsed:F0} | {P(latencies, 50):F1} / {P(latencies, 95):F1} / {P(latencies, 99):F1} | {cpu / (elapsed * allowed) * 100:F0} % | {unexpected} |"));
    }
}

static double P(List<double> values, int percentile)
{
    if (values.Count == 0)
    {
        return double.NaN;
    }

    values.Sort();
    return values[Math.Min(values.Count - 1, (int)Math.Ceiling(percentile / 100.0 * values.Count) - 1)];
}

// The server's own runtime metrics: gen2 collections (forced ones included), all collections and total GC pause time.
async Task<(double Gen2, double PauseSeconds, double All)> GcAsync()
{
    var text = await scrape.GetStringAsync(new Uri("metrics", UriKind.Relative));
    return (Sum(text, @"^dotnet_gc_collections_total\{[^}]*gen2[^}]*\} (\S+)"), Sum(text, @"^dotnet_gc_pause_time_seconds_total(?:\{[^}]*\})? (\S+)"),
        Sum(text, @"^dotnet_gc_collections_total(?:\{[^}]*\})? (\S+)"));
}

// The managed heap after the last collection, and the memory the GC had committed then.
async Task<(double Heap, double Committed)> HeapAsync()
{
    var text = await scrape.GetStringAsync(new Uri("metrics", UriKind.Relative));
    return (Sum(text, @"^dotnet_gc_last_collection_heap_size_bytes(?:\{[^}]*\})? (\S+)"),
        Sum(text, @"^dotnet_gc_last_collection_memory_committed_size_bytes(?:\{[^}]*\})? (\S+)"));
}

static double Sum(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.Multiline)
    .Sum(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));

// The terrabase process itself, not a wrapper (systemd-run, taskset) whose command line holds the same arguments.
int ServerPid()
{
    foreach (var directory in Directory.EnumerateDirectories("/proc").Where(path => int.TryParse(Path.GetFileName(path), out _)))
    {
        try
        {
            var command = File.ReadAllText(Path.Combine(directory, "cmdline")).Replace('\0', ' ');
            if (command.Contains($"--internal-port={internalPort}", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(directory, "comm")).Trim() == "dotnet")
            {
                return int.Parse(Path.GetFileName(directory), CultureInfo.InvariantCulture);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    throw new InvalidOperationException("the server process was not found");
}

// Prints the limits the server process actually has; returns the CPUs it may use (affinity, capped by the CPU quota).
async Task<double> LimitsAsync(int process)
{
    var affinity = Run("taskset", "-cp", process.ToString(CultureInfo.InvariantCulture));
    var cpusAllowed = File.ReadLines($"/proc/{process}/status").First(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)).Split(':')[1].Trim();
    var cgroup = File.ReadAllText($"/proc/{process}/cgroup").Trim().Split(':')[2];
    string Read(string name) => File.Exists($"/sys/fs/cgroup{cgroup}/{name}") ? File.ReadAllText($"/sys/fs/cgroup{cgroup}/{name}").Trim() : "(no such file: controller not enabled here)";
    var (cpuMax, cpuset, memoryMax, swapMax) = (Read("cpu.max"), Read("cpuset.cpus.effective"), Read("memory.max"), Read("memory.swap.max"));
    var runtimeCpus = Sum(await scrape.GetStringAsync(new Uri("metrics", UriKind.Relative)), @"^dotnet_process_cpu_count(?:\{[^}]*\})? (\S+)");
    Console.WriteLine($"# {label} server pid {process}: {affinity}; Cpus_allowed_list {cpusAllowed}; cgroup {cgroup}; cpu.max {cpuMax}; " +
        $"cpuset.cpus.effective {cpuset}; memory.max {memoryMax}; memory.swap.max {swapMax}; runtime CPU count {runtimeCpus}");
    var count = cpusAllowed.Split(',').Sum(range => range.Contains('-', StringComparison.Ordinal)
        ? int.Parse(range.Split('-')[1], CultureInfo.InvariantCulture) - int.Parse(range.Split('-')[0], CultureInfo.InvariantCulture) + 1 : 1);
    return cpuMax.Split(' ') is [var quota, var period] && quota != "max"
        ? Math.Min(count, double.Parse(quota, CultureInfo.InvariantCulture) / double.Parse(period, CultureInfo.InvariantCulture))
        : count;
}

static double CpuSeconds(int process)
{
    // utime and stime, fields 14 and 15, after the parenthesized command name; in clock ticks of 1/100 s.
    var fields = File.ReadAllText($"/proc/{process}/stat").Split(')')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return (long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture)) / 100.0;
}

static long Status(int process, string field) =>
    long.Parse(File.ReadLines($"/proc/{process}/status").First(line => line.StartsWith(field + ":", StringComparison.Ordinal))
        .Split(':')[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture);

static string Run(string file, params string[] arguments)
{
    using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
    var output = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return output;
}

async Task<HttpStatusCode> StatusAsync(HttpMethod method, string path, object? body, bool conditional = false, string? user = null)
{
    using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
    if (user is not null)
    {
        request.Headers.Add("X-TerraBase-User-Id", user);
    }

    if (conditional)
    {
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
    }

    if (body is not null)
    {
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    using var response = await http.SendAsync(request);
    return response.StatusCode;
}

async Task SendAsync(HttpMethod method, string path, object body, HttpStatusCode expected, string? user = null)
{
    using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        Version = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };
    if (user is not null)
    {
        request.Headers.Add("X-TerraBase-User-Id", user);
    }

    using var response = await http.SendAsync(request);
    if (response.StatusCode != expected)
    {
        throw new InvalidOperationException($"{method} {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }
}

string Document(string id) => $"{database}/collections/items/documents/by-key?id={id}";

async Task<(string Owner, string Organization, string Project, string DatabaseId, string Path)> CreateTenantAsync()
{
    var owner = Ulid();
    async Task<string> CreateAsync(string path, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(new { name }),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Add("X-TerraBase-User-Id", owner);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    var org = await CreateAsync("/v1/management/organizations", "drain-bench");
    var root = $"/v1/management/organizations/{org}";
    await SendAsync(HttpMethod.Put, root + "/plan", new { plan = "enterprise" }, HttpStatusCode.OK, owner);
    var proj = await CreateAsync(root + "/projects", "bench");
    var db = await CreateAsync(root + $"/projects/{proj}/databases", "bench");
    return (owner, org, proj, db, $"/v1/projects/{proj}/databases/bench");
}

async Task<Process> StartAsync()
{
    var start = new ProcessStartInfo(wrap[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in wrap.Skip(1).Concat([Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", binary,
        $"--data-directory={data}", $"--port={port}", $"--internal-port={internalPort}", $"--metrics-port={metricsPort}", "--postgres-port=0", "--storage-threads=2"]))
    {
        start.ArgumentList.Add(argument);
    }

    foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TERRABASE_", StringComparison.Ordinal)).ToList())
    {
        start.Environment.Remove(name);
    }

    start.Environment["TERRABASE_PEER_SECRET"] = peerSecret;
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["DOTNET_ENVIRONMENT"] = "Development";
    var process = Process.Start(start)!;
    process.OutputDataReceived += (_, _) => { };
    process.ErrorDataReceived += (_, _) => { };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    for (var attempt = 0; attempt < 600; attempt++)
    {
        try
        {
            using var ready = await http.GetAsync(new Uri("/readyz", UriKind.Relative));
            if (ready.IsSuccessStatusCode)
            {
                return process;
            }
        }
        catch (HttpRequestException)
        {
        }

        await Task.Delay(100);
    }

    throw new TimeoutException("the server did not become ready");
}

async Task StopAsync(Process process)
{
    // SIGTERM to the server itself (not a wrapper): a graceful shutdown checkpoints the files.
    using (var kill = Process.Start("kill", ["-TERM", ServerPid().ToString(CultureInfo.InvariantCulture)]))
    {
        await kill.WaitForExitAsync();
    }

    await process.WaitForExitAsync();
}

static string Ulid()
{
    // A ULID: 48-bit milliseconds then 80 random bits, Crockford base32.
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    var bytes = new byte[16];
    var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    for (var i = 5; i >= 0; i--, ms >>= 8)
    {
        bytes[i] = (byte)ms;
    }

    Random.Shared.NextBytes(bytes.AsSpan(6));
    var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    var chars = new char[26];
    for (var i = 25; i >= 0; i--, value /= 32)
    {
        chars[i] = Alphabet[(int)(value % 32)];
    }

    return new string(chars);
}

static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}
