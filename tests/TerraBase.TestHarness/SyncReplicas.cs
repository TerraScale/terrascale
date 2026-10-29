using Microsoft.Extensions.DependencyInjection;
using Turso;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Testing;

internal static class SyncReplicas
{
    public static async Task<TenantContext> CreateSyncedDatabaseAsync(this TerraBaseTestServer server)
    {
        var database = await server.RegisterAsync();
        await Assert.That(await server.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        await server.ExecuteAsync(database, "CREATE TABLE items(id TEXT PRIMARY KEY, tenant TEXT NOT NULL, v TEXT NOT NULL)");
        return database;
    }

    public static async Task ExecuteAsync(this TerraBaseTestServer server, TenantContext database, string sql)
    {
        var store = (TursoDatabaseStore?)await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the database does not exist");
        await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteNonQuery();
        }, "a sync test write", CancellationToken.None);
    }

    public static Uri PullAddress(this TerraBaseTestServer server, TenantContext database, Uri? listener = null) =>
        new(listener ?? server.BaseAddress,
            $"/v1/sync/organizations/{database.OrganizationId}/projects/{database.ProjectId}/databases/{database.DatabaseId}");

    public static Task<TursoSyncDatabase> OpenReplicaAsync(this TerraBaseTestServer server, TenantContext database, string directory, InternalCredential? credential = null) =>
        TursoSyncDatabase.CreateAsync(new TursoSyncDatabaseOptions(Path.Combine(directory, database.DatabaseId + ".db"), server.PullAddress(database))
        {
            HttpClient = PeerTls.CreateClient(server.BaseAddress, credential: credential),
        });

    public static async Task<List<(string Id, string Tenant, string Value)>> RowsAsync(this TursoSyncDatabase replica)
    {
        await using var connection = await replica.ConnectAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, tenant, v FROM items ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, string, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }
}
