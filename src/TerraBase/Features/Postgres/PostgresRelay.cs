using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using TerraBase.Features.Regions;
using TerraBase.Hosting;

namespace TerraBase.Features.Postgres;

internal sealed class PostgresRelay(IHttpClientFactory http, InternalCredential credential)
{
    internal const string Path = "/v1/internal/postgres";
    internal const string ClientHeader = "X-TerraBase-Postgres-Client";

    private static ReadOnlySpan<byte> CleartextRequest => [(byte)'R', 0, 0, 0, 8, 0, 0, 0, 3];

    internal static byte[] Startup(Dictionary<string, string> parameters, string secret)
    {
        var body = new ArrayBufferWriter<byte>();
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, PostgresStatements.ProtocolVersion);
        body.Write(number);
        foreach (var (name, value) in parameters.Where(parameter => !parameter.Key.StartsWith("_pq_.", StringComparison.Ordinal)))
        {
            CString(body, name);
            CString(body, value);
        }

        body.Write([(byte)0]);
        var password = new ArrayBufferWriter<byte>();
        CString(password, secret);
        var message = new byte[4 + body.WrittenCount + 1 + 4 + password.WrittenCount];
        BinaryPrimitives.WriteInt32BigEndian(message, 4 + body.WrittenCount);
        body.WrittenSpan.CopyTo(message.AsSpan(4));
        var offset = 4 + body.WrittenCount;
        message[offset] = (byte)'p';
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(offset + 1), 4 + password.WrittenCount);
        password.WrittenSpan.CopyTo(message.AsSpan(offset + 5));
        return message;
    }

    internal async Task RunAsync(HomeNode home, byte[] startup, IPAddress? client, PipeReader input, PipeWriter output, CancellationToken closed)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(closed);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var upstream = new Upstream(startup, input, stop.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(home.Address, Path))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = upstream,
        };
        credential.Sign(request.Headers);
        request.Headers.TryAddWithoutValidation(ClientHeader, (client ?? IPAddress.None).ToString());
        try
        {
            using var response = await http.CreateClient(HomeLocator.HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                await RefuseAsync(output, home, closed).ConfigureAwait(false);
                return;
            }

            await using var downstream = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
            var header = new byte[5];
            while (true)
            {
                await downstream.ReadExactlyAsync(header, stop.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (length is < 4 or > 1024 * 1024)
                {
                    throw new InvalidDataException("invalid relay startup message length");
                }

                var body = new byte[length - 4];
                await downstream.ReadExactlyAsync(body, stop.Token).ConfigureAwait(false);
                if (header[0] == 'R' && body.AsSpan().SequenceEqual(CleartextRequest[5..]))
                {
                    continue;
                }

                await output.WriteAsync(header, stop.Token).ConfigureAwait(false);
                await output.WriteAsync(body, stop.Token).ConfigureAwait(false);
                if (header[0] == 'E')
                {
                    return;
                }

                if (header[0] == 'Z')
                {
                    break;
                }
            }

            stop.CancelAfter(Timeout.InfiniteTimeSpan);
            await downstream.CopyToAsync(output.AsStream(leaveOpen: true), stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !closed.IsCancellationRequested)
        {
            await RefuseAsync(output, home, closed).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            input.CancelPendingRead();
            await upstream.DrainAsync().ConfigureAwait(false);
        }
    }

    private static async Task RefuseAsync(PipeWriter output, HomeNode home, CancellationToken cancellationToken)
    {
        var writer = new PgWriter(output);
        writer.Error("08001", $"the database is served in region {home.Region}, which this region cannot reach now; retry", fatal: true);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void CString(ArrayBufferWriter<byte> writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write([(byte)0]);
    }

    private sealed class Upstream(byte[] startup, PipeReader input, CancellationToken stop) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        private Task _forwarding = Task.CompletedTask;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            _forwarding = ForwardAsync(stream, stop);

        public async Task DrainAsync()
        {
            try
            {
                await _forwarding.ConfigureAwait(false);
            }
            catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or IOException or HttpRequestException)
            {
                return;
            }
        }

        private async Task ForwardAsync(Stream stream, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(startup, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (var segment in read.Buffer)
                {
                    await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                input.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted || read.IsCanceled)
                {
                    return;
                }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
