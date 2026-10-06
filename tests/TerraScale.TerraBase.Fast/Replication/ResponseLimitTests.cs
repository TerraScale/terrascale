using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Features.Replication.V1;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Replication;

public sealed class ResponseLimitTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Missing_or_zero_Hello_limits_receive_an_explicit_nonretryable_refusal(bool missing)
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var channel = tls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: deadline.Token);
        var hello = ReplicationProtocol.CreateHello(tenant.Database("db"), RandomNumberGenerator.GetBytes(16));
        if (missing)
        {
            hello.Limits = null;
        }
        else
        {
            hello.Limits.MaxGrpcMessageBytes = 0;
        }

        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello }, deadline.Token);
        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorIncompatibleVersion);
        await Assert.That(call.ResponseStream.Current.Error.Retryable).IsFalse();
        await Assert.That(await call.ResponseStream.MoveNext(deadline.Token)).IsFalse();
    }

    [Test]
    public async Task ACKs_discard_unknown_fields_on_both_event_identity_and_origin_after_a_bounded_commit()
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["region"] = new string('a', 63);
        });
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var origin = RandomNumberGenerator.GetBytes(16);
        var batch = ReplicationProtocol.CreateBatch(tenant.Database("db"), origin, 1, [new DocumentChange("c", "a", 1, null)]);
        batch.EventId.Origin = OriginIdentity.Parser.ParseFrom(WithUnknownField(batch.EventId.Origin));
        batch.EventId = CommittedEventId.Parser.ParseFrom(WithUnknownField(batch.EventId));
        var request = new SyncRequest { CommittedGroup = batch };
        var cap = (uint)request.CalculateSize();
        using var channel = tls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: token);
        var hello = ReplicationProtocol.CreateHello(tenant.Database("db"), origin);
        hello.Limits.MaxGrpcMessageBytes = cap;
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello }, token);
        await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
        var welcome = call.ResponseStream.Current.Welcome;
        await Assert.That(call.ResponseStream.Current.CalculateSize()).IsLessThanOrEqualTo((int)cap);
        var echoed = new SyncResponse
        {
            Durable = new DurableAck
            {
                Context = new AckContext { AcknowledgingPeer = welcome.LocalIdentity, EventId = batch.EventId },
                DurablePrefix = 1,
                JournalDigest = batch.JournalDigest,
            },
        };
        // This request fits, but echoing the nested unknown fields would overflow the ACK cap.
        await Assert.That(echoed.CalculateSize()).IsGreaterThan((int)cap);
        await call.RequestStream.WriteAsync(request, token);
        foreach (var frame in new[] { SyncResponse.FrameOneofCase.Durable, SyncResponse.FrameOneofCase.Applied })
        {
            await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
            var response = call.ResponseStream.Current;
            await Assert.That(response.FrameCase).IsEqualTo(frame);
            await Assert.That(response.CalculateSize()).IsLessThanOrEqualTo((int)cap);
            var context = response.Durable?.Context ?? response.Applied.Context;
            var known = new CommittedEventId
            {
                TenantId = batch.EventId.TenantId,
                ProjectId = batch.EventId.ProjectId,
                DatabaseId = batch.EventId.DatabaseId,
                Sequence = batch.EventId.Sequence,
                Origin = new OriginIdentity { ReplicaId = batch.EventId.Origin.ReplicaId, Incarnation = batch.EventId.Origin.Incarnation },
            };
            await Assert.That(context.EventId.Equals(known)).IsTrue();
        }

        await call.RequestStream.CompleteAsync();
        await Assert.That(await call.ResponseStream.MoveNext(token)).IsFalse();
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), token))!;
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAppliedSequenceAsync(origin, token)).IsEqualTo(1);
        await Assert.That((await read.GetVersionAsync("c", "a", token))!.Value.Deleted).IsTrue();
    }

    private static byte[] WithUnknownField(IMessage message)
    {
        using var encoded = new MemoryStream();
        using (var output = new CodedOutputStream(encoded, leaveOpen: true))
        {
            message.WriteTo(output);
            output.WriteTag(100, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(new byte[256]));
            output.Flush();
        }

        return encoded.ToArray();
    }

    [Test]
    public async Task An_oversized_Welcome_cannot_publish_an_otherwise_valid_caught_up_prefix()
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        var data = Directory.CreateTempSubdirectory("terrabase-welcome-cap-").FullName;
        try
        {
            ReplicationTenant tenant;
            ReadOnlyMemory<byte> proof;
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                clientTls.Apply(settings);
                settings["data-directory"] = data;
            }))
            {
                tenant = await ReplicationTenant.CreateAsync(seed, "db");
                using var writer = clientTls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, "documents:write"));
                using var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" });
                collection.EnsureSuccessStatusCode();
                using var written = await writer.PutAsJsonAsync(new Uri($"{tenant.Path("db")}/collections/items/documents/{Guid.CreateVersion7()}", UriKind.Relative), new { value = "seed" });
                written.EnsureSuccessStatusCode();
                var store = (await seed.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), CancellationToken.None))!;
                await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
                // Group 1 created the collection; group 2 is the head.
                proof = (await read.GetChangeDigestAsync(2, CancellationToken.None))!.Value;
            }

            var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streams = 0;
            await using var peer = await FakePeer.StartAsync(tls, async (requests, responses, context) =>
            {
                if (!await requests.MoveNext(context.CancellationToken))
                {
                    return;
                }

                if (Interlocked.Increment(ref streams) > 1)
                {
                    retry.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                    return;
                }

                var hello = requests.Current.Hello;
                var limits = hello.Limits.Clone();
                limits.MaxGrpcMessageBytes = 512;
                var answer = FakePeer.Welcome(hello, out _, limits);
                answer.Welcome.Frontiers.Add(new PeerFrontier
                {
                    Origin = new OriginIdentity { ReplicaId = hello.LocalIdentity.MemberIncarnation, Incarnation = hello.LocalIdentity.MemberIncarnation },
                    DurableSequence = 2,
                    JournalDigest = ByteString.CopyFrom(proof.Span),
                });
                using var encoded = new MemoryStream();
                using (var output = new CodedOutputStream(encoded, leaveOpen: true))
                {
                    answer.WriteTo(output);
                    output.WriteTag(9, WireFormat.WireType.LengthDelimited);
                    output.WriteBytes(ByteString.CopyFrom(new byte[1024]));
                    output.Flush();
                }

                await responses.WriteAsync(SyncResponse.Parser.ParseFrom(encoded.ToArray()), context.CancellationToken);
                while (await requests.MoveNext(context.CancellationToken))
                {
                    throw new InvalidOperationException("the caught-up prefix requires no group transmission");
                }
            });
            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["data-directory"] = data;
                settings["peer-addresses"] = peer.Address.ToString();
            });
            await retry.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag")).IsEqualTo(2);
            release.TrySetResult();
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Test]
    [Arguments(1u)]
    [Arguments(128u)]
    public async Task A_cap_too_small_for_required_control_responses_ends_without_an_oversized_frame(uint cap)
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var channel = tls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: deadline.Token);
        var hello = ReplicationProtocol.CreateHello(tenant.Database("db"), RandomNumberGenerator.GetBytes(16));
        hello.Limits.MaxGrpcMessageBytes = cap;
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello }, deadline.Token);
        var error = await Assert.That(async () => await call.ResponseStream.MoveNext(deadline.Token)).Throws<RpcException>();
        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Welcome_ACKs_and_keepalives_obey_the_negotiated_serialized_response_cap(bool credit)
    {
        using var tls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var channel = tls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: token);
        var origin = RandomNumberGenerator.GetBytes(16);
        var hello = ReplicationProtocol.CreateHello(tenant.Database("db"), origin);
        hello.Limits.MaxGrpcMessageBytes = 512;
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello }, token);
        await ReadWithinCapAsync(SyncResponse.FrameOneofCase.Welcome);
        await call.RequestStream.WriteAsync(new SyncRequest
        {
            CommittedGroup = ReplicationProtocol.CreateBatch(tenant.Database("db"), origin, 1,
                [new DocumentChange("items", "a", 1, "{}"u8.ToArray())]),
        }, token);
        await ReadWithinCapAsync(SyncResponse.FrameOneofCase.Durable);
        await ReadWithinCapAsync(SyncResponse.FrameOneofCase.Applied);
        await call.RequestStream.WriteAsync(new SyncRequest { KeepAlive = new KeepAlive { Nonce = ulong.MaxValue } }, token);
        await ReadWithinCapAsync(SyncResponse.FrameOneofCase.KeepAlive);

        // Unknown fields are retained by Protobuf and would make a blindly echoed keepalive oversized.
        using var encoded = new MemoryStream();
        using (var writer = new CodedOutputStream(encoded, leaveOpen: true))
        {
            writer.WriteTag(2, WireFormat.WireType.LengthDelimited);
            writer.WriteBytes(ByteString.CopyFrom(new byte[1024]));
            writer.Flush();
        }

        await call.RequestStream.WriteAsync(credit
            ? new SyncRequest { Credit = CreditUpdate.Parser.ParseFrom(encoded.ToArray()) }
            : new SyncRequest { KeepAlive = KeepAlive.Parser.ParseFrom(encoded.ToArray()) }, token);
        var error = await Assert.That(async () => await call.ResponseStream.MoveNext(token)).Throws<RpcException>();
        await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);

        async Task ReadWithinCapAsync(SyncResponse.FrameOneofCase frame)
        {
            await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.FrameCase).IsEqualTo(frame);
            await Assert.That(call.ResponseStream.Current.CalculateSize()).IsLessThanOrEqualTo(512);
        }
    }
}
