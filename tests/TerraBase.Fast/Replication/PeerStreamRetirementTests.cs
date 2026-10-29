using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;
using ReplicaCluster = TerraBase.Fast.Replication.PeerStreamTests.Cluster;

namespace TerraBase.Fast.Replication;

public sealed class PeerStreamRetirementTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly string[] ReplicaRegions = ["sjc"];

    [Test]
    public async Task A_deleted_database_leaves_sender_status_while_another_database_keeps_replicating()
    {
        await using var cluster = await ReplicaCluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        var retired = cluster.Database with { DatabaseId = EntityIds.New().ToString() };
        var deleted = new ManagedDatabaseRef(retired.OrganizationId, retired.ProjectId!, retired.DatabaseId!, $"other-{retired.DatabaseId!.ToLowerInvariant()}");
        await CatalogFixture.RegisterAsync(gru.Api, gru.Api.User, retired, deleted.Name, ReplicaRegions);
        var sender = gru.Api.Server.Services.GetRequiredService<IReplicationStatus>();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(ReportsDeletedDatabase()), Deadline);
        var healthy = new ManagedDatabaseRef(cluster.Database.OrganizationId, cluster.Database.ProjectId!, cluster.Database.DatabaseId!, "main");
        using var data = await gru.Api.CreateClientAsync(retired.OrganizationId, "documents:write", "documents:read");
        using var deadline = new CancellationTokenSource(Deadline);
        await ManagementApi.CreateCollectionAsync(data, deleted, "items", deadline.Token);
        await ManagementApi.CreateCollectionAsync(data, healthy, "items", deadline.Token);
        var retiredId = EntityIds.New().ToString();
        using (var written = await data.PutAsJsonAsync(
            ManagementApi.Uri($"{deleted.Data}/collections/items/documents/by-key?id={retiredId}"), new { n = 1 }, deadline.Token))
        {
            await Assert.That(written.IsSuccessStatusCode).IsTrue().Because(await written.Content.ReadAsStringAsync(deadline.Token));
        }

        await TerraBaseTestServer.EventuallyAsync(async _ => await sjc.ReadAsync(retired, retiredId) == """{"n":1}""", Deadline);

        // Deletion requires removing the database's replica placement first.
        var (status, body) = await gru.DatabaseRequestAsync(HttpMethod.Patch, new { replicaRegions = Array.Empty<string>() }, retired.DatabaseId);
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
        (status, body) = await gru.DatabaseRequestAsync(HttpMethod.Delete, new { }, retired.DatabaseId);
        await Assert.That(status).IsEqualTo(HttpStatusCode.NoContent).Because(body);

        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            await foreach (var database in gru.Groups.ListDatabasesAsync(token))
            {
                if (database == retired)
                {
                    return false;
                }
            }

            return true;
        }, Deadline);

        var healthyId = EntityIds.New().ToString();
        using (var written = await data.PutAsJsonAsync(
            ManagementApi.Uri($"{healthy.Data}/collections/items/documents/by-key?id={healthyId}"), new { n = 2 }, deadline.Token))
        {
            await Assert.That(written.IsSuccessStatusCode).IsTrue().Because(await written.Content.ReadAsStringAsync(deadline.Token));
        }

        await TerraBaseTestServer.EventuallyAsync(async _ => await sjc.ReadAsync(healthyId) == """{"n":2}""", Deadline);
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(sender.Snapshot().Peers.Any(peer => peer.Region == "sjc"
            && peer.Databases.Any(database => database.DatabaseId == healthy.Id)
            && peer.Databases.All(database => database.DatabaseId != deleted.Id))), Deadline);

        bool ReportsDeletedDatabase()
        {
            return sender.Snapshot().Peers.Any(peer => peer.Region == "sjc"
                && peer.Databases.Any(database => database.OrganizationId == retired.OrganizationId
                    && database.ProjectId == retired.ProjectId && database.DatabaseId == retired.DatabaseId));
        }
    }
}
