using System.Collections.Concurrent;
using Bogus;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Sync;

public sealed class ThousandDatabaseSyncTests
{
    private const int Databases = 1_000;
    private const int RowsPerDatabase = 5;

    [Test]
    public async Task TheOfficialClientSyncsAThousandDatabasesWithoutAWrongTenantRow(CancellationToken cancellationToken)
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var directory = Directory.CreateTempSubdirectory("terrabase-page-sync-1000-").FullName;
        try
        {
            var databases = new ConcurrentBag<TenantContext>();
            await Parallel.ForEachAsync(Enumerable.Range(0, Databases), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
                async (_, _) =>
                {
                    var database = await server.CreateSyncedDatabaseAsync();
                    await InsertAsync(server, database, RowsPerDatabase);
                    databases.Add(database);
                });

            await Assert.That(async () => await server.OpenReplicaAsync(databases.First(), directory, new PeerCredential(PeerTls.NewSecret())))
                .Throws<Turso.TursoSyncException>();

            var wrongTenant = 0;
            var missing = 0;
            await Parallel.ForEachAsync(databases, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
                async (database, _) =>
                {
                    await using var replica = await server.OpenReplicaAsync(database, directory);
                    var bootstrapped = await replica.RowsAsync();
                    await InsertAsync(server, database, 1);
                    var pulled = await replica.PullAsync(cancellationToken);
                    var rows = await replica.RowsAsync();
                    Interlocked.Add(ref wrongTenant, bootstrapped.Concat(rows).Count(row => row.Tenant != database.DatabaseId));
                    Interlocked.Add(ref missing, Math.Abs(RowsPerDatabase - bootstrapped.Count) + Math.Abs(RowsPerDatabase + 1 - rows.Count) + (pulled ? 0 : 1));
                });

            await Assert.That(databases.Count).IsEqualTo(Databases);
            await Assert.That(wrongTenant).IsEqualTo(0);
            await Assert.That(missing).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task InsertAsync(TerraBaseTestServer server, TenantContext database, int rows)
    {
        var faker = new Faker();
        return server.ExecuteAsync(database, "INSERT INTO items(id, tenant, v) VALUES "
            + string.Join(", ", Enumerable.Range(0, rows).Select(_ => $"('{EntityIds.New()}', '{database.DatabaseId}', '{faker.Lorem.Sentence()}')")));
    }
}
