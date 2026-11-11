using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using MagicOnion.Serialization.MessagePack;
using MessagePack;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Backups;
using TerraScale.TerraBase.Contracts.Management;
using TerraScale.TerraBase.Contracts.Platform;
using TerraScale.TerraBase.Contracts.Regions;
using TerraScale.TerraBase.Contracts.Usage;

namespace TerraBase.Slow.Rpc;

public sealed class ManagementRpcTests
{
    [Test]
    public async Task Management_rpc_preserves_identity_placement_usage_and_backup_readback()
    {
        await using var api = await DatabaseApi.StartAsync();
        using var channel = Channel(api);
        var token = Token(api, "databases:read databases:write sql:read sql:write");
        var management = Client<IManagementService>(channel, token);
        var backups = Client<IBackupsService>(channel, token);
        var usage = Client<IUsageService>(channel, token);
        var project = api.Tenant.ProjectId;
        var source = await management.GetDatabaseAsync(project, DatabaseApi.Database);
        var regions = await management.GetDatabaseRegionsAsync(project, DatabaseApi.Database);
        var page = await management.ListDatabasesAsync(project, null, 1);
        var created = await management.CreateDatabaseAsync(project, new(Name: "created"));
        var renamed = await management.SetDatabaseAsync(project, created.Id, new(Name: "renamed"));
        var placed = await management.SetDatabaseAsync(project, renamed.Id, new(ReplicaRegions: []));
        var branch = await management.BranchAsync(project, DatabaseApi.Database, "branched");
        var totals = await usage.GetProjectUsageAsync(project, "hour", null, null);
        var measured = await usage.GetDatabaseUsageAsync(project, DatabaseApi.Database, "hour", null, null);
        var analytics = await usage.GetAnalyticsAsync(project, "1h", null, null);
        var snapshot = await backups.CreateAsync(project, DatabaseApi.Database);
        var snapshots = await backups.ListAsync(project, DatabaseApi.Database, null, 100);
        var policy = await backups.GetPolicyAsync(project, DatabaseApi.Database);
        using var settings = JsonDocument.Parse("""{"fullBackupCron":"0 3 * * *","retentionDays":3}""");
        var savedPolicy = await backups.SetPolicyAsync(project, DatabaseApi.Database, settings.RootElement);
        await management.DeleteDatabaseAsync(project, renamed.Id);
        await management.DeleteDatabaseAsync(project, branch.Id);
        using var restore = JsonDocument.Parse("""{"name":"restored"}""");
        var restoreOperation = await backups.StartRestoreAsync(project, DatabaseApi.Database, snapshot.GetProperty("id").GetString()!, restore.RootElement);
        using var owner = api.Management.ProjectClient(project);
        var restored = await ManagementApi.WaitForRestoreAsync(owner, $"/v1/projects/{project}/databases/{DatabaseApi.Database}", restoreOperation);
        var readback = await management.GetDatabaseAsync(project, "restored");
        await management.DeleteDatabaseAsync(project, readback.Id);

        await Assert.That(regions.DatabaseId).IsEqualTo(source.Id);
        await Assert.That(regions.HomeRegion).IsEqualTo(source.HomeRegion);
        await Assert.That(page.Items.Single().Id).IsEqualTo(source.Id);
        await Assert.That(renamed.Id).IsEqualTo(created.Id);
        await Assert.That(placed.Name).IsEqualTo("renamed");
        await Assert.That(placed.HomeRegion).IsEqualTo(source.HomeRegion);
        await Assert.That(branch.BranchedFrom).IsEqualTo(source.Id);
        await Assert.That(totals.ProjectId).IsEqualTo(project);
        await Assert.That(measured.DatabaseId).IsEqualTo(source.Id);
        await Assert.That(analytics.GetProperty("projectId").GetString()).IsEqualTo(project);
        await Assert.That(snapshots.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())).Contains(snapshot.GetProperty("id").GetString());
        await Assert.That(policy.GetProperty("retentionDays").GetInt32()).IsGreaterThan(0);
        await Assert.That(savedPolicy.GetProperty("retentionDays").GetInt32()).IsEqualTo(3);
        await Assert.That(restored.GetProperty("status").GetString()).IsEqualTo("completed");
        await Assert.That(readback.BranchedFrom).IsEqualTo(source.Id);
    }

    [Test]
    public async Task Discovery_and_operator_rpc_keep_public_and_private_listener_authority()
    {
        await using var api = await DatabaseApi.StartIsolatedAsync();
        using var channel = Channel(api);
        var discovery = Client<IRegionsService>(channel, null);
        var regions = await discovery.ListAsync();
        var health = await discovery.HealthAsync();
        var reader = Client<IManagementService>(channel, Token(api, "databases:read"));
        var forbidden = await Fault(() => reader.CreateDatabaseAsync(api.Tenant.ProjectId, new(Name: "forbidden")).ResponseAsync);
        var operatorRoute = await Fault(() => Client<IManagementService>(channel, null).ListDatabasesAsync(api.Tenant.ProjectId, null, 100).ResponseAsync);
        var platformRead = await Fault(() => Client<IPlatformService>(channel, Token(api, "databases:read")).ListDatabasesAsync().ResponseAsync);
        var platform = Client<IPlatformService>(channel, null);
        using var plan = JsonDocument.Parse("""{"plan":"pro"}""");
        var legacyPlan = await Fault(() => platform.SetProjectPlanAsync(api.Tenant.ProjectId, plan.RootElement).ResponseAsync);
        await platform.SetOrganizationPlanAsync(DatabaseApi.NewId(), plan.RootElement);
        var allowances = await Client<IUsageService>(channel, null).ListAllowancesAsync(null, null);
        var userAllowances = await Fault(() => Client<IUsageService>(channel, Token(api, "databases:read")).ListAllowancesAsync(null, null).ResponseAsync);

        await Assert.That(regions.Regions).Contains(health.Region);
        await Assert.That(health.Status).IsEqualTo("ok");
        await Assert.That(forbidden.Status).IsEqualTo(403);
        await Assert.That(operatorRoute.Status).IsEqualTo(404);
        await Assert.That(platformRead.Status).IsEqualTo(403);
        await Assert.That(legacyPlan.Status).IsEqualTo(404);
        await Assert.That(allowances.GetProperty("items").ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(userAllowances.Status).IsEqualTo(404);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Dashboard_rpc_on_a_regional_follower_publishes_the_plan_and_creates_the_database_on_the_writer(CancellationToken token)
    {
        await using var nats = await JetStreamServer.StartAsync(token);
        using var issuer = new TestIssuer();
        var storage = new FakeS3();
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var directory = Directory.CreateTempSubdirectory("rpc-follower-").FullName;
        using var reservation = new TcpListener(IPAddress.Any, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var peers = Path.Combine(directory, "peers");
        await File.WriteAllTextAsync(peers, "gru-1 gru 127.0.0.2\ngru-2 gru 127.0.0.3\n", token);
        try
        {
            await using var follower = await StartAsync("gru-2", "127.0.0.3");
            await using var writer = await StartAsync("gru-1", "127.0.0.2");
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(writer.Server.Services.GetRequiredService<RegionalOwners>().Writer), TimeSpan.FromSeconds(10));
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(follower.Server.Services.GetRequiredService<RegionalOwners>().Find("gru")?.Node == "gru-1"), TimeSpan.FromSeconds(10));
            await Assert.That(follower.Server.Services.GetRequiredService<RegionalOwners>().Writer).IsFalse();
            using var channel = GrpcChannel.ForAddress(new Uri($"http://127.0.0.3:{port}"));
            var organization = EntityIds.New().ToString();
            var project = EntityIds.New().ToString();
            using var plan = JsonDocument.Parse("""{"plan":"hobby"}""");
            await Client<IPlatformService>(channel, null).SetOrganizationPlanAsync(organization, plan.RootElement);
            var bearer = issuer.Mint(EntityIds.New().ToString(), descriptor =>
            {
                descriptor.Claims["org_id"] = organization;
                descriptor.Claims["project_id"] = project;
                descriptor.Claims["scope"] = "databases:write";
            });

            var created = await Client<IManagementService>(channel, bearer).CreateDatabaseAsync(project, new TerraBaseDatabasePlacement(Name: "orders", HomeRegion: "gru"));

            await Assert.That((created.Name, created.ProjectId, created.HomeRegion)).IsEqualTo(("orders", project, "gru"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Task<ManagementApi> StartAsync(string node, string address) => ManagementApi.StartAsync(settings =>
        {
            settings["region"] = "gru";
            settings["data-directory"] = Path.Combine(directory, node);
            settings["master-key"] = masterKey;
            storage.Configure(settings);
        }, services =>
        {
            storage.Route(services);
            var options = (TerraBaseOptions)services.Last(descriptor => descriptor.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
            services.Replace(ServiceDescriptor.Singleton(options with { MachineId = node, PeersFile = peers, InternalPort = port, NatsUrl = nats.Url, NatsToken = nats.Token }));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new ServingGru()));
            services.Configure<KestrelServerOptions>(kestrel => kestrel.Listen(IPAddress.Parse(address), port, listener =>
            {
                listener.Protocols = HttpProtocols.Http2;
                listener.UseTerraBaseListener(TerraBaseListener.Internal);
            }));
        }, sharedIssuer: issuer);
    }

    private sealed class ServingGru : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru"]);
    }

    private static string Token(DatabaseApi api, string scopes) => api.Management.Issuer.Mint(api.Tenant.User, token =>
    {
        token.Claims["project_id"] = api.Tenant.ProjectId;
        token.Claims["scope"] = scopes;
    });

    private static GrpcChannel Channel(DatabaseApi api) => GrpcChannel.ForAddress(api.Server.BaseAddress, new GrpcChannelOptions
    {
        MaxReceiveMessageSize = TerraBaseRpc.MaxMessageBytes,
        MaxSendMessageSize = TerraBaseRpc.MaxMessageBytes,
    });

    private static T Client<T>(GrpcChannel channel, string? token) where T : IService<T>
    {
        var invoker = channel.Intercept(metadata =>
        {
            metadata.Add(TerraBaseRpc.PeerCredentialHeader, PeerTls.Credential.CorrelationKey ?? throw new InvalidOperationException("The test peer credential is missing."));
            if (token is not null)
            {
                metadata.Add(TerraBaseRpc.AuthorizationHeader, "Bearer " + token);
            }

            return metadata;
        });
        return MagicOnionClient.Create<T>(invoker, MessagePackMagicOnionSerializerProvider.Default.WithOptions(TerraBaseRpc.SerializerOptions));
    }

    private static async Task<TerraBaseFault> Fault<T>(Func<Task<T>> call)
    {
        var error = await Assert.ThrowsAsync<RpcException>(async () => await call());
        var trailer = error!.Trailers.GetValueBytes(TerraBaseRpc.FaultTrailer) ?? throw new InvalidOperationException("The refusal carries no fault");
        return MessagePackSerializer.Deserialize<TerraBaseFault>(trailer, TerraBaseRpc.SerializerOptions);
    }
}
