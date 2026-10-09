using System.Text;
using Bogus;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Legacy storage boundaries are enforced through the real mutual TLS repair listener.</summary>
public sealed class AntiEntropyBoundaryTests
{
    [Test]
    [Timeout(120_000)]
    public async Task Legacy_invalid_keys_do_not_change_digests_or_hide_valid_live_and_deleted_states(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, ["documents:write"], cancellationToken));
        using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative),
            new { name = "items" }, cancellationToken))
        {
            collection.EnsureSuccessStatusCode();
        }

        var ids = Enumerable.Range(0, 130).Select(i => $"00000000-0000-7000-8000-{i:x12}").ToArray();
        var deleted = ids.Where((_, index) => index % 2 == 1).ToArray();
        var name = new Faker().Name.FullName();
        var transactions = new Uri($"{tenant.Path("repair")}/transactions", UriKind.Relative);
        using (var created = await writer.PostAsJsonAsync(transactions,
            new { ops = ids.Select(id => new { op = "create", collection = "items", id, document = new { name } }).ToArray() }, cancellationToken))
        {
            created.EnsureSuccessStatusCode();
        }

        using (var removed = await writer.PostAsJsonAsync(transactions,
            new { ops = deleted.Select(id => new { op = "delete", collection = "items", id }).ToArray() }, cancellationToken))
        {
            removed.EnsureSuccessStatusCode();
        }

        // 768 UTF-8 bytes is the maximum for a valid 256-UTF-16-unit ID.
        var unicode = new string('\u754c', 256);
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(tenant.File(server, "repair")));
        await connection.OpenAsync(cancellationToken);
        using (var legacy = connection.CreateCommand())
        {
            legacy.CommandText = "INSERT INTO documents VALUES ('items', $id, 1, '{}');";
            legacy.Parameters.AddWithValue("$id", unicode);
            await legacy.ExecuteNonQueryAsync(cancellationToken);
        }

        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        var (identity, before) = await IdentityAsync(client, tenant, cancellationToken);
        using (var legacy = connection.CreateCommand())
        {
            // More than a metadata page of managed-invalid keys still exercises empty continuations.
            // Sequence zero leaves the public journal's bilateral proofs unchanged.
            legacy.CommandText = """
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 129)
                INSERT INTO documents SELECT 'items', '!' || replace(hex(zeroblob(252)), '00', 'd') || printf('%04d', n), 1, '{}' FROM numbers;
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 129)
                INSERT INTO changes SELECT 0, n, 'items', '!' || replace(hex(zeroblob(252)), '00', 't') || printf('%04d', n), 1, NULL FROM numbers;
                INSERT INTO documents VALUES ('items', $short, 1, '{}'), ('items', $edge, 1, '{}'),
                    ('items', $long, 1, '{}'), ($collection, 'live', 1, '{}');
                INSERT INTO changes VALUES (0, 130, 'items', substr($short, 1, 256) || 'y', 1, NULL),
                    (0, 131, 'items', substr($edge, 1, 1023) || 'y', 1, NULL), (0, 132, 'items', $long || 'y', 1, NULL),
                    (0, 133, $collection, 'deleted', 1, NULL);
                INSERT INTO documents VALUES ('items', '', 1, '{}'), ('', 'empty', 1, '{}');
                INSERT INTO changes VALUES (0, 134, 'items', '', 1, NULL), (0, 135, '', 'empty', 1, NULL);
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 4095)
                INSERT INTO documents SELECT 'items', '!' || replace(hex(zeroblob(1020)), '00', 'a') || printf('%04d', n), 1, '{}' FROM numbers;
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 4095)
                INSERT INTO changes SELECT 0, 136 + n, 'items', '!' || replace(hex(zeroblob(1020)), '00', 'b') || printf('%04d', n), 1, NULL FROM numbers;
                INSERT INTO documents VALUES ($prefixCollection, 'live', 1, '{}');
                INSERT INTO changes VALUES (0, 4232, $prefixCollection, 'deleted', 1, NULL);
                """;
            legacy.Parameters.AddWithValue("$short", ids[126] + new string('x', 221));
            legacy.Parameters.AddWithValue("$edge", ids[127] + new string('x', 988));
            legacy.Parameters.AddWithValue("$long", ids[128] + new string('x', 989));
            legacy.Parameters.AddWithValue("$collection", new string('z', 64));
            legacy.Parameters.AddWithValue("$prefixCollection", new string('!', 64));
            await legacy.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var digestCall = client.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            var after = await digestCall.ResponseAsync;
            await Assert.That(ApplicationEnvelope.Decode(after.Envelope.Span, ReplicationProtocol.MaxApplicationBytes)
                .SequenceEqual(ApplicationEnvelope.Decode(before.Envelope.Span, ReplicationProtocol.MaxApplicationBytes))).IsTrue();
        }

        foreach (var sql in new[] { SqliteStatements.ReplicationStateFirst, SqliteStatements.ReplicationStateAfter })
        {
            var plan = await QueryPlan.ExplainAsync(tenant.File(server, "repair"), sql);
            await Assert.That(plan).Contains("INDEX documents_replication_keys");
            await Assert.That(plan).Contains("USING INDEX changes_replication_deleted");
            await Assert.That(plan.Contains("SCAN documents |", StringComparison.Ordinal)).IsFalse();
        }

        // Both entry points must seek past SQL-excluded prefixes without hiding valid live or deleted keys.
        foreach (var continuation in new[] { false, true })
        {
            var request = new RangeRequest
            {
                Identity = identity,
                Ranges = ushort.MaxValue,
                AfterCollection = continuation ? "items" : "",
                AfterId = continuation ? "!" : "",
            };
            var seen = new HashSet<(string Collection, string Id)>();
            var tombstones = new List<string>();
            var pages = 0;
            var emptyContinuations = 0;
            while (true)
            {
                using var readCall = client.ReadRangeAsync(request, cancellationToken: cancellationToken);
                var page = await readCall.ResponseAsync;
                var changes = page.Envelope.IsEmpty ? [] : ChangeGroup.Decode(ApplicationEnvelope.Decode(page.Envelope.Span, ReplicationProtocol.MaxApplicationBytes));
                await Assert.That(changes.Count <= 128).IsTrue();
                await Assert.That(ChangeGroup.ContentBytes(changes) <= IStorageTransaction.MaxChangeGroupBytes).IsTrue();
                await Assert.That(++pages <= 5).IsTrue();
                if (page.Envelope.IsEmpty && page.HasMore)
                {
                    emptyContinuations++;
                    await Assert.That(DocumentChange.HasValidKey(page.AfterCollection, page.AfterId)).IsFalse();
                }

                foreach (var change in changes)
                {
                    await Assert.That(DocumentChange.HasValidKey(change.Collection, change.Id)).IsTrue();
                    await Assert.That(seen.Add((change.Collection, change.Id))).IsTrue();
                    if (change.Collection == "items" && change.Json is null)
                    {
                        tombstones.Add(change.Id);
                    }
                }

                await Assert.That(page.AfterCollection.Length > 0 && Encoding.UTF8.GetByteCount(page.AfterCollection) <= 63).IsTrue();
                await Assert.That(page.AfterId.Length > 0 && Encoding.UTF8.GetByteCount(page.AfterId) <= 1024).IsTrue();
                await Assert.That(string.CompareOrdinal(page.AfterCollection, request.AfterCollection) > 0
                    || (page.AfterCollection == request.AfterCollection && string.CompareOrdinal(page.AfterId, request.AfterId) > 0)).IsTrue();
                if (!page.HasMore)
                {
                    break;
                }

                request.AfterCollection = page.AfterCollection;
                request.AfterId = page.AfterId;
            }

            await Assert.That(pages).IsGreaterThanOrEqualTo(2);
            await Assert.That(emptyContinuations).IsGreaterThanOrEqualTo(1);
            var expected = ids.Append(unicode).Select(id => ("items", id));
            await Assert.That(seen).IsEquivalentTo(continuation ? expected : expected.Append((DocumentChange.Collections, "items")));
            await Assert.That(tombstones).IsEquivalentTo(deleted);
        }

        await Assert.That(unicode.Length).IsEqualTo(256);
        await Assert.That(Encoding.UTF8.GetByteCount(unicode)).IsEqualTo(768);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Oversized_stored_bodies_and_half_empty_cursors_are_rejected_by_real_repair_Rpcs(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, ["documents:write"], cancellationToken));
        using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections", UriKind.Relative),
            new { name = "items" }, cancellationToken))
        {
            collection.EnsureSuccessStatusCode();
        }

        var id = DocumentApi.NewId();
        using (var created = await writer.PutAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections/items/documents/{id}", UriKind.Relative),
            new { name = new Faker().Name.FullName() }, cancellationToken))
        {
            created.EnsureSuccessStatusCode();
        }

        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        var (identity, _) = await IdentityAsync(client, tenant, cancellationToken);
        foreach (var (collection, cursorId) in new[] { ("items", ""), ("", id) })
        {
            using var read = client.ReadRangeAsync(new RangeRequest
            {
                Identity = identity,
                Ranges = ushort.MaxValue,
                AfterCollection = collection,
                AfterId = cursorId,
            }, cancellationToken: cancellationToken);
            var error = await Assert.ThrowsAsync<RpcException>(async () => await read.ResponseAsync);
            await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        }

        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(tenant.File(server, "repair"))))
        {
            await connection.OpenAsync(cancellationToken);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET body = '{\"payload\":\"' || replace(hex(zeroblob(1048577)), '00', 'x') || '\"}' WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$id", id);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(cancellationToken)).IsEqualTo(1);
        }

        using var digestCall = client.DigestAsync(identity, cancellationToken: cancellationToken);
        var digestError = await Assert.ThrowsAsync<RpcException>(async () => await digestCall.ResponseAsync);
        await Assert.That(digestError!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        using var rangeCall = client.ReadRangeAsync(new RangeRequest { Identity = identity, Ranges = ushort.MaxValue }, cancellationToken: cancellationToken);
        var rangeError = await Assert.ThrowsAsync<RpcException>(async () => await rangeCall.ResponseAsync);
        await Assert.That(rangeError!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    private static async Task<(ShardIdentity Identity, MerkleDigest Digest)> IdentityAsync(
        AntiEntropy.AntiEntropyClient client, ReplicationTenant tenant, CancellationToken token)
    {
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = tenant.Database("repair").DatabaseId! };
        using var call = client.DigestAsync(identity, cancellationToken: token);
        var digest = await call.ResponseAsync;
        identity.Incarnation = digest.Incarnation;
        identity.Origin = ByteString.CopyFrom(Guid.NewGuid().ToByteArray(bigEndian: true));
        identity.SourceProof = new JournalProof { Origin = identity.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(identity.Origin.Span)) };
        identity.TargetProof = new JournalProof { Origin = digest.Origin, Digest = ByteString.CopyFrom(JournalDigest.Initial(digest.Origin.Span)) };
        return (identity, digest);
    }
}
