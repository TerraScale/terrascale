using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
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
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
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
        using var channel = PeerChannel.Create(api.Server.BaseAddress);
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
        using var channel = PeerChannel.Create(api.Server.BaseAddress);
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
        var source = new FakeChangeGroupSource();
        await using var api = await StartSenderAsync(peer, source, time);

        await Assert.That((await peer.NextAsync()).Hello).IsNotNull();
        // Wait for each idle timer before advancing: receipt of Hello alone does not mean the sender read Welcome yet.
        var timers = 0;
        var started = time.GetUtcNow();
        while (time.GetUtcNow() - started < 2 * ReplicationProtocol.ProgressTimeout)
        {
            await time.UntilTimerAsync(Idle, timers);
            timers = time.Timers;
            time.Advance(Idle);
            await Assert.That((await peer.NextAsync()).KindCase).IsEqualTo(PushFrame.KindOneofCase.Heartbeat);
        }

        await Assert.That(peer.Streams).IsEqualTo(1);
    }

    [Test]
    public async Task The_sender_ends_a_stream_whose_batch_write_is_blocked_by_flow_control_and_reconnects()
    {
        var time = new ObservedTime();
        await using var peer = await FakePeer.StartAsync(silentStreams: 1);
        var source = new FakeChangeGroupSource();
        await using var api = await StartSenderAsync(peer, source, time);
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

    private static async Task<ManagementApi> StartSenderAsync(FakePeer peer, FakeChangeGroupSource source, ObservedTime time)
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
            services.AddSingleton<IChangeGroupSource>(source);
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreams>(provider, time));
        });
        // Bootstrap the empty catalog before discovering a transport-only fake, which cannot verify a catalog.
        members.Join(new ClusterMember("zzzz-peer", peer.Address, "sjc", ""));
        await time.UntilTimerAsync(DirectoryMembership.Heartbeat, after: 0);
        time.Advance(DirectoryMembership.Heartbeat);
        return api;
    }

    // A database of this machine placed in the peer's region too.
    private static async Task<TenantContext> CreateDatabaseAsync(ManagementApi api)
    {
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var commit = JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object[]
            {
                new { op = "create_organization", id = database.OrganizationId, name = $"peers-{database.OrganizationId}" },
                new { op = "create_project", organization = database.OrganizationId, id = database.ProjectId, name = "peers" },
                new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "main", id = database.DatabaseId, replica_regions = ReplicaRegions },
            },
        });
        using var service = api.Service(api.User);
        using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
        await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        return database;
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

    /// <summary>A fake clock that records every timer created on it, so a test moves it only once that timer exists.</summary>
    private sealed class ObservedTime : TimeProvider
    {
        private readonly FakeTimeProvider _time = new();
        private readonly Lock _gate = new();
        private readonly List<TimeSpan> _timers = [];
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

        public void Advance(TimeSpan delta) => _time.Advance(delta);

        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();

        public override long GetTimestamp() => _time.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _time.CreateTimer(callback, state, dueTime, period);
            TaskCompletionSource created;
            lock (_gate)
            {
                _timers.Add(dueTime);
                (created, _created) = (_created, new(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            created.SetResult();
            return timer;
        }

        // Waits for a timer due in `due` among those created after the first `after`.
        public async Task UntilTimerAsync(TimeSpan due, int after)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (true)
            {
                Task next;
                lock (_gate)
                {
                    if (_timers.Skip(after).Contains(due))
                    {
                        return;
                    }

                    next = _created.Task;
                }

                await next.WaitAsync(deadline.Token);
            }
        }
    }

    /// <summary>
    /// A peer listener with the smallest HTTP/2 stream window that welcomes every push stream, answers its heartbeats and
    /// refuses its batches. Its first <c>silentStreams</c> streams go silent once welcomed: they read nothing more and
    /// answer nothing, as a half-open connection, and report when the sender has filled their window.
    /// </summary>
    private sealed class FakePeer : Features.Replication.V1.Replication.ReplicationBase, IAsyncDisposable
    {
        private const int Window = 65_535;
        private readonly Channel<PushFrame> _frames = Channel.CreateBounded<PushFrame>(64);
        private readonly int _silentStreams;
        private WebApplication? _app;
        private int _streams;

        private FakePeer(int silentStreams)
        {
            _silentStreams = silentStreams;
        }

        public string Address { get; private set; } = "";

        public int Streams => Volatile.Read(ref _streams);

        // Completes once a silent stream holds a window's worth of unread frames: the sender cannot write more to it.
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<FakePeer> StartAsync(int silentStreams)
        {
            var peer = new FakePeer(silentStreams);
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

        public override async Task Push(IAsyncStreamReader<PushFrame> requestStream, IServerStreamWriter<AckFrame> responseStream, ServerCallContext context)
        {
            var ordinal = Interlocked.Increment(ref _streams);
            using var ended = context.CancellationToken.Register(() => _ = ordinal == 1 && FirstEnded.TrySetResult());
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                var frame = requestStream.Current;
                var answer = frame.KindCase switch
                {
                    PushFrame.KindOneofCase.Hello => new AckFrame { Welcome = new Welcome { Node = ReplicaId.Of(1, 9, 0).Value } },
                    PushFrame.KindOneofCase.Heartbeat => new AckFrame(),
                    PushFrame.KindOneofCase.Batch => new AckFrame { Ack = new Ack { Database = frame.Batch.Database, Refused = true } },
                    PushFrame.KindOneofCase.Refill => new AckFrame { Ack = new Ack { Database = frame.Refill.Database, Refused = true } },
                    _ => null,
                };
                if (answer is not null)
                {
                    await responseStream.WriteAsync(answer, context.CancellationToken);
                }

                await _frames.Writer.WriteAsync(frame, context.CancellationToken);
                if (ordinal <= _silentStreams)
                {
                    await HoldAsync(context);
                    return;
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
