using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
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
            var key = await WireLanes.KeyAsync(node);
            var clients = new WireClient[WireLanes.Connections];
            for (var i = 0; i < clients.Length; i++)
            {
                clients[i] = await WireClient.OpenAsync(node.Api.Server.WirePort, key, node.Database.ProjectId!, WireLanes.DatabaseName);
            }

            return new WireTarget(clients, node.Collection, WireLanes.Engine(node), echo: false);
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
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var socket = await listener.AcceptSocketAsync(cancellationToken);
                        socket.NoDelay = true;
                        _ = Task.Run(async () =>
                        {
                            await using var stream = new NetworkStream(socket, ownsSocket: true);
                            await stream.CopyToAsync(stream, cancellationToken);
                        }, cancellationToken);
                    }
                }, cancellationToken);
            var clients = new WireClient[WireLanes.Connections];
            for (var i = 0; i < clients.Length; i++)
            {
                clients[i] = await WireClient.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port);
            }

            return new WireTarget(clients, node.Collection, WireLanes.Engine(node), echo: true);
        }
    }

    internal static class WireLanes
    {
        /// <summary>The database VsTursoBenchmark creates.</summary>
        public const string DatabaseName = "bench";

        /// <summary>Connections a wire lane spreads its requests over: as many as the largest concurrency, as the turso lane's readers.</summary>
        public static readonly int Connections = int.Parse(Environment.GetEnvironmentVariable("TERRABASE_VS_TURSO_WIRE_CONNECTIONS") ?? "16", System.Globalization.CultureInfo.InvariantCulture);

        public static Task<string> KeyAsync(VsTursoNode node)
        {
            return node.Api.Server.IssueNarrowedAsync(node.Database.OrganizationId, node.Database.ProjectId, null, "documents:read", "documents:write");
        }

        public static ITarget Engine(VsTursoNode node)
        {
            return new EngineTarget(node.Api.Server.Services.GetRequiredService<IDocumentService>(), node.Database, node.Collection);
        }
    }

    internal sealed class WireTarget(WireClient[] clients, string collection, ITarget preload, bool echo) : ITarget
    {
        private int _next;

        private WireClient Next()
        {
            return clients[(uint)Interlocked.Increment(ref _next) % clients.Length];
        }

        public async Task PutAsync(string id, byte[] json)
        {
            var answer = await Next().PutAsync(collection, id, Encoding.UTF8.GetString(json));
            if (!echo && answer.Status is not (200 or 201))
            {
                throw new InvalidOperationException($"wire: put {id} answered {answer.Status} {answer.Code}");
            }
        }

        public async Task GetAsync(string id)
        {
            var answer = await Next().GetAsync(collection, id);
            if (!echo && answer.Status != 200)
            {
                throw new InvalidOperationException($"wire: get {id} answered {answer.Status} {answer.Code}");
            }
        }

        public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            return echo ? Task.CompletedTask : preload.PreloadAsync(documents);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }
}
