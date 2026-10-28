using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Npgsql;
using TerraBase.Testing;

namespace TerraBase.E2E
{
    /// <summary>
    /// The document query surface: a read is <c>POST …/query</c> with <c>{"eq":["$id",id]}</c>, a write is
    /// <c>POST …/transactions</c> with one put. Point reads and writes by key are the <c>api</c> lane.
    /// </summary>
    internal sealed class ApiQueryLane : IVsTursoLane
    {
        public string Name => "api-query";

        public Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var engine = WireLanes.Engine(node);
            return Task.FromResult<ITarget>(new PostTarget(
                node, "/transactions", $"/collections/{node.Collection}/query",
                (id, json) => json is null ? SurfaceLanes.Query([id]) : SurfaceLanes.Transaction(node.Collection, [id], json), (_, documents) => engine.PreloadAsync(documents), expectedItems: 1));
        }
    }

    /// <summary>
    /// Batches of <see cref="SurfaceLanes.Batch"/> documents: a write is one transaction putting the key and the next ones, a read
    /// one query of the same keys with <c>in</c>. Ops/s counts requests: documents/s is <see cref="SurfaceLanes.Batch"/> times it.
    /// </summary>
    internal sealed class ApiBatchLane : IVsTursoLane
    {
        public string Name => "api-batch";

        public Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var engine = WireLanes.Engine(node);
            return Task.FromResult<ITarget>(new PostTarget(
                node, "/transactions", $"/collections/{node.Collection}/query",
                (id, json) => json is null ? SurfaceLanes.Query(SurfaceLanes.Neighbours(id)) : SurfaceLanes.Transaction(node.Collection, SurfaceLanes.Neighbours(id), json),
                (_, documents) => engine.PreloadAsync(documents), expectedItems: SurfaceLanes.Batch));
        }
    }

    /// <summary>Relational SQL over HTTP (<c>POST …/sql/execute</c>), one statement per request on the shared SQL table.</summary>
    internal sealed class SqlLane : IVsTursoLane
    {
        public string Name => "sql";

        public async Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var target = SurfaceLanes.SqlTarget(node);
            await SurfaceLanes.CreateTableAsync(target);
            return target;
        }
    }

    /// <summary>libSQL's Hrana over HTTP (<c>POST …/libsql/v2/pipeline</c>): one execute and a close per request, as a libSQL client's single statement.</summary>
    internal sealed class HranaLane : IVsTursoLane
    {
        public string Name => "hrana";

        public async Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var target = new PostTarget(node, "/libsql/v2/pipeline", "/libsql/v2/pipeline", (id, json) => SurfaceLanes.Hrana(SurfaceLanes.Statement(id, json)), SurfaceLanes.PreloadSqlAsync);
            await SurfaceLanes.CreateTableAsync(target);
            return target;
        }
    }

    /// <summary>The PostgreSQL wire listener through Npgsql with its defaults (a pooled connection per worker, unprepared statements) and an API key.</summary>
    internal sealed class PostgresLane : IVsTursoLane
    {
        public string Name => "pg";

        public async Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var setup = SurfaceLanes.SqlTarget(node);
            await SurfaceLanes.CreateTableAsync(setup);
            return new PostgresTarget(PostgresSql.Source(node.Api, await WireLanes.KeyAsync(node), WireLanes.DatabaseName), setup);
        }
    }

    internal static class SurfaceLanes
    {
        /// <summary>Documents per <c>api-batch</c> request.</summary>
        public const int Batch = 16;

        /// <summary>
        /// The SQL lanes' table, the shape of the turso lane's: a TEXT key is the document id (docs/terrabase/sql/relational-sql.md).
        /// Writes are unconditional puts; reads go through the CTE every SQL read builds, so they scan the table.
        /// </summary>
        public const string Table = "CREATE TABLE IF NOT EXISTS sqlbench (id TEXT PRIMARY KEY, doc TEXT NOT NULL)";

        public const string Upsert = "INSERT OR REPLACE INTO sqlbench (id, doc) VALUES ($1, $2)";

        public const string Select = "SELECT doc FROM sqlbench WHERE id = $1";

        public const string Execute = "/sql/execute";

        // 100 statements of 4 KiB bodies stay under the 1 MiB request limit, escaping included.
        private const int PreloadStatements = 100;

        /// <summary>The statement a SQL lane times, in SQLite's numbered parameters: a write when <paramref name="json"/> is set.</summary>
        public static (string Sql, string[] Args) Statement(string id, byte[]? json)
        {
            if (json is null)
            {
                return (Select.Replace('$', '?'), [id]);
            }

            return (Upsert.Replace('$', '?'), [id, Encoding.UTF8.GetString(json)]);
        }

        public static PostTarget SqlTarget(VsTursoNode node)
        {
            return new PostTarget(node, Execute, Execute, (id, json) => Sql([Statement(id, json)]), PreloadSqlAsync);
        }

        /// <summary>Creates the SQL table, once whichever SQL lane starts first.</summary>
        public static async Task CreateTableAsync(PostTarget target)
        {
            await target.PostAsync(Execute, Sql([(Table, [])]), read: false);
        }

        public static async Task PreloadSqlAsync(PostTarget target, IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            foreach (var chunk in documents.Chunk(PreloadStatements))
            {
                await target.PostAsync(Execute, Sql([.. chunk.Select(document => Statement(document.Id, document.Json))]), read: false);
            }
        }

        /// <summary>The key of <paramref name="id"/> and the next ones, wrapping: ids as <c>VsTursoBenchmark.Bodies.Id</c> makes them (15 characters, then the key).</summary>
        public static string[] Neighbours(string id)
        {
            var key = int.Parse(id.AsSpan(15), CultureInfo.InvariantCulture);
            return [.. Enumerable.Range(0, Batch).Select(i => string.Create(CultureInfo.InvariantCulture, $"{id[..15]}{(key + i) % VsTursoBenchmark.Keys:D11}"))];
        }

        public static byte[] Query(string[] ids)
        {
            return Encoding.UTF8.GetBytes(ids.Length == 1
                ? $$"""{"filter":{"eq":["$id",{{Quote(ids[0])}}]},"limit":1}"""
                : $$"""{"filter":{"in":["$id",[{{string.Join(',', ids.Select(Quote))}}]]},"limit":{{ids.Length}}}""");
        }

        public static byte[] Transaction(string collection, string[] ids, byte[] json)
        {
            var document = Encoding.UTF8.GetString(json);
            return Encoding.UTF8.GetBytes($$"""{"ops":[{{string.Join(',', ids.Select(id => Put(collection, id, document)))}}]}""");
        }

        public static byte[] Sql(IEnumerable<(string Sql, string[] Args)> statements)
        {
            return Encoding.UTF8.GetBytes($$"""{"statements":[{{string.Join(',', statements.Select(SqlStatement))}}]}""");
        }

        public static byte[] Hrana((string Sql, string[] Args) statement)
        {
            return Encoding.UTF8.GetBytes(
                $$$"""{"requests":[{"type":"execute","stmt":{"sql":{{{Quote(statement.Sql)}}},"args":[{{{string.Join(',', statement.Args.Select(HranaText))}}}]}},{"type":"close"}]}""");
        }

        private static string Put(string collection, string id, string document)
        {
            return $$"""{"op":"put","collection":{{Quote(collection)}},"id":{{Quote(id)}},"document":{{document}}}""";
        }

        private static string SqlStatement((string Sql, string[] Args) statement)
        {
            return $$"""{"sql":{{Quote(statement.Sql)}},"args":[{{string.Join(',', statement.Args.Select(Quote))}}]}""";
        }

        private static string HranaText(string value)
        {
            return $$"""{"type":"text","value":{{Quote(value)}}}""";
        }

        /// <summary>A JSON string; quotes escape as \", not \u0022: the documents carry many.</summary>
        private static string Quote(string value)
        {
            return "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
        }
    }

    /// <summary>The <c>pg</c> lane's operations on <paramref name="source"/>; <paramref name="sql"/> preloads over SQL HTTP.</summary>
    internal sealed class PostgresTarget(NpgsqlDataSource source, PostTarget sql) : ITarget
    {
        public async Task PutAsync(string id, byte[] json)
        {
            await using var command = source.CreateCommand(SurfaceLanes.Upsert);
            command.Parameters.Add(new NpgsqlParameter { Value = id });
            command.Parameters.Add(new NpgsqlParameter { Value = Encoding.UTF8.GetString(json) });
            if (await command.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException("pg: upsert did not affect exactly one row");
            }
        }

        public async Task GetAsync(string id)
        {
            await using var command = source.CreateCommand(SurfaceLanes.Select);
            command.Parameters.Add(new NpgsqlParameter { Value = id });
            _ = await command.ExecuteScalarAsync() as string ?? throw new InvalidOperationException($"pg: {id} is missing");
        }

        public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            return sql.PreloadAsync(documents);
        }

        public async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            await sql.DisposeAsync();
        }
    }

    /// <summary>
    /// One POST per operation to the bench database's client API with the node's project token, over loopback as <see cref="ApiTarget"/>:
    /// writes to <paramref name="writes"/>, reads to <paramref name="reads"/> (routes under the database). A failure status or a
    /// Hrana error result is an error, and so is a read that found no row or document.
    /// </summary>
    internal sealed class PostTarget(
        VsTursoNode node, string writes, string reads, Func<string, byte[]?, byte[]> request, Func<PostTarget, IReadOnlyList<(string Id, byte[] Json)>, Task> preload, int expectedItems = 0)
        : ITarget
    {
        private static readonly MediaTypeHeaderValue Json = new("application/json");
        private readonly HttpClient _client = new(new SocketsHttpHandler { PooledConnectionLifetime = Timeout.InfiniteTimeSpan, UseCookies = false })
        {
            BaseAddress = node.Api.Server.ClientAddress,
            DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", node.Token) },
        };

        private readonly string _database = $"/v1/projects/{node.Database.ProjectId}/databases/{WireLanes.DatabaseName}";

        public Task PutAsync(string id, byte[] json)
        {
            return PostAsync(writes, request(id, json), read: false);
        }

        public Task GetAsync(string id)
        {
            return PostAsync(reads, request(id, null), read: true);
        }

        public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            return preload(this, documents);
        }

        public async Task PostAsync(string route, byte[] body, bool read)
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = Json;
            using var response = await _client.PostAsync(new Uri(_database + route, UriKind.Relative), content);
            var answer = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode || answer.AsSpan().IndexOf("\"type\":\"error\""u8) >= 0
                || (read && (answer.AsSpan().IndexOf("\"rows\":[]"u8) >= 0 || answer.AsSpan().IndexOf("\"items\":[]"u8) >= 0)))
            {
                throw new InvalidOperationException($"{route}: answered {(int)response.StatusCode} {Encoding.UTF8.GetString(answer.AsSpan(0, Math.Min(answer.Length, 200)))}");
            }

            if (read && expectedItems > 0)
            {
                using var page = JsonDocument.Parse(answer);
                if (page.RootElement.GetProperty("items").GetArrayLength() != expectedItems)
                {
                    throw new InvalidOperationException($"{route}: expected {expectedItems} documents");
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
