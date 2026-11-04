using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Sync;

public sealed class RegionReplicaTests
{
    private static readonly string[] Sjc = ["sjc"];

    private readonly Faker _faker = new();

    [Test]
    public async Task AReplicaFollowsItsHomeThroughDmlDdlACheckpointAndAReopen()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var directory = Directory.CreateTempSubdirectory("terrabase-region-replica-").FullName;
        try
        {
            var database = await server.CreateSyncedDatabaseAsync();
            var marker = _faker.Lorem.Word();
            var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < 100; i++)
            {
                var id = $"r{i:D4}";
                expected[id] = marker + i;
                await server.ExecuteAsync(database, $"INSERT INTO items(id, tenant, v) VALUES ('{id}', '{database.DatabaseId}', '{expected[id]}')");
            }

            var options = server.Services.GetRequiredService<TerraBaseOptions>() with { DataDirectory = directory };
            using var http = PeerTls.CreateClient(server.BaseAddress);
            var replicas = new TursoReplicas(options, NullLogger<TursoReplicas>.Instance);
            await using (replicas)
            {
                await replicas.OpenAsync(database, server.PullAddress(database), http, token);
                await Assert.That(await RowsAsync(replicas, database, token)).IsEquivalentTo(expected);


                expected["r0000"] = "updated";
                expected.Remove("r0099");
                await server.ExecuteAsync(database, "UPDATE items SET v = 'updated' WHERE id = 'r0000'");
                await server.ExecuteAsync(database, "DELETE FROM items WHERE id = 'r0099'");
                await server.ExecuteAsync(database, "CREATE TABLE extra(k INTEGER PRIMARY KEY, w TEXT NOT NULL)");
                await server.ExecuteAsync(database, "INSERT INTO extra(k, w) VALUES (7, 'seven')");
                await Assert.That((await replicas.PullAsync(database, token))!.Changed).IsTrue();
                await Assert.That(await RowsAsync(replicas, database, token)).IsEquivalentTo(expected);
                await Assert.That(await ScalarAsync(replicas, database, "SELECT w FROM extra WHERE k = 7", token)).IsEqualTo("seven");

                await server.ExecuteAsync(database, "PRAGMA wal_checkpoint(TRUNCATE)");
                expected["r1000"] = "after-checkpoint";
                await server.ExecuteAsync(database, $"INSERT INTO items(id, tenant, v) VALUES ('r1000', '{database.DatabaseId}', 'after-checkpoint')");
                await Assert.That((await replicas.PullAsync(database, token))!.Changed).IsTrue();
                await Assert.That(await RowsAsync(replicas, database, token)).IsEquivalentTo(expected);
                await Assert.That((await replicas.PullAsync(database, token))!.Changed).IsFalse();

                await using var reader = (await replicas.BeginReadAsync(database, token))!;
                var refused = await Assert.That(async () => await reader.ExecuteAsync(Statement("INSERT INTO extra(k, w) VALUES (8, 'eight')"), token))
                    .Throws<SqlStatementException>();
                await Assert.That(refused!.Failure).IsEqualTo(SqlFailure.ReadOnly);
            }

            // A restart reopens the replica from its own files, then resumes pulling incrementally.
            expected["r2000"] = "while-closed";
            await server.ExecuteAsync(database, $"INSERT INTO items(id, tenant, v) VALUES ('r2000', '{database.DatabaseId}', 'while-closed')");
            await using var reopened = new TursoReplicas(options, NullLogger<TursoReplicas>.Instance);
            await reopened.OpenAsync(database, server.PullAddress(database), http, token);
            await Assert.That((await RowsAsync(reopened, database, token)).ContainsKey("r2000")).IsFalse();
            await Assert.That((await reopened.PullAsync(database, token))!.Changed).IsTrue();
            await Assert.That(await RowsAsync(reopened, database, token)).IsEquivalentTo(expected);
            await Assert.That(await reopened.DeleteAsync(database, token)).IsTrue();
            await Assert.That(await reopened.BeginReadAsync(database, token)).IsNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AHomeListsTheDatabasesItPlacesInAReplicaRegionOnlyToPeersAndTheApiRefusesReplicasUntilEncrypted()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync(services: collection =>
            collection.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new ServingRegions(["sjc", "test-region"]))));
        var organization = await api.CreateOrganizationAsync("Org " + _faker.Random.AlphaNumeric(8));
        var project = await api.CreateProjectAsync(organization, "Project");
        var databases = $"/v1/management/organizations/{organization}/projects/{project}/databases";
        // Replica placement waits for replicas encrypted at rest like their home (Q-ENC).
        using var refused = await api.Client.PostAsJsonAsync(ManagementApi.Uri(databases), new { name = "refused", replicaRegions = Sjc }, token);
        await ErrorAssert.ErrorAsync(refused, HttpStatusCode.BadRequest, "replicas_unavailable");
        using var local = await api.Client.PostAsJsonAsync(ManagementApi.Uri(databases), new { name = "local" }, token);
        await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var replicated = new TenantContext(
            Primitives.EntityIds.New().ToString(), PlanTier.Hobby, Primitives.EntityIds.New().ToString(), Primitives.EntityIds.New().ToString());
        await CatalogFixture.RegisterAsync(api, api.User, replicated, "replicated", Sjc, token);
        var replicatedId = replicated.DatabaseId!;
        (organization, project) = (replicated.OrganizationId, replicated.ProjectId!);

        var peer = api.Server.Client;
        var page = (await peer.GetFromJsonAsync<ReplicaCatalogPage>("/v1/sync/regions/sjc/databases", TerraBaseJsonContext.Default.Options, token))!;
        var entry = page.Databases.Single(listed => listed.DatabaseId == replicatedId);
        await Assert.That(entry.OrganizationId).IsEqualTo(organization);
        await Assert.That(entry.ProjectId).IsEqualTo(project);
        await Assert.That(entry.Name).IsEqualTo("replicated");
        await Assert.That(entry.HomeRegion).IsEqualTo("test-region");
        await Assert.That(entry.ReplicaRegions).IsEquivalentTo(Sjc);
        await Assert.That(page.Databases.Any(listed => listed.Name == "local")).IsFalse();
        var other = (await peer.GetFromJsonAsync<ReplicaCatalogPage>("/v1/sync/regions/fra/databases", TerraBaseJsonContext.Default.Options, token))!;
        await Assert.That(other.Databases.Any(listed => listed.DatabaseId == replicatedId)).IsFalse();

        var placement = $"/v1/sync/organizations/{organization}/projects/{project}/databases/{replicatedId}/placement";
        await Assert.That((await peer.GetFromJsonAsync<ReplicaCatalogEntry>(placement, TerraBaseJsonContext.Default.Options, token))!.ReplicaRegions)
            .IsEquivalentTo(Sjc);
        using var missing = await peer.GetAsync($"/v1/sync/organizations/{organization}/projects/{project}/databases/{Primitives.EntityIds.New()}/placement", token);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        using var forged = PeerTls.CreateClient(api.Server.BaseAddress, credential: new InternalCredential(PeerTls.NewSecret()));
        using var unauthorized = await forged.GetAsync("/v1/sync/regions/sjc/databases", token);
        await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var data = api.DataClient();
        using var hidden = await data.GetAsync("/v1/sync/regions/sjc/databases", token);
        await Assert.That(hidden.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static SqlStatement Statement(string sql) => new(sql, [], new Dictionary<string, object?>(), 10_000, 8 * 1024 * 1024, TimeSpan.FromSeconds(5));

    private static async Task<SortedDictionary<string, string>> RowsAsync(TursoReplicas replicas, TenantContext database, CancellationToken token)
    {
        await using var reader = (await replicas.BeginReadAsync(database, token))!;
        var result = await reader.ExecuteAsync(Statement("SELECT id, v FROM items ORDER BY id"), token);
        return new SortedDictionary<string, string>(result.Rows.ToDictionary(row => (string)row[0]!, row => (string)row[1]!), StringComparer.Ordinal);
    }

    private static async Task<object?> ScalarAsync(TursoReplicas replicas, TenantContext database, string sql, CancellationToken token)
    {
        await using var reader = (await replicas.BeginReadAsync(database, token))!;
        return (await reader.ExecuteAsync(Statement(sql), token)).Rows.Single()[0];
    }

    private sealed class ServingRegions(IReadOnlyList<string> regions) : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(regions);
    }
}
