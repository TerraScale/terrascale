using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TerraBase.Features.Documents;
using TerraBase.Testing;

namespace TerraBase.E2E
{
    /// <summary>
    /// The TerraBase wire protocol (docs/terrabase/wire-protocol.md) with an API key: one connection shared by every worker,
    /// so concurrency N is N requests pipelined on one connection.
    /// </summary>
    internal sealed class WireLane : IVsTursoLane
    {
        public string Name => "wire";

        public async Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var client = await WireClient.OpenAsync(node.Api.Server.WirePort, await WireLanes.KeyAsync(node), node.Database.ProjectId!, WireLanes.DatabaseName);
            return new WireTarget(client, node.Collection, WireLanes.Engine(node), echo: false);
        }
    }

    /// <summary>
    /// The physical floor of the wire lane: the same client and frames against a loopback TCP socket that echoes every byte back,
    /// so no server work at all. Concurrency N is N frames pipelined on one connection.
    /// </summary>
    internal sealed class TcpEchoLane : IVsTursoLane
    {
        public string Name => "tcp-echo";

        public async Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = Task.Run(
                async () =>
                {
                    using var socket = await listener.AcceptSocketAsync(cancellationToken);
                    socket.NoDelay = true;
                    await using var stream = new NetworkStream(socket);
                    await stream.CopyToAsync(stream, cancellationToken);
                }, cancellationToken);
            var client = await WireClient.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port);
            return new WireTarget(client, node.Collection, WireLanes.Engine(node), echo: true);
        }
    }

    /// <summary>The public HTTP API with an API key instead of a ts-auth token: the credential the wire and pgwire lanes use.</summary>
    internal sealed class ApiKeyLane : IVsTursoLane
    {
        public string Name => "api-key";

        public async Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            return new ApiTarget(node.Api.Server.ClientAddress!, await WireLanes.KeyAsync(node),
                $"/v1/projects/{node.Database.ProjectId}/databases/{WireLanes.DatabaseName}/collections/{node.Collection}/documents/by-key?id=", WireLanes.Engine(node));
        }
    }

    /// <summary>
    /// pgwire (#407) with an API key: Npgsql multiplexing over one connection, so concurrency N is N statements pipelined on it.
    /// SQL reaches SQL tables, not schemaless collections, so the lane uses its own table <c>kv (id TEXT PRIMARY KEY, doc TEXT)</c>.
    /// </summary>
    internal sealed class PgwireLane : IVsTursoLane
    {
        public string Name => "pgwire";

        public async Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = node.Api.Server.PostgresPort,
                Username = "bench",
                Password = await WireLanes.KeyAsync(node),
                Database = WireLanes.DatabaseName,
                SslMode = SslMode.Disable,
                Multiplexing = true,
                MaxPoolSize = 1,
            }.ConnectionString);
            await using var create = source.CreateCommand("CREATE TABLE kv (id TEXT PRIMARY KEY, doc TEXT NOT NULL)");
            await create.ExecuteNonQueryAsync(cancellationToken);
            return new PgwireTarget(source);
        }
    }

    internal static class WireLanes
    {
        /// <summary>The database VsTursoBenchmark creates.</summary>
        public const string DatabaseName = "bench";

        public static Task<string> KeyAsync(VsTursoNode node)
        {
            return node.Api.Server.IssueNarrowedAsync(node.Database.OrganizationId, node.Database.ProjectId, null, "documents:read", "documents:write");
        }

        public static ITarget Engine(VsTursoNode node)
        {
            return new EngineTarget(node.Api.Server.Services.GetRequiredService<IDocumentService>(), node.Database, node.Collection);
        }
    }

    internal sealed class WireTarget(WireClient client, string collection, ITarget preload, bool echo) : ITarget
    {
        public async Task PutAsync(string id, byte[] json)
        {
            var answer = await client.PutAsync(collection, id, Encoding.UTF8.GetString(json));
            if (!echo && answer.Status is not (200 or 201))
            {
                throw new InvalidOperationException($"wire: put {id} answered {answer.Status} {answer.Code}");
            }
        }

        public async Task GetAsync(string id)
        {
            var answer = await client.GetAsync(collection, id);
            if (!echo && answer.Status != 200)
            {
                throw new InvalidOperationException($"wire: get {id} answered {answer.Status} {answer.Code}");
            }
        }

        public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            return echo ? Task.CompletedTask : preload.PreloadAsync(documents);
        }

        public ValueTask DisposeAsync()
        {
            return client.DisposeAsync();
        }
    }

    internal sealed class PgwireTarget(NpgsqlDataSource source) : ITarget
    {
        public async Task PutAsync(string id, byte[] json)
        {
            await using var update = source.CreateCommand("UPDATE kv SET doc = $1 WHERE id = $2");
            update.Parameters.Add(new() { Value = Encoding.UTF8.GetString(json) });
            update.Parameters.Add(new() { Value = id });
            if (await update.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException($"pgwire: {id} was not updated");
            }
        }

        public async Task GetAsync(string id)
        {
            await using var select = source.CreateCommand("SELECT doc FROM kv WHERE id = $1");
            select.Parameters.Add(new() { Value = id });
            if (await select.ExecuteScalarAsync() is not string)
            {
                throw new InvalidOperationException($"pgwire: {id} is missing");
            }
        }

        public async Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            // One transaction per 100 rows (TerraBase allows 256 statements and 1 MiB per transaction): one commit, not one per row.
            await using var connection = await source.OpenConnectionAsync();
            foreach (var chunk in documents.Chunk(100))
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await using var batch = new NpgsqlBatch(connection, transaction);
                foreach (var (id, json) in chunk)
                {
                    var insert = new NpgsqlBatchCommand("INSERT INTO kv (id, doc) VALUES ($1, $2)");
                    insert.Parameters.Add(new() { Value = id });
                    insert.Parameters.Add(new() { Value = Encoding.UTF8.GetString(json) });
                    batch.BatchCommands.Add(insert);
                }

                await batch.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
        }

        public ValueTask DisposeAsync()
        {
            return source.DisposeAsync();
        }
    }
}
