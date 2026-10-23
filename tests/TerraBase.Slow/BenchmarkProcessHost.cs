using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow;

/// <summary>One benchmark worker, or an authenticated integration smoke when launched by the ordinary Slow suite.</summary>
[Category("benchmark-process-host")]
public sealed class BenchmarkProcessHost
{
    private static readonly string[] Regions = ["gru", "sjc", "fra"];

    [Test]
    [Timeout(6 * 3600 * 1000)]
    public async Task Serves_until_its_control_file_disappears(CancellationToken cancellationToken)
    {
        var configPath = Environment.GetEnvironmentVariable("TERRABASE_BENCH_PROCESS_CONFIG");
        if (string.IsNullOrEmpty(configPath))
        {
            await SmokeAsync(cancellationToken);
            return;
        }

        var config = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken))!.AsObject();
        string Get(string name)
        {
            return config[name]?.GetValue<string>() ?? throw new InvalidOperationException($"missing benchmark config field {name}");
        }
        var root = Get("run_directory");
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException("run_directory must name an existing absolute private directory");
        }

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(root) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new InvalidOperationException("run_directory must have mode 0700");
        }

        var region = Get("region");
        if (region is not ("gru" or "sjc" or "fra"))
        {
            throw new InvalidOperationException("region must be gru, sjc or fra");
        }

        foreach (var name in new[] { "machine", "organization", "project", "database", "user" })
        {
            if (!EntityIds.IsValid(Get(name)))
            {
                throw new InvalidOperationException($"benchmark config field {name} must be a ULID");
            }
        }

        var secret = Environment.GetEnvironmentVariable(PeerTls.TestSecretVariable);
        if (secret is not { Length: 64 } || !secret.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException("TERRABASE_TEST_PEER_SECRET must contain a shared 32-byte hexadecimal test credential");
        }

        var advertised = config["advertised_peer_url"]?.GetValue<string>();
        if (advertised is not null)
        {
            ValidatePeer(advertised);
        }

        var directory = Directory.CreateDirectory(Path.Combine(root, region)).FullName;
        var members = Directory.CreateDirectory(Path.Combine(root, "members")).FullName;
        var control = Path.Combine(directory, "control");
        if (!File.Exists(control))
        {
            throw new InvalidOperationException("create the worker control file before launching");
        }

        var member = Path.Combine(members, Get("machine"));
        var listening = Path.Combine(directory, "listening.json");
        var target = Path.Combine(directory, "target.json");
        // Exclusive worker ownership prevents a duplicate launch from deleting the active worker's discovery files.
        await using var ownership = new FileStream(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.Delete(target);
        File.Delete(listening);
        try
        {
            await using var api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = Get("machine").ToLowerInvariant();
                settings["data-directory"] = Path.Combine(directory, "data");
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(new DirectoryOfMembers(members));
                MeasureDurability(services);
            }, userId: Get("user"), cancellationToken: cancellationToken);
            await WriteAsync(listening, new JsonObject { ["peer_url"] = api.Server.PeerAddress!.ToString() }.ToJsonString(), cancellationToken);
            await WriteAsync(member, $"{region}\n{advertised ?? api.Server.PeerAddress!.ToString()}", cancellationToken);
            await RegisterAsync(api, config, cancellationToken);
            await api.VerifyProjectBindingAsync(Get("organization"), Get("project"));
            var token = api.Issuer.Mint(api.User, descriptor =>
            {
                descriptor.Claims["project_id"] = Get("project");
                descriptor.Claims["scope"] = "databases:read databases:write documents:read documents:write";
                descriptor.Expires = DateTime.UtcNow.AddHours(12);
            });
            using var client = api.WithToken(token);
            await WaitForWritesAsync(client, new Uri($"/v1/projects/{Get("project")}/databases/canary/collections", UriKind.Relative),
                config["readiness_collection"]?.GetValue<string>(), cancellationToken);
            await Assert.That(api.Server.Services.GetRequiredService<IRemoteDurability>().Confirms).IsTrue();
            await WriteAsync(target, new JsonObject
            {
                ["kind"] = "local-process",
                ["region"] = region,
                ["machine"] = Get("machine").ToLowerInvariant(),
                ["organization"] = Get("organization"),
                ["project"] = Get("project"),
                ["database"] = "canary",
                ["database_id"] = Get("database"),
                ["url"] = api.Server.ClientAddress!.ToString(),
                ["token"] = token,
                ["peer_url"] = api.Server.PeerAddress!.ToString(),
                ["metrics_url"] = new Uri(api.Server.MetricsAddress, "/metrics").ToString(),
                ["pid"] = Environment.ProcessId,
                ["data_directory"] = api.Server.DataDirectory,
                ["remote_confirmation"] = true,
            }.ToJsonString(), cancellationToken);
            string? sampled = null;
            var sampleRequest = Path.Combine(directory, "sample.request");
            while (File.Exists(control))
            {
                if (File.Exists(sampleRequest))
                {
                    var id = (await File.ReadAllTextAsync(sampleRequest, cancellationToken)).Trim();
                    if (EntityIds.IsValid(id) && id != sampled)
                    {
                        var snapshot = api.Server.Services.GetRequiredService<BenchmarkLocalAck>().Snapshot(id);
                        await WriteAsync(Path.Combine(directory, "sample.json"), snapshot.ToJsonString(), cancellationToken);
                        sampled = id;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
        finally
        {
            File.Delete(target);
            File.Delete(listening);
            File.Delete(member);
        }
    }

    internal static async Task WaitForWritesAsync(HttpClient client, Uri collections, string? requiredCollection, CancellationToken cancellationToken)
    {
        var collection = requiredCollection ?? "bench-ready";
        var document = new Uri($"{collections.OriginalString}/{collection}/documents/by-key?id={EntityIds.New()}", UriKind.Relative);
        await TerraBaseTestServer.EventuallyAsync(async cancel =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, cancellationToken);
            if (requiredCollection is null)
            {
                using var content = new StringContent(new JsonObject { ["name"] = collection }.ToJsonString(), Encoding.UTF8, "application/json");
                using var created = await client.PostAsync(collections, content, linked.Token);
                if (created.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
                {
                    return false;
                }

                if (created.StatusCode != HttpStatusCode.Conflict)
                {
                    created.EnsureSuccessStatusCode();
                }
            }

            // Keep the same ULID/body if a 503 follows a committed write.
            using var body = new StringContent("{\"ready\":true}", Encoding.UTF8, "application/json");
            using var written = await client.PutAsync(document, body, linked.Token);
            if (written.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
            {
                return false;
            }

            written.EnsureSuccessStatusCode();
            return true;
        }, TimeSpan.FromMinutes(2));
    }

    private static async Task RegisterAsync(ManagementApi api, JsonObject config, CancellationToken cancellationToken)
    {
        string Get(string name)
        {
            return config[name]!.GetValue<string>();
        }
        var (organization, project) = (Get("organization"), Get("project"));
        var replicas = new JsonArray([.. Regions.Where(region => region != Get("region")).Select(region => (JsonNode?)JsonValue.Create(region))]);
        var commit = new JsonObject
        {
            ["id"] = EntityIds.New().ToString(),
            ["ops"] = new JsonArray
            {
                new JsonObject { ["op"] = "create_organization", ["id"] = organization, ["name"] = "benchmark" },
                new JsonObject { ["op"] = "set_plan", ["organization"] = organization, ["plan"] = "enterprise" },
                new JsonObject { ["op"] = "create_project", ["organization"] = organization, ["id"] = project, ["name"] = "benchmark" },
                new JsonObject { ["op"] = "create_database", ["organization"] = organization, ["project"] = project,
                    ["id"] = Get("database"), ["name"] = "canary", ["replica_regions"] = replicas },
            },
        };
        using var service = api.Service(api.User);
        using var content = new StringContent(commit.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.Conflict || (!body.Contains("\"id_taken\"", StringComparison.Ordinal) && !body.Contains("\"name_taken\"", StringComparison.Ordinal)))
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private static async Task SmokeAsync(CancellationToken cancellationToken)
    {
        await using var api = await ManagementApi.StartAsync(settings =>
            settings["node-name"] = EntityIds.New().ToString().ToLowerInvariant(), services: MeasureDurability, cancellationToken: cancellationToken);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await WaitForWritesAsync(data, new Uri($"{database.Data}/collections", UriKind.Relative), null, cancellationToken);
        await ManagementApi.CreateCollectionAsync(data, database, "smoke", cancellationToken);
        var path = new Uri(ManagementApi.DocumentPath(database, "smoke", EntityIds.New().ToString()), UriKind.Relative);
        using var content = new StringContent("{\"value\":1}", Encoding.UTF8, "application/json");
        using var written = await data.PutAsync(path, content, cancellationToken);
        written.EnsureSuccessStatusCode();
        using var read = await data.GetAsync(path, cancellationToken);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var feedDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        feedDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var feed = await data.GetAsync(new Uri($"{database.Data}/changes?cursor=now", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead, feedDeadline.Token);
        feed.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await feed.Content.ReadAsStreamAsync(feedDeadline.Token));
        var cursor = false;
        while (await reader.ReadLineAsync(feedDeadline.Token) is { } line)
        {
            if (line == "event: cursor")
            {
                cursor = true;
                break;
            }
        }

        await Assert.That(cursor).IsTrue();
        using var anonymous = api.DataClient();
        using var denied = await anonymous.GetAsync(path, cancellationToken);
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(api.Server.Services.GetRequiredService<IRemoteDurability>().Confirms).IsFalse();
        using var metrics = new HttpClient();
        var scrape = await metrics.GetStringAsync(new Uri(api.Server.MetricsAddress, "/metrics"), cancellationToken);
        await Assert.That(scrape).Contains("terrabase_benchmark_admission_duration");
    }

    private static void MeasureDurability(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<BenchmarkLocalAck>();
        services.AddSingleton<IStartupFilter, BenchmarkLocalAck.DispatchTimer>();
        var original = services.Single(service => service.ServiceType == typeof(IRemoteDurability));
        services.Remove(original);
        services.AddSingleton<IRemoteDurability>(provider => new TimedDurability(
            (IRemoteDurability)original.ImplementationFactory!(provider), provider.GetRequiredService<IMeterFactory>(),
            provider.GetRequiredService<BenchmarkLocalAck>()));
    }

    private static void ValidatePeer(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip)
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
        {
            throw new InvalidOperationException("advertised_peer_url must be a loopback HTTP origin");
        }
    }

    private static async Task WriteAsync(string path, string text, CancellationToken cancellationToken)
    {
        var partial = Path.Combine(Path.GetDirectoryName(path)!, "." + EntityIds.New().ToString());
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            await using (var stream = new FileStream(partial, options))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(text.AsMemory(), cancellationToken);
            }

            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private sealed class DirectoryOfMembers(string directory) : IMemberDirectory
    {
        public Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken)
        {
            List<ClusterMember> members = [];
            foreach (var file in Directory.EnumerateFiles(directory).Where(file => EntityIds.IsValid(Path.GetFileName(file))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.ReadAllText(file).Split('\n') is [var region, var address] && region is "gru" or "sjc" or "fra")
                    {
                        ValidatePeer(address);
                        members.Add(new ClusterMember(Path.GetFileName(file).ToLowerInvariant(), address, region, string.Empty));
                    }
                }
                catch (FileNotFoundException)
                {
                    // A stopping worker may leave between listing and reading.
                }
            }

            return Task.FromResult<ImmutableArray<ClusterMember>>([.. members.OrderBy(member => member.Name, StringComparer.Ordinal)]);
        }
    }

    private sealed class TimedDurability(IRemoteDurability inner, IMeterFactory meters, BenchmarkLocalAck local) : IRemoteDurability
    {
        private readonly Histogram<double> _admission = meters.Create(Telemetry.MeterName).CreateHistogram<double>("terrabase.benchmark.admission.duration", "s");
        private readonly Histogram<double> _wait = meters.Create(Telemetry.MeterName).CreateHistogram<double>("terrabase.benchmark.remote_wait.duration", "s");

        public bool Confirms => inner.Confirms;
        public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => inner.OtherRegionsAsync(cancellationToken);
        public ValueTask<IReadOnlySet<ReplicaId>> HeldOriginsAsync(TenantContext database, CancellationToken cancellationToken) => inner.HeldOriginsAsync(database, cancellationToken);

        public async ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await inner.AdmitAsync(database, cancellationToken);
            }
            finally
            {
                _admission.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
        }

        public async ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken)
        {
            local.Record();
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await inner.WaitAsync(database, marker, cancellationToken);
            }
            finally
            {
                _wait.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
        }
    }
}
