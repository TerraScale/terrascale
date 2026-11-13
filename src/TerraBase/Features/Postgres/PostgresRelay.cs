using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using TerraBase.Tenancy;

namespace TerraBase.Features.Postgres;

internal sealed class PostgresRelay(IHttpClientFactory http, InternalCredential credential, IQosAdmission admission, TimeProvider time, IHomeLocator locator)
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

    internal async Task RunAsync(HomeNode home, TenantContext tenant, byte[] startup, IPAddress? client, PipeReader input, PipeWriter output, IDisposable admissionLease, ProxyRequestBudget budget, CancellationToken closed)
    {
        using var admitted = admissionLease;
        var remainingAttempts = 2;
        while (remainingAttempts-- > 0)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(closed);
            using var exchange = new PostgresRelayAdmission(tenant, admission, time, stop);
            using var writing = new SemaphoreSlim(1, 1);
            using var handshake = budget.Link(closed);
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, handshake.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(home.Address, Path))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new Upstream(startup, input, output, writing, exchange),
            };
            request.Headers.TryAddWithoutValidation(ProxyRequestBudget.RemainingHeader, budget.Milliseconds);
            credential.Sign(request.Headers);
            request.Headers.TryAddWithoutValidation(ClientHeader, (client ?? IPAddress.None).ToString());
            HttpResponseMessage response;
            try
            {
                response = await http.CreateClient(HomeLocator.HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, opening.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !closed.IsCancellationRequested)
            {
                if (exchange.RetryAfter is { } retryAfter)
                {
                    var writer = new PgWriter(output);
                    writer.Error("53300", $"tenant busy, retry after {retryAfter}s", fatal: true);
                    await writer.FlushAsync(closed).ConfigureAwait(false);
                }
                else
                {
                    await RefuseAsync(output, home, closed).ConfigureAwait(false);
                }
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
                var buffer = ArrayPool<byte>.Shared.Rent(SqlBatch.MaxBytes);
                try
                {
                    var receiving = opening.Token;
                    var first = 0;
                    while (first < CleartextRequest.Length)
                    {
                        var read = await downstream.ReadAsync(buffer.AsMemory(first, CleartextRequest.Length - first), receiving).ConfigureAwait(false);
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

                    var header = new byte[5];
                    while (true)
                    {
                        await downstream.ReadExactlyAsync(header, receiving).ConfigureAwait(false);
                        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;
                        if (length is < 0 or > SqlBatch.MaxBytes)
                        {
                            throw new InvalidDataException("the destination returned an oversized Postgres message");
                        }

                        await downstream.ReadExactlyAsync(buffer.AsMemory(0, length), receiving).ConfigureAwait(false);
                        if (remainingAttempts > 0 && !exchange.Ready.IsCompleted && header[0] == 'E'
                            && PlacementMiss(buffer.AsSpan(0, length)))
                        {
                            var fresh = await locator.RefreshAsync(tenant.ProjectId!, tenant.DatabaseId, receiving).ConfigureAwait(false);
                            if (fresh is not null && fresh.Region != home.Region)
                            {
                                home = fresh;
                                break;
                            }
                        }
                        await writing.WaitAsync(stop.Token).ConfigureAwait(false);
                        try
                        {
                            await output.WriteAsync(header, stop.Token).ConfigureAwait(false);
                            await output.WriteAsync(buffer.AsMemory(0, length), stop.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            writing.Release();
                        }

                        if (header[0] == 'Z')
                        {
                            admitted.Dispose();
                            exchange.Status = buffer[0];
                            exchange.End();
                            exchange.Open();
                            handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                            receiving = stop.Token;
                        }
                    }
                }
                catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !closed.IsCancellationRequested)
                {
                    var writer = new PgWriter(output);
                    writer.Error(exchange.RetryAfter is not null ? "53300" : exchange.Pending ? "08007" : "08006", exchange.RetryAfter is { } retryAfter
                        ? $"tenant busy, retry after {retryAfter}s" : exchange.Pending
                        ? "write outcome unknown: the statement or commit may have run in the database region"
                        : "the relayed session ended; its open transaction was rolled back", fatal: true);
                    await writer.FlushAsync(closed).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    await stop.CancelAsync().ConfigureAwait(false);
                }
            }
        }
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

    private sealed class Upstream(byte[] startup, PipeReader input, PipeWriter output, SemaphoreSlim writing, PostgresRelayAdmission exchange) : HttpContent
    {
        private bool _rejectedUntilSync;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(startup, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await exchange.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var read = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                var remaining = read.Buffer;
                while (remaining.Length >= 5)
                {
                    var header = remaining.Slice(0, 5).ToArray();
                    var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                    if (length is < 4 or > SqlBatch.MaxBytes)
                    {
                        throw new InvalidDataException("a Postgres message exceeds its bound");
                    }

                    if (remaining.Length < length + 1)
                    {
                        break;
                    }

                    if (_rejectedUntilSync)
                    {
                        if (header[0] == 'S')
                        {
                            await BusyAsync(readyOnly: true, cancellationToken).ConfigureAwait(false);
                            _rejectedUntilSync = false;
                        }
                        remaining = remaining.Slice(length + 1);
                        continue;
                    }
                    if (!await exchange.EnterAsync((char)header[0], cancellationToken).ConfigureAwait(false))
                    {
                        _rejectedUntilSync = header[0] != 'Q';
                        await BusyAsync(readyOnly: false, cancellationToken).ConfigureAwait(false);
                        remaining = remaining.Slice(length + 1);
                        continue;
                    }
                    foreach (var segment in remaining.Slice(0, length + 1))
                    {
                        await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                    }
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    remaining = remaining.Slice(length + 1);
                }

                input.AdvanceTo(remaining.Start, remaining.End);
                if (read.IsCompleted || read.IsCanceled)
                {
                    return;
                }
            }
        }

        private async Task BusyAsync(bool readyOnly, CancellationToken cancellationToken)
        {
            await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var writer = new PgWriter(output);
                if (!readyOnly)
                {
                    writer.Error("53300", $"tenant busy, retry after {exchange.RetryAfter}s", fatal: false);
                }
                if (readyOnly || !_rejectedUntilSync)
                {
                    writer.Begin('Z');
                    writer.Raw(exchange.Status);
                    writer.End();
                }
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writing.Release();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
