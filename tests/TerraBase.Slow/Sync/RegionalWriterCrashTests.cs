using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Slow.Sync;

public sealed partial class RegionalWriterCrashTests
{
    private const string HostConfig = "TERRABASE_TEST_REGIONAL_CRASH_HOST";
    private static readonly string[] ReplicaRegions = ["sjc"];

    [Test]
    [Timeout(120_000)]
    public async Task Killing_the_regional_writer_before_commit_preserves_every_acknowledged_write_through_recovery_and_promotion(CancellationToken token)
    {
        if (Environment.GetEnvironmentVariable(HostConfig) is { } config)
        {
            await HostAsync(config, token);
            return;
        }
        await using var nats = await JetStreamServer.StartAsync(token);
        var root = Directory.CreateTempSubdirectory("regional-crash-").FullName;
        var master = Path.Combine(root, "master-key");
        await File.WriteAllTextAsync(master, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(master, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        using var reservation = new TcpListener(IPAddress.Any, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var peers = Path.Combine(root, "peers");
        await File.WriteAllTextAsync(peers, "gru-1 gru 127.0.0.2\ngru-2 gru 127.0.0.3\n", token);
        await using var writer = new Node(root, "gru-1", "127.0.0.2", port, peers, nats);
        await using var follower = new Node(root, "gru-2", "127.0.0.3", port, peers, nats);
        try
        {
            await writer.StartAsync(token);
            await follower.StartAsync(token);
            using var initial = PeerTls.CreateClient(writer.Address);
            await TerraBaseTestServer.EventuallyAsync(async _ =>
            {
                using var status = await initial.GetAsync(ManagementApi.Uri("/v1/rollout/status"), token);
                var body = await status.Content.ReadFromJsonAsync<JsonElement>(token);
                return body.GetProperty("writer").GetBoolean() && body.GetProperty("ready").GetBoolean();
            }, TimeSpan.FromSeconds(10));
            using var management = PeerTls.CreateClient(writer.Address);
            management.DefaultRequestHeaders.Add("X-TerraBase-User-Id", EntityIds.New().ToString());
            var organization = await CreateAsync("/v1/management/organizations", new { name = "Crash drill" });
            var project = await CreateAsync($"/v1/management/organizations/{organization}/projects", new { name = "Project" });
            var database = await CreateAsync($"/v1/management/organizations/{organization}/projects/{project}/databases", new { name = "acked", replicaRegions = ReplicaRegions });
            var sql = $"/v1/projects/{project}/databases/{database}/sql/execute";
            using var data = PeerTls.CreateClient(writer.Address);
            await ExecuteAsync(data, "CREATE TABLE acked(id TEXT PRIMARY KEY)");
            for (var i = 0; i < 10; i++)
            {
                await ExecuteAsync(data, $"INSERT INTO acked VALUES ('ack-{i}')");
            }
            await File.WriteAllTextAsync(writer.Arm, database, token);
            var interrupted = data.PostAsJsonAsync(ManagementApi.Uri(sql), new { statements = new[] { new { sql = "INSERT INTO acked VALUES ('unacknowledged')" } } }, token);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(File.Exists(writer.MidWrite)), TimeSpan.FromSeconds(10));
            await Assert.That(interrupted.IsCompleted).IsFalse();
            await writer.KillAsync();
            await follower.KillAsync();
            try
            {
                using var response = await interrupted;
                await Assert.That(response.IsSuccessStatusCode).IsFalse();
            }
            catch (HttpRequestException)
            {
            }
            File.Delete(writer.Arm);
            await writer.StartAsync(token);
            await follower.StartAsync(token);
            using var recovered = PeerTls.CreateClient(writer.Address);
            await TerraBaseTestServer.EventuallyAsync(async _ =>
            {
                using var status = await recovered.GetAsync(ManagementApi.Uri("/v1/rollout/status"), token);
                var body = await status.Content.ReadFromJsonAsync<JsonElement>(token);
                return body.GetProperty("writer").GetBoolean() && body.GetProperty("ready").GetBoolean();
            }, TimeSpan.FromSeconds(15));
            await ExecuteAsync(recovered, "INSERT INTO acked VALUES ('after-restart')");
            using var standby = PeerTls.CreateClient(follower.Address);
            await TerraBaseTestServer.EventuallyAsync(async _ =>
            {
                using var status = await standby.GetAsync(ManagementApi.Uri("/v1/rollout/status"), token);
                var body = await status.Content.ReadFromJsonAsync<JsonElement>(token);
                return body.GetProperty("ready").GetBoolean() && body.GetProperty("session").GetString() is { Length: > 0 };
            }, TimeSpan.FromSeconds(15));
            using var transfer = await recovered.PostAsync(ManagementApi.Uri("/v1/rollout/drain?target=gru-2"), null, token);
            await Assert.That(transfer.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await transfer.Content.ReadAsStringAsync(token));
            using var survivor = PeerTls.CreateClient(follower.Address);
            await TerraBaseTestServer.EventuallyAsync(async _ =>
            {
                using var status = await survivor.GetAsync(ManagementApi.Uri("/v1/rollout/status"), token);
                var body = await status.Content.ReadFromJsonAsync<JsonElement>(token);
                return body.GetProperty("writer").GetBoolean() && body.GetProperty("ready").GetBoolean();
            }, TimeSpan.FromSeconds(10));
            using var read = await survivor.PostAsJsonAsync(ManagementApi.Uri(sql), new { statements = new[] { new { sql = "SELECT id FROM acked ORDER BY id" } } }, token);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await read.Content.ReadAsStringAsync(token));
            var rows = (await read.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows");
            await Assert.That(rows.GetArrayLength()).IsEqualTo(11);
            for (var i = 0; i < 10; i++)
            {
                await Assert.That(rows[i][0].GetString()).IsEqualTo($"ack-{i}");
            }
            await Assert.That(rows[10][0].GetString()).IsEqualTo("after-restart");
            await ExecuteAsync(survivor, "INSERT INTO acked VALUES ('after-promotion')");

            async Task<string> CreateAsync(string path, object body)
            {
                using var response = await management.PostAsJsonAsync(ManagementApi.Uri(path), body, token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync(token));
                return (await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
            }
            async Task ExecuteAsync(HttpClient client, string statement)
            {
                using var response = await client.PostAsJsonAsync(ManagementApi.Uri(sql), new { statements = new[] { new { sql = statement } } }, token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync(token));
            }
        }
        finally
        {
            await writer.KillAsync();
            await follower.KillAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task HostAsync(string path, CancellationToken token)
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!;
        var directory = config["directory"]!.GetValue<string>();
        var node = config["node"]!.GetValue<string>();
        var hosting = Stopwatch.StartNew();
        var storage = new FakeS3();
        var objects = directory + ".objects";
        if (File.Exists(objects))
        {
            foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, byte[]>>(await File.ReadAllBytesAsync(objects, token))!)
            {
                storage.StoreObject(key, value);
            }
        }
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = "gru";
            settings["data-directory"] = directory;
            storage.Configure(settings);
            settings["master-key"] = File.ReadAllText(Path.Combine(Path.GetDirectoryName(directory)!, "master-key"));
        }, services =>
        {
            storage.Route(services);
            TerraBaseTestServer.CaptureDebugLogs(services, new RecoveryLog(directory + ".recovery-failed"));
            services.AddHttpClient("backup-s3").AddHttpMessageHandler(() => new PersistObjects(storage, objects));
            var options = (TerraBaseOptions)services.Last(descriptor => descriptor.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
            services.Replace(ServiceDescriptor.Singleton(options with
            {
                MachineId = node,
                PeersFile = config["peers"]!.GetValue<string>(),
                InternalPort = config["port"]!.GetValue<int>(),
                NatsUrl = new Uri(config["nats"]!.GetValue<string>()),
                NatsToken = config["token"]!.GetValue<string>(),
            }));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Replace(ServiceDescriptor.Singleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                var arm = directory + ".arm";
                if (step == "writer.commit" && File.Exists(arm) && File.ReadAllText(arm) == database.DatabaseId)
                {
                    using var blocked = new ManualResetEventSlim();
                    using var changed = new FileSystemWatcher(Path.GetDirectoryName(arm)!, Path.GetFileName(arm));
                    changed.Deleted += (_, _) => blocked.Set();
                    changed.EnableRaisingEvents = true;
                    File.WriteAllText(directory + ".mid-write", "native transaction is open; COMMIT has not run");
                    if (!File.Exists(arm))
                    {
                        blocked.Set();
                    }
                    blocked.Wait(token);
                }
            }));
            services.Configure<KestrelServerOptions>(kestrel => kestrel.Listen(IPAddress.Parse(config["address"]!.GetValue<string>()), config["port"]!.GetValue<int>(), listener =>
            {
                listener.Protocols = HttpProtocols.Http2;
                listener.UseTerraBaseListener(TerraBaseListener.Internal);
            }));
        }, lifetimeCancellationToken: token);
        var nativeRows = ExportNativeRowsAsync(api.Server.Services, directory, token);
        await File.WriteAllTextAsync(directory + ".ready.new", $"server_ms={hosting.Elapsed.TotalMilliseconds:F0}", token);
        File.Move(directory + ".ready.new", directory + ".ready", overwrite: true);
        var requestPath = directory + ".snapshot-request";
        using var requests = new FileSystemWatcher(Path.GetDirectoryName(directory)!, Path.GetFileName(requestPath));
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        requests.Created += (_, _) => requested.TrySetResult();
        requests.EnableRaisingEvents = true;
        if (File.Exists(requestPath)) { requested.TrySetResult(); }
        await requested.Task.WaitAsync(token);
        var catalog = api.Server.Services.GetRequiredService<IReplicaCatalog>();
        var revisions = api.Server.Services.GetRequiredService<IDatabaseRevisions>();
        var positions = new List<RegionalPosition>();
        TenantContext? after = null;
        do
        {
            if (await catalog.ListHomedAsync("gru", after, token) is not TenantDatabasePage page)
            {
                throw new IOException("fixture could not export regional database catalog");
            }
            foreach (var database in page.Databases)
            {
                var revision = await revisions.CurrentRevisionAsync(database, token);
                if (revision is { } applied)
                {
                    positions.Add(new RegionalPosition(database.OrganizationId, database.ProjectId!, database.DatabaseId!, "gru", applied.ToString()));
                }
            }
            after = page.Next;
        }
        while (after is not null);
        var snapshot = new RegionalPrepare(node, positions, 0,
            await RegionalCatalog.ExportAsync(api.Server.Services.GetRequiredService<IStorageEngine>(), token));
        await File.WriteAllBytesAsync(directory + ".snapshot.new", JsonSerializer.SerializeToUtf8Bytes(snapshot, ReplicasJsonContext.Default.RegionalPrepare), token);
        File.Move(directory + ".snapshot.new", directory + ".snapshot", overwrite: true);
        await nativeRows;
    }

    private static async Task ExportNativeRowsAsync(IServiceProvider services, string directory, CancellationToken token)
    {
        var path = directory + ".native-request";
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(directory)!, Path.GetFileName(path));
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => requested.TrySetResult();
        watcher.Renamed += (_, _) => requested.TrySetResult();
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path)) { requested.TrySetResult(); }
        await requested.Task.WaitAsync(token);
        var tenant = JsonSerializer.Deserialize<TenantContext>(await File.ReadAllTextAsync(path, token))!;
        var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, token)
            ?? throw new IOException("source primary is absent");
        await using var transaction = await store.BeginTransactionAsync(write: false, token);
        var result = await transaction.ExecuteStatementAsync(new SqlStatement("SELECT id FROM acked ORDER BY id", [],
            new Dictionary<string, object?>(), 100, 8192, TimeSpan.FromSeconds(5)), token);
        await File.WriteAllTextAsync(directory + ".native-rows.new", JsonSerializer.Serialize(result.Rows.Select(row => (string)row[0]!).ToArray()), token);
        File.Move(directory + ".native-rows.new", directory + ".native-rows", overwrite: true);
        await Task.Delay(Timeout.Infinite, token);
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }

    private sealed class PersistObjects(FakeS3 storage, string path) : DelegatingHandler
    {
        private static readonly SemaphoreSlim Saving = new(1, 1);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Saving.WaitAsync(cancellationToken);
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete || request.Method == HttpMethod.Post)
                {
                    var temporary = path + ".new";
                    await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(storage.Objects), cancellationToken);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    File.Move(temporary, path, overwrite: true);
                }
                return response;
            }
            finally
            {
                Saving.Release();
            }
        }
    }

    private sealed class RecoveryLog(string path) : ILoggerProvider, ILogger
    {
        private readonly object _logGate = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.StartsWith("Regional ", StringComparison.Ordinal) || message.StartsWith("A replica could not", StringComparison.Ordinal))
            {
                var diagnostic = path + ".handoff-log";
                lock (_logGate)
                {
                    if (!File.Exists(diagnostic) || new FileInfo(diagnostic).Length < 64 * 1024)
                    {
                        File.AppendAllText(diagnostic, message + "\n");
                    }
                }
            }
            if (message.Contains("Regional handoff recovery retries from its durable phase", StringComparison.Ordinal))
            {
                File.WriteAllText(path, "bounded rollback failed; durable retry remains pending");
            }
        }
        public void Dispose() { }
    }

    private sealed class Node(string root, string name, string address, int port, string peers, JetStreamServer nats, Uri? natsUrl = null) : IAsyncDisposable
    {
        private Process? _process;
        private readonly Queue<string> _errors = new();
        private string DirectoryPath => Path.Combine(root, name);
        internal string Diagnostics => File.Exists(DirectoryPath + ".recovery-failed.handoff-log")
            ? File.ReadAllText(DirectoryPath + ".recovery-failed.handoff-log") : "no handoff diagnostics";
        internal string Arm => DirectoryPath + ".arm";
        internal string MidWrite => DirectoryPath + ".mid-write";
        internal async Task<RegionalPrepare> SnapshotAsync(CancellationToken token)
        {
            await File.WriteAllTextAsync(DirectoryPath + ".snapshot-request", "export", token);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(File.Exists(DirectoryPath + ".snapshot")), TimeSpan.FromSeconds(10));
            return JsonSerializer.Deserialize(await File.ReadAllBytesAsync(DirectoryPath + ".snapshot", token), ReplicasJsonContext.Default.RegionalPrepare)!;
        }
        internal async Task<string[]> NativeRowsAsync(TenantContext tenant, CancellationToken token)
        {
            var request = DirectoryPath + ".native-request";
            var temporary = request + ".new";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(tenant), token);
            File.Move(temporary, request);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(File.Exists(DirectoryPath + ".native-rows")), TimeSpan.FromSeconds(10));
            return JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(DirectoryPath + ".native-rows", token))!;
        }
        internal Uri Address => new($"http://{address}:{port}");
        internal Task RecoveryFailed => TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(File.Exists(DirectoryPath + ".recovery-failed")), TimeSpan.FromSeconds(10));

        internal async Task StartAsync(CancellationToken token)
        {
            File.Delete(DirectoryPath + ".recovery-failed");
            var ready = DirectoryPath + ".ready";
            File.Delete(ready);
            var configPath = DirectoryPath + ".json";
            await File.WriteAllTextAsync(configPath, new JsonObject
            {
                ["directory"] = DirectoryPath,
                ["node"] = name,
                ["address"] = address,
                ["port"] = port,
                ["peers"] = peers,
                ["nats"] = (natsUrl ?? nats.Url).ToString(),
                ["token"] = nats.Token,
            }.ToJsonString(), token);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerraBase.Slow.dll"));
            start.ArgumentList.Add("--treenode-filter");
            start.ArgumentList.Add("/*/*/*RegionalWriterCrashTests/Killing_the_regional_writer_before_commit_preserves_every_acknowledged_write_through_recovery_and_promotion");
            start.ArgumentList.Add("--results-directory");
            start.ArgumentList.Add(DirectoryPath + ".results");
            start.Environment[HostConfig] = configPath;
            start.Environment[PeerTls.TestSecretVariable] = PeerTls.Secret;
            _process?.Dispose();
            _process = Process.Start(start) ?? throw new IOException("regional process did not start");
            _process.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is not { } text)
                {
                    return;
                }
                lock (_errors)
                {
                    _errors.Enqueue(text);
                    while (_errors.Count > 100)
                    {
                        _ = _errors.Dequeue();
                    }
                }
            };
            var launched = Stopwatch.StartNew();
            using var watcher = new FileSystemWatcher(Path.GetDirectoryName(ready)!, Path.GetFileName(ready));
            var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Created += (_, _) => signalled.TrySetResult();
            watcher.Renamed += (_, _) => signalled.TrySetResult();
            watcher.EnableRaisingEvents = true;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            if (File.Exists(ready))
            {
                signalled.TrySetResult();
            }

            await Task.WhenAny(signalled.Task, _process.WaitForExitAsync(token)).WaitAsync(token);
            if (!File.Exists(ready))
            {
                lock (_errors)
                {
                    throw new IOException("regional process exited before it was ready: " + string.Join('\n', _errors));
                }
            }

            Console.WriteLine($"DRILL {name} ready process_ms={launched.Elapsed.TotalMilliseconds:F0} {await File.ReadAllTextAsync(ready, token)}");
        }

        internal async Task KillAsync()
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await KillAsync();
            _process?.Dispose();
        }
    }
}
