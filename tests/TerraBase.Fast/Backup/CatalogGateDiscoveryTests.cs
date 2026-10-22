using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CatalogGateDiscoveryTests
{
    [Test]
    public async Task An_existing_catalog_waits_for_discovery_to_list_self_even_when_its_anchor_matches()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var data = Directory.CreateTempSubdirectory("terrabase-gate-discovery-").FullName;
        var machine = EntityIds.New().ToString();
        var bucket = new FakeS3();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var directory = new DirectoryView();
        try
        {
            await using (var api = await ManagementApi.StartAsync(settings =>
            {
                bucket.Configure(settings);
                settings["data-directory"] = data;
                settings["node-name"] = machine;
            }, services =>
            {
                bucket.Route(services);
                ManualBackupHosting.Configure(services);
            }, time: clock))
            {
                _ = await api.CreateOrganizationAsync("discovery control");
                await Assert.That(await api.Server.Services.GetRequiredService<ICatalogGate>().TryAnchorAsync(token)).IsTrue();
            }

            await using var reopened = await TerraBaseTestServer.StartAsync(settings =>
            {
                bucket.Configure(settings);
                settings["data-directory"] = data;
                settings["node-name"] = machine;
            }, services =>
            {
                bucket.Route(services);
                ManualBackupHosting.Configure(services);
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IMemberDirectory>(directory);
            });
            var gate = (CatalogGate)reopened.Services.GetRequiredService<ICatalogGate>();
            await Assert.That(gate.IsOpen).IsFalse();
            using (var ready = await reopened.Client.GetAsync(ManagementApi.Uri("/readyz"), token))
            {
                await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            }

            directory.Members = [new(machine, reopened.BaseAddress.ToString(), "test-region", "")];
            await Assert.That(await gate.VerifyAsync(token)).IsTrue();
            using var recovered = await reopened.Client.GetAsync(ManagementApi.Uri("/readyz"), token);
            await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    private sealed class DirectoryView : IMemberDirectory
    {
        public ImmutableArray<ClusterMember> Members { get; set; } = [];

        public Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(Members);
    }
}
