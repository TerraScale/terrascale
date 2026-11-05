using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Sync;

public sealed class RegionalWriterCrashTests
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
            try
            {
                using var response = await interrupted;
                await Assert.That(response.IsSuccessStatusCode).IsFalse();
            }
            catch (HttpRequestException)
            {
                // The process was killed with the native write transaction open, before COMMIT.
            }
            File.Delete(writer.Arm);
            await writer.StartAsync(token);
            using var recovered = PeerTls.CreateClient(writer.Address);
            await TerraBaseTestServer.EventuallyAsync(async _ =>
            {
                using var status = await recovered.GetAsync(ManagementApi.Uri("/v1/rollout/status"), token);
                var body = await status.Content.ReadFromJsonAsync<JsonElement>(token);
                return body.GetProperty("writer").GetBoolean() && body.GetProperty("ready").GetBoolean();
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
            await Assert.That(rows.GetArrayLength()).IsEqualTo(10);
            for (var i = 0; i < 10; i++)
            {
                await Assert.That(rows[i][0].GetString()).IsEqualTo($"ack-{i}");
            }
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
                    File.WriteAllText(directory + ".mid-write", "native transaction is open; COMMIT has not run");
                    using var blocked = new ManualResetEventSlim();
                    blocked.Wait(token);
                }
            }));
            services.Configure<KestrelServerOptions>(kestrel => kestrel.Listen(IPAddress.Parse(config["address"]!.GetValue<string>()), config["port"]!.GetValue<int>(), listener =>
            {
                listener.Protocols = HttpProtocols.Http2;
                listener.UseTerraBaseListener(TerraBaseListener.Internal);
            }));
        }, lifetimeCancellationToken: token);
        await File.WriteAllTextAsync(directory + ".ready", "started", token);
        await Task.Delay(Timeout.Infinite, token);
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }

    // Keep the fake external bucket alive across the app's SIGKILL. A successful
    // fake object-store response persists its objects before catalog anchoring returns.
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

    private sealed class Node(string root, string name, string address, int port, string peers, JetStreamServer nats) : IAsyncDisposable
    {
        private Process? _process;
        private readonly Queue<string> _errors = new();
        private string DirectoryPath => Path.Combine(root, name);
        internal string Arm => DirectoryPath + ".arm";
        internal string MidWrite => DirectoryPath + ".mid-write";
        internal Uri Address => new($"http://{address}:{port}");

        internal async Task StartAsync(CancellationToken token)
        {
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
                ["nats"] = nats.Url.ToString(),
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
            start.ArgumentList.Add("/*/*/*RegionalWriterCrashTests/*");
            start.ArgumentList.Add("--results-directory");
            start.ArgumentList.Add(DirectoryPath + ".results");
            start.Environment[HostConfig] = configPath;
            start.Environment[PeerTls.TestSecretVariable] = PeerTls.Secret;
            _process?.Dispose();
            _process = Process.Start(start) ?? throw new IOException("regional process did not start");
            _process.OutputDataReceived += static (_, _) => { };
            _process.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is not { } text)
                {
                    return;
                }
                lock (_errors)
                {
                    _errors.Enqueue(text);
                    while (_errors.Count > 10)
                    {
                        _ = _errors.Dequeue();
                    }
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                if (_process.HasExited)
                {
                    lock (_errors)
                    {
                        throw new IOException("regional process exited: " + string.Join('\n', _errors));
                    }
                }
                return Task.FromResult(File.Exists(ready));
            }, TimeSpan.FromSeconds(30));
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
