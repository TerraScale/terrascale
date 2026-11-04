using System.Text.Json;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using MagicOnion.Serialization.MessagePack;
using MessagePack;
using TerraBase.Testing;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Backups;
using TerraScale.TerraBase.Contracts.Management;
using TerraScale.TerraBase.Contracts.Platform;
using TerraScale.TerraBase.Contracts.Regions;
using TerraScale.TerraBase.Contracts.Usage;

namespace TerraBase.Fast.Rpc;

public sealed class ManagementRpcTests
{
    [Test]
    public async Task Management_rpc_preserves_identity_placement_usage_and_backup_readback()
    {
        await using var api = await DatabaseApi.StartAsync();
        using var channel = Channel(api);
        var token = Token(api, "databases:read databases:write documents:read documents:write");
        var management = Client<IManagementService>(channel, token);
        var backups = Client<IBackupsService>(channel, token);
        var usage = Client<IUsageService>(channel, token);
        var project = api.Tenant.ProjectId;
        var source = await management.GetDatabaseAsync(project, DatabaseApi.Database);
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
        using var settings = JsonDocument.Parse("""{"fullBackupCron":"0 3 * * *","retentionDays":3,"disabled":false}""");
        var savedPolicy = await backups.SetPolicyAsync(project, DatabaseApi.Database, settings.RootElement);
        await management.DeleteDatabaseAsync(project, renamed.Id);
        await management.DeleteDatabaseAsync(project, branch.Id);
        using var restore = JsonDocument.Parse("""{"name":"restored"}""");
        var restored = await backups.RestoreBackupAsync(project, DatabaseApi.Database, snapshot.GetProperty("id").GetString()!, restore.RootElement);
        var readback = await management.GetDatabaseAsync(project, "restored");
        await management.DeleteDatabaseAsync(project, readback.Id);

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
        await Assert.That(restored.GetProperty("id").GetString()).IsEqualTo(readback.Id);
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
