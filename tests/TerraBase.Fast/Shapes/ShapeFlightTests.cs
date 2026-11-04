using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Shapes;

public sealed class ShapeFlightTests
{
    [Test]
    public async Task Tail_readers_share_one_flight_and_one_answer_when_a_reader_disconnects()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT)");
        var shape = new TableShapeClient(db.Reader, db.Database.Data, "items");
        await shape.SyncAsync();
        using var disconnected = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var target = shape.Uri(shape.NextOffset, live: true);
        var leaving = db.Reader.GetAsync(target, disconnected.Token);
        await WaitingAsync(db, 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var readers = new List<Task<HttpResponseMessage>>();
        for (var index = 0; index < 8; index++)
        {
            readers.Add(db.Reader.GetAsync(target, deadline.Token));
            await WaitingAsync(db, index + 2);
        }
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("terrascale.shape.v1");
        var address = new UriBuilder(new Uri(db.Api.Server.ClientAddress!, $"{shape.Path}/ws?handle={shape.Handle}&offset={shape.NextOffset}")) { Scheme = "ws" }.Uri;
        await socket.ConnectAsync(address, deadline.Token);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "auth", token = db.WriterKey }), WebSocketMessageType.Text, true, deadline.Token);
        var hintBuffer = new byte[16 * 1024];
        var ready = await socket.ReceiveAsync(hintBuffer, deadline.Token);
        await Assert.That(JsonElement.Parse(hintBuffer.AsSpan(0, ready.Count)).GetProperty("type").GetString()).IsEqualTo("ready");
        await WaitingAsync(db, 10);
        var metrics = await db.Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        await Assert.That(Sum(metrics, "terrabase_shape_flights_active")).IsEqualTo(1);
        disconnected.Cancel();
        try
        {
            using var response = await leaving;
        }
        catch (OperationCanceledException)
        {
        }

        await db.OkAsync("INSERT INTO items VALUES (1, 'shared')");
        var received = await socket.ReceiveAsync(hintBuffer, deadline.Token);
        var hint = JsonElement.Parse(hintBuffer.AsSpan(0, received.Count));
        await Assert.That(hint.GetProperty("type").GetString()).IsEqualTo("new_data");
        await Assert.That(hint.GetProperty("offset").GetString()).IsEqualTo(shape.NextOffset);
        await Assert.That(hint.TryGetProperty("changes", out _)).IsFalse();
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, deadline.Token);
        var answers = await Task.WhenAll(readers);
        try
        {
            var bodies = await Task.WhenAll(answers.Select(answer => answer.Content.ReadAsStringAsync()));
            await Assert.That(answers.All(answer => answer.StatusCode == HttpStatusCode.OK)).IsTrue();
            await Assert.That(bodies.Distinct().Count()).IsEqualTo(1);
            await Assert.That(bodies[0]).Contains("shared");
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    [Test]
    public async Task A_tenant_over_its_shape_flight_cap_gets_429_and_retry_after()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT)");
        var shapes = Enumerable.Range(0, 9).Select(index => new TableShapeClient(db.Reader, db.Database.Data, "items", $"&where=id%3E{index}")).ToArray();
        foreach (var shape in shapes)
        {
            await shape.SyncAsync();
        }

        using var stop = new CancellationTokenSource();
        var readers = new List<Task<HttpResponseMessage>>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                readers.Add(db.Reader.GetAsync(shapes[index].Uri(shapes[index].NextOffset, live: true), stop.Token));
                await WaitingAsync(db, index + 1);
            }

            using var refused = await db.Reader.GetAsync(shapes[8].Uri(shapes[8].NextOffset, live: true));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(refused.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("shape_busy");
        }
        finally
        {
            stop.Cancel();
            foreach (var reader in readers)
            {
                try
                {
                    using var response = await reader;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private static Task WaitingAsync(SqlNativeDatabase db, int readers) => TerraBaseTestServer.EventuallyAsync(async token =>
        Sum(await db.Api.Server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token), "terrabase_shape_requests_waiting") >= readers);

    private static double Sum(string text, string name) => text.Split('\n').Where(line => line.StartsWith(name, StringComparison.Ordinal))
        .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture));
}
