using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// The system database's acknowledged writes survive a power loss of their node: its commits sync the write-ahead log
/// (<c>synchronous = FULL</c>), unlike a tenant database's (decision 0003). Its organization, project, database
/// registration and API key are all still there after every unsynced byte of every log is dropped.
/// </summary>
public sealed class SystemDatabasePowerLossTests
{
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_power_loss_keeps_the_catalog_and_the_api_keys_the_system_database_acknowledged()
    {
        Skip.When(Cluster.FlyRequested, "kills a local node process and edits its files");
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await Node.StartAsync();
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var owner = EntityIds.New().ToString();
        await node.CommitAsync(owner, new JsonObject
        {
            ["id"] = EntityIds.New().ToString(),
            ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "create_organization", ["id"] = organization, ["name"] = $"power-{organization}" },
                new JsonObject { ["op"] = "create_project", ["organization"] = organization, ["id"] = project, ["name"] = "power" },
                new JsonObject { ["op"] = "create_database", ["organization"] = organization, ["project"] = project, ["name"] = "canary", ["id"] = EntityIds.New().ToString() }),
        });
        using var created = await node.Client.PostAsJsonAsync("/admin/api-keys", new JsonObject
        {
            ["name"] = "power loss",
            ["scopes"] = new JsonArray("documents:read"),
            ["organizationId"] = organization,
            ["projectId"] = project,
        });
        await Docs.EnsureSuccessAsync(created);
        var secret = (await created.Content.ReadFromJsonAsync<JsonObject>())!["secret"]!.GetValue<string>();
        var read = new Uri($"/v1/projects/{project}/databases/canary/collections/items/documents/by-key?id={EntityIds.New()}", UriKind.Relative);
        await Assert.That(await node.StatusAsync(read, null)).IsNotEqualTo(HttpStatusCode.Unauthorized).Because("the operator reaches the registered project");
        await Assert.That(await node.StatusAsync(read, secret)).IsNotEqualTo(HttpStatusCode.Unauthorized).Because("the new key authenticates");

        await node.PowerLossAsync();
        await node.StartAgainAsync();

        await Assert.That(await node.StatusAsync(read, null)).IsNotEqualTo(HttpStatusCode.Unauthorized)
            .Because("the project's registration was acknowledged before the power loss");
        await Assert.That(await node.StatusAsync(read, secret)).IsNotEqualTo(HttpStatusCode.Unauthorized)
            .Because("the key was acknowledged before the power loss");
    }

    // shortcut: one node of RegionProcessHost started here with the shim, until RegionProcesses loads it (hookup on #108);
    // then this scenario moves onto RegionProcesses.PowerLossAsync.
    private sealed class Node : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("terrabase-power-").FullName;
        private readonly string _machine = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        private Process? _process;

        private Node()
        {
            Faults = new WalSyncFaults(Data + ".walfaults");
        }

        public HttpClient Client { get; private set; } = new();

        private string Data => Path.Combine(_root, Cluster.Gru);

        private WalSyncFaults Faults { get; }

        public static async Task<Node> StartAsync()
        {
            var node = new Node();
            try
            {
                _ = Directory.CreateDirectory(node.Data);
                _ = Directory.CreateDirectory(Path.Combine(node._root, "members"));
                await node.StartAgainAsync();
                return node;
            }
            catch
            {
                await node.DisposeAsync();
                throw;
            }
        }

        public async Task StartAgainAsync()
        {
            var info = Path.Combine(_root, "gru.info");
            File.Delete(info);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerraBase.E2E.dll"));
            start.ArgumentList.Add("--treenode-filter");
            start.ArgumentList.Add($"/*/*/{nameof(RegionProcessHost)}/*");
            start.ArgumentList.Add("--results-directory");
            start.ArgumentList.Add(Path.Combine(_root, "results"));
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name.StartsWith("FLY_", StringComparison.Ordinal)).ToList())
            {
                _ = start.Environment.Remove(name);
            }

            start.Environment[RegionProcessHost.InfoVariable] = info;
            start.Environment[RegionProcessHost.RegionVariable] = Cluster.Gru;
            start.Environment[RegionProcessHost.MachineVariable] = _machine;
            start.Environment[RegionProcessHost.DataVariable] = Data;
            start.Environment[RegionProcessHost.MembersVariable] = Path.Combine(_root, "members");
            Faults.AddTo(start.Environment);
            _process = Process.Start(start) ?? throw new InvalidOperationException("the node process did not start");
            _process.OutputDataReceived += static (_, _) => { };
            _process.ErrorDataReceived += static (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            var clock = Stopwatch.StartNew();
            while (!File.Exists(info))
            {
                if (_process.HasExited || clock.Elapsed > TimeSpan.FromSeconds(120))
                {
                    throw new InvalidOperationException($"the node did not start (exited: {_process.HasExited})");
                }

                await Task.Delay(100);
            }

            Client.Dispose();
            Client = new HttpClient(new SocketsHttpHandler { UseCookies = false })
            {
                BaseAddress = new Uri(JsonNode.Parse(await File.ReadAllTextAsync(info))!["internal"]!.GetValue<string>()),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(30),
            };
        }

        public async Task CommitAsync(string user, JsonObject commit)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/management/commits", UriKind.Relative))
            {
                Content = JsonContent.Create(commit),
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.Add("X-TerraBase-User-Id", user);
            using var response = await Client.SendAsync(request);
            await Docs.EnsureSuccessAsync(response);
        }

        public async Task<HttpStatusCode> StatusAsync(Uri uri, string? key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            if (key is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using var response = await Client.SendAsync(request);
            return response.StatusCode;
        }

        // SIGKILL, then every write-ahead log back to its length at its last sync.
        public async Task PowerLossAsync()
        {
            _process!.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            Faults.DropUnsynced(Data);
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            _process?.Dispose();
            Client.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
