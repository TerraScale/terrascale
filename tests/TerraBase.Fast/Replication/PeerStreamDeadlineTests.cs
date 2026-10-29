using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The peer streams' progress deadline over real listeners: each side ends a stream whose other side stays silent for
/// <see cref="ReplicationProtocol.ProgressTimeout"/> (a half-open connection, or a peer that stopped reading, so writes to it
/// stall), while an idle link, kept alive by heartbeats and their empty answers, stays connected. The side under test reads
/// an <see cref="ObservedTime"/>; the test moves it only once the timer it means to fire exists.
/// </summary>
public sealed class PeerStreamDeadlineTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    private static readonly string[] ReplicaRegions = ["sjc"];

    [Test]
    public async Task The_receiver_answers_heartbeats_past_the_deadline_and_ends_a_silent_stream()
    {
        var time = new ObservedTime();
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru", services =>
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreamReceiver>(provider, time)));
        using var channel = PeerChannel.Create(api.Server.BaseAddress, api.Server.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = ReplicaId.Of(1, 7, 0).Value, Region = "sjc" } });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Deadline)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();

        // Idle for three deadlines in all, a heartbeat each second before one ends: every heartbeat gets its empty answer.
        for (var round = 0; round < 3; round++)
        {
            time.Advance(ReplicationProtocol.ProgressTimeout - Idle);
            await call.RequestStream.WriteAsync(new PushFrame { Heartbeat = new Heartbeat() });
            await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Deadline)).IsTrue();
            await Assert.That(call.ResponseStream.Current.KindCase).IsEqualTo(AckFrame.KindOneofCase.None);
        }

        // The receiver moves its deadline before it answers, so one more deadline of silence ends the stream.
        time.Advance(ReplicationProtocol.ProgressTimeout);
        await Assert.That(await EndsAsync(call.ResponseStream)).IsTrue();
    }

    [Test]
    public async Task The_receiver_ends_a_stream_that_never_says_hello()
    {
        var time = new ObservedTime();
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru", services =>
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreamReceiver>(provider, time)));
        using var channel = PeerChannel.Create(api.Server.BaseAddress, api.Server.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();

        // The receiver's deadline is the only timer on its clock: once it exists, the call reached the receiver.
        await time.UntilTimerAsync(ReplicationProtocol.ProgressTimeout, after: 0);
        time.Advance(ReplicationProtocol.ProgressTimeout);
        await Assert.That(await EndsAsync(call.ResponseStream)).IsTrue();
    }

    [Test]
    public async Task The_sender_keeps_an_idle_link_past_the_deadline_on_one_stream()
    {
        var time = new ObservedTime();
        await using var peer = await FakePeer.StartAsync(silentStreams: 0);
        var (source, bindSource) = CreateSource();
        await using var api = await StartSenderAsync(peer, source, bindSource, time);

        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        // The heartbeat uses one periodic timer; each answer resets the silence timer before the next clock advance.
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        var started = time.GetUtcNow();
        while (time.GetUtcNow() - started < 2 * ReplicationProtocol.ProgressTimeout)
        {
            var changes = time.Changes;
            time.Advance(Idle);
            await Assert.That((await peer.NextAsync()).KindCase).IsEqualTo(PushFrame.KindOneofCase.Heartbeat);
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
        }

        await Assert.That(peer.Streams).IsEqualTo(1);
    }

    [Test]
    public async Task The_sender_ends_an_idle_stream_whose_peer_reads_heartbeats_but_never_answers_and_reconnects()
    {
        var time = new ObservedTime();
        var warnings = new PeerWarnings(time);
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, unansweredStreams: 1);
        var (source, bindSource) = CreateSource();
        await using var api = await StartSenderAsync(peer, source, bindSource, time, warnings: warnings);

        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        var started = time.GetUtcNow();
        // No databases or batches: reads keep the request window open, but only replies can reset sender silence.
        time.Advance(Idle);
        await Assert.That((await peer.NextAsync()).KindCase).IsEqualTo(PushFrame.KindOneofCase.Heartbeat);
        await Assert.That(peer.FirstEnded.Task.IsCompleted).IsFalse();
        await Assert.That(peer.Streams).IsEqualTo(1);
        time.Advance(ReplicationProtocol.ProgressTimeout - Idle);
        await peer.FirstEnded.Task.WaitAsync(Deadline);
        await Assert.That(time.GetUtcNow() - started).IsEqualTo(ReplicationProtocol.ProgressTimeout);
        var failure = await warnings.Failure.Task.WaitAsync(Deadline);
        await Assert.That(failure).IsTypeOf<TimeoutException>();
        await Assert.That(failure!.Message).IsEqualTo("the peer did not answer in time");
        var timers = await warnings.BackoffBoundary.Task.WaitAsync(Deadline);
        await time.UntilTimerAsync(Idle, timers, period: Timeout.InfiniteTimeSpan, createdAt: started + ReplicationProtocol.ProgressTimeout);
        time.Advance(Idle);
        // Heartbeats read during the clock advance can precede the replacement, but no database frame may do so.
        var replacement = await peer.NextAsync();
        while (replacement.Heartbeat is not null)
        {
            replacement = await peer.NextAsync();
        }

        await Assert.That(replacement.Hello).IsNotNull();
        await Assert.That(peer.Streams).IsEqualTo(2);
    }

    [Test]
    public async Task The_sender_ends_a_stream_whose_batch_write_is_blocked_by_flow_control_and_reconnects()
    {
        var time = new ObservedTime();
        await using var peer = await FakePeer.StartAsync(silentStreams: 1);
        var (source, bindSource) = CreateSource();
        await using var api = await StartSenderAsync(peer, source, bindSource, time);
        var database = await CreateDatabaseAsync(api);
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();

        // The first stream welcomes, then reads nothing: a batch far larger than its HTTP/2 window fills the window, and the
        // sender's write of it cannot finish.
        var node = api.Server.Services.GetRequiredService<LocalReplica>().Id;
        _ = source.Add(database, Incompressible(node, 512 * 1024));
        await peer.Stalled.Task.WaitAsync(Deadline);

        var timers = time.Timers;
        time.Advance(ReplicationProtocol.ProgressTimeout);
        await peer.FirstEnded.Task.WaitAsync(Deadline);
        // The reconnect waits its backoff: the first idle timer since the deadline fired.
        await time.UntilTimerAsync(Idle, timers);
        time.Advance(Idle);
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await Assert.That(peer.Streams).IsEqualTo(2);
    }

    [Test]
    public async Task An_unacknowledged_batch_expires_while_heartbeat_writing_is_blocked_despite_a_delayed_stable_reply()
    {
        var time = new ObservedTime();
        var warnings = new PeerWarnings(time, streamFailuresOnly: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, frontierRelease: release);
        var (source, bindSource) = CreateSource();
        var replica = ReplicaId.Of(1, 9, 0);
        TenantContext[] databases = [];
        await using var api = await StartSenderAsync(peer, source, bindSource, time, async api =>
        {
            databases = await CreateDatabasesAsync(api, 2);
            var node = api.Server.Services.GetRequiredService<LocalReplica>().Id;
            // The first database has no unsent groups, so its Frontier precedes the second database's large Batch.
            var head = source.Add(databases[0], Incompressible(node, 32));
            await Assert.That(await source.AckAsync(databases[0], await IncarnationAsync(api, databases[0]), replica, head, default, CancellationToken.None)).IsTrue();
            _ = source.Add(databases[1], Incompressible(node, 512 * 1024));
        }, warnings: warnings);
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await Assert.That((await peer.NextAsync()).Frontier.Database).IsEqualTo(ChangeGroups.Wire(databases[0], await IncarnationAsync(api, databases[0])));
        await peer.Stalled.Task.WaitAsync(Deadline);
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);

        // The unread Batch fills the HTTP/2 window and holds the writer; the heartbeat tick cannot write past it.
        var started = time.GetUtcNow();
        time.Advance(ReplicationProtocol.ProgressTimeout - Idle);
        var changes = time.Changes;
        release.SetResult();
        // Receipt of the earlier Frontier's Stable resets silence, but cannot acknowledge the blocked Batch.
        await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
        await Assert.That(peer.FirstEnded.Task.IsCompleted).IsFalse();
        await Assert.That(source.AckedBy(databases[1], replica)).IsEqualTo(0);

        time.Advance(Idle);
        await peer.FirstEnded.Task.WaitAsync(Deadline);
        await Assert.That(time.GetUtcNow() - started).IsEqualTo(ReplicationProtocol.ProgressTimeout);
        await Assert.That(source.AckedBy(databases[1], replica)).IsEqualTo(0);
        await Assert.That(peer.Streams).IsEqualTo(1);
        var failure = await warnings.Failure.Task.WaitAsync(Deadline);
        await Assert.That(failure).IsTypeOf<TimeoutException>();
        await Assert.That(failure!.Message).IsEqualTo("the peer did not acknowledge a batch in time");
        var timers = await warnings.BackoffBoundary.Task.WaitAsync(Deadline);
        await time.UntilTimerAsync(Idle, timers, period: Timeout.InfiniteTimeSpan,
            createdAt: started + ReplicationProtocol.ProgressTimeout);
        time.Advance(Idle);
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await Assert.That(peer.Streams).IsEqualTo(2);
    }

    [Test]
    public async Task The_sender_keeps_heartbeats_flowing_while_a_local_source_read_is_held_past_the_deadline()
    {
        var time = new ObservedTime();
        await using var peer = await FakePeer.StartAsync(silentStreams: 0);
        var (source, bindSource) = CreateSource();
        var held = new HeldSource(source);
        await using var api = await StartSenderAsync(peer, held, bindSource, time);
        var database = await CreateDatabaseAsync(api);
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        _ = source.Add(database, Incompressible(api.Server.Services.GetRequiredService<LocalReplica>().Id, 32));
        await held.Entered.Task.WaitAsync(Deadline);

        for (var round = 0; round < 30; round++)
        {
            var changes = time.Changes;
            time.Advance(Idle);
            await Assert.That((await peer.NextAsync()).KindCase).IsEqualTo(PushFrame.KindOneofCase.Heartbeat);
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
        }

        await Assert.That(peer.Streams).IsEqualTo(1);
        await Assert.That(source.AckedBy(database, ReplicaId.Of(1, 9, 0))).IsEqualTo(0);
        await Assert.That(held.Release.Task.IsCompleted).IsFalse();
    }

    [Test]
    public async Task A_held_cold_cursor_does_not_hold_back_a_later_databases_batch_or_ack()
    {
        var time = new ObservedTime();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        answered.SetResult();
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, batchRelease: answered);
        var (source, bindSource) = CreateSource();
        var held = new HeldSource(source) { HoldCursor = true };
        TenantContext[] databases = [];
        await using var api = await StartSenderAsync(peer, held, bindSource, time, async api =>
        {
            databases = await CreateDatabasesAsync(api, 2);
            held.Database = databases[0];
            var node = api.Server.Services.GetRequiredService<LocalReplica>().Id;
            foreach (var database in databases)
            {
                _ = source.Add(database, Incompressible(node, 32));
            }
        });
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await held.Entered.Task.WaitAsync(Deadline);
        await Assert.That(await held.OtherCursorReady.Task.WaitAsync(Deadline)).IsEqualTo(databases[1]);
        var healthy = await peer.NextAsync();
        await Assert.That(healthy.Batch.Database).IsEqualTo(ChangeGroups.Wire(databases[1], await IncarnationAsync(api, databases[1])));
        using var deadline = new CancellationTokenSource(Deadline);
        await source.UntilAckedAsync(databases[1], ReplicaId.Of(1, 9, 0), (long)healthy.Batch.LastChangeId, deadline.Token);
        await Assert.That(source.AckedBy(databases[0], ReplicaId.Of(1, 9, 0))).IsEqualTo(0);
        await Assert.That(held.Release.Task.IsCompleted).IsFalse();
        await Assert.That(peer.Streams).IsEqualTo(1);

        held.Release.SetResult();
        await held.CursorReady.Task.WaitAsync(Deadline);
        var expected = ChangeGroups.Wire(databases[0], await IncarnationAsync(api, databases[0]));
        PushFrame resumed;
        do
        {
            resumed = await peer.NextAsync();
        }
        while (resumed.Batch?.Database.Equals(expected) is not true);
        await source.UntilAckedAsync(databases[0], ReplicaId.Of(1, 9, 0), (long)resumed.Batch.LastChangeId, deadline.Token);
        await Assert.That(peer.Streams).IsEqualTo(1);
    }

    [Test]
    public async Task The_sender_bounds_delayed_database_responses_and_keeps_heartbeats_live_during_a_later_held_read()
    {
        var time = new ObservedTime();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, batchRelease: release);
        var (source, bindSource) = CreateSource();
        var held = new HeldSource(source);
        TenantContext[] databases = [];
        await using var api = await StartSenderAsync(peer, held, bindSource, time, async api =>
        {
            databases = await CreateDatabasesAsync(api, 71);
            held.Database = databases[^1];
            var node = api.Server.Services.GetRequiredService<LocalReplica>().Id;
            foreach (var database in databases)
            {
                _ = source.Add(database, Incompressible(node, 32));
            }
        });
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        for (var index = 0; index < 32; index++)
        {
            await Assert.That((await peer.NextAsync()).Batch.Database).IsEqualTo(ChangeGroups.Wire(databases[index], await IncarnationAsync(api, databases[index])));
        }

        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        var changes = time.Changes;
        time.Advance(Idle);
        await Assert.That((await peer.NextAsync()).KindCase).IsEqualTo(PushFrame.KindOneofCase.Heartbeat);
        await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
        await Assert.That(peer.BatchesBeforeRelease).IsEqualTo(32);
        await Assert.That(held.Entered.Task.IsCompleted).IsFalse();
        await Assert.That(databases.All(database => source.AckedBy(database, ReplicaId.Of(1, 9, 0)) == 0)).IsTrue();

        // Release the delayed replies only after a heartbeat proves the sender stopped at its response budget.
        release.SetResult();
        for (var index = 32; index < 70; index++)
        {
            await Assert.That((await NextFrameAsync(index)).Batch.Database).IsEqualTo(ChangeGroups.Wire(databases[index], await IncarnationAsync(api, databases[index])));
        }

        await held.Entered.Task.WaitAsync(Deadline);
        await Assert.That(peer.BatchesBeforeRelease).IsEqualTo(32);
        for (var round = 0; round < 30; round++)
        {
            changes = time.Changes;
            time.Advance(Idle);
            // ACK-driven empty reads can leave valid frontiers ahead of the first heartbeat.
            _ = await NextFrameAsync(70, PushFrame.KindOneofCase.Heartbeat);
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
        }

        await Assert.That(peer.Streams).IsEqualTo(1);
        await Assert.That(held.Release.Task.IsCompleted).IsFalse();
        await Assert.That(held.Cancelled.Task.IsCompleted).IsFalse();
        await Assert.That(source.AckedBy(databases[^1], ReplicaId.Of(1, 9, 0))).IsEqualTo(0);

        held.Release.SetResult();
        await Assert.That((await NextFrameAsync(70)).Batch.Database).IsEqualTo(ChangeGroups.Wire(databases[^1], await IncarnationAsync(api, databases[^1])));
        using var deadline = new CancellationTokenSource(Deadline);
        await Task.WhenAll(databases.Select(database => source.UntilAckedAsync(database, ReplicaId.Of(1, 9, 0), 1, deadline.Token)));
        await Assert.That(peer.Streams).IsEqualTo(1);

        async Task<PushFrame> NextFrameAsync(int index, PushFrame.KindOneofCase expected = PushFrame.KindOneofCase.Batch)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (true)
            {
                var frame = await peer.NextAsync().WaitAsync(deadline.Token);
                if (frame.Frontier is not { } frontier)
                {
                    await Assert.That(frame.KindCase).IsEqualTo(expected);
                    return frame;
                }

                // Independent cold initialization can finish across passes; caught-up databases report their frontier.
                var database = ChangeGroups.Tenant(frontier.Database);
                await Assert.That(databases.Take(index).Contains(database)).IsTrue();
                await Assert.That(frontier.Database).IsEqualTo(ChangeGroups.Wire(database, await IncarnationAsync(api, database)));
            }
        }
    }

    [Test]
    public async Task Thirty_two_unanswered_frontiers_do_not_consume_the_healthy_batch_budget()
    {
        var time = new ObservedTime();
        // Commit-woken passes must not straddle the clock advances that drive these frontier rounds.
        var wakes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        answered.SetResult();
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, batchRelease: answered);
        var (unused, bind) = CreateSource();
        ResetSource? source = null;
        TenantContext[] databases = [];
        var replica = ReplicaId.Of(1, 9, 0);
        await using var api = await StartSenderAsync(peer, unused, bind, time, async api =>
        {
            databases = await CreateDatabasesAsync(api, 2);
            source!.Databases = databases;
            foreach (var database in databases)
            {
                await PutStoredAsync(api, database, "warm");
                var pin = await IncarnationAsync(api, database);
                var head = await source.HeadAsync(database, pin, CancellationToken.None)
                    ?? throw new InvalidOperationException("the source head was refused");
                await Assert.That(await source.AckAsync(database, pin, replica, head, default, CancellationToken.None)).IsTrue();
            }
        }, sourceFactory: provider => source = new ResetSource(provider.GetRequiredService<FaultAwareReplicaSource>()) { Wakes = wakes.Task });
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        for (var round = 0; round < 16; round++)
        {
            var frontiers = 0;
            while (frontiers < 2)
            {
                var frame = await peer.NextAsync();
                await Assert.That(frame.Hello).IsNull();
                if (frame.Frontier is not null)
                {
                    frontiers++;
                }
            }
            // Earlier passes can leave canceled waits at this same fake instant.
            await time.UntilTimerAsync(Idle, after: 0, period: Timeout.InfiniteTimeSpan, createdAt: time.GetUtcNow(), activeOnly: true);
            // Advance only one second after the frontier pass: no frontier is due, so a heartbeat must answer.
            var changes = time.Changes;
            time.Advance(Idle);
            await Assert.That((await peer.NextAsync()).Heartbeat).IsNotNull();
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
            if (round < 15)
            {
                // The pass this heartbeat second woke must re-arm its wait now: armed after the advance, no frontier pass would follow it.
                await time.UntilTimerAsync(Idle, after: 0, period: Timeout.InfiniteTimeSpan, createdAt: time.GetUtcNow(), activeOnly: true);
                time.Advance(TimeSpan.FromSeconds(4));
            }
        }

        wakes.SetResult();
        await PutStoredAsync(api, databases[1], "sentinel");
        PushFrame sentinel;
        do
        {
            sentinel = await peer.NextAsync();
            await Assert.That(sentinel.Hello).IsNull();
        }
        while (sentinel.Batch is null);
        await Assert.That(sentinel.Batch.Database).IsEqualTo(ChangeGroups.Wire(databases[1], await IncarnationAsync(api, databases[1])));
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await source!.AckedAsync(databases[1], replica, token))?.Acked == (long)sentinel.Batch.LastChangeId, Deadline);
        await Assert.That(peer.Streams).IsEqualTo(1);
    }

    [Test]
    public async Task Sixty_five_optional_stable_replies_cannot_block_heartbeats_while_a_real_source_read_is_held()
    {
        var time = new ObservedTime();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        answered.SetResult();
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, batchRelease: answered, stableBurst: true);
        var (unused, bind) = CreateSource();
        ResetSource? source = null;
        TenantContext database = null!;
        var replica = ReplicaId.Of(1, 9, 0);
        await using var api = await StartSenderAsync(peer, unused, bind, time, async api =>
        {
            database = await CreateDatabaseAsync(api);
            source!.Databases = [database];
            await PutStoredAsync(api, database, "warm");
            var pin = await IncarnationAsync(api, database);
            var head = await source.HeadAsync(database, pin, CancellationToken.None)
                ?? throw new InvalidOperationException("the source head was refused");
            await Assert.That(await source.AckAsync(database, pin, replica, head, default, CancellationToken.None)).IsTrue();
        }, sourceFactory: provider => source = new ResetSource(provider.GetRequiredService<FaultAwareReplicaSource>()));
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await Assert.That((await peer.NextAsync()).Frontier).IsNotNull();
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        source!.HoldDatabase = database;
        await PutStoredAsync(api, database, "sentinel");
        await source.Prepared.Task.WaitAsync(Deadline);
        var changes = time.Changes;
        time.Advance(Idle);
        await Assert.That((await peer.NextAsync()).Heartbeat).IsNotNull();
        await peer.StableSent.Task.WaitAsync(Deadline);
        // Every received frame resets silence: 65 Stable frames plus the heartbeat prove receipt beyond the 64-slot queue.
        await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes, count: 66);
        await Assert.That(source.Release.Task.IsCompleted).IsFalse();
        await Assert.That(peer.Streams).IsEqualTo(1);
        await Assert.That(peer.FirstEnded.Task.IsCompleted).IsFalse();
        source.Release.SetResult();
        var stored = api.Server.Services.GetRequiredService<FaultAwareReplicaSource>();
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await stored.AckedAsync(database, replica, token))?.Acked
                == await stored.HeadAsync(database, await IncarnationAsync(api, database), token), Deadline);
        await Assert.That(peer.Streams).IsEqualTo(1);
    }

    [Test]
    public async Task A_delayed_stable_reset_during_a_full_budget_abandons_the_prepared_batch_without_reconnecting()
    {
        var time = new ObservedTime();
        var warnings = new PeerWarnings(time, streamFailuresOnly: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, batchRelease: release, stableBeforeBatches: true);
        // The reset is logged once the cursor is reset: only then may an ACK end the prepared send's wait for budget.
        peer.AcksAfterStable = warnings.BookkeepingFailed.Task;
        var (unused, bind) = CreateSource();
        ResetSource? source = null;
        TenantContext[] databases = [];
        var replica = ReplicaId.Of(1, 9, 0);
        await using var api = await StartSenderAsync(peer, unused, bind, time, async api =>
        {
            databases = await CreateDatabasesAsync(api, 33);
            source!.Databases = databases;
            foreach (var database in databases)
            {
                await PutStoredAsync(api, database, "warm");
            }

            var pin = await IncarnationAsync(api, databases[0]);
            var head = await source.HeadAsync(databases[0], pin, CancellationToken.None)
                ?? throw new InvalidOperationException("the source head was refused");
            await Assert.That(await source.AckAsync(databases[0], pin, replica, head, default, CancellationToken.None)).IsTrue();
        }, warnings, provider => source = new ResetSource(provider.GetRequiredService<FaultAwareReplicaSource>()));
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await Assert.That((await peer.NextAsync()).Frontier.Database).IsEqualTo(ChangeGroups.Wire(databases[0], await IncarnationAsync(api, databases[0])));
        // Cold registrations finish independently; the budget needs one batch per database, in any database order.
        var waiting = databases.Skip(1).ToHashSet();
        while (waiting.Count != 0)
        {
            var batch = (await peer.NextAsync()).Batch;
            await Assert.That(batch).IsNotNull();
            var database = ChangeGroups.Tenant(batch.Database);
            await Assert.That(waiting.Remove(database)).IsTrue()
                .Because($"each initial batch names a different client database; received {database.DatabaseId} on {peer.Streams} stream(s)");
            await Assert.That(batch.Database).IsEqualTo(ChangeGroups.Wire(database, await IncarnationAsync(api, database)));
        }

        await Assert.That(peer.BatchesBeforeRelease).IsEqualTo(32);
        source!.ResetDatabase = databases[0];
        try
        {
            await PutStoredAsync(api, databases[0], "prepared");
            await source.Prepared.Task.WaitAsync(Deadline);
            // Stable is queued before the held page returns; only ReserveAsync can drain it during this prepared send.
            release.SetResult();
            await peer.StableSent.Task.WaitAsync(Deadline);
            source.Release.SetResult();
            await source.Reset.Task.WaitAsync(Deadline);
            await warnings.BookkeepingFailed.Task.WaitAsync(Deadline);
            var stored = api.Server.Services.GetRequiredService<FaultAwareReplicaSource>();
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                foreach (var database in databases.Skip(1))
                {
                    var pin = await IncarnationAsync(api, database);
                    var head = await stored.HeadAsync(database, pin, token);
                    if ((await stored.AckedAsync(database, replica, token))?.Acked != head)
                    {
                        return false;
                    }
                }

                return true;
            }, Deadline);
            // ACK persistence precedes queued empty reads; observe their writes before advancing the clock.
            var frontiers = databases.Skip(1).ToHashSet();
            while (frontiers.Count != 0)
            {
                var frontier = (await peer.NextAsync()).Frontier;
                await Assert.That(frontier).IsNotNull();
                await Assert.That(frontiers.Remove(ChangeGroups.Tenant(frontier.Database))).IsTrue();
            }

            await Assert.That(source.Reinitialized.Task.IsCompleted).IsFalse();
            // Consume the retry tick's heartbeat before using a later heartbeat to order the held cursor read.
            await AdvanceHeartbeatAsync();
            await source.Reinitialized.Task.WaitAsync(Deadline);
            await AdvanceHeartbeatAsync();
            await Assert.That(peer.BatchesReceived).IsEqualTo(32)
                .Because("the prepared batch never reached the peer before cursor reinitialization");
            var abandoned = await stored.AckedAsync(databases[0], replica, CancellationToken.None)
                ?? throw new InvalidOperationException("the reset source cursor was refused");
            var preparedHead = await stored.HeadAsync(databases[0], await IncarnationAsync(api, databases[0]), CancellationToken.None)
                ?? throw new InvalidOperationException("the prepared source head was refused");
            await Assert.That(abandoned.Acked).IsLessThan(preparedHead)
                .Because("the prepared frame is abandoned when the matching cursor is reset");
            await Assert.That(peer.Streams).IsEqualTo(1);
            await Assert.That(peer.FirstEnded.Task.IsCompleted).IsFalse();
            await Assert.That(warnings.Failure.Task.IsCompleted).IsFalse();
        }
        finally
        {
            _ = release.TrySetResult();
            _ = source.Release.TrySetResult();
            _ = source.ReinitializeRelease.TrySetResult();
        }

        async Task AdvanceHeartbeatAsync()
        {
            await time.UntilTimerAsync(Idle, after: 0, period: Timeout.InfiniteTimeSpan, createdAt: time.GetUtcNow(), activeOnly: true);
            await time.UntilTimerAsync(Idle, after: 0, period: Idle);
            var changes = time.Changes;
            time.Advance(Idle);
            PushFrame observed;
            do
            {
                observed = await peer.NextAsync();
                await Assert.That(observed.Hello).IsNull();
            }
            while (observed.Heartbeat is null);
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
        }
    }

    private static async Task PutStoredAsync(ManagementApi api, TenantContext database, string id)
    {
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the test database is missing");
        await using var transaction = await store.BeginDurableTransactionAsync(CancellationToken.None);
        _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
        _ = await transaction.PutAsync("items", id, "{\"n\":1}"u8.ToArray(), CancellationToken.None);
        _ = await transaction.CommitAsync(CancellationToken.None);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_unacknowledged_batch_expires_while_another_database_read_is_held_despite_live_heartbeats(bool holdCursor)
    {
        var time = new ObservedTime();
        var warnings = new PeerWarnings(time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, batchRelease: release);
        var (source, bindSource) = CreateSource();
        var held = new HeldSource(source) { HoldCursor = holdCursor };
        TenantContext[] databases = [];
        await using var api = await StartSenderAsync(peer, held, bindSource, time, async api =>
        {
            databases = await CreateDatabasesAsync(api, 2);
            held.Database = databases[1];
            var node = api.Server.Services.GetRequiredService<LocalReplica>().Id;
            foreach (var database in databases)
            {
                _ = source.Add(database, Incompressible(node, 32));
            }
        }, warnings);
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await Assert.That((await peer.NextAsync()).Batch.Database).IsEqualTo(ChangeGroups.Wire(databases[0], await IncarnationAsync(api, databases[0])));
        await held.Entered.Task.WaitAsync(Deadline);
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        for (var elapsed = Idle; elapsed < ReplicationProtocol.ProgressTimeout; elapsed += Idle)
        {
            var changes = time.Changes;
            time.Advance(Idle);
            await Assert.That((await peer.NextAsync()).KindCase).IsEqualTo(PushFrame.KindOneofCase.Heartbeat);
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
        }

        var timers = time.Timers;
        time.Advance(Idle);
        await held.Cancelled.Task.WaitAsync(Deadline);
        await peer.FirstEnded.Task.WaitAsync(Deadline);
        var failure = await warnings.Failure.Task.WaitAsync(Deadline);
        await Assert.That(failure).IsTypeOf<TimeoutException>();
        await Assert.That(failure!.Message).IsEqualTo("the peer did not acknowledge a batch in time");
        await Assert.That(release.Task.IsCompleted).IsFalse();
        await Assert.That(source.AckedBy(databases[0], ReplicaId.Of(1, 9, 0))).IsEqualTo(0);
        await Assert.That(source.AckedBy(databases[1], ReplicaId.Of(1, 9, 0))).IsEqualTo(0);
        await time.UntilTimerAsync(Idle, timers);
        time.Advance(Idle);
        // The heartbeat and independent deadline timers can run in either order at 15s; one last heartbeat may precede cancellation.
        var replacement = await peer.NextAsync();
        if (replacement.Heartbeat is not null)
        {
            replacement = await peer.NextAsync();
        }

        await Assert.That(replacement.Hello).IsNotNull();
        await Assert.That(peer.Streams).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Missing_coverage_refills_immediately_but_a_refused_refill_keeps_the_retry_backoff(bool repeatedCoverageRefusal)
    {
        var time = new ObservedTime();
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, coverageRefusal: true, repeatedCoverageRefusal: repeatedCoverageRefusal);
        var (source, bindSource) = CreateSource();
        TenantContext database = null!;
        await using var api = await StartSenderAsync(peer, source, bindSource, time, async api =>
        {
            database = await CreateDatabaseAsync(api);
            _ = source.Add(database, Incompressible(api.Server.Services.GetRequiredService<LocalReplica>().Id, 32));
        });
        var started = time.GetUtcNow();
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await Assert.That((await peer.NextAsync()).Batch).IsNotNull();
        await Assert.That((await peer.NextAsync()).Refill).IsNotNull();
        await Assert.That(time.GetUtcNow()).IsEqualTo(started).Because("the clock has not advanced");

        // The Stable after the refill refusal crosses the same response queue, proving the refusal was processed.
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (await source.AckedAsync(database, ReplicaId.Of(1, 9, 0), token))?.Stable.Value == 1, Deadline);
        for (var second = 1; second < 5; second++)
        {
            var timers = time.Timers;
            var changes = time.Changes;
            time.Advance(Idle);
            await Assert.That((await peer.NextAsync()).Heartbeat).IsNotNull().Because("a failed refill must not retry immediately");
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
            await time.UntilTimerAsync(Idle, timers, period: Timeout.InfiniteTimeSpan, createdAt: time.GetUtcNow());
        }

        time.Advance(Idle);
        var retry = await peer.NextAsync();
        while (retry.Heartbeat is not null)
        {
            retry = await peer.NextAsync();
        }

        await Assert.That(retry.Refill).IsNotNull();
        await Assert.That(time.GetUtcNow() - started).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(source.AckedBy(database, ReplicaId.Of(1, 9, 0))).IsEqualTo(0);
    }

    [Test]
    public async Task The_sender_keeps_a_live_refill_on_one_stream_past_the_deadline_and_acks_only_after_its_release()
    {
        var time = new ObservedTime();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, refillRelease: release);
        var (source, bindSource) = CreateSource();
        TenantContext database = null!;
        await using var api = await StartSenderAsync(peer, source, bindSource, time, async api =>
        {
            database = await CreateDatabaseAsync(api);
            var node = api.Server.Services.GetRequiredService<LocalReplica>().Id;
            _ = source.Add(database, Incompressible(node, 32));
            _ = source.Add(database, Incompressible(node, 32));
            // Prepare both groups and the retention cut before discovery can start the first source read.
            source.CutBeforeNextRead(database, 1);
        });
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        var refill = (await peer.NextAsync()).Refill;
        await Assert.That(refill).IsNotNull();
        await Assert.That(refill.ChangeId).IsEqualTo(2UL);
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        // The pass timer is created after ShipAsync records the refill in flight and the write's completion timestamp.
        await time.UntilTimerAsync(Idle, after: 0, period: Timeout.InfiniteTimeSpan);

        for (var round = 0; round < 30; round++)
        {
            var changes = time.Changes;
            time.Advance(Idle);
            await Assert.That((await peer.NextAsync()).KindCase).IsEqualTo(PushFrame.KindOneofCase.Heartbeat);
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes);
            await Assert.That(source.AckedBy(database, ReplicaId.Of(1, 9, 0))).IsEqualTo(0);
        }

        await Assert.That(peer.Streams).IsEqualTo(1);
        release.SetResult();
        using var deadline = new CancellationTokenSource(Deadline);
        await source.UntilAckedAsync(database, ReplicaId.Of(1, 9, 0), 2, deadline.Token);
        await Assert.That(source.AckedBy(database, ReplicaId.Of(1, 9, 0))).IsEqualTo(2);
        await Assert.That(peer.Streams).IsEqualTo(1);
    }

    [Test]
    public async Task The_receiver_aborts_a_blocked_response_write_and_releases_its_refill_at_the_deadline()
    {
        var time = new ObservedTime();
        var held = new HeldRepair();
        var blocked = new BlockedResponses();
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, repair: Repair());
        await using var api = await StartReceiverAsync(peer, time, held, blocked);
        var database = await CreateDatabaseAsync(api);
        var memory = (RefillMemory)api.Server.Services.GetRequiredService<IMemoryBudget>();
        using var channel = PeerChannel.Create(api.Server.BaseAddress, api.Server.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        var hello = new Hello { Node = ReplicaId.Of(1, 7, 0).Value, Region = "sjc", Machine = "zzzz-peer" };
        await call.RequestStream.WriteAsync(new PushFrame { Hello = hello });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Deadline)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        await call.RequestStream.WriteAsync(new PushFrame { Refill = new Refill { Database = ChangeGroups.Wire(database, peer.Incarnation), ChangeId = 57 } });
        await held.Entered.Task.WaitAsync(Deadline);
        await Assert.That(memory.Reserved).IsEqualTo(RefillMemory.BytesPerRefill);
        await call.RequestStream.WriteAsync(new PushFrame { Heartbeat = new Heartbeat() });
        await blocked.Entered.Task.WaitAsync(Deadline);

        // The writer only exits on RequestAborted: cancelling the receiver's own work alone cannot unblock it.
        time.Advance(ReplicationProtocol.ProgressTimeout);
        await blocked.Ended.Task.WaitAsync(Deadline);
        await held.Ended.Task.WaitAsync(Deadline);
        await Assert.That(memory.Reserved).IsEqualTo(0);
        await Assert.That(await EndsAsync(call.ResponseStream)).IsTrue();

        // Reusing the origin also proves the old stream's cleanup no longer holds its replacement's welcome.
        using var replacement = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        await replacement.RequestStream.WriteAsync(new PushFrame { Hello = hello });
        await Assert.That(await replacement.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Deadline)).IsTrue();
        await Assert.That(replacement.ResponseStream.Current.Welcome).IsNotNull();
    }

    [Test]
    public async Task A_live_refill_keeps_one_stream_past_the_deadline_without_acknowledging_uncommitted_repairs()
    {
        var time = new ObservedTime();
        var held = new HeldRepair();
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, repair: Repair());
        await using var api = await StartReceiverAsync(peer, time, held);
        var database = await CreateDatabaseAsync(api);
        var memory = (RefillMemory)api.Server.Services.GetRequiredService<IMemoryBudget>();
        using var channel = PeerChannel.Create(api.Server.BaseAddress, api.Server.Credential);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Push();
        await call.RequestStream.WriteAsync(new PushFrame
        {
            Hello = new Hello { Node = ReplicaId.Of(1, 7, 0).Value, Region = "sjc", Machine = "zzzz-peer" },
        });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Deadline)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        await call.RequestStream.WriteAsync(new PushFrame { Refill = new Refill { Database = ChangeGroups.Wire(database, peer.Incarnation), ChangeId = 57 } });
        await held.Entered.Task.WaitAsync(Deadline);

        for (var round = 0; round < 3; round++)
        {
            time.Advance(ReplicationProtocol.ProgressTimeout - Idle);
            await call.RequestStream.WriteAsync(new PushFrame { Heartbeat = new Heartbeat() });
            await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Deadline)).IsTrue();
            await Assert.That(call.ResponseStream.Current.KindCase).IsEqualTo(AckFrame.KindOneofCase.None);
            await Assert.That(memory.Reserved).IsEqualTo(RefillMemory.BytesPerRefill);
            await Assert.That(held.Ended.Task.IsCompleted).IsFalse();
        }

        // Failure after 42 seconds of live heartbeats must refuse the refill, never turn liveness into a durable cursor.
        held.Release.SetResult(false);
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(Deadline)).IsTrue();
        var ack = call.ResponseStream.Current.Ack;
        await Assert.That(ack).IsNotNull();
        await Assert.That(ack.Database).IsEqualTo(ChangeGroups.Wire(database, peer.Incarnation));
        await Assert.That(ack.Refused).IsTrue();
        await Assert.That(ack.ChangeId).IsEqualTo(0UL);
        await Assert.That(ack.Applied).IsEqualTo(0UL);
        await Assert.That(memory.Reserved).IsEqualTo(0);
        await call.RequestStream.CompleteAsync();
        await Assert.That(await EndsAsync(call.ResponseStream)).IsTrue();
    }

    [Test]
    public async Task A_stalled_progress_counter_expires_the_batch_one_deadline_after_its_last_increase()
    {
        var time = new ObservedTime();
        var warnings = new PeerWarnings(time);
        var observed = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Every heartbeat repeats one count: only its first report is an increase.
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, batchRelease: release,
            progress: (database, changeId, _) => [Progress(database, changeId, 1)]);
        var (source, bindSource) = CreateSource();
        TenantContext database = null!;
        await using var api = await StartSenderAsync(peer, source, bindSource, time, async api =>
        {
            database = await CreateDatabaseAsync(api);
            _ = source.Add(database, Incompressible(api.Server.Services.GetRequiredService<LocalReplica>().Id, 32));
        }, warnings, checkpoint: Observed(() => _ = Interlocked.Increment(ref observed)));
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        await Assert.That((await peer.NextAsync()).Batch).IsNotNull();
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        var started = time.GetUtcNow();
        for (var elapsed = Idle; elapsed <= ReplicationProtocol.ProgressTimeout; elapsed += Idle)
        {
            var changes = time.Changes;
            time.Advance(Idle);
            await Assert.That((await peer.NextAsync()).Heartbeat).IsNotNull();
            // The progress frame and the empty answer each reset silence, so both were read before the next advance.
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes, count: 2);
            await Assert.That(Volatile.Read(ref observed)).IsEqualTo(1);
            await Assert.That(peer.FirstEnded.Task.IsCompleted).IsFalse();
        }

        // The increase at one second moved the deadline once; the repeats since then did not.
        time.Advance(Idle);
        await peer.FirstEnded.Task.WaitAsync(Deadline);
        var failure = await warnings.Failure.Task.WaitAsync(Deadline);
        await Assert.That(failure).IsTypeOf<TimeoutException>();
        await Assert.That(failure!.Message).IsEqualTo("the peer did not acknowledge a batch in time");
        await Assert.That(time.GetUtcNow() - started).IsEqualTo(ReplicationProtocol.ProgressTimeout + Idle);
        await Assert.That(source.AckedBy(database, ReplicaId.Of(1, 9, 0))).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Progress_outside_the_in_flight_batch_is_ignored_and_progress_never_acknowledges(bool refill)
    {
        var time = new ObservedTime();
        var observed = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TenantContext database = null!;
        // Another incarnation, another change id and no increase come first; a refill's in-flight id is never a batch.
        await using var peer = await FakePeer.StartAsync(silentStreams: 0, batchRelease: refill ? null : release, refillRelease: refill ? release : null,
            progress: (wire, changeId, heartbeat) => heartbeat switch
            {
                1 => [Progress(ChangeGroups.Wire(database, Ulid.NewUlid()), changeId, 5)],
                2 => [Progress(wire, changeId + 1, 5)],
                3 => [Progress(wire, changeId, 0)],
                _ => [Progress(wire, changeId, 5)],
            });
        var (source, bindSource) = CreateSource();
        await using var api = await StartSenderAsync(peer, source, bindSource, time, async api =>
        {
            database = await CreateDatabaseAsync(api);
            var node = api.Server.Services.GetRequiredService<LocalReplica>().Id;
            _ = source.Add(database, Incompressible(node, 32));
            if (refill)
            {
                _ = source.Add(database, Incompressible(node, 32));
                source.CutBeforeNextRead(database, 1);
            }
        }, checkpoint: Observed(() => _ = Interlocked.Increment(ref observed)));
        var waiter = api.Server.Services.GetRequiredService<RemoteAckWaiter>();
        using var lease = waiter.TryAdmit(database) is RemoteLease admitted ? admitted : throw new InvalidOperationException("a free confirmation slot was refused");
        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        var sent = await peer.NextAsync();
        await Assert.That(sent.KindCase).IsEqualTo(refill ? PushFrame.KindOneofCase.Refill : PushFrame.KindOneofCase.Batch);
        await time.UntilTimerAsync(Idle, after: 0, period: Idle);
        if (refill)
        {
            await time.UntilTimerAsync(Idle, after: 0, period: Timeout.InfiniteTimeSpan);
        }

        var marker = new CommitMarker(await IncarnationAsync(api, database), refill ? 2 : 1);
        for (var heartbeat = 1; heartbeat <= 5; heartbeat++)
        {
            var changes = time.Changes;
            time.Advance(Idle);
            await Assert.That((await peer.NextAsync()).Heartbeat).IsNotNull();
            await time.UntilChangeAsync(ReplicationProtocol.ProgressTimeout, changes, count: 2);
            await Assert.That(Volatile.Read(ref observed)).IsEqualTo(!refill && heartbeat >= 4 ? 1 : 0);
            await Assert.That(source.AckedBy(database, ReplicaId.Of(1, 9, 0))).IsEqualTo(0);
        }

        // A covered marker returns at once; accepted progress left the confirmation ranges untouched.
        var waiting = waiter.WaitAsync(database, marker, () => true, CancellationToken.None).AsTask();
        await Assert.That(waiting.IsCompleted).IsFalse();
        release.SetResult();
        using var deadline = new CancellationTokenSource(Deadline);
        // Admission can move a refill below the marker; only the frame's own position was acknowledged.
        var acknowledged = (long)(refill ? sent.Refill.ChangeId : sent.Batch.LastChangeId);
        await source.UntilAckedAsync(database, ReplicaId.Of(1, 9, 0), acknowledged, deadline.Token);
        await Assert.That(peer.Streams).IsEqualTo(1);
        if (refill && acknowledged < marker.ChangeId)
        {
            var replay = (await peer.NextAsync()).Batch;
            await Assert.That(replay).IsNotNull();
            await Assert.That((long)replay!.LastChangeId).IsEqualTo(marker.ChangeId);
            await Assert.That(source.AckedBy(database, ReplicaId.Of(1, 9, 0))).IsEqualTo(acknowledged);
            await Assert.That(waiting.IsCompleted).IsFalse();
        }
        if (!refill)
        {
            await Assert.That(await waiter.WaitAsync(database, marker, () => true, deadline.Token)).IsTrue();
        }

        await Assert.That(await waiting).IsEqualTo(!refill);
    }

    // The sender calls it on its response reader: count only, never block.
    private static DatabaseLifecycleCheckpoint Observed(Action count) => (step, _) =>
    {
        if (step == "replication.progress.observed")
        {
            count();
        }
    };

    private static AckFrame Progress(Features.Replication.V1.Database database, ulong changeId, ulong progress) =>
        new() { Applying = new Applying { Database = database, ChangeId = changeId, Progress = progress } };

    private static RepairState Repair()
    {
        var delta = Incompressible(ReplicaId.Of(1, 9, 0), 32);
        return new RepairState(delta.Collection, delta.Id, delta.State);
    }

    private static async Task<ManagementApi> StartReceiverAsync(FakePeer peer, ObservedTime time, HeldRepair held, BlockedResponses? blocked = null)
    {
        var members = new FakeMemberStore();
        var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru", services =>
        {
            services.AddSingleton<IMemberDirectory>(members);
            services.AddSingleton<IMembership>(provider => ActivatorUtilities.CreateInstance<DirectoryMembership>(provider, time));
            services.AddSingleton<IChangeGroupSource>(provider => new FakeChangeGroupSource((database, token) =>
                IncarnationAsync(provider.GetRequiredService<IStorageEngine>(), database, token)));
            services.AddSingleton<IChangeGroupSink>(provider => new HeldSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), held));
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreamReceiver>(provider, time));
            var memory = services.Single(service => service.ServiceType == typeof(IMemoryBudget));
            services.AddSingleton<IMemoryBudget>(provider => new RefillMemory((IMemoryBudget)memory.ImplementationFactory!(provider)));
            if (blocked is not null)
            {
                services.AddSingleton(blocked);
                services.AddGrpc(options => options.Interceptors.Add<BlockedResponses>());
            }
        });
        members.Join(new ClusterMember("zzzz-peer", peer.Address, "sjc", ""));
        time.Advance(DirectoryMembership.Heartbeat);
        _ = await api.Server.Services.GetRequiredService<IMembership>().RefreshAsync(CancellationToken.None);
        return api;
    }

    private static async Task<ManagementApi> StartSenderAsync(
        FakePeer peer, IChangeGroupSource source, Action<IStorageEngine> bindSource, ObservedTime time,
        Func<ManagementApi, Task>? beforeJoin = null, PeerWarnings? warnings = null,
        Func<IServiceProvider, IChangeGroupSource>? sourceFactory = null, DatabaseLifecycleCheckpoint? checkpoint = null)
    {
        var members = new FakeMemberStore();
        var api = await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = "gru";
            settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        }, services =>
        {
            services.AddSingleton<IMemberDirectory>(members);
            services.AddSingleton<IMembership>(provider => ActivatorUtilities.CreateInstance<DirectoryMembership>(provider, time));
            services.AddSingleton<IChangeGroupSource>(provider =>
            {
                bindSource(provider.GetRequiredService<IStorageEngine>());
                return sourceFactory?.Invoke(provider) ?? source;
            });
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreams>(provider, time));
            if (warnings is not null)
            {
                services.AddSingleton<ILogger<PeerStreams>>(warnings);
            }

            if (checkpoint is not null)
            {
                services.AddSingleton(checkpoint);
            }
        });
        if (beforeJoin is not null)
        {
            try
            {
                await beforeJoin(api);
            }
            catch
            {
                await api.DisposeAsync();
                throw;
            }
        }

        // Bootstrap the empty catalog before discovering a transport-only fake, which cannot verify a catalog.
        members.Join(new ClusterMember("zzzz-peer", peer.Address, "sjc", ""));
        await time.UntilTimerAsync(DirectoryMembership.Heartbeat, after: 0);
        time.Advance(DirectoryMembership.Heartbeat);
        return api;
    }

    private static (FakeChangeGroupSource Source, Action<IStorageEngine> Bind) CreateSource()
    {
        IStorageEngine? engine = null;
        // Hosted services can read the source before StartAsync returns: bind to the engine when DI resolves it.
        return (new FakeChangeGroupSource((database, token) =>
            IncarnationAsync(engine ?? throw new InvalidOperationException("the source engine is not bound"), database, token)), current => engine = current);
    }

    private static async ValueTask<Ulid?> IncarnationAsync(IStorageEngine engine, TenantContext database, CancellationToken token) =>
        database == ChangeGroups.System
            ? (await engine.OpenSystemDatabaseAsync(token)).Incarnation
            : (await engine.OpenDatabaseAsync(database, token))?.Incarnation;

    private static async Task<Ulid> IncarnationAsync(ManagementApi api, TenantContext database) =>
        await IncarnationAsync(api.Server.Services.GetRequiredService<IStorageEngine>(), database, CancellationToken.None)
            ?? throw new InvalidOperationException("the database is missing");

    // A database of this machine placed in the peer's region too.
    private static async Task<TenantContext> CreateDatabaseAsync(ManagementApi api) => (await CreateDatabasesAsync(api, 1))[0];

    private static async Task<TenantContext[]> CreateDatabasesAsync(ManagementApi api, int count)
    {
        var organization = EntityIds.New().ToString();
        var project = EntityIds.New().ToString();
        var databases = Enumerable.Range(0, count)
            .Select(_ => new TenantContext(organization, PlanTier.Hobby, project, EntityIds.New().ToString())).ToArray();
        foreach (var database in databases)
        {
            await CatalogFixture.RegisterAsync(api, api.User, database,
                count == 1 ? "main" : $"peers-{database.DatabaseId}".ToLowerInvariant(), ReplicaRegions);
        }

        return databases;
    }

    // A put of a document holding `bytes` random bytes (base64): LZ4 cannot shrink it.
    private static ChangeDelta Incompressible(ReplicaId node, int bytes)
    {
        var at = new EntryVersion(new Hlc((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16), node);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(new { blob = Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)) }));
        return new ChangeDelta("items", EntityIds.New().ToString(), at.Stamp, DocumentState.Bottom.Put(at, body.RootElement).DeltaAt(at.Stamp));
    }

    // Whether the stream ends (completes or fails) within the deadline.
    private static async Task<bool> EndsAsync(IAsyncStreamReader<AckFrame> responses)
    {
        try
        {
            while (await responses.MoveNext(CancellationToken.None).WaitAsync(Deadline))
            {
            }

            return true;
        }
        catch (RpcException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private sealed class PeerWarnings(ObservedTime time, bool streamFailuresOnly = false) : ILogger<PeerStreams>
    {
        public TaskCompletionSource<Exception?> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<int> BackoffBoundary { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BookkeepingFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning
                && formatter(state, exception).Contains("Peer bookkeeping for database", StringComparison.Ordinal))
            {
                // BookkeepAsync logs after installing the tenant's retry deadline.
                _ = BookkeepingFailed.TrySetResult();
            }

            if (logLevel == LogLevel.Warning
                && (!streamFailuresOnly || formatter(state, exception).Contains("reconnects and resumes", StringComparison.Ordinal)))
            {
                // RunAsync logs after stream cleanup and before scheduling its reconnect backoff.
                _ = BackoffBoundary.TrySetResult(time.Timers);
                _ = Failure.TrySetResult(exception);
            }
        }
    }

    private sealed class HeldSource(IChangeGroupSource source) : IChangeGroupSource
    {
        public TenantContext? Database { get; set; }
        public bool HoldCursor { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CursorReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TenantContext> OtherCursorReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken) => source.ListDatabasesAsync(cancellationToken);
        public ValueTask<bool> ProveAsync(TenantContext database, Ulid incarnation, long from, long through, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken) =>
            source.ProveAsync(database, incarnation, from, through, held, cancellationToken);
        public async ValueTask<SourceGroups?> ReadAsync(TenantContext database, Ulid incarnation, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken)
        {
            if (!HoldCursor && (Database is null || database == Database))
            {
                await HoldAsync(cancellationToken);
            }

            return await source.ReadAsync(database, incarnation, after, maxGroups, maxBytes, cancellationToken);
        }

        public async ValueTask<SourceCursor?> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken)
        {
            if (HoldCursor && database == Database)
            {
                await HoldAsync(cancellationToken);
            }

            var cursor = await source.AckedAsync(database, peer, cancellationToken);
            if (HoldCursor)
            {
                _ = database == Database ? CursorReady.TrySetResult() : OtherCursorReady.TrySetResult(database);
            }

            return cursor;
        }

        public ValueTask<bool> AckAsync(TenantContext database, Ulid incarnation, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken) => source.AckAsync(database, incarnation, peer, changeId, stable, cancellationToken);
        public ValueTask<bool> RecordStableAsync(TenantContext database, Ulid incarnation, ReplicaId peer, Hlc stable, CancellationToken cancellationToken) => source.RecordStableAsync(database, incarnation, peer, stable, cancellationToken);
        public ValueTask<bool?> RetainsAsync(TenantContext database, Ulid incarnation, long after, CancellationToken cancellationToken) => source.RetainsAsync(database, incarnation, after, cancellationToken);
        public ValueTask<long?> HeadAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken) => source.HeadAsync(database, incarnation, cancellationToken);
        public ValueTask<long?> RetainedAfterAsync(TenantContext database, Ulid incarnation, long? through, long head, CancellationToken cancellationToken) => source.RetainedAfterAsync(database, incarnation, through, head, cancellationToken);
        public Task ChangedAsync(CancellationToken cancellationToken) => source.ChangedAsync(cancellationToken);
        public IDisposable Subscribe(Action<TenantContext> changed) => source.Subscribe(changed);

        private async Task HoldAsync(CancellationToken cancellationToken)
        {
            _ = Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = Cancelled.TrySetResult();
                throw;
            }
        }
    }

    // Storage stays real; only the prepared read boundary and one tenant-local bookkeeping error are controlled.
    private sealed class ResetSource(FaultAwareReplicaSource inner) : IChangeGroupSource
    {
        public Task Wakes { get; init; } = Task.CompletedTask;
        public TenantContext[] Databases { get; set; } = [];
        public TenantContext? ResetDatabase { get; set; }
        public TenantContext? HoldDatabase { get; set; }
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reset { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reinitialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReinitializeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<TenantContext> ListDatabasesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var database in Databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return database;
            }

            await Task.CompletedTask;
        }

        public async ValueTask<SourceGroups?> ReadAsync(TenantContext database, Ulid incarnation, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken)
        {
            var page = await inner.ReadAsync(database, incarnation, after, maxGroups, maxBytes, cancellationToken);
            if (database == (HoldDatabase ?? ResetDatabase) && page is { Groups.Count: > 0 } && Prepared.TrySetResult())
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return page;
        }

        public async ValueTask<SourceCursor?> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken)
        {
            var cursor = await inner.AckedAsync(database, peer, cancellationToken);
            if (database == ResetDatabase && Reset.Task.IsCompleted)
            {
                _ = Reinitialized.TrySetResult();
                await ReinitializeRelease.Task.WaitAsync(cancellationToken);
            }

            return cursor;
        }

        public ValueTask<bool> RecordStableAsync(TenantContext database, Ulid incarnation, ReplicaId peer, Hlc stable, CancellationToken cancellationToken)
        {
            if (database == ResetDatabase && Reset.TrySetResult())
            {
                throw new InvalidOperationException("a test tenant bookkeeping error without an established copy fault");
            }

            return inner.RecordStableAsync(database, incarnation, peer, stable, cancellationToken);
        }

        public ValueTask<bool> AckAsync(TenantContext database, Ulid incarnation, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken) => inner.AckAsync(database, incarnation, peer, changeId, stable, cancellationToken);
        public ValueTask<bool> ProveAsync(TenantContext database, Ulid incarnation, long from, long through, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken) => inner.ProveAsync(database, incarnation, from, through, held, cancellationToken);
        public ValueTask<bool?> RetainsAsync(TenantContext database, Ulid incarnation, long after, CancellationToken cancellationToken) => inner.RetainsAsync(database, incarnation, after, cancellationToken);
        public ValueTask<long?> HeadAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken) => inner.HeadAsync(database, incarnation, cancellationToken);
        public ValueTask<long?> RetainedAfterAsync(TenantContext database, Ulid incarnation, long? through, long head, CancellationToken cancellationToken) => inner.RetainedAfterAsync(database, incarnation, through, head, cancellationToken);
        public IDisposable Subscribe(Action<TenantContext> changed) => inner.Subscribe(database =>
        {
            // Match discovery's client-only scope; frontier tests also delay these commit wakes.
            if (Wakes.IsCompletedSuccessfully && Databases.Contains(database))
            {
                changed(database);
            }
        });

        public async Task ChangedAsync(CancellationToken cancellationToken)
        {
            await inner.ChangedAsync(cancellationToken);
            await Wakes.WaitAsync(cancellationToken);
        }
    }

    private sealed class HeldRepair
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HeldSink(IChangeGroupSink sink, HeldRepair held) : IChangeGroupSink
    {
        public ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            sink.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);

        public ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null) =>
            sink.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);

        public async ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken)
        {
            _ = held.Entered.TrySetResult();
            try
            {
                return await held.Release.Task.WaitAsync(cancellationToken) ? await sink.RepairAsync(database, incarnation, states, cancellationToken) : null;
            }
            finally
            {
                _ = held.Ended.TrySetResult();
            }
        }
    }

    /// <summary>A listener interceptor that holds an empty response until the HTTP request is aborted.</summary>
    private sealed class BlockedResponses : Interceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
            ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            try
            {
                await continuation(requestStream, new Writer<TResponse>(responseStream, context, this), context);
            }
            finally
            {
                _ = Ended.TrySetResult();
            }
        }

        private sealed class Writer<T>(IServerStreamWriter<T> inner, ServerCallContext context, BlockedResponses blocked) : IServerStreamWriter<T>
        {
            public WriteOptions? WriteOptions { get => inner.WriteOptions; set => inner.WriteOptions = value; }

            public Task WriteAsync(T message) => WriteAsync(message, CancellationToken.None);

            public async Task WriteAsync(T message, CancellationToken cancellationToken)
            {
                if (message is AckFrame { KindCase: AckFrame.KindOneofCase.None })
                {
                    _ = blocked.Entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                }

                await inner.WriteAsync(message, cancellationToken);
            }
        }
    }

    private sealed class RefillMemory(IMemoryBudget inner) : IMemoryBudget
    {
        public const long BytesPerRefill = (2 * 4096 * 16) + (4L * ReplicationProtocol.MaxApplicationBytes);
        private long _reserved;
        public long Reserved => Interlocked.Read(ref _reserved);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            if (inner.TryReserve(lane, bytes, tenant) is not { } reservation)
            {
                return null;
            }

            if (lane != MemoryLane.Bulk || bytes != BytesPerRefill)
            {
                return reservation;
            }

            _ = Interlocked.Add(ref _reserved, bytes);
            return new Reservation(this, reservation);
        }

        private sealed class Reservation(RefillMemory memory, IMemoryReservation inner) : IMemoryReservation
        {
            private int _released;
            public long Bytes => inner.Bytes;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    inner.Dispose();
                    _ = Interlocked.Add(ref memory._reserved, -Bytes);
                }
            }
        }
    }

    /// <summary>A fake clock that records timer creation and resets, so clock advances follow scheduling and response processing.</summary>
    private sealed class ObservedTime : TimeProvider
    {
        private readonly FakeTimeProvider _time = new();
        private readonly Lock _gate = new();
        private readonly List<(TimeSpan Due, TimeSpan Period, DateTimeOffset Created, ObservedTimer Timer)> _timers = [];
        private readonly List<TimeSpan> _changes = [];
        private TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Timers
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count;
                }
            }
        }

        public override long TimestampFrequency => _time.TimestampFrequency;

        public int Changes
        {
            get
            {
                lock (_gate)
                {
                    return _changes.Count;
                }
            }
        }

        public void Advance(TimeSpan delta) => _time.Advance(delta);

        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();

        public override long GetTimestamp() => _time.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var at = _time.GetUtcNow();
            var timer = new ObservedTimer(this, _time.CreateTimer(callback, state, dueTime, period));
            TaskCompletionSource created;
            lock (_gate)
            {
                _timers.Add((dueTime, period, at, timer));
                (created, _created) = (_created, new(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            created.SetResult();
            return timer;
        }

        // Waits for a timer due in `due` among those created after the first `after`.
        public async Task UntilTimerAsync(TimeSpan due, int after, TimeSpan? period = null, DateTimeOffset? createdAt = null, bool activeOnly = false)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (true)
            {
                Task next;
                lock (_gate)
                {
                    if (_timers.Skip(after).Any(timer => timer.Due == due && (period is null || timer.Period == period)
                        && (createdAt is null || timer.Created == createdAt) && (!activeOnly || !timer.Timer.IsDisposed)))
                    {
                        return;
                    }

                    next = _created.Task;
                }

                await next.WaitAsync(deadline.Token);
            }
        }

        public async Task UntilChangeAsync(TimeSpan due, int after, int count = 1)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (true)
            {
                Task next;
                lock (_gate)
                {
                    if (_changes.Skip(after).Count(change => change == due) >= count)
                    {
                        return;
                    }

                    next = _created.Task;
                }

                await next.WaitAsync(deadline.Token);
            }
        }

        private sealed class ObservedTimer(ObservedTime owner, ITimer inner) : ITimer
        {
            private bool _disposed;

            public bool IsDisposed => Volatile.Read(ref _disposed);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = inner.Change(dueTime, period);
                if (changed)
                {
                    TaskCompletionSource created;
                    lock (owner._gate)
                    {
                        owner._changes.Add(dueTime);
                        (created, owner._created) = (owner._created, new(TaskCreationOptions.RunContinuationsAsynchronously));
                    }

                    created.SetResult();
                }

                return changed;
            }

            public void Dispose()
            {
                Volatile.Write(ref _disposed, true);
                inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Volatile.Write(ref _disposed, true);
                return inner.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// A peer listener with the smallest HTTP/2 stream window that welcomes every push stream, answers its heartbeats and
    /// refuses its batches. Its first <c>silentStreams</c> streams go silent once welcomed: they read nothing more and
    /// answer nothing, as a half-open connection, and report when the sender has filled their window. Its first
    /// <c>unansweredStreams</c> streams keep reading after welcoming but never answer another frame.
    /// </summary>
    private sealed class FakePeer : Features.Replication.V1.Replication.ReplicationBase, IAsyncDisposable
    {
        private const int Window = 65_535;
        private readonly Channel<PushFrame> _frames = Channel.CreateBounded<PushFrame>(64);
        private readonly int _silentStreams;
        private readonly int _unansweredStreams;
        private readonly RepairState? _repair;
        private readonly TaskCompletionSource? _refillRelease;
        private readonly TaskCompletionSource? _batchRelease;
        private readonly TaskCompletionSource? _frontierRelease;
        private readonly bool _stableBeforeBatches;
        private readonly bool _stableBurst;
        private readonly bool _coverageRefusal;
        private readonly bool _repeatedCoverageRefusal;
        private readonly Func<Features.Replication.V1.Database, ulong, int, AckFrame[]>? _progress;
        private WebApplication? _app;
        private int _streams;
        private int _batchesBeforeRelease;
        private int _batchesReceived;

        private FakePeer(
            int silentStreams, RepairState? repair, TaskCompletionSource? refillRelease, TaskCompletionSource? batchRelease,
            TaskCompletionSource? frontierRelease, int unansweredStreams, bool stableBeforeBatches, bool stableBurst, bool coverageRefusal, bool repeatedCoverageRefusal,
            Func<Features.Replication.V1.Database, ulong, int, AckFrame[]>? progress)
        {
            _progress = progress;
            _silentStreams = silentStreams;
            _unansweredStreams = unansweredStreams;
            _repair = repair;
            _refillRelease = refillRelease;
            _batchRelease = batchRelease;
            _frontierRelease = frontierRelease;
            _stableBeforeBatches = stableBeforeBatches;
            _stableBurst = stableBurst;
            _coverageRefusal = coverageRefusal;
            _repeatedCoverageRefusal = repeatedCoverageRefusal;
        }

        public string Address { get; private set; } = "";

        public Ulid Incarnation { get; } = Ulid.NewUlid();

        public int Streams => Volatile.Read(ref _streams);
        public int BatchesBeforeRelease => Volatile.Read(ref _batchesBeforeRelease);
        public int BatchesReceived => Volatile.Read(ref _batchesReceived);

        // Completes once a silent stream holds a window's worth of unread frames: the sender cannot write more to it.
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StableSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // With stableBeforeBatches: the held batch ACKs wait for this after the stable report, so the sender's drain of that
        // report has finished its consequences before any ACK can free its budget.
        public Task? AcksAfterStable { get; set; }

        public TaskCompletionSource FirstEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<FakePeer> StartAsync(
            int silentStreams, RepairState? repair = null, TaskCompletionSource? refillRelease = null, TaskCompletionSource? batchRelease = null,
            TaskCompletionSource? frontierRelease = null, int unansweredStreams = 0, bool stableBeforeBatches = false, bool stableBurst = false, bool coverageRefusal = false, bool repeatedCoverageRefusal = false,
            Func<Features.Replication.V1.Database, ulong, int, AckFrame[]>? progress = null)
        {
            var peer = new FakePeer(silentStreams, repair, refillRelease, batchRelease, frontierRelease, unansweredStreams, stableBeforeBatches, stableBurst, coverageRefusal, repeatedCoverageRefusal, progress);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Limits.Http2.InitialStreamWindowSize = Window;
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
            });
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(peer);
            peer._app = builder.Build();
            peer._app.MapGrpcService<FakePeer>();
            await peer._app.StartAsync();
            peer.Address = peer._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return peer;
        }

        // The next frame a stream read, in order.
        public Task<PushFrame> NextAsync() => _frames.Reader.ReadAsync().AsTask().WaitAsync(Deadline);

        public override Task<DigestReply> Digest(DigestRequest request, ServerCallContext context) => Task.FromResult(new DigestReply
        {
            Held = _repair is not null && (ChangeGroups.Incarnation(request.Database) is not { } expected || expected == Incarnation),
            Incarnation = Google.Protobuf.ByteString.CopyFrom(Incarnation.ToByteArray()),
            Buckets = Google.Protobuf.UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(Enumerable.Repeat((byte)1, 4096 * 16).ToArray())),
        });

        public override async Task ReadBucket(BucketRequest request, IServerStreamWriter<StatePage> responseStream, ServerCallContext context)
        {
            if (ChangeGroups.RequiredIncarnation(request.Database) != Incarnation)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "the test peer holds another incarnation"));
            }

            if (_repair is not null)
            {
                await responseStream.WriteAsync(AntiEntropy.Encode([_repair], Incarnation), context.CancellationToken);
            }
        }

        public override async Task Push(IAsyncStreamReader<PushFrame> requestStream, IServerStreamWriter<AckFrame> responseStream, ServerCallContext context)
        {
            var ordinal = Interlocked.Increment(ref _streams);
            try
            {
                if (_batchRelease is not null)
                {
                    await HoldBatchesAsync(requestStream, responseStream, context);
                    return;
                }

                if (_refillRelease is not null)
                {
                    await HoldRefillAsync(requestStream, responseStream, context);
                    return;
                }

                while (await requestStream.MoveNext(context.CancellationToken))
                {
                    var frame = requestStream.Current;
                    var answer = frame.KindCase switch
                    {
                        PushFrame.KindOneofCase.Hello => new AckFrame { Welcome = new Welcome { Node = ReplicaId.Of(1, 9, 0).Value } },
                        PushFrame.KindOneofCase.Heartbeat => new AckFrame(),
                        PushFrame.KindOneofCase.Batch => new AckFrame { Ack = new Ack { Database = frame.Batch.Database, Refused = true, RefillRequired = _coverageRefusal } },
                        PushFrame.KindOneofCase.Refill => new AckFrame { Ack = new Ack { Database = frame.Refill.Database, Refused = true, RefillRequired = _repeatedCoverageRefusal } },
                        _ => null,
                    };
                    if (answer is not null && (ordinal > _unansweredStreams || frame.Hello is not null))
                    {
                        await responseStream.WriteAsync(answer, context.CancellationToken);
                        if (_coverageRefusal && frame.Refill is { } refused)
                        {
                            await responseStream.WriteAsync(new AckFrame
                            {
                                Stable = new StableReport { Database = refused.Database, Stable = 1 },
                            }, context.CancellationToken);
                        }
                    }

                    await _frames.Writer.WriteAsync(frame, context.CancellationToken);
                    if (ordinal == 1 && _frontierRelease is not null && frame.Frontier is { } frontier)
                    {
                        // Hold one earlier Stable reply while the next Batch fills the unread request window.
                        var holding = HoldAsync(context);
                        try
                        {
                            await _frontierRelease.Task.WaitAsync(context.CancellationToken);
                            await responseStream.WriteAsync(new AckFrame
                            {
                                Stable = new StableReport { Database = frontier.Database, Stable = frontier.Hlc },
                            }, context.CancellationToken);
                        }
                        finally
                        {
                            await holding;
                        }

                        return;
                    }

                    if (ordinal <= _silentStreams)
                    {
                        await HoldAsync(context);
                        return;
                    }
                }
            }
            finally
            {
                if (ordinal == 1)
                {
                    _ = FirstEnded.TrySetResult();
                }
            }
        }

        // Serialize delayed batch ACKs and heartbeat responses on the real listener's single response writer.
        private async Task HoldBatchesAsync(IAsyncStreamReader<PushFrame> requests, IServerStreamWriter<AckFrame> responses, ServerCallContext context)
        {
            var released = _batchRelease!.Task;
            var reading = requests.MoveNext(context.CancellationToken);
            Queue<Ack> pending = [];
            Frontier? frontier = null;
            var heartbeats = 0;
            try
            {
                while (true)
                {
                    if (pending.Count > 0)
                    {
                        _ = await Task.WhenAny(reading, released);
                        if (released.IsCompleted)
                        {
                            if (_stableBeforeBatches && frontier is not null)
                            {
                                await responses.WriteAsync(new AckFrame { Stable = new StableReport { Database = frontier.Database, Stable = frontier.Hlc } }, context.CancellationToken);
                                frontier = null;
                                StableSent.SetResult();
                                if (AcksAfterStable is { } gate)
                                {
                                    await gate.WaitAsync(context.CancellationToken);
                                }
                            }

                            while (pending.TryDequeue(out var ack))
                            {
                                await responses.WriteAsync(new AckFrame { Ack = ack }, context.CancellationToken);
                            }
                        }
                    }

                    if (!await reading)
                    {
                        return;
                    }

                    var frame = requests.Current;
                    reading = requests.MoveNext(context.CancellationToken);
                    if (frame.Batch is { } batch)
                    {
                        _ = Interlocked.Increment(ref _batchesReceived);
                        var ack = new Ack { Database = batch.Database, ChangeId = batch.LastChangeId };
                        if (released.IsCompleted)
                        {
                            await responses.WriteAsync(new AckFrame { Ack = ack }, context.CancellationToken);
                        }
                        else
                        {
                            if (pending.Count == 71)
                            {
                                throw new InvalidDataException("the test peer holds at most 71 database acknowledgements");
                            }

                            _ = Interlocked.Increment(ref _batchesBeforeRelease);
                            pending.Enqueue(ack);
                        }
                    }
                    else if (frame.Frontier is not null && (_stableBeforeBatches || _stableBurst))
                    {
                        frontier = frame.Frontier;
                    }
                    else if (frame.Hello is not null)
                    {
                        await responses.WriteAsync(new AckFrame { Welcome = new Welcome { Node = ReplicaId.Of(1, 9, 0).Value } }, context.CancellationToken);
                    }
                    else if (frame.Heartbeat is not null)
                    {
                        if (_stableBurst && frontier is not null)
                        {
                            for (var index = 0; index < 65; index++)
                            {
                                await responses.WriteAsync(new AckFrame
                                {
                                    Stable = new StableReport { Database = frontier.Database, Stable = frontier.Hlc },
                                }, context.CancellationToken);
                            }

                            frontier = null;
                            _ = StableSent.TrySetResult();
                        }

                        if (pending.TryPeek(out var held))
                        {
                            await ProgressAsync(responses, held.Database, held.ChangeId, ++heartbeats, context);
                        }

                        await responses.WriteAsync(new AckFrame(), context.CancellationToken);
                    }

                    await _frames.Writer.WriteAsync(frame, context.CancellationToken);
                }
            }
            finally
            {
                if (context.CancellationToken.IsCancellationRequested)
                {
                    await ((Task)reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }

        // Scripted Applying frames for the held batch or refill, before the heartbeat's empty answer.
        private async Task ProgressAsync(IServerStreamWriter<AckFrame> responses, Features.Replication.V1.Database database, ulong changeId, int heartbeat, ServerCallContext context)
        {
            foreach (var frame in _progress?.Invoke(database, changeId, heartbeat) ?? [])
            {
                await responses.WriteAsync(frame, context.CancellationToken);
            }
        }

        // One serialized response loop keeps answering heartbeats while the refill's durable acknowledgement is held.
        private async Task HoldRefillAsync(IAsyncStreamReader<PushFrame> requests, IServerStreamWriter<AckFrame> responses, ServerCallContext context)
        {
            var released = _refillRelease!.Task;
            var reading = requests.MoveNext(context.CancellationToken);
            Refill? pending = null;
            var heartbeats = 0;
            try
            {
                while (true)
                {
                    if (pending is not null)
                    {
                        _ = await Task.WhenAny(reading, released);
                        if (released.IsCompleted)
                        {
                            await responses.WriteAsync(new AckFrame { Ack = new Ack { Database = pending.Database, ChangeId = pending.ChangeId } }, context.CancellationToken);
                            pending = null;
                        }
                    }

                    if (!await reading)
                    {
                        return;
                    }

                    var frame = requests.Current;
                    reading = requests.MoveNext(context.CancellationToken);
                    if (frame.Refill is { } refill)
                    {
                        pending = refill;
                    }
                    else if (frame.Hello is not null)
                    {
                        await responses.WriteAsync(new AckFrame { Welcome = new Welcome { Node = ReplicaId.Of(1, 9, 0).Value } }, context.CancellationToken);
                    }
                    else if (frame.Heartbeat is not null)
                    {
                        if (pending is not null)
                        {
                            await ProgressAsync(responses, pending.Database, pending.ChangeId, ++heartbeats, context);
                        }

                        await responses.WriteAsync(new AckFrame(), context.CancellationToken);
                    }

                    await _frames.Writer.WriteAsync(frame, context.CancellationToken);
                }
            }
            finally
            {
                if (context.CancellationToken.IsCancellationRequested)
                {
                    await ((Task)reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }

        // Looks at the frames that arrive without consuming them, so the stream's window never reopens, until the call ends.
        private async Task HoldAsync(ServerCallContext context)
        {
            var body = context.GetHttpContext().Request.BodyReader;
            while (true)
            {
                var read = await body.ReadAsync(context.CancellationToken);
                // The window counts the welcomed hello too; less than a frame header's worth of it may stay open.
                if (read.Buffer.Length >= Window - 1024)
                {
                    _ = Stalled.TrySetResult();
                }

                if (read.IsCompleted)
                {
                    return;
                }

                body.AdvanceTo(read.Buffer.Start, read.Buffer.End);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
