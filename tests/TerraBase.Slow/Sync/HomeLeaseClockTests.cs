using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;

namespace TerraBase.Slow.Sync;

public sealed class HomeLeaseClockTests
{
    private static readonly string[] ReplicaRegions = ["sjc"];

    [Test]
    public async Task Creating_a_replicated_database_stays_up_when_the_query_deadline_clock_differs_from_the_lease_clock()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        var directory = Directory.CreateTempSubdirectory("terrabase-lease-clock-").FullName;
        var peers = Path.Combine(directory, "peers");
        await File.WriteAllTextAsync(peers, "gru-1 gru 127.0.0.1\n", token);
        var host = new FakeTimeProvider(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            await using var api = await ManagementApi.StartAsync(
                settings => settings["region"] = "gru",
                services =>
                {
                    var options = (TerraBaseOptions)services.Last(descriptor => descriptor.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
                    services.Replace(ServiceDescriptor.Singleton(options with
                    {
                        MachineId = "gru-1",
                        PeersFile = peers,
                        NatsUrl = nats.Url,
                        NatsToken = nats.Token,
                    }));
                    services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
                },
                time: host);
            await TerraBaseTestServer.EventuallyAsync(
                _ => Task.FromResult(api.Server.Services.GetRequiredService<RegionalOwners>().Writer), TimeSpan.FromSeconds(10));
            var organization = await api.CreateOrganizationAsync("Org " + EntityIds.New());
            var project = await api.CreateProjectAsync(organization, "Project");
            using var created = await api.ProjectClient(project).PostAsJsonAsync(
                ManagementApi.Uri($"/v1/projects/{project}/databases"),
                new { name = "leased", replicaRegions = ReplicaRegions },
                token);
            var body = await created.Content.ReadAsStringAsync(token);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(body);
            await Assert.That(body.Contains("storage_busy", StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }
}
