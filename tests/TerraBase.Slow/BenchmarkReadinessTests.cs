using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow;

public sealed class BenchmarkReadinessTests
{
    private static readonly string[] RemoteRegions = ["sjc", "fra"];

    [Test]
    public async Task Quiescence_joins_backgrounds_while_the_peer_listener_remains_live(CancellationToken cancellationToken)
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<IHostedService>(new HeldBackground(stopping, release)), cancellationToken: cancellationToken);
        var quiescing = BenchmarkProcessHost.QuiesceAsync(api.Server.Services);
        try
        {
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await Assert.That(quiescing.IsCompleted).IsFalse();
            await ConnectPeerAsync(api, cancellationToken);
        }
        finally
        {
            release.TrySetResult();
            await quiescing;
        }

        await Assert.That(api.Server.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .All(service => service.ExecuteTask is null || service.ExecuteTask.IsCompleted)).IsTrue();
        await ConnectPeerAsync(api, cancellationToken);
        using var response = await api.Server.Client.GetAsync("/healthz", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Quiescence_and_full_disposal_retain_faulted_cancellation_and_stop_errors(CancellationToken cancellationToken)
    {
        var executionError = new OperationCanceledException("benchmark faulted ExecuteTask");
        var stopError = new InvalidOperationException("benchmark StopAsync failure");
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = await ManagementApi.StartAsync(services: services =>
        {
            services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
            services.AddSingleton<IHostedService>(new FaultingBackground(execution.Task, stopError));
        }, cancellationToken: cancellationToken);
        execution.SetException(executionError);
        Exception? quiescenceError = null;
        Exception? disposalError = null;
        try
        {
            await BenchmarkProcessHost.QuiesceAsync(api.Server.Services);
        }
        catch (Exception error)
        {
            quiescenceError = error;
        }
        finally
        {
            try
            {
                await api.DisposeAsync();
            }
            catch (Exception error)
            {
                disposalError = error;
            }

            try
            {
                await api.CleanupCompletion;
            }
            catch (Exception error)
            {
                disposalError = error;
            }
        }

        await Assert.That(quiescenceError).IsNotNull();
        await Assert.That(new AggregateException(quiescenceError!).Flatten().InnerExceptions.Contains(executionError)).IsTrue();
        await Assert.That(new AggregateException(quiescenceError!).Flatten().InnerExceptions.Contains(stopError)).IsTrue();
        await Assert.That(disposalError).IsNotNull();
        await Assert.That(new AggregateException(disposalError!).Flatten().InnerExceptions.Contains(executionError)).IsTrue();
    }

    private static async Task ConnectPeerAsync(ManagementApi api, CancellationToken cancellationToken)
    {
        using var peer = new TcpClient();
        await peer.ConnectAsync(api.Server.PeerAddress!.Host, api.Server.PeerAddress.Port, cancellationToken);
    }

    private sealed class HeldBackground(TaskCompletionSource stopping, TaskCompletionSource release) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            finally
            {
                stopping.TrySetResult();
                await release.Task;
            }
        }
    }

    private sealed class FaultingBackground(Task execution, Exception stopError) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => execution;

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            throw stopError;
        }
    }

    [Test]
    public async Task Registration_keeps_the_requested_database_identities(CancellationToken cancellationToken)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru",
            services: new OtherRegions("sjc", "fra").Register, cancellationToken: cancellationToken);
        var config = new JsonObject
        {
            ["region"] = "gru",
            ["organization"] = EntityIds.New().ToString(),
            ["project"] = EntityIds.New().ToString(),
            ["database"] = EntityIds.New().ToString(),
            ["extra_databases"] = new JsonArray([.. Enumerable.Range(0, 17).Select(_ => (JsonNode?)JsonValue.Create(EntityIds.New().ToString()))]),
        };
        await BenchmarkProcessHost.RegisterAsync(api, config, cancellationToken);
        await Assert.That(BenchmarkProcessHost.DatabaseIds(config).Count).IsEqualTo(18);
        JsonArray[] invalidDatabases = [["idle-1"], [config["database"]!.GetValue<string>()],
            ["01ARZ3NDEKTSV4RRFFQ69G5FAV", "01ARZ3NDEKTSV4RRFFQ69G5FAV"]];
        foreach (var invalid in invalidDatabases)
        {
            var badConfig = (JsonObject)config.DeepClone();
            badConfig["extra_databases"] = invalid;
            await Assert.That(() => BenchmarkProcessHost.DatabaseIds(badConfig)).Throws<InvalidOperationException>();
        }
        await BenchmarkProcessHost.WaitForProjectAsync(api, config["organization"]!.GetValue<string>(),
            config["project"]!.GetValue<string>(), cancellationToken);
    }

    [Test]
    public async Task Readiness_waits_for_the_project_to_be_registered(CancellationToken cancellationToken)
    {
        var missing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<IStartupFilter>(new MissingReadiness(missing)), cancellationToken: cancellationToken);
        var organization = await api.CreateOrganizationAsync("benchmark");
        var project = EntityIds.New().ToString();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = BenchmarkProcessHost.WaitForProjectAsync(api, organization, project, deadline.Token);
        try
        {
            await missing.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await Assert.That(ready.IsCompleted).IsFalse();
            await CatalogFixture.RegisterProjectAsync(api, api.User, organization, project, cancellationToken);
            await ready;
        }
        finally
        {
            await deadline.CancelAsync();
            try
            {
                await ready;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Join the readiness task if project registration or its assertion fails.
            }
        }
    }

    [Test]
    public async Task Readiness_waits_for_the_existing_marker_collection(CancellationToken cancellationToken)
    {
        var missing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddSingleton<IStartupFilter>(new MissingReadiness(missing)), cancellationToken: cancellationToken);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = BenchmarkProcessHost.WaitForWritesAsync(data, new Uri($"{database.Data}/collections", UriKind.Relative), "bench-wait", deadline.Token);
        try
        {
            await missing.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await Assert.That(ready.IsCompleted).IsFalse();
            await ManagementApi.CreateCollectionAsync(data, database, "bench-wait", cancellationToken);
            await ready;
        }
        finally
        {
            await deadline.CancelAsync();
            try
            {
                await ready;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Join the readiness task even if the assertion or collection creation fails.
            }
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Bootstrap_requires_physical_databases_and_all_peer_acks_through_a_frozen_cutoff(int regions, CancellationToken cancellationToken)
    {
        await using var api = await ManagementApi.StartAsync(cancellationToken: cancellationToken);
        var database = await api.CreateManagedDatabaseAsync("canary");
        await api.SetPlanAsync(api.User, database.Organization, "enterprise", cancellationToken);
        using var client = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var collections = new Uri($"{database.Data}/collections", UriKind.Relative);
        await BenchmarkProcessHost.WaitForWritesAsync(client, collections, null, cancellationToken);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var source = api.Server.Services.GetRequiredService<IChangeGroupSource>();
        var self = api.Server.Services.GetRequiredService<LocalReplica>().Id;
        var peers = Enumerable.Range(1, 3).Select(id => ReplicaId.Of((ushort)id, 1, 1)).Where(peer => peer.Region != self.Region).Take(regions - 1).ToArray();
        var extra = EntityIds.New().ToString();
        var root = Directory.CreateTempSubdirectory("benchmark-bootstrap-").FullName;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        Task ready = Task.CompletedTask;
        try
        {
            foreach (var (index, region) in RemoteRegions.Take(regions - 1).Index())
            {
                Directory.CreateDirectory(Path.Combine(root, region));
                await File.WriteAllTextAsync(Path.Combine(root, region, "bootstrap.json"),
                    new JsonObject { ["replica"] = peers[index].Value }.ToJsonString(), deadline.Token);
            }
            var directory = Directory.CreateDirectory(Path.Combine(root, "gru")).FullName;
            ready = BenchmarkProcessHost.WaitForBootstrapAsync(api, root, directory, [database.Id, extra], deadline.Token);
            async Task WaitForReasonAsync(string reason, ulong? peer = null)
            {
                await TerraBaseTestServer.EventuallyAsync(async cancel =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, deadline.Token);
                    var path = Path.Combine(directory, "bootstrap-progress.json");
                    if (!File.Exists(path))
                    {
                        return false;
                    }
                    var progress = JsonNode.Parse(await File.ReadAllTextAsync(path, linked.Token))!;
                    return progress["reason"]!.GetValue<string>() == reason
                        && (peer is null || progress["peer"]!.GetValue<ulong>() == peer);
                }, TimeSpan.FromSeconds(30));
                await Assert.That(ready.IsCompleted).IsFalse();
            }
            await WaitForReasonAsync("physical");
            await CatalogFixture.RegisterAsync(api, api.User,
                new TenantContext(database.Organization, PlanTier.Hobby, database.Project, extra), "idle-1", [], deadline.Token);
            await BenchmarkProcessHost.WaitForWritesAsync(client,
                new Uri($"/v1/projects/{database.Project}/databases/idle-1/collections", UriKind.Relative), null, deadline.Token);
            await WaitForReasonAsync("own-prefix");
            var bounds = new List<(TenantContext Database, Ulid Incarnation, long Head)>();
            await foreach (var tenant in engine.ListDatabasesAsync(deadline.Token))
            {
                var store = (await engine.OpenDatabaseAsync(tenant, deadline.Token))!;
                bounds.Add((tenant, store.Incarnation, (await source.HeadAsync(tenant, store.Incarnation, deadline.Token))!.Value));
            }
            var system = await engine.OpenSystemDatabaseAsync(deadline.Token);
            bounds.Add((ChangeGroups.System, system.Incarnation, (await source.HeadAsync(ChangeGroups.System, system.Incarnation, deadline.Token))!.Value));
            // A later local write must not move the already frozen bootstrap requirement.
            await BenchmarkProcessHost.WaitForWritesAsync(client, collections, "bench-ready", deadline.Token);
            foreach (var peer in peers)
            {
                if (peer != peers[0])
                {
                    await WaitForReasonAsync("own-prefix", peer.Value);
                }
                foreach (var (tenant, incarnation, head) in bounds)
                {
                    await Assert.That(await source.AckAsync(tenant, EntityIds.New(), peer, head, default, deadline.Token)).IsFalse();
                    await Assert.That(await source.AckAsync(tenant, incarnation, peer, head, default, deadline.Token)).IsTrue();
                }
            }
            await ready;
            var (canary, canaryIncarnation, canaryHead) = bounds.Single(bound => bound.Database.DatabaseId == database.Id);
            await Assert.That((await source.HeadAsync(canary, canaryIncarnation, deadline.Token))!.Value).IsGreaterThan(canaryHead);
        }
        finally
        {
            await deadline.CancelAsync();
            try
            {
                await ready;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Join bootstrap before removing its private discovery files.
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class MissingReadiness(TaskCompletionSource missing) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                await proceed(context);
                if ((context.Request.Path.Value?.Contains("/collections/bench-wait/", StringComparison.Ordinal) == true
                    || context.Request.Path.Value?.Contains("/projects/", StringComparison.Ordinal) == true)
                    && context.Response.StatusCode == 404)
                {
                    missing.TrySetResult();
                }
            });
            next(app);
        };
    }

}
