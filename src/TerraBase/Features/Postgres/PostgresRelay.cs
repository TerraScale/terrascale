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
    private const int Protocol30 = 196608;
    private static ReadOnlySpan<byte> CleartextRequest => [(byte)'R', 0, 0, 0, 8, 0, 0, 0, 3];

    internal static byte[] Startup(Dictionary<string, string> parameters, string secret)
    {
        var body = new ArrayBufferWriter<byte>();
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, Protocol30);
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
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(home.Address, Path))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new Upstream(startup, input),
        };
        credential.Sign(request.Headers);
        request.Headers.TryAddWithoutValidation(ClientHeader, (client ?? IPAddress.None).ToString());
        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(HomeLocator.HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await RefuseAsync(output, home, closed).ConfigureAwait(false);
            return;
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                await RefuseAsync(output, home, closed).ConfigureAwait(false);
                return;
            }

            await using var downstream = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var first = 0;
                while (first < CleartextRequest.Length)
                {
                    var read = await downstream.ReadAsync(buffer.AsMemory(first, CleartextRequest.Length - first), stop.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    first += read;
                }

                if (!buffer.AsSpan(0, first).SequenceEqual(CleartextRequest))
                {
                    await output.WriteAsync(buffer.AsMemory(0, first), stop.Token).ConfigureAwait(false);
                }

                for (int read; (read = await downstream.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0;)
                {
                    if ((await output.WriteAsync(buffer.AsMemory(0, read), stop.Token).ConfigureAwait(false)).IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task RefuseAsync(PipeWriter output, HomeNode home, CancellationToken cancellationToken)
    {
        var fields = new ArrayBufferWriter<byte>();
        foreach (var (type, value) in new[] { ('S', "FATAL"), ('V', "FATAL"), ('C', "08001"),
            ('M', $"the database is served in region {home.Region}, which this region cannot reach now; retry") })
        {
            fields.Write([(byte)type]);
            CString(fields, value);
        }

        fields.Write([(byte)0]);
        var message = new byte[5 + fields.WrittenCount];
        message[0] = (byte)'E';
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1), 4 + fields.WrittenCount);
        fields.WrittenSpan.CopyTo(message.AsSpan(5));
        await output.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static void CString(ArrayBufferWriter<byte> writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write([(byte)0]);
    }

    private sealed class Upstream(byte[] startup, PipeReader input) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
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
