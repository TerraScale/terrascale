using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Sql;

public sealed class TerraBaseSqlSocketTests
{
    private sealed class Abandoned : Exception;

    private static List<(JsonObject Client, JsonObject Server)> Recorded()
    {
        var fixture = JsonNode.Parse(TerraBaseIntegrationFixture.ReadContract("sql/websocket-session.json"))!;
        return [.. fixture["exchanges"]!.AsArray().Select(exchange => ((JsonObject)exchange!["client"]!, (JsonObject)exchange["server"]!))];
    }

    [Test]
    public async Task A_session_runs_transactions_aborts_rolls_back_and_reads_blobs_like_the_recorded_server()
    {
        await using var server = await TerraBaseSqlSocketServer.StartAsync(Recorded());
        await using var session = await server.Database(region: "gru").ConnectAsync();
        var upgrade = (await server.Upgraded).Request;

        var amount = await session.TransactionAsync(async (tx, token) =>
        {
            await tx.ExecuteAsync("INSERT INTO ledger VALUES (?, ?)", [2, 25], token);
            await Assert.That(tx.InTransaction).IsTrue();
            return (await tx.QueryAsync("SELECT amount FROM ledger WHERE id = ?", [2], token))[0].GetInt64("amount");
        });
        var afterCommit = session.InTransaction;

        await session.BeginAsync();
        var duplicate = await Assert.ThrowsAsync<TerraBaseSqlException>(() => session.ExecuteAsync("INSERT INTO ledger VALUES (1, 0)"));
        var aborted = await Assert.ThrowsAsync<TerraBaseSqlException>(() => session.QueryAsync("SELECT count(*) FROM ledger"));
        var rolledBack = await Assert.ThrowsAsync<TerraBaseSqlException>(() => session.CommitAsync());
        var afterFailedCommit = session.InTransaction;

        await Assert.ThrowsAsync<Abandoned>(() => session.TransactionAsync(async (tx, token) =>
        {
            await tx.ExecuteAsync("DELETE FROM ledger", cancellationToken: token);
            throw new Abandoned();
        }));

        var results = await session.BatchAsync([new("SELECT ? AS data, ? AS name", [TerraBaseSqlValue.FromBytes([1, 2, 3]), "Ada"]), new("SELECT count(*) AS n FROM ledger")]);

        await Assert.That(server.Mismatches).IsEmpty();
        await Assert.That(upgrade.Headers.Authorization.ToString()).IsEqualTo("Bearer tb_test_key");
        await Assert.That(upgrade.Headers[TerraBaseRegion.PinHeader].ToString()).IsEqualTo("gru");
        await Assert.That(upgrade.Path.Value).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/sql/ws");
        await Assert.That(amount).IsEqualTo(25);
        await Assert.That(afterCommit).IsFalse();
        await Assert.That((duplicate!.Code, duplicate.Sqlstate, duplicate.Statement, duplicate.StatusCode)).IsEqualTo(("primary_key_violation", "23505", 0, (HttpStatusCode?)null));
        await Assert.That((aborted!.Code, aborted.Sqlstate)).IsEqualTo((TerraBaseSqlErrorCodes.TransactionAborted, "25P02"));
        await Assert.That((rolledBack!.Code, rolledBack.Sqlstate)).IsEqualTo((TerraBaseSqlErrorCodes.TransactionRolledBack, "40000"));
        await Assert.That(afterFailedCommit).IsFalse();
        await Assert.That(session.InTransaction).IsFalse();
        await Assert.That(results[0].Rows[0].GetBytes("data")).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(results[0].Rows[0].GetString("name")).IsEqualTo("Ada");
        await Assert.That(results[1].Rows[0].GetInt64("n")).IsEqualTo(2);
    }

    [Test]
    public async Task Overlapping_calls_are_matched_to_their_answers_by_id()
    {
        var statements = Enumerable.Range(0, 32).ToList();
        await using var server = await TerraBaseSqlSocketServer.StartAsync(async (context, _) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(TerraBaseSqlSession.Subprotocol);
            var frames = new List<JsonObject>();
            for (var n = 0; n < statements.Count; n++) frames.Add((await TerraBaseSqlSocketServer.ReceiveAsync(socket))!);
            foreach (var frame in frames)
            {
                var value = frame["statements"]![0]!["args"]![0]!.GetValue<long>();
                var reply = new JsonObject
                {
                    ["id"] = frame["id"]!.GetValue<long>(),
                    ["type"] = "result",
                    ["results"] = new JsonArray(new JsonObject { ["columns"] = new JsonArray(new JsonObject { ["name"] = "v" }), ["rows"] = new JsonArray(new JsonArray(value)), ["rows_affected"] = 0 }),
                    ["transaction"] = false,
                };
                await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(reply.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            }

            while (await TerraBaseSqlSocketServer.ReceiveAsync(socket) is not null)
            {
            }
        });
        await using var session = await server.Database().ConnectAsync();

        var values = await Task.WhenAll(statements.Select(async i => (await session.QueryAsync("SELECT ? AS v", [i]))[0].GetInt64("v")));

        await Assert.That(values).IsEquivalentTo(statements.Select(i => (long)i));
    }

    [Test]
    public async Task An_unauthorized_frame_fails_its_call_and_the_closed_session_fails_the_next()
    {
        var exchanges = new List<(JsonObject, JsonObject)>
        {
            ((JsonObject)JsonNode.Parse("""{"type":"execute","statements":[{"sql":"SELECT 1"}]}""")!,
             (JsonObject)JsonNode.Parse("""{"type":"error","error":{"code":"unauthorized","message":"the API key was revoked","statement":0,"sqlstate":"28000"},"transaction":false}""")!),
        };
        await using var server = await TerraBaseSqlSocketServer.StartAsync(exchanges, WebSocketCloseStatus.PolicyViolation);
        await using var session = await server.Database().ConnectAsync();

        var revoked = await Assert.ThrowsAsync<TerraBaseSqlException>(() => session.ExecuteAsync("SELECT 1"));
        await Assert.That(revoked!.Code).IsEqualTo(TerraBaseSqlErrorCodes.Unauthorized);

        var closed = await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            while (true) await session.ExecuteAsync("SELECT 1");
        });
        await Assert.That(closed!.Message).Contains("1008");
    }

    [Test]
    [Arguments(401, "unauthorized")]
    [Arguments(421, "wrong_region")]
    [Arguments(404, "not_found")]
    public async Task A_refused_upgrade_throws_the_matching_exception(int status, string code)
    {
        await using var server = await TerraBaseSqlSocketServer.StartAsync((context, _) =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers["X-TerraBase-Region"] = "sjc";
            if (status == 421) context.Response.Headers["X-TerraBase-Database-Home"] = "gru";
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""");
        });

        var error = await Assert.ThrowsAsync<TerraBaseException>(() => server.Database().ConnectAsync());

        await Assert.That((int?)error!.StatusCode).IsEqualTo(status);
        await Assert.That(error.Region).IsEqualTo("sjc");
        switch (status)
        {
            case 401: await Assert.That(error).IsTypeOf<TerraBaseAuthenticationException>(); break;
            case 404: await Assert.That(error).IsTypeOf<TerraBaseNotFoundException>(); break;
            default: await Assert.That(((TerraBaseWrongRegionException)error).HomeRegion).IsEqualTo("gru"); break;
        }
    }

}
