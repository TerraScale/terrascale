using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthStepUpRedirectTests
{
    private const string Proof = "fixture-redirect-proof";
    private const string Receipt = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Grant = "fixture-redirect-grant";

    [Test]
    [Arguments(307, "consume")]
    [Arguments(308, "consume")]
    [Arguments(307, "proof_delete")]
    [Arguments(308, "proof_delete")]
    [Arguments(307, "continue_delete")]
    [Arguments(308, "continue_delete")]
    public async Task RegisteredTransportDoesNotForwardCapabilitiesToRedirectTarget(int status, string operation)
    {
        await using var destination = new LoopbackServer("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using var origin = new LoopbackServer($"HTTP/1.1 {status} Redirect\r\nLocation: {destination.Address}leak\r\nX-TerraScale-Deletion-Authorization: {Grant}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var services = new ServiceCollection().AddTerraScaleAuthClient(options =>
        {
            options.BaseAddress = origin.Address;
            options.InstanceSlug = "terrascale";
        }).BuildServiceProvider();
        var platform = services.GetRequiredService<TerraScaleAuthClient>().Platform;
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => operation switch
        {
            "consume" => (Task)platform.ConsumeStepUpAsync("fixture-sign-in", new(Proof, "delete_account", "ts-dashboard", Receipt)),
            "proof_delete" => platform.DeleteAccountWithProofAsync("fixture-sign-in", Proof, Receipt),
            _ => platform.DeleteAccountAsync("fixture-sign-in", Receipt, Grant)
        });
        await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
        foreach (var secret in new[] { Proof, Receipt, Grant }) await Assert.That(error.ToString()).DoesNotContain(secret);
        await Assert.That(origin.Requests.Count).IsEqualTo(1);
        // Following a redirect must finish its target request before the SDK call can complete: no sleep is needed.
        await Assert.That(destination.Requests.Count).IsEqualTo(0);
        var sent = origin.Requests.Single();
        if (operation == "consume")
        {
            await Assert.That(sent).Contains("\"proof\":\"" + Proof + "\"");
            await Assert.That(sent).Contains("\"operation_receipt\":\"" + Receipt + "\"");
        }
        else
        {
            await Assert.That(sent).Contains("X-TerraScale-Deletion-Receipt: " + Receipt);
            await Assert.That(sent).Contains(operation == "proof_delete" ? "X-TerraScale-Step-Up-Proof: " + Proof : "X-TerraScale-Deletion-Authorization: " + Grant);
        }
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task serving;
        public Uri Address { get; }
        public ConcurrentQueue<string> Requests { get; } = new();

        public LoopbackServer(string response)
        {
            listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            serving = ServeAsync(Encoding.ASCII.GetBytes(response));
        }

        private async Task ServeAsync(byte[] response)
        {
            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync(stopping.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = new StringBuilder();
                var length = 0;
                while (await reader.ReadLineAsync(stopping.Token) is { Length: > 0 } line)
                {
                    request.AppendLine(line);
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(line.AsSpan("Content-Length:".Length).Trim(), CultureInfo.InvariantCulture);
                }
                var body = new char[length];
                var offset = 0;
                while (offset < body.Length)
                {
                    var read = await reader.ReadAsync(body.AsMemory(offset), stopping.Token);
                    if (read == 0) throw new IOException("Incomplete loopback request.");
                    offset += read;
                }
                request.Append(body);
                Requests.Enqueue(request.ToString());
                await stream.WriteAsync(response, stopping.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            listener.Stop();
            try { await serving; }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (SocketException) when (stopping.IsCancellationRequested) { }
            finally { stopping.Dispose(); }
        }
    }
}
