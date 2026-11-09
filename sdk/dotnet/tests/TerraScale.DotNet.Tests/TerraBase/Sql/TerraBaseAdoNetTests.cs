using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Sql;

public sealed partial class TerraBaseAdoNetTests
{
    private static readonly string Route = $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/sql/execute";

    internal sealed record Person(long Id, string Name);

    [Test]
    public async Task Dapper_queries_and_executes_through_the_ado_net_provider()
    {
        var bodies = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(Route);
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return TerraBaseIntegrationFixture.Json("""
                {"results":[{"columns":[{"name":"id","decltype":"INTEGER"},{"name":"name","decltype":"TEXT"}],
                "rows":[[1,"Ada"],[2,"Bob"]],"rows_affected":1,"last_insert_rowid":null}]}
                """);
        });
        using var http = new HttpClient(handler);
        using var connection = new TerraBaseDbConnection(TerraBaseSqlRestTests.Database(http));
        await connection.OpenAsync();

        var people = await connection.QueryAsync<Person>("SELECT id, name FROM people WHERE id > @min", new { min = 0L });
        var affected = await connection.ExecuteAsync("INSERT INTO people (name) VALUES (@name)", new { name = "Cara" });

        await Assert.That(bodies[0]).IsEqualTo("""{"statements":[{"sql":"SELECT id, name FROM people WHERE id > @min","named":{"min":0}}]}""");
        await Assert.That(bodies[1]).IsEqualTo("""{"statements":[{"sql":"INSERT INTO people (name) VALUES (@name)","named":{"name":"Cara"}}]}""");
        await Assert.That(people).IsEquivalentTo([new Person(1, "Ada"), new Person(2, "Bob")]);
        await Assert.That(affected).IsEqualTo(1);
        await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Open);
    }

    [Test]
    public async Task Dapper_executes_inside_transactions_with_rollback_then_commit()
    {
        JsonObject Begin() => new() { ["type"] = "begin" };
        JsonObject Execute() => new()
        {
            ["type"] = "execute",
            ["statements"] = new JsonArray(new JsonObject
            {
                ["sql"] = "INSERT INTO ledger VALUES (@id, @amount)",
                ["named"] = new JsonObject { ["id"] = 2, ["amount"] = 25 },
            }),
        };
        JsonObject Result(bool transaction) => new() { ["type"] = "result", ["results"] = new JsonArray(), ["transaction"] = transaction };
        JsonObject Write() => new()
        {
            ["type"] = "result",
            ["results"] = new JsonArray(new JsonObject
            {
                ["columns"] = new JsonArray(),
                ["rows"] = new JsonArray(),
                ["rows_affected"] = 1,
                ["last_insert_rowid"] = 2,
            }),
            ["transaction"] = true,
        };
        var pending = new Queue<(JsonObject Client, JsonObject Server)>([
            (Begin(), Result(true)),
            (Execute(), Write()),
            (new() { ["type"] = "rollback" }, Result(false)),
            (Begin(), Result(true)),
            (Execute(), Write()),
            (new() { ["type"] = "commit" }, Result(false)),
        ]);
        var order = new List<string>();
        await using var server = await TerraBaseSqlSocketServer.StartAsync(async (context, socketServer) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(TerraBaseSqlSession.Subprotocol);
            while (pending.Count > 0)
            {
                var frame = await TerraBaseSqlSocketServer.ReceiveAsync(socket);
                if (frame is null) return;
                var id = frame["id"]!.GetValue<long>();
                frame.Remove("id");
                var (client, answer) = pending.Dequeue();
                if (!JsonNode.DeepEquals(frame, client)) socketServer.Mismatches.Add($"expected {client.ToJsonString()} got {frame.ToJsonString()}");
                order.Add(client["type"]!.GetValue<string>());
                var reply = (JsonObject)answer.DeepClone();
                reply["id"] = id;
                await socket.SendAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            }

            while (await TerraBaseSqlSocketServer.ReceiveAsync(socket) is not null)
            {
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        using var connection = new TerraBaseDbConnection(server.Database());
        await connection.OpenAsync();

        await using (var rolled = await connection.BeginTransactionAsync())
        {
            await Assert.That(rolled.IsolationLevel).IsEqualTo(System.Data.IsolationLevel.Serializable);
            var affected = await connection.ExecuteAsync("INSERT INTO ledger VALUES (@id, @amount)", new { id = 2, amount = 25 }, transaction: rolled);
            await Assert.That(affected).IsEqualTo(1);
            await rolled.RollbackAsync();
        }

        await using (var committed = await connection.BeginTransactionAsync())
        {
            var affected = await connection.ExecuteAsync("INSERT INTO ledger VALUES (@id, @amount)", new { id = 2, amount = 25 }, transaction: committed);
            await Assert.That(affected).IsEqualTo(1);
            await committed.CommitAsync();
        }

        await connection.CloseAsync();
        await Assert.That(string.Join(",", order)).IsEqualTo("begin,execute,rollback,begin,execute,commit");
        await Assert.That(server.Mismatches).IsEmpty();
    }
}
