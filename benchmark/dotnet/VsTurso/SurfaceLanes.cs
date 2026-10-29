using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Npgsql;
using TerraBase.Testing;

namespace TerraBase.E2E
{
    internal sealed class ApiQueryLane : IVsTursoLane
    {
        public string Name => "api-query";

        public Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var engine = BenchmarkSetup.Engine(node);
            return Task.FromResult<ITarget>(new PostTarget(
                node, "/transactions", $"/collections/{node.Collection}/query",
                (id, json) => json is null ? SurfaceLanes.Query([id]) : SurfaceLanes.Transaction(node.Collection, [id], json), (_, documents) => engine.PreloadAsync(documents), expectedItems: 1));
        }
    }

    internal sealed class ApiBatchLane : IVsTursoLane
    {
        public string Name => "api-batch";

        public Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var engine = BenchmarkSetup.Engine(node);
            return Task.FromResult<ITarget>(new PostTarget(
                node, "/transactions", $"/collections/{node.Collection}/query",
                (id, json) => json is null ? SurfaceLanes.Query(SurfaceLanes.Neighbours(id)) : SurfaceLanes.Transaction(node.Collection, SurfaceLanes.Neighbours(id), json),
                (_, documents) => engine.PreloadAsync(documents), expectedItems: SurfaceLanes.Batch));
        }
    }

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

    internal sealed class PostgresLane : IVsTursoLane
    {
        public string Name => "pg";

        public async Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var setup = SurfaceLanes.SqlTarget(node);
            await SurfaceLanes.CreateTableAsync(setup);
            return new PostgresTarget(PostgresSql.Source(node.Api, await BenchmarkSetup.KeyAsync(node), BenchmarkSetup.DatabaseName), setup);
        }
    }

    internal static class SurfaceLanes
    {
        public const int Batch = 16;

        public const string Table = "CREATE TABLE IF NOT EXISTS sqlbench (id TEXT PRIMARY KEY, doc TEXT NOT NULL)";

        public const string Upsert = "INSERT OR REPLACE INTO sqlbench (id, doc) VALUES ($1, $2)";

        public const string Select = "SELECT doc FROM sqlbench WHERE id = $1";

        public const string Execute = "/sql/execute";

        private const int PreloadStatements = 100;

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

        private static string Put(string collection, string id, string document)
        {
            return $$"""{"op":"put","collection":{{Quote(collection)}},"id":{{Quote(id)}},"document":{{document}}}""";
        }

        private static string SqlStatement((string Sql, string[] Args) statement)
        {
            return $$"""{"sql":{{Quote(statement.Sql)}},"args":[{{string.Join(',', statement.Args.Select(Quote))}}]}""";
        }

        private static string Quote(string value)
        {
            return "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
        }
    }

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

        private readonly string _database = $"/v1/projects/{node.Database.ProjectId}/databases/{BenchmarkSetup.DatabaseName}";

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
            if (!response.IsSuccessStatusCode
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
