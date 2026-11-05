using System.Net.Http.Headers;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Sync;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Turso;

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
        var store = await server.StoreAsync(database);
        await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteNonQuery();
        }, "a sync test write", CancellationToken.None);
    }

    public static async Task FillAsync(this TerraBaseTestServer server, TenantContext database, long bytes)
    {
        const int RowBytes = 3000;
        const int RowsPerStatement = 300;
        for (long written = 0; written < bytes; written += RowBytes * RowsPerStatement)
        {
            await server.ExecuteAsync(database, "INSERT INTO items(id, tenant, v) VALUES "
                + string.Join(", ", Enumerable.Range(0, RowsPerStatement).Select(_ => $"('{EntityIds.New()}', '{database.DatabaseId}', '{Convert.ToHexString(RandomNumberGenerator.GetBytes(RowBytes / 2))}')")));
        }
    }

    public static async Task<TursoDatabaseStore> StoreAsync(this TerraBaseTestServer server, TenantContext database) =>
        (TursoDatabaseStore?)await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
        ?? throw new InvalidOperationException("the database does not exist");

    public static async Task<HttpResponseMessage> StartPullAsync(this TerraBaseTestServer server, TenantContext database, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(new PullUpdatesRequest().ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.PullAddress(database) + "/pull-updates")) { Content = content };
        var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                throw new HttpRequestException($"the pull answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
            }
        }

        return response;
    }

    public static async Task<T?> ReadMessageAsync<T>(this Stream answer, MessageParser<T> parser, CancellationToken cancellationToken)
        where T : IMessage<T>
    {
        var length = 0;
        var single = new byte[1];
        for (var shift = 0; ; shift += 7)
        {
            if (await answer.ReadAsync(single, cancellationToken) == 0)
            {
                return shift == 0 ? default : throw new EndOfStreamException("the pull ended inside a length prefix");
            }

            length |= (single[0] & 0x7F) << shift;
            if (single[0] < 0x80)
            {
                break;
            }
        }

        var message = new byte[length];
        await answer.ReadExactlyAsync(message, cancellationToken);
        return parser.ParseFrom(message);
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
