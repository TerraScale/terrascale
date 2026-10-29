using System.Net;
using System.Net.Http.Headers;
using Bogus;
using Google.Protobuf;
using Turso;
using TerraBase.Features.Replication;
using TerraBase.Features.Sync;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Sync;

public sealed class PageSyncTests
{
    private readonly Faker _faker = new();

    [Test]
    public async Task TheOfficialClientStaysExactThroughWritesAndACheckpoint()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var directory = Directory.CreateTempSubdirectory("terrabase-page-sync-").FullName;
        try
        {
            var database = await server.CreateSyncedDatabaseAsync();
            var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
            await InsertAsync(server, database, expected, 200);
            await using var replica = await server.OpenReplicaAsync(database, directory);
            await AssertRowsAsync(replica, database, expected);

            await InsertAsync(server, database, expected, 50);
            var updated = expected.Keys.First();
            expected[updated] = _faker.Lorem.Sentence();
            await server.ExecuteAsync(database, $"UPDATE items SET v = '{expected[updated]}' WHERE id = '{updated}'");
            var deleted = expected.Keys.Last();
            expected.Remove(deleted);
            await server.ExecuteAsync(database, $"DELETE FROM items WHERE id = '{deleted}'");
            await Assert.That(await replica.PullAsync()).IsTrue();
            await AssertRowsAsync(replica, database, expected);

            await server.ExecuteAsync(database, "PRAGMA wal_checkpoint(TRUNCATE)");
            await InsertAsync(server, database, expected, 1);
            await Assert.That(await replica.PullAsync()).IsTrue();
            await AssertRowsAsync(replica, database, expected);
            await Assert.That(await replica.PullAsync()).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task OnlyAPeerOnTheInternalListenerPullsAndOnlyItsOwnDatabase()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var directory = Directory.CreateTempSubdirectory("terrabase-page-sync-auth-").FullName;
        try
        {
            var database = await server.CreateSyncedDatabaseAsync();
            var other = await server.CreateSyncedDatabaseAsync();

            await Assert.That(async () => await server.OpenReplicaAsync(database, directory, new PeerCredential(PeerTls.NewSecret())))
                .Throws<TursoSyncException>();
            using var stranger = PeerTls.CreateClient(server.BaseAddress, credential: new PeerCredential(PeerTls.NewSecret()));
            await Assert.That(await StatusAsync(stranger, server.PullAddress(database), new PullUpdatesRequest())).IsEqualTo(HttpStatusCode.Unauthorized);

            using var anyone = new HttpClient();
            await Assert.That(await StatusAsync(anyone, server.PullAddress(database, server.ClientAddress), new PullUpdatesRequest())).IsEqualTo(HttpStatusCode.NotFound);

            var crossed = database with { DatabaseId = other.DatabaseId };
            await Assert.That(await StatusAsync(server.Client, server.PullAddress(crossed), new PullUpdatesRequest())).IsEqualTo(HttpStatusCode.NotFound);
            var missing = database with { DatabaseId = EntityIds.New().ToString() };
            await Assert.That(await StatusAsync(server.Client, server.PullAddress(missing), new PullUpdatesRequest())).IsEqualTo(HttpStatusCode.NotFound);

            var partial = new PullUpdatesRequest { ServerPagesSelector = ByteString.CopyFrom(1, 2, 3) };
            await Assert.That(await StatusAsync(server.Client, server.PullAddress(database), partial)).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await StatusAsync(server.Client, server.PullAddress(database), new PullUpdatesRequest())).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task InsertAsync(TerraBaseTestServer server, TenantContext database, SortedDictionary<string, string> expected, int rows)
    {
        var values = Enumerable.Range(0, rows).Select(_ => (Id: EntityIds.New().ToString(), Value: _faker.Lorem.Sentence())).ToList();
        await server.ExecuteAsync(database,
            "INSERT INTO items(id, tenant, v) VALUES " + string.Join(", ", values.Select(row => $"('{row.Id}', '{database.DatabaseId}', '{row.Value}')")));
        foreach (var (id, value) in values)
        {
            expected[id] = value;
        }
    }

    private static async Task AssertRowsAsync(TursoSyncDatabase replica, TenantContext database, SortedDictionary<string, string> expected)
    {
        var rows = await replica.RowsAsync();
        await Assert.That(rows.Select(row => (row.Id, row.Tenant, row.Value)))
            .IsEquivalentTo(expected.Select(row => (row.Key, database.DatabaseId!, row.Value)));
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, Uri database, PullUpdatesRequest request)
    {
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
        using var response = await client.PostAsync(new Uri(database + "/pull-updates"), content);
        return response.StatusCode;
    }
}
