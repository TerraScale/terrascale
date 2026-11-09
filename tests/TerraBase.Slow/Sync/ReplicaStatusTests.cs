using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraBase.Fast.Sync;

public sealed class ReplicaStatusTests
{
    [Test]
    public async Task Regions_report_only_served_pulls_and_the_oldest_missing_commit()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-replica-status-").FullName;
        try
        {
            var peers = Path.Combine(directory, "peers");
            await File.WriteAllTextAsync(peers, "replica replica-region 127.0.0.1\n", token);
            await using var api = await ManagementApi.StartAsync(services: services =>
            {
                var options = (TerraBaseOptions)services.Single(service => service.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
                services.Replace(ServiceDescriptor.Singleton(options with { PeersFile = peers }));
            });
            var managed = await api.CreateManagedDatabaseAsync();
            var database = new TenantContext(managed.Organization, PlanTier.Hobby, managed.Project, managed.Id);
            var placement = await api.Server.Services.GetRequiredService<TerraBase.Features.Management.IManagementService>()
                .SetDatabaseRegionsAsync(api.User, managed.Organization, managed.Project, managed.Id, ["replica-region"], token);
            await Assert.That(placement is ManagedDatabase).IsTrue();
            await api.Server.ExecuteAsync(database, "CREATE TABLE items(id INTEGER PRIMARY KEY, v TEXT)");
            var path = $"/v1/projects/{managed.Project}/databases/{managed.Id}/regions";
            using var client = api.WithToken(api.Issuer.Mint(api.User, claims =>
            {
                claims.Claims["project_id"] = managed.Project;
                claims.Claims["scope"] = "databases:read";
            }));
            var initial = await ReadAsync();
            await Assert.That(initial.HomeRegion).IsEqualTo("test-region");
            await Assert.That(initial.Replicas.Single().LagMs).IsNull();
            await Assert.That(initial.Replicas.Single().LastPulledAt).IsNull();
            await Assert.That(initial.Replicas.Single().LastVerified).IsNull();

            // Use the real page client against the authenticated internal listener.
            await using var replica = await api.Server.OpenReplicaAsync(database, directory);
            var caughtUp = (await ReadAsync()).Replicas.Single();
            await Assert.That(caughtUp.LagMs).IsEqualTo(0);
            await Assert.That(caughtUp.LastPulledAt).IsNotNull();
            await Assert.That(caughtUp.Revision).IsNotNull();
            await api.Server.ExecuteAsync(database, "INSERT INTO items VALUES (1, 'first missed commit')");
            var behind = (await ReadAsync()).Replicas.Single();
            await Assert.That(behind.LagMs).IsNotNull();
            await Assert.That(behind.LagMs!.Value).IsGreaterThanOrEqualTo(0);
            await Assert.That(behind.LastPulledAt).IsEqualTo(caughtUp.LastPulledAt);
            await api.Server.ExecuteAsync(database, "INSERT INTO items VALUES (2, 'second missed commit')");
            await Assert.That((await ReadAsync()).Replicas.Single().LagMs!.Value).IsGreaterThanOrEqualTo(behind.LagMs.Value);
            await replica.PullAsync();
            var final = (await ReadAsync()).Replicas.Single();
            await Assert.That(final.LagMs).IsEqualTo(0);
            await Assert.That(final.Revision).IsNotEqualTo(caughtUp.Revision);
            await Assert.That(final.LastVerified).IsNull();

            var verifiedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            var report = new ReplicaStatusReport("replica-region", ReplicaStates.Following, 999, null, verifiedAt);
            using var content = new StringContent(JsonSerializer.Serialize(report, TerraBaseJsonContext.Default.ReplicaStatusReport), Encoding.UTF8, "application/json");
            using var reported = await api.Server.Client.PostAsync(new Uri(api.Server.BaseAddress,
                $"/v1/sync/organizations/{managed.Organization}/projects/{managed.Project}/databases/{managed.Id}/replica-status"), content, token);
            await Assert.That(reported.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            var verified = (await ReadAsync()).Replicas.Single();
            await Assert.That(verified.LastVerified).IsEqualTo(verifiedAt);
            await Assert.That(verified.LagMs).IsEqualTo(0);
            await Assert.That(verified.LastPulledAt).IsEqualTo(final.LastPulledAt);

            async Task<TerraBaseDatabaseRegions> ReadAsync()
            {
                using var response = await client.GetAsync(ManagementApi.Uri(path), token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                return (await response.Content.ReadFromJsonAsync(ManagementJson.Default.TerraBaseDatabaseRegions, token))!;
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Another_project_cannot_read_replica_observations()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var stranger = api.WithToken(api.Issuer.Mint(api.User, claims =>
        {
            claims.Claims["project_id"] = TerraBase.Primitives.EntityIds.New().ToString();
            claims.Claims["scope"] = "databases:read";
        }));
        using var response = await stranger.GetAsync(ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{database.Id}/regions"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
