using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Sql;

internal sealed class TerraBaseSqlSocketServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly TaskCompletionSource<HttpContext> upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TerraBaseSqlSocketServer(WebApplication app) => this.app = app;

    public Uri Address { get; private set; } = null!;

    public List<string> Mismatches { get; } = [];

    public Task<HttpContext> Upgraded => upgraded.Task;

    public static Task<TerraBaseSqlSocketServer> StartAsync(IReadOnlyList<(JsonObject Client, JsonObject Server)> exchanges, WebSocketCloseStatus? closeAfter = null) =>
        StartAsync(async (context, server) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(TerraBaseSqlSession.Subprotocol);
            foreach (var (client, answer) in exchanges)
            {
                var frame = await ReceiveAsync(socket);
                if (frame is null) return;
                var id = frame["id"]!.GetValue<long>();
                frame.Remove("id");
                if (!JsonNode.DeepEquals(frame, client)) server.Mismatches.Add($"expected {client.ToJsonString()} got {frame.ToJsonString()}");
                var reply = (JsonObject)answer.DeepClone();
                reply["id"] = id;
                await socket.SendAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            }

            if (closeAfter is { } status)
            {
                await socket.CloseOutputAsync(status, "closing", CancellationToken.None);
                while (await ReceiveAsync(socket) is not null)
                {
                }

                return;
            }

            while (await ReceiveAsync(socket) is not null)
            {
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });

    public static async Task<TerraBaseSqlSocketServer> StartAsync(Func<HttpContext, TerraBaseSqlSocketServer, Task> handle)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new TerraBaseSqlSocketServer(app);
        app.UseWebSockets();
        app.Run(async context =>
        {
            server.upgraded.TrySetResult(context);
            await handle(context, server);
        });
        await app.StartAsync();
        server.Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return server;
    }

    public TerraBaseSqlDatabase Database(string apiKey = "tb_test_key", string? region = null) =>
        new TerraBaseSqlClient(new HttpClient(), new TerraBaseSqlClientOptions { BaseAddress = Address, ApiKey = apiKey, Region = region })
            .Database(TerraBaseIntegrationFixture.ProjectId, "app");

    public static async Task<JsonObject?> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (JsonObject)JsonNode.Parse(message.ToArray())!;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
