using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Regions;

internal sealed class SocketRequestRelay(IQosAdmission admission, IMemoryBudget memory, TimeProvider time)
{
    internal async Task RunAsync(HttpContext context, WebSocket client, WebSocket destination, CancellationToken cancellationToken)
    {
        var tenant = QosContext.Current ?? throw new InvalidOperationException("a relayed socket must have an admitted organization");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var turn = new SemaphoreSlim(1, 1);
        using var sending = new SemaphoreSlim(1, 1);
        using var exchange = new Exchange(time, stop);
        try
        {
            await Task.WhenAll(FromClientAsync(), FromDestinationAsync()).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException)
        {
            if (client.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                if (exchange.Pending)
                {
                    await HomeSocketRelay.RefuseAsync(context, client, exchange.Id, SqlErrors.WriteOutcomeUnknown,
                        "the database region did not answer; the statement or commit may have run there", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await client.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "the relayed session ended", cancellationToken).ConfigureAwait(false);
                }
            }
        }

        async Task FromClientAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var frame = await ReceiveAsync(client, tenant, stop.Token).ConfigureAwait(false);
                    if (frame.Type == WebSocketMessageType.Close)
                    {
                        await destination.CloseOutputAsync(client.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            client.CloseStatusDescription, stop.Token).ConfigureAwait(false);
                        return;
                    }

                    await turn.WaitAsync(stop.Token).ConfigureAwait(false);
                    var parsed = RequestJson.Read<SqlSocketRequest>(frame.Bytes.Span, SqlErrors.InvalidRequest);
                    var id = parsed is SqlSocketRequest request ? request.Id : null;
                    var lease = admission.TryEnter(tenant, out var retryAfter);
                    if (lease is null)
                    {
                        var busy = new SqlSocketError(id, "error", SqlBatch.Body(new SqlBatchFailure(
                            new SqlError(SqlErrors.TenantBusy, $"organization request budget exceeded; retry after {retryAfter}s"), 0), context.TraceIdentifier).Error, exchange.Transaction);
                        await sending.WaitAsync(stop.Token).ConfigureAwait(false);
                        try
                        {
                            await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(busy, SqlJsonContext.Default.SqlSocketError),
                                WebSocketMessageType.Text, true, stop.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            sending.Release();
                            turn.Release();
                        }
                        continue;
                    }

                    exchange.Begin(id, lease);
                    await destination.SendAsync(frame.Bytes, frame.Type, true, stop.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }

        async Task FromDestinationAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var frame = await ReceiveAsync(destination, tenant, stop.Token).ConfigureAwait(false);
                    await sending.WaitAsync(stop.Token).ConfigureAwait(false);
                    try
                    {
                        if (frame.Type == WebSocketMessageType.Close)
                        {
                            await client.CloseOutputAsync(destination.CloseStatus ?? WebSocketCloseStatus.EndpointUnavailable,
                                destination.CloseStatusDescription, stop.Token).ConfigureAwait(false);
                            return;
                        }

                        await client.SendAsync(frame.Bytes, frame.Type, true, stop.Token).ConfigureAwait(false);
                        using var answer = JsonDocument.Parse(frame.Bytes);
                        if (answer.RootElement.TryGetProperty("transaction", out var transaction))
                        {
                            exchange.Transaction = transaction.GetBoolean();
                        }
                        exchange.End();
                        turn.Release();
                    }
                    finally
                    {
                        sending.Release();
                    }
                }
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<Frame> ReceiveAsync(WebSocket socket, TenantContext tenant, CancellationToken cancellationToken)
    {
        var reservation = memory.TryReserve(SqlBatch.MaxBytes, tenant) ?? throw new QosMemoryException();
        var buffer = ArrayPool<byte>.Shared.Rent(SqlBatch.MaxBytes);
        try
        {
            var length = 0;
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(length, SqlBatch.MaxBytes - length), cancellationToken).ConfigureAwait(false);
                length += read.Count;
                if (read.EndOfMessage || read.MessageType == WebSocketMessageType.Close)
                {
                    return new Frame(buffer, length, read.MessageType, reservation);
                }

                if (length == SqlBatch.MaxBytes)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "a frame holds at most 1 MiB", cancellationToken).ConfigureAwait(false);
                    throw new WebSocketException("the relayed frame exceeds its bound");
                }
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            reservation.Dispose();
            throw;
        }
    }

    private sealed record Frame(byte[] Buffer, int Length, WebSocketMessageType Type, IMemoryReservation Reservation) : IDisposable
    {
        internal ReadOnlyMemory<byte> Bytes => Buffer.AsMemory(0, Length);

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Buffer);
            Reservation.Dispose();
        }
    }

    private sealed class Exchange(TimeProvider time, CancellationTokenSource stop) : IDisposable
    {
        private readonly Lock _gate = new();
        private IDisposable? _lease;
        private ITimer? _timer;
        internal bool Transaction { get; set; }
        internal long? Id { get; private set; }
        internal bool Pending { get { lock (_gate) { return _lease is not null; } } }

        internal void Begin(long? id, IDisposable lease)
        {
            lock (_gate)
            {
                (Id, _lease) = (id, lease);
                _timer = time.CreateTimer(_ => stop.Cancel(), null, SqlLimits.MaxRequestTime, Timeout.InfiniteTimeSpan);
            }
        }

        internal void End()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _lease?.Dispose();
                (_timer, _lease, Id) = (null, null, null);
            }
        }

        public void Dispose() => End();
    }
}
