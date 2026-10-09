using System.Net;
using Bogus;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed partial class UniqueReplicationTests
{
    [Test]
    public async Task Merkle_digest_and_identical_repair_preserve_hidden_contenders_and_their_promotion(CancellationToken cancellationToken)
    {
        using var tls = new PeerTls();
        await using var source = await ManagementApi.StartAsync(tls.Apply);
        await using var receiver = await ManagementApi.StartAsync(tls.Apply);
        var tenant = await ReplicationTenant.CreateAsync(source.Server, ["db"], cancellationToken);
        await tenant.BindAsync(receiver.Server);
        var ids = new[] { Ulid.NewUlid().ToString(), Ulid.NewUlid().ToString() }.Order(StringComparer.Ordinal).ToArray();
        var email = new Faker().Internet.Email();
        await SeedAsync(source, tenant, ids[1], email, cancellationToken);
        await SeedAsync(receiver, tenant, ids[0], email, cancellationToken);
        await ReplayAsync(source.Server, receiver.Server, tls, tenant.Database("db"), cancellationToken);
        await ReplayAsync(receiver.Server, source.Server, tls, tenant.Database("db"), cancellationToken);

        using var client = receiver.Tls.CreateClient(receiver.Server.ClientAddress!,
            await tenant.KeyAsync(receiver.Server, ["documents:read", "documents:write"], cancellationToken));
        using var sourceChannel = tls.CreateChannel(source.Server.PeerAddress!);
        using var receiverChannel = tls.CreateChannel(receiver.Server.PeerAddress!);
        var sourceRpc = new AntiEntropy.AntiEntropyClient(sourceChannel);
        var receiverRpc = new AntiEntropy.AntiEntropyClient(receiverChannel);
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = tenant.Database("db").DatabaseId! };
        using var sourceDigestCall = sourceRpc.DigestAsync(identity, cancellationToken: cancellationToken);
        using var receiverDigestCall = receiverRpc.DigestAsync(identity, cancellationToken: cancellationToken);
        await Assert.That((await sourceDigestCall.ResponseAsync).Envelope).IsEqualTo((await receiverDigestCall.ResponseAsync).Envelope);

        var request = new RangeRequest
        {
            Identity = await UniqueRepairIdentityAsync(receiver.Server, source.Server, tenant, cancellationToken),
            Ranges = ushort.MaxValue,
        };
        using var readCall = sourceRpc.ReadRangeAsync(request, cancellationToken: cancellationToken);
        var page = await readCall.ResponseAsync;
        await Assert.That(page.HasMore).IsFalse();
        var changes = ChangeGroup.Decode(ApplicationEnvelope.Decode(page.Envelope.Span, ReplicationProtocol.MaxApplicationBytes));
        await Assert.That(changes.Single(change => change.Collection == "people" && change.Id == ids[1]).Json is not null).IsTrue();
        using var mergeCall = receiverRpc.MergeAsync(new StateRequest
        {
            Identity = await UniqueRepairIdentityAsync(source.Server, receiver.Server, tenant, cancellationToken),
            Envelope = page.Envelope,
        }, cancellationToken: cancellationToken);
        await Assert.That((await mergeCall.ResponseAsync).RepairedKeys).IsEqualTo(0U);
        using (var hidden = await client.GetAsync(Document(tenant, ids[1]), cancellationToken))
        {
            await Assert.That(hidden.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }

        using (var deleted = await client.DeleteAsync(Document(tenant, ids[0]), cancellationToken))
        {
            deleted.EnsureSuccessStatusCode();
        }

        await VisibleAsync(client, tenant, ids[1], cancellationToken);
        await ReplayAsync(receiver.Server, source.Server, tls, tenant.Database("db"), cancellationToken);
        using var sourceAfter = sourceRpc.DigestAsync(identity, cancellationToken: cancellationToken);
        using var receiverAfter = receiverRpc.DigestAsync(identity, cancellationToken: cancellationToken);
        await Assert.That((await sourceAfter.ResponseAsync).Envelope).IsEqualTo((await receiverAfter.ResponseAsync).Envelope);
    }

    private static async Task<ShardIdentity> UniqueRepairIdentityAsync(
        TerraBaseTestServer initiator, TerraBaseTestServer target, ReplicationTenant tenant, CancellationToken token)
    {
        var database = tenant.Database("db");
        var sourceStore = (await initiator.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token))!;
        var targetStore = (await target.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, token))!;
        await using var sourceRead = await sourceStore.BeginTransactionAsync(write: false, token);
        await using var targetRead = await targetStore.BeginTransactionAsync(write: false, token);
        var sourceOrigin = await sourceRead.GetOriginAsync(token);
        var targetOrigin = await targetRead.GetOriginAsync(token);
        var (sourceSequence, sourceDigest) = await targetRead.GetAppliedFrontierAsync(sourceOrigin, token);
        var (targetSequence, targetDigest) = await sourceRead.GetAppliedFrontierAsync(targetOrigin, token);
        return new ShardIdentity
        {
            Organization = tenant.OrganizationId,
            Project = tenant.ProjectId,
            Database = database.DatabaseId!,
            Incarnation = ByteString.CopyFrom(targetStore.Incarnation.ToByteArray()),
            Origin = ByteString.CopyFrom(sourceOrigin.Span),
            SourceProof = new JournalProof { Origin = ByteString.CopyFrom(sourceOrigin.Span), Sequence = checked((ulong)sourceSequence), Digest = ByteString.CopyFrom(sourceDigest.Span) },
            TargetProof = new JournalProof { Origin = ByteString.CopyFrom(targetOrigin.Span), Sequence = checked((ulong)targetSequence), Digest = ByteString.CopyFrom(targetDigest.Span) },
        };
    }
}
