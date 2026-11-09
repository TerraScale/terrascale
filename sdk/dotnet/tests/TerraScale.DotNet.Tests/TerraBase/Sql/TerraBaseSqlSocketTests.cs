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
    public async Task A_session_runs_transactions_aborts_rolls_back_reads_blobs_and_reconnects_like_the_recorded_server()
    {
        await using var server = await TerraBaseSqlSocketServer.StartAsync(Recorded(), WebSocketCloseStatus.EndpointUnavailable);
        await using var session = await server.Database(region: "gru").ConnectAsync();
        var upgrade = (await server.Upgraded).Request;
        var (authorization, pin, upgradePath) = (upgrade.Headers.Authorization.ToString(), upgrade.Headers[TerraBaseRegion.PinHeader].ToString(), upgrade.Path.Value);

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

        var afterBatch = session.InTransaction;

        // The server restarts (1001) after the recording: a call that meets the drop fails and is not resent, a later one reconnects.
        var reconnected = false;
        for (var attempt = 0; attempt < 3 && !reconnected; attempt++)
        {
            try
            {
                await session.BeginAsync();
                reconnected = session.InTransaction;
            }
            catch (WebSocketException)
            {
            }
        }

        await Assert.That(server.Mismatches).IsEmpty();
        await Assert.That(reconnected).IsTrue();
        await Assert.That(authorization).IsEqualTo("Bearer tb_test_key");
        await Assert.That(pin).IsEqualTo("gru");
        await Assert.That(upgradePath).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/sql/ws");
        await Assert.That(amount).IsEqualTo(25);
        await Assert.That(afterCommit).IsFalse();
        await Assert.That((duplicate!.Code, duplicate.Sqlstate, duplicate.Statement, duplicate.StatusCode)).IsEqualTo(("primary_key_violation", "23505", 0, (HttpStatusCode?)null));
        await Assert.That((aborted!.Code, aborted.Sqlstate)).IsEqualTo((TerraBaseSqlErrorCodes.TransactionAborted, "25P02"));
        await Assert.That((rolledBack!.Code, rolledBack.Sqlstate)).IsEqualTo((TerraBaseSqlErrorCodes.TransactionRolledBack, "40000"));
        await Assert.That(afterFailedCommit).IsFalse();
        await Assert.That(afterBatch).IsFalse();
        await Assert.That(results[0].Rows[0].GetBytes("data")).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(results[0].Rows[0].GetString("name")).IsEqualTo("Ada");
        await Assert.That(results[1].Rows[0].GetInt64("n")).IsEqualTo(2);
    }

    [Test]
    public async Task A_refused_key_fails_the_upgrade_with_an_authentication_error_that_keeps_the_websocket_failure()
    {
        await using var server = await TerraBaseSqlSocketServer.StartAsync((context, _) =>
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("""{"error":{"code":"invalid_key","message":"refused"}}""");
        });

        var error = await Assert.ThrowsAsync<TerraBaseAuthenticationException>(() => server.Database().ConnectAsync());

        await Assert.That((int?)error!.StatusCode).IsEqualTo(401);
        await Assert.That(error.InnerException).IsTypeOf<WebSocketException>();
    }
}
