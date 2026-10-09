using System.Net;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
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
        using var owner = source.As(tenant.UserId);
        await source.VerifyProjectBindingAsync(tenant.OrganizationId, tenant.ProjectId, owner);
        using var feed = source.WithToken(source.Issuer.Mint(tenant.UserId, jwt =>
        {
            jwt.Claims["project_id"] = tenant.ProjectId;
            jwt.Claims["scope"] = "documents:read";
        }));
        var feedRoute = $"/projects/{tenant.ProjectId}/databases/db/collections/people/changes";
        var start = await DocumentApi.SendAsync(feed, HttpMethod.Get, feedRoute, cancellationToken: cancellationToken);
        start.Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That(start.Json.GetProperty("changes").EnumerateArray().Single().GetProperty("id").GetString()).IsEqualTo(ids[1]);
        await ReplayAsync(receiver.Server, source.Server, tls, tenant.Database("db"), cancellationToken);
        await ReplayAsync(source.Server, receiver.Server, tls, tenant.Database("db"), cancellationToken);
        var merged = await DocumentApi.SendAsync(feed, HttpMethod.Get,
            $"{feedRoute}?resume_token={start.Json.GetProperty("resume_token").GetString()}", cancellationToken: cancellationToken);
        await AssertUniqueFeedTransitionAsync(merged, ids[1], ids[0], email);

        using var client = source.Tls.CreateClient(source.Server.ClientAddress!,
            await tenant.KeyAsync(source.Server, ["documents:read", "documents:write"], cancellationToken));
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
            Identity = await UniqueRepairIdentityAsync(source.Server, receiver.Server, tenant, cancellationToken),
            Ranges = ushort.MaxValue,
        };
        using var readCall = receiverRpc.ReadRangeAsync(request, cancellationToken: cancellationToken);
        var page = await readCall.ResponseAsync;
        await Assert.That(page.HasMore).IsFalse();
        var changes = ChangeGroup.Decode(ApplicationEnvelope.Decode(page.Envelope.Span, ReplicationProtocol.MaxApplicationBytes));
        await Assert.That(changes.Single(change => change.Collection == "people" && change.Id == ids[1]).Json is not null).IsTrue();
        using var mergeCall = sourceRpc.MergeAsync(new StateRequest
        {
            Identity = await UniqueRepairIdentityAsync(receiver.Server, source.Server, tenant, cancellationToken),
            Envelope = page.Envelope,
        }, cancellationToken: cancellationToken);
        await Assert.That((await mergeCall.ResponseAsync).RepairedKeys).IsEqualTo(0U);
        var unchanged = await DocumentApi.SendAsync(feed, HttpMethod.Get,
            $"{feedRoute}?resume_token={merged.Json.GetProperty("resume_token").GetString()}", cancellationToken: cancellationToken);
        unchanged.Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That(unchanged.Json.GetProperty("changes").GetArrayLength()).IsEqualTo(0);
        await Assert.That(unchanged.Json.GetProperty("resume_token").GetString()).IsEqualTo(merged.Json.GetProperty("resume_token").GetString());
        using (var hidden = await client.GetAsync(Document(tenant, ids[1]), cancellationToken))
        {
            await Assert.That(hidden.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }

        using (var deleted = await client.DeleteAsync(Document(tenant, ids[0]), cancellationToken))
        {
            deleted.EnsureSuccessStatusCode();
        }

        await VisibleAsync(client, tenant, ids[1], cancellationToken);
        var promoted = await DocumentApi.SendAsync(feed, HttpMethod.Get,
            $"{feedRoute}?resume_token={unchanged.Json.GetProperty("resume_token").GetString()}", cancellationToken: cancellationToken);
        await AssertUniqueFeedTransitionAsync(promoted, ids[0], ids[1], email);
        await ReplayAsync(source.Server, receiver.Server, tls, tenant.Database("db"), cancellationToken);
        using var sourceAfter = sourceRpc.DigestAsync(identity, cancellationToken: cancellationToken);
        using var receiverAfter = receiverRpc.DigestAsync(identity, cancellationToken: cancellationToken);
        await Assert.That((await sourceAfter.ResponseAsync).Envelope).IsEqualTo((await receiverAfter.ResponseAsync).Envelope);
    }

    private static async Task AssertUniqueFeedTransitionAsync(ApiResponse page, string deletedId, string visibleId, string email)
    {
        page.Status.ShouldBe(HttpStatusCode.OK);
        var changes = page.Json.GetProperty("changes").EnumerateArray().ToArray();
        await Assert.That(changes.Length).IsEqualTo(2);
        var deleted = changes.Single(change => change.GetProperty("id").GetString() == deletedId);
        var visible = changes.Single(change => change.GetProperty("id").GetString() == visibleId);
        await Assert.That(deleted.GetProperty("operation").GetString()).IsEqualTo("delete");
        await Assert.That(deleted.GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(visible.GetProperty("operation").GetString()).IsEqualTo("upsert");
        await Assert.That(visible.GetProperty("document").GetProperty("email").GetString()).IsEqualTo(email);
        await Assert.That(changes[0].GetProperty("offset").GetString()).IsEqualTo(changes[1].GetProperty("offset").GetString());
        await Assert.That(long.Parse(changes[0].GetProperty("sequence").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .IsLessThan(long.Parse(changes[1].GetProperty("sequence").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(page.Json.GetProperty("has_more").GetBoolean()).IsFalse();
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
