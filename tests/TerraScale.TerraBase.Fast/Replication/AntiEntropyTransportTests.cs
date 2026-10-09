using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Bogus;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Admission lasts through real HTTP/2 response writes and returns after aborting a stalled reader.</summary>
public sealed class AntiEntropyTransportTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(120_000)]
    public async Task Unread_repair_responses_release_all_admission_on_timeout_or_peer_cancellation(bool cancelPeer, CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            settings["memory-budget"] = "536870912";
        }, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            var registration = services.Single(service => service.ServiceType == typeof(IMemoryBudget));
            services.Remove(registration);
            services.AddSingleton(provider => new AdmissionProbe((IMemoryBudget)registration.ImplementationFactory!(provider)));
            services.AddSingleton<IMemoryBudget>(provider => provider.GetRequiredService<AdmissionProbe>());
        });
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, ["documents:write"], cancellationToken));
        using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative),
            new { name = "items" }, cancellationToken))
        {
            collection.EnsureSuccessStatusCode();
        }

        using (var created = await writer.PutAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections/items/documents/by-key?id={DocumentApi.NewId()}", UriKind.Relative),
            new { payload = new Faker().Random.AlphaNumeric(900_000) }, cancellationToken))
        {
            created.EnsureSuccessStatusCode();
        }

        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = tenant.Database("repair").DatabaseId! };
        using (var digestCall = client.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            var digest = await digestCall.ResponseAsync;
            identity.Incarnation = digest.Incarnation;
            identity.Origin = ByteString.CopyFrom(EntityIds.New().ToByteArray());
            identity.SourceProof = new JournalProof { Origin = identity.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(identity.Origin.Span)) };
            identity.TargetProof = new JournalProof { Origin = digest.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(digest.Origin.Span)) };
        }

        var ranges = new RangeRequest { Identity = identity, Ranges = ushort.MaxValue };
        using (var read = client.ReadRangeAsync(ranges, cancellationToken: cancellationToken))
        {
            // Larger than the client's stream window plus Kestrel's output buffering, after LZ4.
            await Assert.That((await read.ResponseAsync).Envelope.Length).IsGreaterThan(512 * 1024);
        }

        var probe = server.Services.GetRequiredService<AdmissionProbe>();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(probe.Active == 0));
        using var handler = peerTls.CreateHandler();
        handler.InitialHttp2StreamWindowSize = 65_535;
        using var unreadClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var message = ranges.ToByteArray();
        var frame = new byte[message.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
        message.CopyTo(frame, 5);
        var responses = new List<HttpResponseMessage>((int)ReplicationProtocol.MaxOpenStreams);
        try
        {
            for (var index = 0; index < ReplicationProtocol.MaxOpenStreams; index++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.PeerAddress!, "/terrabase.alpha.AntiEntropy/ReadRange"))
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new ByteArrayContent(frame),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
                request.Headers.Add("TE", "trailers");
                var response = await unreadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                responses.Add(response);
                response.EnsureSuccessStatusCode();
                await Assert.That(response.Version).IsEqualTo(HttpVersion.Version20);
                await Assert.That(response.Headers.Contains("grpc-status")).IsFalse();
                // Consume only the gRPC prefix to prove this is a large response, not a trailers-only error.
                var prefix = new byte[5];
                var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                await body.ReadExactlyAsync(prefix, cancellationToken);
                await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(1))).IsGreaterThan(512U * 1024);
            }

            var beforeAdvance = probe.Active;
            await Assert.That(beforeAdvance).IsEqualTo((int)ReplicationProtocol.MaxOpenStreams);
            using (var refused = client.DigestAsync(identity, cancellationToken: cancellationToken))
            {
                var error = await Assert.ThrowsAsync<RpcException>(async () => await refused.ResponseAsync);
                await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
                await Assert.That(error.Status.Detail).IsEqualTo("too many anti-entropy calls");
            }

            var remaining = TimeSpan.FromMilliseconds(1);
            clock.Advance(ReplicationProtocol.ProgressTimeout - remaining);
            var beforeTimeout = probe.Active;
            await Assert.That(beforeTimeout).IsEqualTo((int)ReplicationProtocol.MaxOpenStreams);
            if (cancelPeer)
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }

                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(probe.Active == 0));
            }

            clock.Advance(remaining);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(probe.Active == 0));
            // In the timeout case all unread responses remain owned by the peer: only the server aborted them.
            using var recoveredChannel = peerTls.CreateChannel(server.PeerAddress!);
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                using var recovered = new AntiEntropy.AntiEntropyClient(recoveredChannel).DigestAsync(identity, cancellationToken: token);
                try
                {
                    await Assert.That((await recovered.ResponseAsync).Envelope.IsEmpty).IsFalse();
                    return true;
                }
                catch (RpcException error) when (error.StatusCode == StatusCode.ResourceExhausted)
                {
                    // Reservation disposal precedes the final call-count decrement in response completion.
                    return false;
                }
            });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(probe.Active == 0));
            await Assert.That(probe.Reservations.All(reservation => reservation.Disposals == 1)).IsTrue();
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private sealed class AdmissionProbe(IMemoryBudget inner) : IMemoryBudget
    {
        private readonly ObservedReservation?[] _reservations = new ObservedReservation[32];
        private int _count;

        public IEnumerable<ObservedReservation> Reservations => _reservations.Take(Volatile.Read(ref _count)).OfType<ObservedReservation>();
        public int Active => Reservations.Count(reservation => reservation.Disposals == 0);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes)
        {
            var held = inner.TryReserve(lane, bytes);
            if (held is null || lane != MemoryLane.Bulk)
            {
                return held;
            }

            var index = Interlocked.Increment(ref _count) - 1;
            if (index >= _reservations.Length)
            {
                held.Dispose();
                throw new InvalidOperationException("the repair admission observation bound was exceeded");
            }

            var observed = new ObservedReservation(held);
            Volatile.Write(ref _reservations[index], observed);
            return observed;
        }
    }

    private sealed class ObservedReservation(IMemoryReservation inner) : IMemoryReservation
    {
        private int _disposals;

        public long Bytes => inner.Bytes;
        public int Disposals => Volatile.Read(ref _disposals);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            inner.Dispose();
        }
    }
}
