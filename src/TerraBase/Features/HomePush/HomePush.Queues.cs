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
    private const int ReadAhead = 32;
    private const int MaxHints = 1024;
    private const int NoMessageFound = 10037;
    private static readonly TimeSpan HeldBackoffCeiling = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AppliedMargin = TimeSpan.FromHours(1);
    private static readonly TimeSpan DecideWait = TimeSpan.FromSeconds(10);

    private closed record class PushStep;

    private sealed record class PushDone(TenantContext Database, string Region, ulong Sequence, HomeApplyResult Result) : PushStep;

    private sealed record class PushPark(string Reason) : PushStep;

    private sealed record class PushWait(string Kind) : PushStep;

    private sealed record class PushAway : PushStep;

    private sealed record class PushSkip : PushStep;

    private readonly record struct Held(string Kind, ulong At);

    private sealed class ConsumeRun(string origin, CancellationTokenSource stopping)
    {
        internal string Origin => origin;
        internal string? Source { get; set; }
        internal PreviousFloor Previous { get; set; } = new(0, 0);
        internal HashSet<string> Marked { get; } = new(StringComparer.Ordinal);
        internal CancellationTokenSource Stopping => stopping;
        internal CancellationToken Token { get; } = stopping.Token;
        internal bool Ended { get; set; }
        internal HashSet<PushQueue> Active { get; } = [];
        internal TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PushQueue(TenantContext database, string filter, string key, ConsumeRun run)
    {
        internal TenantContext Database => database;
        internal string Filter => filter;
        internal string Key => key;
        internal ConsumeRun Run => run;
        internal string Origin => run.Origin;
        internal string Source => run.Source!;
        internal List<INatsJSMsg<byte[]>> Unacked { get; } = [];
        internal SortedSet<ulong> Hints { get; } = [];
        internal List<TaskCompletionSource> Waiters { get; } = [];
        internal bool Wake { get; set; }
        internal ulong? Cursor { get; set; }
        internal INatsJSStream? Stream { get; set; }
        internal long? HeldSince { get; set; }
        internal string? HeldKind { get; set; }
    }

    private Measurement<int>[] HeldCount()
    {
        lock (_queuesGate)
        {
            return [.. _queues.Values.Where(queue => queue.HeldKind is not null).GroupBy(queue => queue.HeldKind!, StringComparer.Ordinal)
                .Select(kind => new Measurement<int>(kind.Count(), new KeyValuePair<string, object?>("reason", kind.Key)))];
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

    internal async Task CatchUpAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var nats = _nats ?? throw new IOException("the push connection is not open");
        var reached = new List<Task>();
        foreach (var origin in (await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).Select(peer => peer.Region).Append(options.Region).Distinct(StringComparer.Ordinal))
        {
            reached.Add(Wake(nats, origin, database)?.Waiter.Task ?? throw new IOException("this node does not consume the push from " + origin));
        }

        await Task.WhenAll(reached).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private (TaskCompletionSource Waiter, PushQueue Queue)? Wake(NatsConnection nats, string origin, TenantContext database)
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PushQueue? started;
        PushQueue queue;
        lock (_queuesGate)
        {
            if (!_runs.TryGetValue(origin, out var run) || run.Ended)
            {
                return null;
            }

            queue = Attach(run, database, out started);
            queue.Wake = true;
            queue.Waiters.Add(waiter);
        }

        if (started is not null)
        {
            _ = DrainAsync(nats, started);
        }

        return (waiter, queue);
    }

    internal async Task<HomeApplyResult?> DecidedAsync(TenantContext database, RegionTransaction transaction, ulong? position, CancellationToken cancellationToken)
    {
        if (_nats is not { } nats || Wake(nats, transaction.Region!, database) is not { } woken)
        {
            return null;
        }

        var (waiter, queue) = woken;

        try
        {
            await waiter.Task.WaitAsync(DecideWait, time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or IOException)
        {
            lock (_queuesGate)
            {
                _ = queue.Waiters.Remove(waiter);
            }

            logger.ZLogInformation($"A transaction finished by a partner waits for its origin's sequencer: {error.GetType().Name:@Error}");
        }

        var source = queue.Source;

        return await apply.StateAsync(database, transaction.Id!, cancellationToken).ConfigureAwait(false) switch
        {
            null => null,
            { State: not WriteStates.Provisional } recorded => recorded,
            var unrecorded when position is { } at && source is not null && await apply.FrontierAsync(database, source, cancellationToken).ConfigureAwait(false) >= at => unrecorded,
            _ => null,
        };
    }

    internal async Task<IReadOnlyList<(string Origin, ulong Frontier, ulong Last)>> BehindAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var js = _js ?? throw new IOException("the push connection is not open");
        (string Origin, string Source)[] runs;
        lock (_queuesGate)
        {
            runs = [.. _runs.Values.Where(run => run.Source is not null).Select(run => (run.Origin, run.Source!))];
        }

        var behind = new List<(string, ulong, ulong)>();
        foreach (var (origin, source) in runs)
        {
            var last = (await js.GetStreamAsync(PushNames.Stream(origin), cancellationToken: cancellationToken).ConfigureAwait(false)).Info.State.LastSeq;
            var frontier = await apply.FrontierAsync(database, source, cancellationToken).ConfigureAwait(false) ?? 0;
            if (frontier < last)
            {
                behind.Add((origin, frontier, last));
            }
        }

        return behind;
    }

    private PushQueue Attach(ConsumeRun run, TenantContext database, out PushQueue? started)
    {
        var filter = PushNames.DatabaseFilter(run.Origin, database);
        var key = filter + "/" + run.Source;
        started = null;
        if (!_queues.TryGetValue(key, out var queue))
        {
            _queues[key] = queue = started = new PushQueue(database, filter, key, run);
            _ = run.Active.Add(queue);
        }

        return queue;
    }

    private async Task DispatchAsync(NatsConnection nats, ConsumeRun run, INatsJSMsg<byte[]> message)
    {
        var sequence = message.Metadata?.Sequence.Stream ?? throw new IOException("a pushed message has no stream sequence");
        if (PushNames.Database(message.Subject) is not { } database)
        {
            await ParkAsync(run.Origin, run.Source!, sequence, message.Subject, message.Data, "malformed", run.Token).ConfigureAwait(false);
            await message.AckAsync(cancellationToken: run.Token).AsTask().WaitAsync(AckWait).ConfigureAwait(false);
            return;
        }

        PushQueue? started;
        var ackNow = false;
        lock (_queuesGate)
        {
            if (run.Ended)
            {
                return;
            }

            var queue = Attach(run, database, out started);
            queue.Wake = true;
            if (queue.Hints.Count < MaxHints)
            {
                _ = queue.Hints.Add(sequence);
            }

            if (queue.HeldKind is not null)
            {
                ackNow = true;
            }
            else if (!queue.Unacked.Any(delivered => delivered.Metadata?.Sequence.Stream == sequence))
            {
                if (queue.Unacked.Count < HeldInFlight)
                {
                    queue.Unacked.Add(message);
                }
                else
                {
                    ackNow = true;
                }
            }
        }

        if (started is not null)
        {
            _ = DrainAsync(nats, started);
        }

        if (ackNow)
        {
            await message.AckAsync(cancellationToken: run.Token).AsTask().WaitAsync(AckWait).ConfigureAwait(false);
        }
    }

    private async Task EndRunAsync(ConsumeRun run)
    {
        lock (_queuesGate)
        {
            run.Ended = true;
            if (_runs.GetValueOrDefault(run.Origin) == run)
            {
                _ = _runs.Remove(run.Origin);
            }

            if (run.Active.Count == 0)
            {
                _ = run.Idle.TrySetResult();
            }
        }

        await run.Stopping.CancelAsync().ConfigureAwait(false);
        await run.Idle.Task.ConfigureAwait(false);
        run.Stopping.Dispose();
    }

    private void Retire(PushQueue queue)
    {
        bool owed;
        TaskCompletionSource[] waiting;
        lock (_queuesGate)
        {
            owed = queue.Wake || queue.Unacked.Count > 0;
            if (owed)
            {
                queue.Run.Ended = true;
            }

            waiting = [.. queue.Waiters];
            queue.Waiters.Clear();
            queue.Unacked.Clear();
            queue.Hints.Clear();
            queue.Wake = false;
            queue.HeldSince = null;
            queue.HeldKind = null;
            if (_queues.GetValueOrDefault(queue.Key) == queue)
            {
                _ = _queues.Remove(queue.Key);
            }
        }

        foreach (var waiter in waiting)
        {
            _ = waiter.TrySetException(new IOException("the sequencer stopped with its consumer run"));
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

    private async Task DrainAsync(NatsConnection nats, PushQueue queue)
    {
        var cancellationToken = queue.Run.Token;
        var backoff = new Backoff(TimeSpan.FromMilliseconds(100), HeldBackoffCeiling);
        try
        {
            while (true)
            {
                TaskCompletionSource[] waiting;
                lock (_queuesGate)
                {
                    if (!queue.Wake)
                    {
                        _ = _queues.Remove(queue.Key);
                        _ = queue.Run.Active.Remove(queue);
                        if (queue.Run.Ended && queue.Run.Active.Count == 0)
                        {
                            _ = queue.Run.Idle.TrySetResult();
                        }

                        return;
                    }

                    queue.Wake = false;
                    waiting = [.. queue.Waiters];
                    queue.Waiters.Clear();
                }

                Held held;
                try
                {
                    if (await SequenceAsync(nats, queue, cancellationToken).ConfigureAwait(false) is not { } waits)
                    {
                        Released(queue);
                        backoff.Reset();
                        foreach (var waiter in waiting)
                        {
                            _ = waiter.TrySetResult();
                        }

                        continue;
                    }

                    held = waits;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    held = new Held(RetryKind(error), queue.Cursor + 1 ?? 0);
                }

                lock (_queuesGate)
                {
                    queue.Wake = true;
                    queue.Waiters.InsertRange(0, waiting);
                }

                Hold(queue, held);
                _applyRetries?.Add(1, new KeyValuePair<string, object?>("reason", held.Kind));
                try
                {
                    await AckHeldAsync(queue, cancellationToken).ConfigureAwait(false);
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
            Retire(queue);
        }
        catch (Exception error)
        {
            logger.ZLogError($"A push queue ends its consumer run after an unexpected failure: {error.GetType().Name:@Error}");
            Retire(queue);
        }
    }

    private async Task<Held?> SequenceAsync(NatsConnection nats, PushQueue queue, CancellationToken cancellationToken)
    {
        var database = queue.Database;
        var cursor = queue.Cursor ?? 0;
        var home = false;
        switch (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false))
        {
            case Unavailable:
                return new Held("catalog", queue.Cursor + 1 ?? 0);
            case NotFound:
                switch (await catalog.DeletedAsync(database, cancellationToken).ConfigureAwait(false))
                {
                    case true:
                        break;
                    case false:
                        return new Held("unknown_database", queue.Cursor + 1 ?? 0);
                    case Unavailable:
                        return new Held("catalog", queue.Cursor + 1 ?? 0);
                }

                cursor = Math.Max(cursor, queue.Run.Previous.Floor);
                break;
            case TenantDatabaseRecord record when record.Placement.Home != options.Region:
                await AckThroughAsync(queue, ulong.MaxValue, cancellationToken).ConfigureAwait(false);
                queue.Cursor = null;
                return null;
            case TenantDatabaseRecord:
                if (await apply.FrontierAsync(database, queue.Source, cancellationToken).ConfigureAwait(false) is not { } frontier)
                {
                    return new Held("not_stored", queue.Cursor + 1 ?? 0);
                }

                cursor = Math.Max(frontier, queue.Run.Previous.Floor);
                home = true;
                Progressed(queue.Origin, database, cursor);
                break;
        }

        queue.Cursor = cursor;
        if (home)
        {
            var replayed = new List<PushDone>();
            foreach (var replay in Unacked(queue).Where(delivered => delivered.Metadata?.Sequence.Stream <= cursor))
            {
                var sequence = replay.Metadata!.Value.Sequence.Stream;
                switch (await ApplyAsync(queue, replay.Subject, sequence, replay.Data, cancellationToken).ConfigureAwait(false))
                {
                    case PushWait(var kind):
                        return new Held(kind, sequence);
                    case PushAway:
                        await AckThroughAsync(queue, ulong.MaxValue, cancellationToken).ConfigureAwait(false);
                        return null;
                    case PushDone done:
                        replayed.Add(done);
                        break;
                    case PushPark or PushSkip:
                        break;
                }
            }

            await AckThroughAsync(queue, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var done in replayed)
            {
                await NoticeAsync(nats, done, cancellationToken).ConfigureAwait(false);
            }
        }

        var backlog = false;
        while (true)
        {
            var (chain, ended, stop) = await ReadAsync(queue, cursor, backlog, home, cancellationToken).ConfigureAwait(false);
            if (stop is not null)
            {
                return new Held(stop, cursor + 1);
            }

            backlog = !ended;
            foreach (var stored in chain)
            {
                var position = new StreamSequence(queue.Source, stored.Seq);
                PushDone? applied = null;
                switch (await ApplyAsync(queue, stored.Subject, stored.Seq, stored.Data.ToArray(), cancellationToken).ConfigureAwait(false))
                {
                    case PushWait(var kind):
                        return new Held(kind, stored.Seq);
                    case PushAway:
                        await AckThroughAsync(queue, ulong.MaxValue, cancellationToken).ConfigureAwait(false);
                        queue.Cursor = null;
                        return null;
                    case PushPark(var reason):
                        await ParkAsync(queue.Origin, queue.Source, stored.Seq, stored.Subject, stored.Data.ToArray(), reason, cancellationToken).ConfigureAwait(false);
                        await apply.SkipAsync(database, position, cancellationToken).ConfigureAwait(false);
                        Progressed(queue.Origin, database, stored.Seq);
                        break;
                    case PushSkip:
                        await apply.SkipAsync(database, position, cancellationToken).ConfigureAwait(false);
                        Progressed(queue.Origin, database, stored.Seq);
                        break;
                    case PushDone done:
                        applied = done;
                        break;
                }

                cursor = stored.Seq;
                queue.Cursor = cursor;
                Released(queue);
                await AckThroughAsync(queue, cursor, cancellationToken).ConfigureAwait(false);
                if (applied is not null)
                {
                    await NoticeAsync(nats, applied, cancellationToken).ConfigureAwait(false);
                }
            }

            if (ended)
            {
                await AckThroughAsync(queue, ulong.MaxValue, cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
    }

    private async Task<(IReadOnlyList<StoredMessage> Chain, bool Ended, string? Stop)> ReadAsync(PushQueue queue, ulong cursor, bool backlog, bool home, CancellationToken cancellationToken)
    {
        var js = _js ?? throw new IOException("the push connection is not open");
        var stream = queue.Stream ??= await js.GetStreamAsync(PushNames.Stream(queue.Origin), cancellationToken: cancellationToken).ConfigureAwait(false);
        SortedSet<ulong> wanted;
        lock (_queuesGate)
        {
            wanted = [cursor + 1, .. queue.Hints.GetViewBetween(cursor + 1, ulong.MaxValue).Take(backlog ? ReadAhead : ReadAhead - 1).Select(hint => hint + 1)];
        }

        for (var ahead = cursor + 2; backlog && ahead <= cursor + ReadAhead; ahead++)
        {
            _ = wanted.Add(ahead);
        }

        ulong[] starts = [.. wanted];

        var reads = await Task.WhenAll(starts.Select(start => NextAsync(stream, queue.Filter, start, cancellationToken))).ConfigureAwait(false);
        var current = await js.GetStreamAsync(PushNames.Stream(queue.Origin), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (Source(current) != queue.Source)
        {
            lock (_queuesGate)
            {
                queue.Run.Ended = true;
            }

            await queue.Run.Stopping.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (current.Info.State.LastSeq < cursor)
        {
            return ([], true, "stream_behind");
        }

        if (home && await PurgedThroughAsync(stream, queue.Origin, queue.Database, cancellationToken).ConfigureAwait(false) > cursor)
        {
            return ([], true, "purged");
        }

        if (home && (current.Info.State.FirstSeq > 1 || current.Info.State.NumDeleted > 0)
            && !await OriginMarksAsync(stream, queue.Run, cancellationToken).ConfigureAwait(false))
        {
            return ([], true, "origin_previous_build");
        }

        var next = new Dictionary<ulong, StoredMessage?>(starts.Length);
        for (var i = 0; i < starts.Length; i++)
        {
            next[starts[i]] = reads[i];
        }

        var chain = new List<StoredMessage>();
        var at = cursor;
        while (next.TryGetValue(at + 1, out var stored))
        {
            if (stored is null)
            {
                return (chain, true, null);
            }

            chain.Add(stored);
            at = stored.Seq;
        }

        return (chain, false, null);
    }

    private static async Task<StoredMessage?> NextAsync(INatsJSStream stream, string filter, ulong start, CancellationToken cancellationToken)
    {
        try
        {
            return (await stream.GetAsync(new StreamMsgGetRequest { Seq = start, NextBySubj = filter }, cancellationToken).ConfigureAwait(false)).Message;
        }
        catch (NatsJSApiException error) when (error.Error.Code == 404 || error.Error.ErrCode == NoMessageFound)
        {
            return null;
        }
    }

    private async Task AckHeldAsync(PushQueue queue, CancellationToken cancellationToken)
    {
        INatsJSMsg<byte[]>[] delivered;
        lock (_queuesGate)
        {
            delivered = [.. queue.Unacked];
            queue.Unacked.Clear();
            _ = queue.Hints.RemoveWhere(hint => hint <= (queue.Cursor ?? 0));
        }

        foreach (var message in delivered)
        {
            await message.AckAsync(cancellationToken: cancellationToken).AsTask().WaitAsync(AckWait, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AckThroughAsync(PushQueue queue, ulong through, CancellationToken cancellationToken)
    {
        INatsJSMsg<byte[]>[] decided;
        lock (_queuesGate)
        {
            decided = [.. queue.Unacked.Where(delivered => delivered.Metadata?.Sequence.Stream <= through)];
            _ = queue.Unacked.RemoveAll(delivered => delivered.Metadata?.Sequence.Stream <= through);
            _ = queue.Hints.RemoveWhere(hint => hint <= through);
        }

        foreach (var delivered in decided)
        {
            await delivered.AckAsync(cancellationToken: cancellationToken).AsTask().WaitAsync(AckWait, cancellationToken).ConfigureAwait(false);
        }
    }

    private INatsJSMsg<byte[]>[] Unacked(PushQueue queue)
    {
        lock (_queuesGate)
        {
            return [.. queue.Unacked];
        }
    }

    private void Hold(PushQueue queue, Held held)
    {
        lock (_queuesGate)
        {
            if (queue.HeldKind == held.Kind)
            {
                return;
            }

            queue.HeldSince ??= time.GetTimestamp();
            queue.HeldKind = held.Kind;
        }

        _holds?.Add(1, new KeyValuePair<string, object?>("reason", held.Kind));
        logger.ZLogWarning($"A pushed database is held: {queue.Database.DatabaseId:@Database} from {queue.Origin:@Origin} at stream sequence {held.At:@Sequence} cannot apply yet: {held.Kind:@Kind}");
    }

    private void Released(PushQueue queue)
    {
        string? kind;
        lock (_queuesGate)
        {
            kind = queue.HeldSince is null ? null : queue.HeldKind;
            queue.HeldSince = null;
            queue.HeldKind = null;
        }

        if (kind is not null)
        {
            logger.ZLogWarning($"A held pushed database is released: {queue.Database.DatabaseId:@Database} from {queue.Origin:@Origin} at stream sequence {queue.Cursor ?? 0:@Sequence} after {kind:@Kind}");
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

    private async Task NoticeAsync(NatsConnection nats, PushDone done, CancellationToken cancellationToken)
    {
        if (done.Result.State == WriteStates.Provisional)
        {
            Progressed(done.Region, done.Database, done.Sequence);
            return;
        }

        await nats.PublishAsync(PushNames.Applied(done.Region, done.Database),
            JsonSerializer.SerializeToUtf8Bytes(done.Result, HomeApplyJsonContext.Default.HomeApplyResult), cancellationToken: cancellationToken).ConfigureAwait(false);
        Progressed(done.Region, done.Database, done.Sequence);
        _applies?.Add(1, new KeyValuePair<string, object?>("state", done.Result.State));
    }

    private async Task<PushStep> ApplyAsync(PushQueue queue, string subject, ulong sequence, byte[]? payload, CancellationToken cancellationToken)
    {
        if (PushNames.Database(subject) is not { } database)
        {
            return new PushPark("malformed");
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
                return new PushAway();
            case TenantDatabaseRecord:
                break;
        }

        if (payload is not { Length: > 0 } data)
        {
            return new PushPark("empty");
        }

        var seen = sequence <= queue.Run.Previous.Last;
        if (await DecodeAsync(database, data, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return seen && await ParkedBeforeAsync(_js ?? throw new IOException("the push connection is not open"), queue.Origin, database, sequence, cancellationToken)
                .ConfigureAwait(false)
                ? new PushSkip()
                : new PushWait("undecodable");
        }

        if (seen && Ulid.TryParse(transaction.Id, out var created) && created.Time < time.GetUtcNow() - HomeApplier.KeptApplied + AppliedMargin)
        {
            switch (await apply.StateAsync(database, transaction.Id!, cancellationToken).ConfigureAwait(false))
            {
                case null:
                    return new PushWait("not_stored");
                case { State: WriteStates.Provisional }:
                    return new PushWait("upgrade_ambiguous");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        await _applying.WaitAsync(cancellationToken).ConfigureAwait(false);
        HomeApplyResult? result;
        try
        {
            result = await apply.ApplyAsync(database, transaction, new StreamSequence(queue.Source, sequence), cancellationToken).ConfigureAwait(false);
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

        return new PushDone(database, transaction.Region!, sequence, result);
    }
}
