using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sync;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.HomePush;

internal partial class HomePush
{
    private const int HeldInFlight = 32;
    private const int NoMessageFound = 10037;
    private static readonly TimeSpan HeldBackoffCeiling = TimeSpan.FromSeconds(60);

    private closed record class PushItem(string Subject, ulong Sequence, byte[]? Data);

    private sealed record class Delivered(INatsJSMsg<byte[]> Message, ulong Sequence) : PushItem(Message.Subject, Sequence, Message.Data);

    private sealed record class Reread(string Subject, ulong Sequence, byte[]? Data) : PushItem(Subject, Sequence, Data);

    private closed record class PushStep;

    private sealed record class PushDone : PushStep;

    private sealed record class PushPark(string Reason) : PushStep;

    private sealed record class PushWait(string Kind) : PushStep;

    private sealed class ConsumeRun(CancellationTokenSource stopping)
    {
        internal CancellationTokenSource Stopping => stopping;
        internal CancellationToken Token { get; } = stopping.Token;
        internal bool Ended { get; set; }
        internal HashSet<PushQueue> Active { get; } = [];
        internal TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PushQueue(string filter, string origin, string source, ConsumeRun run)
    {
        internal string Filter => filter;
        internal string Origin => origin;
        internal string Source => source;
        internal ConsumeRun Run => run;
        internal Queue<PushItem> Items { get; } = new();
        internal ulong? ReadFrom { get; set; }
        internal ulong HandedBack { get; set; }
        internal long? HeldSince { get; set; }
        internal string? HeldKind { get; set; }
    }

    private int HeldCount()
    {
        lock (_queuesGate)
        {
            return _queues.Values.Count(queue => queue.HeldSince is not null);
        }
    }

    private double OldestHeld()
    {
        lock (_queuesGate)
        {
            return _queues.Values.Select(queue => queue.HeldSince).OfType<long>()
                .Select(since => time.GetElapsedTime(since).TotalSeconds).DefaultIfEmpty(0).Max();
        }
    }

    private async Task DispatchAsync(NatsConnection nats, ConsumeRun run, INatsJSMsg<byte[]> message, string source)
    {
        var sequence = message.Metadata?.Sequence.Stream ?? throw new IOException("a pushed message has no stream sequence");
        var filter = PushNames.QueueFilter(message.Subject);
        var key = filter + "/" + source;
        PushQueue? started = null;
        var handBack = false;
        lock (_queuesGate)
        {
            if (run.Ended)
            {
                return;
            }

            if (!_queues.TryGetValue(key, out var queue))
            {
                _queues[key] = queue = started = new PushQueue(filter, PushNames.Origin(message.Subject) ?? "unknown", source, run);
                _ = run.Active.Add(queue);
            }

            if (queue.Items.Any(item => item.Sequence == sequence))
            {
                return;
            }

            if (queue.ReadFrom is null && queue.Items.Count < HeldInFlight)
            {
                queue.Items.Enqueue(new Delivered(message, sequence));
            }
            else
            {
                queue.ReadFrom = Math.Min(queue.ReadFrom ?? sequence, sequence);
                queue.HandedBack = Math.Max(queue.HandedBack, sequence);
                handBack = true;
            }
        }

        if (started is not null)
        {
            _ = DrainAsync(nats, key, started);
        }

        if (handBack)
        {
            await message.AckAsync(cancellationToken: run.Token).ConfigureAwait(false);
        }
    }

    private async Task EndRunAsync(ConsumeRun run)
    {
        lock (_queuesGate)
        {
            run.Ended = true;
            if (run.Active.Count == 0)
            {
                _ = run.Idle.TrySetResult();
            }
        }

        await run.Stopping.CancelAsync().ConfigureAwait(false);
        await run.Idle.Task.ConfigureAwait(false);
        run.Stopping.Dispose();
    }

    private void Retire(string key, PushQueue queue)
    {
        bool owed;
        lock (_queuesGate)
        {
            owed = queue.Items.Count > 0 || queue.ReadFrom is not null;
            if (owed)
            {
                queue.Run.Ended = true;
            }

            queue.Items.Clear();
            queue.ReadFrom = null;
            queue.HeldSince = null;
            queue.HeldKind = null;
            if (_queues.GetValueOrDefault(key) == queue)
            {
                _ = _queues.Remove(key);
            }
        }

        if (owed)
        {
            queue.Run.Stopping.Cancel();
        }

        lock (_queuesGate)
        {
            _ = queue.Run.Active.Remove(queue);
            if (queue.Run.Ended && queue.Run.Active.Count == 0)
            {
                _ = queue.Run.Idle.TrySetResult();
            }
        }
    }

    private async Task DrainAsync(NatsConnection nats, string key, PushQueue queue)
    {
        var cancellationToken = queue.Run.Token;
        var backoff = new Backoff(TimeSpan.FromMilliseconds(100), HeldBackoffCeiling);
        try
        {
            while (true)
            {
                PushItem? head;
                ulong? readFrom;
                ulong handedBack;
                lock (_queuesGate)
                {
                    if (queue.Items.Count == 0 && queue.ReadFrom is null)
                    {
                        return;
                    }

                    head = queue.Items.TryPeek(out var next) ? next : null;
                    readFrom = queue.ReadFrom;
                    handedBack = queue.HandedBack;
                }

                string kind;
                try
                {
                    if (head is null)
                    {
                        await RereadAsync(queue, readFrom!.Value, handedBack, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (await ApplyHeadAsync(nats, queue, head, cancellationToken).ConfigureAwait(false) is not { } waiting)
                    {
                        Released(queue, head);
                        backoff.Reset();
                        continue;
                    }

                    kind = waiting;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    kind = RetryKind(error);
                }

                Held(queue, head?.Sequence ?? readFrom!.Value, kind);
                _applyRetries?.Add(1, new KeyValuePair<string, object?>("reason", kind));
                try
                {
                    foreach (var delivered in Waiting(queue))
                    {
                        await delivered.Message.AckProgressAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _applyRetries?.Add(1, new KeyValuePair<string, object?>("reason", RetryKind(error)));
                }

                await Task.Delay(backoff.Next(), time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.ZLogInformation($"A push queue stops with its consumer run");
        }
        catch (Exception error)
        {
            logger.ZLogError($"A push queue ends its consumer run after an unexpected failure: {error.GetType().Name:@Error}");
        }
        finally
        {
            Retire(key, queue);
        }
    }

    private async Task<string?> ApplyHeadAsync(NatsConnection nats, PushQueue queue, PushItem head, CancellationToken cancellationToken)
    {
        switch (await ApplyAsync(nats, queue, head, cancellationToken).ConfigureAwait(false))
        {
            case PushWait(var waiting):
                return waiting;
            case PushPark(var reason):
                await ParkAsync(queue, head, reason, cancellationToken).ConfigureAwait(false);
                break;
            case PushDone:
                break;
        }

        if (head is Delivered delivered)
        {
            await delivered.Message.AckAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private Delivered[] Waiting(PushQueue queue)
    {
        lock (_queuesGate)
        {
            return [.. queue.Items.OfType<Delivered>()];
        }
    }

    private void Held(PushQueue queue, ulong sequence, string kind)
    {
        lock (_queuesGate)
        {
            if (queue.HeldSince is not null)
            {
                return;
            }

            queue.HeldSince = time.GetTimestamp();
            queue.HeldKind = kind;
        }

        logger.ZLogWarning($"A pushed database is held: {PushNames.Database(queue.Filter)?.DatabaseId:@Database} from {queue.Origin:@Origin} at stream sequence {sequence:@Sequence} cannot apply yet: {kind:@Kind}");
    }

    private void Released(PushQueue queue, PushItem head)
    {
        string? kind;
        lock (_queuesGate)
        {
            _ = queue.Items.Dequeue();
            kind = queue.HeldSince is null ? null : queue.HeldKind;
            queue.HeldSince = null;
            queue.HeldKind = null;
        }

        if (kind is not null)
        {
            logger.ZLogWarning($"A held pushed database is released: {PushNames.Database(head.Subject)?.DatabaseId:@Database} from {queue.Origin:@Origin} at stream sequence {head.Sequence:@Sequence} after {kind:@Kind}");
        }
    }

    private async Task RereadAsync(PushQueue queue, ulong from, ulong handedBack, CancellationToken cancellationToken)
    {
        var js = _js ?? throw new IOException("the push connection is not open");
        var stream = await js.GetStreamAsync(PushNames.Stream(queue.Origin), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (Source(stream) != queue.Source)
        {
            lock (_queuesGate)
            {
                queue.Run.Ended = true;
            }

            await queue.Run.Stopping.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var next = from;
        for (var read = 0; read < HeldInFlight; read++)
        {
            StoredMessage? stored;
            try
            {
                stored = (await stream.GetAsync(new StreamMsgGetRequest { Seq = next, NextBySubj = queue.Filter }, cancellationToken).ConfigureAwait(false)).Message;
            }
            catch (NatsJSApiException error) when (error.Error.Code == 404 || error.Error.ErrCode == NoMessageFound)
            {
                stored = null;
            }

            lock (_queuesGate)
            {
                if (stored is null)
                {
                    queue.ReadFrom = queue.HandedBack > handedBack ? next : null;
                    return;
                }

                queue.Items.Enqueue(new Reread(stored.Subject, stored.Seq, stored.Data.ToArray()));
                next = stored.Seq + 1;
                queue.ReadFrom = next;
            }
        }
    }

    private static string RetryKind(Exception error) => error switch
    {
        SqlStatementException or StorageBusyException => "engine",
        AmazonS3Exception or HttpRequestException => "object_store",
        CryptographicException => "crypto",
        NatsException => "broker",
        OperationCanceledException => "cancelled",
        ObjectDisposedException => "disposed",
        IOException or TimeoutException => "io",
        _ => "other",
    };

    private async Task<PushStep> ApplyAsync(NatsConnection nats, PushQueue queue, PushItem item, CancellationToken cancellationToken)
    {
        if (PushNames.Database(item.Subject) is not { } database)
        {
            return new PushPark("malformed");
        }

        if (item.Data is not { Length: > 0 } data)
        {
            return new PushPark("empty");
        }

        switch (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false))
        {
            case Unavailable:
                return new PushWait("catalog");
            case NotFound:
                return await catalog.DeletedAsync(database, cancellationToken).ConfigureAwait(false) switch
                {
                    true => new PushPark("deleted_database"),
                    false => new PushWait("unknown_database"),
                    Unavailable => new PushWait("catalog"),
                };
            case TenantDatabaseRecord record when record.Placement.Home != options.Region:
                return new PushDone();
            case TenantDatabaseRecord:
                break;
        }

        if (await DecodeAsync(database, data, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return new PushWait("undecodable");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        await _applying.WaitAsync(cancellationToken).ConfigureAwait(false);
        HomeApplyResult? result;
        try
        {
            result = await apply.ApplyAsync(database, transaction, new StreamSequence(queue.Source, item.Sequence), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applyTime?.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            _applying.Release();
        }

        if (result is null)
        {
            return new PushWait("not_stored");
        }

        await nats.PublishAsync(PushNames.Applied(transaction.Region!, database),
            JsonSerializer.SerializeToUtf8Bytes(result, HomeApplyJsonContext.Default.HomeApplyResult), cancellationToken: cancellationToken).ConfigureAwait(false);
        Progressed(transaction.Region!, database, item.Sequence);
        _applies?.Add(1, new KeyValuePair<string, object?>("state", result.State));
        return new PushDone();
    }
}
