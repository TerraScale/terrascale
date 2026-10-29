using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
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

        var extraDatabases = config["extra_databases"]?.AsArray() ?? [];
        var databaseIds = DatabaseIds(config);

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
        File.Delete(Path.Combine(directory, "bootstrap.json"));
        ManagementApi? api = null;
        Task? failedStartCleanup = null;
        var failures = new List<Exception>();
        try
        {
            api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = Get("machine").ToLowerInvariant();
                settings["data-directory"] = Path.Combine(directory, "data");
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(new DirectoryOfMembers(members));
                MeasureDurability(services, directory);
            }, userId: Get("user"), cancellationToken: cancellationToken, failedStartCleanup: task => failedStartCleanup = task);
            await WriteAsync(listening, new JsonObject { ["peer_url"] = api.Server.PeerAddress!.ToString() }.ToJsonString(), cancellationToken);
            await WriteAsync(member, $"{region}\n{advertised ?? api.Server.PeerAddress!.ToString()}", cancellationToken);
            await RegisterAsync(api, config, cancellationToken);
            await WaitForProjectAsync(api, Get("organization"), Get("project"), cancellationToken);
            var token = api.Issuer.Mint(api.User, descriptor =>
            {
                descriptor.Claims["project_id"] = Get("project");
                descriptor.Claims["scope"] = "databases:read databases:write documents:read documents:write";
                descriptor.Expires = DateTime.UtcNow.AddHours(12);
            });
            using var client = api.WithToken(token);
            await WaitForWritesAsync(client, new Uri($"/v1/projects/{Get("project")}/databases/canary/collections", UriKind.Relative),
                config["readiness_collection"]?.GetValue<string>(), cancellationToken);
            await Parallel.ForEachAsync(Enumerable.Range(0, extraDatabases.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    await WaitForWritesAsync(client, new Uri($"/v1/projects/{Get("project")}/databases/idle-{index + 1}/collections", UriKind.Relative),
                        null, token);
                });
            await WaitForBootstrapAsync(api, root, directory, databaseIds, cancellationToken);
            await Assert.That(api.Server.Services.GetRequiredService<IRemoteDurability>().Confirms).IsTrue();
            var timing = api.Server.Services.GetRequiredService<CommitTiming>();
            timing.Attach((TursoDatabaseStore?)await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(
                new TenantContext(Get("organization"), PlanTier.Hobby, Get("project"), Get("database")), cancellationToken)
                ?? throw new InvalidOperationException("benchmark database not open"));
            timing.Origin = api.Server.Services.GetRequiredService<LocalReplica>().Id.ToString();
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
            var quiesceRequest = Path.Combine(directory, "quiesce.request");
            var generation = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            var quiesced = false;
            while (File.Exists(control))
            {
                if (!quiesced && File.Exists(quiesceRequest)
                    && await File.ReadAllTextAsync(quiesceRequest, cancellationToken) == generation)
                {
                    await QuiesceAsync(api.Server.Services);
                    await WriteAsync(Path.Combine(directory, "quiesced"), generation, cancellationToken);
                    quiesced = true;
                }

                if (File.Exists(sampleRequest))
                {
                    var id = (await File.ReadAllTextAsync(sampleRequest, cancellationToken)).Trim();
                    if (EntityIds.IsValid(id) && id != sampled)
                    {
                        var snapshot = api.Server.Services.GetRequiredService<BenchmarkLocalAck>().Snapshot(id);
                        snapshot["commit_timing"] = timing.Snapshot();
                        await WriteAsync(Path.Combine(directory, "sample.json"), snapshot.ToJsonString(), cancellationToken);
                        sampled = id;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        finally
        {
            if (api is not null)
            {
                try
                {
                    await api.DisposeAsync();
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }

            // A bounded harness disposal can return before its owner; retain the full completion and its faults.
            var cleanup = api?.CleanupCompletion ?? failedStartCleanup;
            if (cleanup is not null)
            {
                try
                {
                    await cleanup;
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }

            Console.WriteLine($"benchmark worker lifecycle utc={DateTime.UtcNow:O} boundary=api-disposed-cleanup");
            foreach (var file in new[] { target, listening, member, Path.Combine(directory, "bootstrap.json") })
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(failures).Flatten();
        }
    }

    internal static async Task QuiesceAsync(IServiceProvider services)
    {
        // Manual cancellation precedes ApplicationStopping; keep the host from closing listeners for canceled tasks.
        // The execution join below and ApplicationRunCompletion still report every fault.
        services.GetRequiredService<IOptions<HostOptions>>().Value.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        // GenericWebHostService is not a BackgroundService: its peer listener must keep serving other workers.
        var background = services.GetServices<IHostedService>().OfType<BackgroundService>().ToArray();
        var failures = new List<Exception>();
        var stopping = Task.WhenAll(background.Select(async service => await service.StopAsync(CancellationToken.None)));
        try
        {
            await stopping;
        }
        catch (Exception error)
        {
            if (stopping.Exception is { } faults)
            {
                failures.AddRange(faults.InnerExceptions);
            }
            else
            {
                failures.Add(error);
            }
        }

        var joined = Task.WhenAll(background.Select(service => service.ExecuteTask).OfType<Task>());
        try
        {
            await joined;
        }
        catch (OperationCanceledException) when (joined.IsCanceled)
        {
            // Match ApplicationRunCompletion: only a genuinely canceled aggregate is a clean worker exit.
        }
        catch (Exception) when (joined.IsFaulted)
        {
            failures.AddRange(joined.Exception!.InnerExceptions);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(failures).Flatten();
        }
    }

    internal static async Task WaitForProjectAsync(ManagementApi api, string organization, string project, CancellationToken cancellationToken)
    {
        using var client = api.As(api.User);
        await TerraBaseTestServer.EventuallyAsync(async cancel =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, cancellationToken);
            using var response = await client.GetAsync(new Uri($"/v1/management/organizations/{organization}/projects/{project}", UriKind.Relative), linked.Token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            return true;
        }, TimeSpan.FromMinutes(2));
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

    internal static HashSet<string> DatabaseIds(JsonObject config)
    {
        var extraDatabases = config["extra_databases"]?.AsArray() ?? [];
        var databaseIds = new HashSet<string>(StringComparer.Ordinal) { config["database"]!.GetValue<string>() };
        foreach (var value in extraDatabases)
        {
            var id = value?.GetValue<string>();
            if (id is null || !EntityIds.IsValid(id) || !databaseIds.Add(id))
            {
                throw new InvalidOperationException("extra_databases must contain distinct tenant ULIDs");
            }
        }

        return databaseIds;
    }

    internal static async Task RegisterAsync(ManagementApi api, JsonObject config, CancellationToken cancellationToken)
    {
        string Get(string name)
        {
            return config[name]!.GetValue<string>();
        }
        var (organization, project) = (Get("organization"), Get("project"));
        string[] replicas = [.. Regions.Where(region => region != Get("region"))];
        await CatalogFixture.RegisterProjectAsync(api, api.User, organization, project, cancellationToken);
        await api.SetPlanAsync(api.User, organization, "enterprise", cancellationToken);
        var database = new TenantContext(organization, PlanTier.Enterprise, project, Get("database"));
        await CatalogFixture.RegisterAsync(api, api.User, database, "canary", replicas, cancellationToken);
        var extra = config["extra_databases"]?.AsArray() ?? [];
        for (var index = 0; index < extra.Count; index++)
        {
            await CatalogFixture.RegisterAsync(api, api.User, database with { DatabaseId = extra[index]!.GetValue<string>() },
                $"idle-{index + 1}", replicas, cancellationToken);
        }
    }

    internal static async Task WaitForBootstrapAsync(ManagementApi api, string root, string directory,
        HashSet<string> expected, CancellationToken cancellationToken)
    {
        var services = api.Server.Services;
        var engine = services.GetRequiredService<IStorageEngine>();
        var source = services.GetRequiredService<IChangeGroupSource>();
        var self = services.GetRequiredService<LocalReplica>().Id;
        Dictionary<TenantContext, (Ulid Incarnation, long Head)>? bounds = null;
        Dictionary<(TenantContext Database, ReplicaId Peer), (Ulid Incarnation, long Required)> cutoffs = [];
        await WriteAsync(Path.Combine(directory, "bootstrap.json"), new JsonObject { ["replica"] = self.Value }.ToJsonString(), cancellationToken);
        await TerraBaseTestServer.EventuallyAsync(async cancel =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, cancellationToken);
            var token = linked.Token;
            var peers = new List<ReplicaId>();
            var activeRegions = Regions.Where(region => Directory.Exists(Path.Combine(root, region))).ToArray();
            foreach (var region in activeRegions)
            {
                var path = Path.Combine(root, region, "bootstrap.json");
                if (!File.Exists(path))
                {
                    return false;
                }
                var peer = new ReplicaId(JsonNode.Parse(await File.ReadAllTextAsync(path, token))!["replica"]!.GetValue<ulong>());
                if (peer != self)
                {
                    peers.Add(peer);
                }
            }
            if (activeRegions.Length is < 2 or > 3 || peers.Count != activeRegions.Length - 1 || peers.Distinct().Count() != peers.Count)
            {
                throw new InvalidOperationException("bootstrap requires distinct remote replicas for every active region");
            }
            var physical = new HashSet<string>(StringComparer.Ordinal);
            var stores = new List<(TenantContext Database, IDatabaseStore Store)>();
            await foreach (var database in engine.ListDatabasesAsync(token))
            {
                physical.Add(database.DatabaseId!);
                if (await engine.OpenDatabaseAsync(database, token) is not { } store)
                {
                    return false;
                }
                stores.Add((database, store));
            }
            if (!physical.SetEquals(expected))
            {
                return await NotReadyAsync("physical", physical.Count, default, default, 0, 0, token);
            }
            stores.Add((ChangeGroups.System, await engine.OpenSystemDatabaseAsync(token)));
            if (bounds is null)
            {
                Dictionary<TenantContext, (Ulid Incarnation, long Head)> snapshot = [];
                foreach (var (database, store) in stores)
                {
                    if (await source.HeadAsync(database, store.Incarnation, token) is not { } head)
                    {
                        return await NotReadyAsync("scope", snapshot.Count, default, store.Incarnation, 0, 0, token);
                    }
                    snapshot.Add(database, (store.Incarnation, head));
                }
                bounds = snapshot;
            }
            foreach (var (index, (database, store)) in stores.Index())
            {
                var (boundIncarnation, boundHead) = bounds[database];
                foreach (var peer in peers)
                {
                    var position = await source.AckedAsync(database, peer, token);
                    if (position is null || position.Value.Incarnation != boundIncarnation || store.Incarnation != boundIncarnation)
                    {
                        return await NotReadyAsync("scope", index, peer, store.Incarnation, position?.Acked ?? 0, 0, token);
                    }
                    if (position.Value.RequiresRefill)
                    {
                        return await NotReadyAsync("refill", index, peer, store.Incarnation, position.Value.Acked, 0, token);
                    }
                    var key = (database, peer);
                    if (!cutoffs.TryGetValue(key, out var cutoff))
                    {
                        var after = position.Value.Acked;
                        var required = after;
                        // Raw heads bound the scan; only actual own-origin group IDs become ACK requirements.
                        while (after < boundHead)
                        {
                            var page = await source.ReadAsync(database, boundIncarnation, after,
                                ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, token);
                            if (page is null || page.Incarnation != boundIncarnation)
                            {
                                return await NotReadyAsync("scope", index, peer, store.Incarnation, position.Value.Acked, required, token);
                            }
                            if (page.Groups.Count == 0)
                            {
                                break;
                            }
                            foreach (var group in page.Groups)
                            {
                                if (group.ChangeId <= boundHead)
                                {
                                    required = group.ChangeId;
                                }
                            }
                            var next = page.Groups[^1].ChangeId;
                            if (next <= after)
                            {
                                throw new InvalidOperationException("bootstrap own-group scan did not advance");
                            }
                            after = next;
                        }
                        cutoff = (boundIncarnation, required);
                        cutoffs.Add(key, cutoff);
                    }
                    if (position.Value.Incarnation != cutoff.Incarnation || position.Value.Acked < cutoff.Required)
                    {
                        return await NotReadyAsync("own-prefix", index, peer, store.Incarnation, position.Value.Acked, cutoff.Required, token);
                    }
                }
            }
            return true;
        }, TimeSpan.FromMinutes(20));

        async Task<bool> NotReadyAsync(string reason, int index, ReplicaId peer, Ulid incarnation, long acknowledged, long required, CancellationToken token)
        {
            await WriteAsync(Path.Combine(directory, "bootstrap-progress.json"), new JsonObject
            {
                ["reason"] = reason,
                ["database_index"] = index,
                ["database_count"] = expected.Count,
                ["peer"] = peer.Value,
                ["incarnation"] = incarnation.ToString(),
                ["acknowledged"] = acknowledged,
                ["required_own_group"] = required,
                ["frozen_cutoffs"] = cutoffs.Count,
            }.ToJsonString(), token);
            return false;
        }
    }

    private static async Task SmokeAsync(CancellationToken cancellationToken)
    {
        await using var api = await ManagementApi.StartAsync(settings =>
            settings["node-name"] = EntityIds.New().ToString().ToLowerInvariant(), services: services => MeasureDurability(services), cancellationToken: cancellationToken);
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

    private static void MeasureDurability(IServiceCollection services, string? directory = null)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<BenchmarkLocalAck>();
        services.AddSingleton<IStartupFilter, BenchmarkLocalAck.DispatchTimer>();
        var original = services.Single(service => service.ServiceType == typeof(IRemoteDurability));
        services.Remove(original);
        services.AddSingleton<IRemoteDurability>(provider => new TimedDurability(
            (IRemoteDurability)original.ImplementationFactory!(provider), provider.GetRequiredService<IMeterFactory>(),
            provider.GetRequiredService<BenchmarkLocalAck>()));
        if (directory is not null)
        {
            services.AddSingleton<CommitTiming>();
            services.AddSingleton<DatabaseLifecycleCheckpoint>(provider => provider.GetRequiredService<CommitTiming>().Checkpoint);
            services.AddSingleton<IChangeGroupSink>(provider => new HeldAckSink(
                ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), directory, provider.GetRequiredService<CommitTiming>()));
        }
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

    private sealed class HeldAckSink(IChangeGroupSink inner, string directory, CommitTiming timing) : IChangeGroupSink
    {
        public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
        {
            var entered = Stopwatch.GetTimestamp();
            var nativeCommits = timing.BeginApply(database);
            Hlc? applied;
            try
            {
                applied = await inner.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);
            }
            finally
            {
                timing.EndApply();
            }
            var completed = Stopwatch.GetTimestamp();
            if (applied is not null && database == timing.Store?.Tenant)
            {
                timing.Applied(origin, groups, entered, completed, nativeCommits!);
            }
            var hold = Path.Combine(directory, "ack.hold");
            if (applied is not null && File.Exists(hold)
                && groups.Any(group => group.Deltas.Any(delta => delta.Id == File.ReadAllText(hold))))
            {
                // The real receiver committed; only delivery of its ACK is held by the disposable benchmark.
                await WriteAsync(Path.Combine(directory, "ack.reached"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture), cancellationToken);
                while (File.Exists(hold))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                }
            }

            return applied;
        }

        public ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            inner.RepairAsync(database, incarnation, states, cancellationToken);

        public ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            inner.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);
    }

    // Same-host Linux monotonic timestamps; no per-commit file I/O and no runtime instrumentation changes.
    private sealed class CommitTiming
    {
        private readonly Lock _gate = new();
        private readonly JsonArray _commits = [];
        private readonly JsonArray _applies = [];
        private readonly AsyncLocal<List<long>?> _applyCommits = new();
        private long _nativeCommit;
        private long _dropped;
        public TursoDatabaseStore? Store { get; private set; }
        public string? Origin { get; set; }

        public void Attach(TursoDatabaseStore store)
        {
            Store = store;
            var previous = store.AfterTursoCommit;
            store.AfterTursoCommit = () =>
            {
                Volatile.Write(ref _nativeCommit, Stopwatch.GetTimestamp());
                previous?.Invoke();
            };
        }

        public List<long>? BeginApply(TenantContext database) =>
            _applyCommits.Value = database == Store?.Tenant ? [] : null;

        public void EndApply() => _applyCommits.Value = null;

        public void Checkpoint(string step, TenantContext database)
        {
            var at = Stopwatch.GetTimestamp();
            if (Store is not { } store || database != store.Tenant)
            {
                return;
            }

            if (step == "replication.apply.committed")
            {
                // This checkpoint still holds the exclusive writer, so another commit cannot replace the timestamp.
                _applyCommits.Value?.Add(Volatile.Read(ref _nativeCommit));
                return;
            }

            if (step != "writer.marker")
            {
                return;
            }

            using var command = store.CurrentWriter()!.Connection.CreateCommand();
            command.CommandText = "SELECT change_id FROM turso_cdc ORDER BY change_id DESC LIMIT 1;";
            var marker = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            var identified = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (_commits.Count >= 65_536)
                {
                    _dropped++;
                    return;
                }

                _commits.Add(new JsonObject { ["marker"] = marker, ["committed"] = at, ["identified"] = identified });
            }
        }

        public void Applied(ReplicaId origin, IReadOnlyList<OriginGroup> groups, long entered, long completed, List<long> nativeCommits)
        {
            lock (_gate)
            {
                var chunk = 0;
                var deltas = 0;
                foreach (var group in groups)
                {
                    // Match StorageChangeGroupSink's whole-group transaction boundaries.
                    if (deltas != 0 && deltas + group.Deltas.Count > ChangeGroups.MaxDeltasOf(Store!.Tenant!))
                    {
                        chunk++;
                        deltas = 0;
                    }
                    deltas += group.Deltas.Count;
                    if (chunk >= nativeCommits.Count)
                    {
                        throw new InvalidOperationException("benchmark apply commit timestamp missing");
                    }
                    if (_applies.Count >= 65_536)
                    {
                        _dropped++;
                        return;
                    }

                    var documents = new JsonArray();
                    foreach (var delta in group.Deltas)
                    {
                        documents.Add(new JsonObject { ["collection"] = delta.Collection, ["id"] = delta.Id });
                    }

                    _applies.Add(new JsonObject
                    {
                        ["origin"] = origin.ToString(),
                        ["marker"] = group.ChangeId,
                        ["entered"] = entered,
                        ["native_completed"] = nativeCommits[chunk],
                        ["completed"] = completed,
                        ["documents"] = documents
                    });
                }
                if (nativeCommits.Count != chunk + 1)
                {
                    throw new InvalidOperationException("benchmark apply commit timestamp count differs");
                }
            }
        }

        public JsonObject Snapshot()
        {
            if (!OperatingSystem.IsLinux() || Stopwatch.Frequency != 1_000_000_000)
            {
                throw new InvalidOperationException("commit timing requires Linux CLOCK_MONOTONIC nanoseconds");
            }

            lock (_gate)
            {
                return new JsonObject
                {
                    ["frequency"] = Stopwatch.Frequency,
                    ["boot_id"] = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(),
                    ["time_namespace"] = new FileInfo("/proc/self/ns/time").LinkTarget,
                    ["incarnation"] = Store!.Incarnation.ToString(),
                    ["origin"] = Origin,
                    ["dropped"] = _dropped,
                    ["commits"] = _commits.DeepClone(),
                    ["applies"] = _applies.DeepClone()
                };
            }
        }
    }

    private sealed class TimedDurability(IRemoteDurability inner, IMeterFactory meters, BenchmarkLocalAck local) : IRemoteDurability
    {
        private readonly Histogram<double> _admission = meters.Create(Telemetry.MeterName).CreateHistogram<double>("terrabase.benchmark.admission.duration", "s");
        private readonly Histogram<double> _wait = meters.Create(Telemetry.MeterName).CreateHistogram<double>("terrabase.benchmark.remote_wait.duration", "s");

        public bool Confirms => inner.Confirms;
        public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => inner.OtherRegionsAsync(cancellationToken);
        public ValueTask<IReadOnlySet<ReplicaId>> HeldOriginsAsync(TenantContext database, CancellationToken cancellationToken) => inner.HeldOriginsAsync(database, cancellationToken);
        public ValueTask<DatabasePlacement?> PlacementAsync(TenantContext database, CancellationToken cancellationToken) => inner.PlacementAsync(database, cancellationToken);

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
