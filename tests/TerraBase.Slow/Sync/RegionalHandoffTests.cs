using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Sync;

public sealed class RegionalHandoffTests
{
    [Test]
    public async Task A_regional_follower_takes_over_only_after_applying_the_drained_writer_position_and_can_hand_back()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var nats = await JetStreamServer.StartAsync(token);
        using var issuer = new TestIssuer();
        var storage = new FakeS3();
        var directory = Directory.CreateTempSubdirectory("regional-handoff-").FullName;
        using var reservation = new TcpListener(IPAddress.Any, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var peers = Path.Combine(directory, "peers");
        await File.WriteAllTextAsync(peers, "gru-1 gru 127.0.0.2\ngru-2 gru 127.0.0.3\n", token);
        try
        {
            await using var first = await StartAsync("gru-1", "127.0.0.2");
            await using var second = await StartAsync("gru-2", "127.0.0.3");
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(first.Server.Services.GetRequiredService<RegionalOwners>().Writer), TimeSpan.FromSeconds(10));
            var organization = await first.CreateOrganizationAsync("Org " + EntityIds.New());
            var project = await first.CreateProjectAsync(organization, "Project");
            var bearer = issuer.Mint(first.User, descriptor =>
            {
                descriptor.Claims["project_id"] = project;
                descriptor.Claims["org_id"] = organization;
                descriptor.Claims["scope"] = "databases:read databases:write sql:read sql:write";
            });
            using var viaFirst = first.WithToken(bearer);
            using var viaSecond = second.WithToken(bearer);
            var basePath = $"/v1/projects/{project}/databases";
            using var created = await viaFirst.PostAsJsonAsync(ManagementApi.Uri(basePath), new { name = "rollout", replicaRegions = new[] { "sjc" } }, token);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
            var database = (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!;
            var sql = ManagementApi.Uri(basePath + "/" + database + "/sql/execute");
            await ExecuteAsync(viaFirst, "CREATE TABLE acked(id TEXT PRIMARY KEY)");
            await ExecuteAsync(viaFirst, "INSERT INTO acked VALUES ('before')");
            var handoff = first.Server.Services.GetRequiredService<RegionalHandoff>();
            await Assert.That(await handoff.DrainAsync("gru-2", token)).IsTrue();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(second.Server.Services.GetRequiredService<RegionalOwners>().Writer), TimeSpan.FromSeconds(10));
            await ExecuteAsync(viaSecond, "INSERT INTO acked VALUES ('after')");
            using var read = await viaSecond.PostAsJsonAsync(sql, new { statements = new[] { new { sql = "SELECT count(*) FROM acked" } } }, token);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await read.Content.ReadAsStringAsync(token));
            await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("results")[0].GetProperty("rows")[0][0].GetInt32()).IsEqualTo(2);
            await Assert.That(first.Server.Services.GetRequiredService<RegionalOwners>().Writer).IsFalse();
            // Keep both processes alive: this exercises role reversal without relying on a restart to clear fencing.
            await Assert.That(await second.Server.Services.GetRequiredService<RegionalHandoff>().DrainAsync("gru-1", token)).IsTrue();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(first.Server.Services.GetRequiredService<RegionalOwners>().Writer), TimeSpan.FromSeconds(10));
            await ExecuteAsync(viaFirst, "INSERT INTO acked VALUES ('returned')");

            async Task ExecuteAsync(HttpClient client, string statement)
            {
                using var response = await client.PostAsJsonAsync(sql, new { statements = new[] { new { sql = statement } } }, token);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync(token));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Task<ManagementApi> StartAsync(string node, string address) => ManagementApi.StartAsync(settings =>
        {
            settings["region"] = "gru";
            storage.Configure(settings);
        }, services =>
        {
            storage.Route(services);
            var options = (TerraBaseOptions)services.Last(descriptor => descriptor.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
            services.Replace(ServiceDescriptor.Singleton(options with { MachineId = node, PeersFile = peers, InternalPort = port, NatsUrl = nats.Url, NatsToken = nats.Token }));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Configure<KestrelServerOptions>(kestrel => kestrel.Listen(IPAddress.Parse(address), port, listener =>
            {
                listener.Protocols = HttpProtocols.Http2;
                listener.UseTerraBaseListener(TerraBaseListener.Internal);
            }));
        }, sharedIssuer: issuer);
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }
}
