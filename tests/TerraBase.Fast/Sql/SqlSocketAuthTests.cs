using System.Net.WebSockets;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlSocketAuthTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_socket_without_a_valid_credential_is_refused()
    {
        await using var db = await StartAsync();
        var forged = "tb_" + Faker.Random.AlphaNumeric(40);
        await using var sqlFirst = await db.BrowserSocketAsync();
        await using var badToken = await db.BrowserSocketAsync();
        await using var silent = await db.BrowserSocketAsync();

        var badHeader = await Assert.ThrowsAsync<WebSocketException>(() => SqlSocketClient.ConnectAsync(db.Api, db.Database.Data, forged));
        var refusedSql = await sqlFirst.ExecuteAsync(Stmt("SELECT 1"));
        var refusedSqlClose = await sqlFirst.ReceiveAsync();
        var refusedToken = await badToken.AuthAsync(forged);
        var refusedTokenClose = await badToken.ReceiveAsync();
        var timedOut = await silent.ReceiveAsync();

        await Assert.That(badHeader).IsNotNull();
        await Assert.That((Code(refusedSql), refusedSqlClose, sqlFirst.CloseStatus)).IsEqualTo(("unauthorized", (JsonElement?)null, WebSocketCloseStatus.PolicyViolation));
        await Assert.That((Code(refusedToken), refusedTokenClose, badToken.CloseStatus)).IsEqualTo(("unauthorized", (JsonElement?)null, WebSocketCloseStatus.PolicyViolation));
        await Assert.That((timedOut, silent.CloseStatus)).IsEqualTo(((JsonElement?)null, WebSocketCloseStatus.PolicyViolation));
    }

    private static string? Code(JsonElement frame) =>
        frame.GetProperty("type").GetString() == "error" ? frame.GetProperty("error").GetProperty("code").GetString() : null;
}
