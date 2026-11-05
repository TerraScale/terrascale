using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Engine;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Sql;

public sealed class ColdReplicaTests
{
    [Test]
    public async Task A_catalogued_database_forwards_reads_and_writes_before_its_replica_opens()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var home = await ManagementApi.StartSharedAsync();
        var database = await home.CreateManagedDatabaseAsync();
        using var homeClient = home.WithToken(home.ProjectToken(database.Project, "sql:read sql:write"));
        using (var schema = await ManagementApi.SqlAsync(homeClient, database, "CREATE TABLE notes (id TEXT PRIMARY KEY, body TEXT)", token))
        {
            await Assert.That(schema.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await using var away = await ManagementApi.StartAsync(settings => settings["region"] = "sjc", services =>
            services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new HomePeer(home.Server.BaseAddress))), sharedIssuer: home.Issuer);
        // Hold this fixture in the real startup interval: catalog present, follower not yet running, no primary file.
        await away.Server.Services.GetRequiredService<RegionReplication>().StopAsync(token);
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        await Assert.That(await away.Server.Services.GetRequiredService<IReplicaCatalog>().RecordAsync(
            new ReplicaCatalogEntry(database.Organization, database.Project, database.Id, database.Name, "gru", ["sjc"], 1), token)).IsTrue();
        await Assert.That(away.Server.Services.GetRequiredService<IDatabaseReplicas>().Holds(tenant)).IsFalse();
        using var client = away.WithToken(home.ProjectToken(database.Project, "sql:read sql:write"));
        var id = EntityIds.New().ToString();
        using var written = await ManagementApi.SqlAsync(client, database, $"INSERT INTO notes VALUES ('{id}', 'first')", token);
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await written.Content.ReadAsStringAsync(token));
        using var read = await ManagementApi.SqlAsync(client, database, $"SELECT body FROM notes WHERE id = '{id}'", token);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await read.Content.ReadAsStringAsync(token));
        var body = await read.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(body.GetProperty("results")[0].GetProperty("rows")[0][0].GetString()).IsEqualTo("first");
        await Assert.That(string.Join(",", read.Headers.GetValues("Server-Timing"))).Contains("home;dur=");
        await Assert.That(away.Server.Services.GetRequiredService<IDatabaseReplicas>().Holds(tenant)).IsFalse();
    }

    private sealed class HomePeer(Uri address) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([new("gru", "gru-1", address)]);
    }
}
