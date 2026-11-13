using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using TerraBase.Features.Qos;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;
using TerraBase.Features.Sql;
using TerraBase.Features.Replicas;
using TerraBase.Features.Regions;
using TerraBase.Hosting;

namespace TerraBase.Features.Postgres;

internal sealed class PostgresRelay(IHttpClientFactory http, InternalCredential credential, TimeProvider time, IMeterFactory meters, IMemoryBudget memory, IQosAdmission admission, IHomeLocator locator)
{
    private readonly Counter<long> _outputTimeouts = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.qos.relay.output.timeouts");

    internal const string Path = "/v1/internal/postgres";
    internal const string ClientHeader = "X-TerraBase-Postgres-Client";

    private static readonly SqlError SessionEnded = new(SqlErrors.Unavailable, "the relayed session ended; its open transaction was rolled back");

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

    internal async Task RunAsync(HomeNode home, TenantContext tenant, byte[] startup, IPAddress? client, PipeReader input, PipeWriter output, IDisposable admissionLease, ProxyRequestBudget budget, CancellationToken closed)
    {
        using var admitted = admissionLease;
        for (var attempt = 0; await AttemptAsync(home, attempt) is { } fresh; attempt++)
        {
            home = fresh;
        }

        async Task<HomeNode?> AttemptAsync(HomeNode home, int attempt)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(closed);
            using var handshake = budget.Link(closed);
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, handshake.Token);
            using var state = new SessionRelay(time);
            using var writing = new SemaphoreSlim(1, 1);
            var established = false;
            var closing = 0;
            using var upstream = new Upstream(startup, input, output, writing, state, memory, tenant, BeginClose, Admit, stop.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(home.Address, Path))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = upstream,
            };
            request.Headers.TryAddWithoutValidation(ProxyRequestBudget.RemainingHeader, budget.Milliseconds);
            credential.Sign(request.Headers);
            request.Headers.TryAddWithoutValidation(ClientHeader, (client ?? IPAddress.None).ToString());
            try
            {
                using var response = await http.CreateClient(HomeLocator.HttpClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, opening.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    await RefuseAsync(output, home, closed).ConfigureAwait(false);
                    return null;
                }

                await using var downstream = await response.Content.ReadAsStreamAsync(opening.Token).ConfigureAwait(false);
                var header = new byte[5];
                while (true)
                {
                    await downstream.ReadExactlyAsync(header, opening.Token).ConfigureAwait(false);
                    var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                    if (length is < 4 or > 1024 * 1024)
                    {
                        throw new InvalidDataException("invalid relay startup message length");
                    }

                    var body = new byte[length - 4];
                    await downstream.ReadExactlyAsync(body, opening.Token).ConfigureAwait(false);
                    if (header[0] == 'R' && body.AsSpan().SequenceEqual(CleartextRequest[5..]))
                    {
                        continue;
                    }

                    if (header[0] == 'E' && attempt == 0 && PlacementMiss(body)
                        && await locator.RefreshAsync(tenant.ProjectId!, tenant.DatabaseId, opening.Token).ConfigureAwait(false) is { } fresh
                        && fresh.Region != home.Region)
                    {
                        return fresh;
                    }

                    await output.WriteAsync(header, stop.Token).ConfigureAwait(false);
                    await output.WriteAsync(body, stop.Token).ConfigureAwait(false);
                    if (header[0] == 'E')
                    {
                        return null;
                    }

                    if (header[0] == 'Z')
                    {
                        upstream.Status = body[0];
                        break;
                    }
                }

                established = true;
                admitted.Dispose();
                upstream.Open();
                await RelayAsync(downstream, output, writing, upstream, state, tenant, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (established && !closed.IsCancellationRequested && !stop.IsCancellationRequested && upstream.Expiry is null && !state.Answer.IsCancellationRequested)
            {
                _outputTimeouts.Add(1);
            }
            catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !closed.IsCancellationRequested)
            {
                if (established)
                {
                    BeginClose();
                    var refusal = state.Pending.Commits ? SqlErrors.OutcomeUnknown(home.Region)
                        : upstream.Expiry is { } expiry ? new SqlError(SqlErrors.QueryCanceled, expiry.Message) : SessionEnded;
                    await writing.WaitAsync(stop.Token).ConfigureAwait(false);
                    try
                    {
                        var writer = new PgWriter(output);
                        writer.Error(state.Pending.Commits ? refusal.Sqlstate : upstream.Expiry?.State ?? "08006", refusal.Message, fatal: true);
                        await writer.FlushAsync(stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        writing.Release();
                    }
                }
                else
                {
                    await RefuseAsync(output, home, closed).ConfigureAwait(false);
                }
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                if (established)
                {
                    input.CancelPendingRead();
                }

                await upstream.DrainAsync().ConfigureAwait(false);
            }

            return null;

            void BeginClose()
            {
                if (Interlocked.Exchange(ref closing, 1) == 0)
                {
                    stop.CancelAfter(Shutdown.DrainBudget);
                }
            }
        }

        (IDisposable? Lease, int RetryAfter) Admit()
        {
            var lease = admission.TryEnter(tenant, out var retryAfter);
            return (lease, retryAfter);
        }
    }

    private async Task RelayAsync(Stream downstream, PipeWriter output, SemaphoreSlim writing, Upstream upstream, SessionRelay state, TenantContext tenant, CancellationToken cancellationToken)
    {
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Answer.Token);
        var header = new byte[5];
        while (true)
        {
            if (!await ReadAnswerAsync(downstream, header, state.Answer, receiving.Token).ConfigureAwait(false))
            {
                throw new EndOfStreamException("the relay ended before its next response");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;
            if (length < 0 || length > HomeSessionFrames.MaxBytes)
            {
                throw new InvalidDataException("invalid relay response length");
            }

            using var reservation = memory.TryReserve(length, tenant) ?? throw new QosMemoryException();
            var body = new byte[length];
            if (!await ReadAnswerAsync(downstream, body, state.Answer, receiving.Token).ConfigureAwait(false))
            {
                throw new EndOfStreamException("a relay response ended early");
            }

            bool? transaction = header[0] == 'Z' && body.Length > 0 ? body[0] != 'I'
                : header[0] == 'C' && body.AsSpan().SequenceEqual("BEGIN\0"u8) ? true
                : header[0] == 'C' && (body.AsSpan().SequenceEqual("COMMIT\0"u8) || body.AsSpan().SequenceEqual("ROLLBACK\0"u8)) ? false : null;
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sending.CancelAfter(QosSessions.MessageTimeout);
            state.Answer.Pause();
            try
            {
                await writing.WaitAsync(sending.Token).ConfigureAwait(false);
                try
                {
                    output.Write(header);
                    output.Write(body);
                    if (header[0] == 'Z' && body.Length > 0)
                    {
                        upstream.Status = body[0];
                    }

                    state.Answered((char)header[0], transaction);
                    var flushed = await output.FlushAsync(sending.Token).ConfigureAwait(false);
                    if (flushed.IsCompleted || flushed.IsCanceled)
                    {
                        return;
                    }
                }
                finally
                {
                    writing.Release();
                }
            }
            finally
            {
                state.Answer.Unpause();
            }
        }
    }

    private static async ValueTask<bool> ReadAnswerAsync(Stream stream, Memory<byte> bytes, SessionMessageDeadline deadline, CancellationToken cancellationToken)
    {
        var received = 0;
        while (received < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes[received..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return received == 0 ? false : throw new EndOfStreamException("a relay response ended early");
            }

            received += count;
            deadline.Progress(count);
        }

        return true;
    }

    private static bool PlacementMiss(ReadOnlySpan<byte> body)
    {
        string? code = null;
        string? message = null;
        while (!body.IsEmpty && body[0] != 0)
        {
            var field = body[0];
            body = body[1..];
            var end = body.IndexOf((byte)0);
            if (end < 0)
            {
                return false;
            }
            if (field == 'C')
            {
                code = Encoding.UTF8.GetString(body[..end]);
            }
            else if (field == 'M')
            {
                message = Encoding.UTF8.GetString(body[..end]);
            }
            body = body[(end + 1)..];
        }
        return code == "57P03" && message == HomeForwarding.PlacementMissMessage;
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

    private sealed class Upstream(byte[] startup, PipeReader input, PipeWriter output, SemaphoreSlim writing, SessionRelay state, IMemoryBudget memory, TenantContext tenant, Action beginClose,
        Func<(IDisposable? Lease, int RetryAfter)> admit, CancellationToken stop) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        private readonly PgRelayRequests _requests = new(memory, tenant);
        private Task _forwarding = Task.CompletedTask;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _rejectedUntilSync;

        public byte Status { get; set; } = (byte)'I';

        public void Open() => _ready.TrySetResult();

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
            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                using var deadline = state.Wait();
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                try
                {
                    var header = await ReadAsync(5, deadline, waiting.Token).ConfigureAwait(false);
                    if (header is null)
                    {
                        beginClose();
                        await state.DrainAsync(cancellationToken).ConfigureAwait(false);
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
                    var refused = _rejectedUntilSync;
                    var retryAfter = 0;
                    if (!refused && header[0] is (byte)'Q' or (byte)'P' or (byte)'B' or (byte)'E')
                    {
                        refused = !state.TryAdmit(admit, out retryAfter);
                    }

                    if (refused)
                    {
                        await RefuseAsync((char)header[0], retryAfter, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

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
                    var intent = _requests.Sent((char)header[0], body, state.State);
                    state.Sent(reply, commits: intent.Commits, completed: intent.Completed);
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
                    beginClose();
                    Expiry = deadline.IsTransaction && !deadline.IsPartial
                        ? ("57014", SqlErrors.TransactionExpired.Message)
                        : ("57P05", deadline.IsPartial ? "incomplete message timeout exceeded" : "session idle timeout exceeded");
                    await state.DrainAsync(cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }
        }

        private async Task RefuseAsync(char type, int retryAfter, CancellationToken cancellationToken)
        {
            var refusing = !_rejectedUntilSync;
            _rejectedUntilSync = type switch
            {
                'S' => false,
                'Q' => _rejectedUntilSync,
                _ => true,
            };
            if (!refusing && type != 'S')
            {
                return;
            }

            await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var writer = new PgWriter(output);
                if (refusing)
                {
                    writer.Error("53300", $"tenant busy, retry after {retryAfter}s", fatal: false);
                }

                if (type is 'Q' or 'S')
                {
                    writer.Begin('Z');
                    writer.Raw(Status);
                    writer.End();
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writing.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _requests.Dispose();
            }

            base.Dispose(disposing);
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
