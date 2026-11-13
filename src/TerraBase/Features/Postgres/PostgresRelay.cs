using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using TerraBase.Features.Replicas;
using TerraBase.Features.Regions;
using TerraBase.Hosting;

namespace TerraBase.Features.Postgres;

internal sealed class PostgresRelay(IHttpClientFactory http, InternalCredential credential, TimeProvider time, IMeterFactory meters)
{
    private readonly Counter<long> _outputTimeouts = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.qos.relay.output.timeouts");

    internal const string Path = "/v1/internal/postgres";
    internal const string ClientHeader = "X-TerraBase-Postgres-Client";

    private static readonly byte[] FlushRequest = [(byte)'H', 0, 0, 0, 4];

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
        using var state = new SessionRelay(time);
        var established = false;
        using var upstream = new Upstream(startup, input, state, stop.Token);
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

            established = true;
            stop.CancelAfter(Timeout.InfiniteTimeSpan);
            await RelayAsync(downstream, output, state, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (established && !closed.IsCancellationRequested && !stop.IsCancellationRequested && upstream.Expiry is null)
        {
            _outputTimeouts.Add(1);
        }
        catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !closed.IsCancellationRequested)
        {
            if (established && upstream.Expiry is { } expiry)
            {
                using var drain = new CancellationTokenSource(Shutdown.DrainBudget);
                var writer = new PgWriter(output);
                writer.Error(expiry.State, expiry.Message, fatal: true);
                await writer.FlushAsync(drain.Token).ConfigureAwait(false);
            }
            else if (!established)
            {
                await RefuseAsync(output, home, closed).ConfigureAwait(false);
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            input.CancelPendingRead();
            await upstream.DrainAsync().ConfigureAwait(false);
        }
    }

    private static async Task RelayAsync(Stream downstream, PipeWriter output, SessionRelay state, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        var buffer = new byte[16 * 1024];
        while (true)
        {
            if (await downstream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
            {
                return;
            }

            await downstream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            var remaining = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;
            if (remaining < 0 || remaining > HomeSessionFrames.MaxBytes)
            {
                throw new InvalidDataException("invalid relay response length");
            }

            output.Write(header);
            bool? transaction = null;
            while (remaining > 0)
            {
                var take = Math.Min(remaining, buffer.Length);
                await downstream.ReadExactlyAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
                if (header[0] == 'Z')
                {
                    transaction = buffer[0] != 'I';
                }

                output.Write(buffer.AsSpan(0, take));
                remaining -= take;
                using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sending.CancelAfter(QosSessions.MessageTimeout);
                var flushed = await output.FlushAsync(sending.Token).ConfigureAwait(false);
                if (flushed.IsCompleted || flushed.IsCanceled)
                {
                    return;
                }
            }

            state.Answered((char)header[0], transaction);
            using var final = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            final.CancelAfter(QosSessions.MessageTimeout);
            if ((await output.FlushAsync(final.Token).ConfigureAwait(false)).IsCompleted)
            {
                return;
            }
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

    private sealed class Upstream(byte[] startup, PipeReader input, SessionRelay state, CancellationToken stop) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        private Task _forwarding = Task.CompletedTask;

        public (string State, string Message)? Expiry { get; private set; }

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
                using var deadline = state.Wait();
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                try
                {
                    var header = await ReadAsync(5, deadline, waiting.Token).ConfigureAwait(false);
                    if (header is null)
                    {
                        return;
                    }

                    var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                    if (length is < 4 or > 1024 * 1024)
                    {
                        throw new InvalidDataException("invalid relay message length");
                    }

                    var body = await ReadAsync(length - 4, deadline, waiting.Token).ConfigureAwait(false);
                    if (body is null)
                    {
                        return;
                    }

                    deadline.Complete();
                    var reply = (char)header[0] switch
                    {
                        'Q' or 'S' => 'Z',
                        'P' => '1',
                        'B' => '2',
                        'D' => 'T',
                        'E' => 'C',
                        'C' => '3',
                        _ => '\0',
                    };
                    state.Sent(reply);
                    await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                    if (header[0] is (byte)'P' or (byte)'B' or (byte)'D' or (byte)'E' or (byte)'C')
                    {
                        await stream.WriteAsync(FlushRequest, cancellationToken).ConfigureAwait(false);
                    }

                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    await state.DrainAsync(cancellationToken).ConfigureAwait(false);
                    Expiry = deadline.IsTransaction && !deadline.IsPartial
                        ? ("57014", SqlErrors.TransactionExpired.Message)
                        : ("57P05", deadline.IsPartial ? "incomplete message timeout exceeded" : "session idle timeout exceeded");
                    throw;
                }
            }
        }

        private async ValueTask<byte[]?> ReadAsync(int count, SessionMessageDeadline deadline, CancellationToken cancellationToken)
        {
            var bytes = new byte[count];
            var received = 0;
            while (received < count)
            {
                var read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                var take = (int)Math.Min(read.Buffer.Length, count - received);
                read.Buffer.Slice(0, take).CopyTo(bytes.AsSpan(received));
                input.AdvanceTo(read.Buffer.GetPosition(take));
                received += take;
                if (take > 0)
                {
                    deadline.Progress(take);
                }

                if (read.IsCompleted && received < count || read.IsCanceled)
                {
                    return null;
                }
            }

            return bytes;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
