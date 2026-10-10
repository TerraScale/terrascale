using System.Text;
using Bogus;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.Alpha;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Stored-key boundaries are enforced through the real mutual TLS repair listener.</summary>
public sealed class AntiEntropyBoundaryTests
{
    [Test]
    public async Task Repair_accepts_canonical_ULID_scopes_and_rejects_UUID_and_noncanonical_selectors(CancellationToken cancellationToken)
    {
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings => { peerTls.Apply(settings); clientTls.Apply(settings); });
        var tenant = await ReplicationTenant.CreateAsync(server, ["repair"], cancellationToken);
        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        var identity = new ShardIdentity { Organization = tenant.OrganizationId, Project = tenant.ProjectId, Database = tenant.Database("repair").DatabaseId! };
        using (var accepted = client.DigestAsync(identity, cancellationToken: cancellationToken))
        {
            var digest = await accepted.ResponseAsync;
            await Assert.That(digest.Incarnation.Length).IsEqualTo(16);
        }

        foreach (var id in new[] { "00000000-0000-7000-8000-000000000001", "01arz3ndektsv4rrffq69g5fa1", "81ARZ3NDEKTSV4RRFFQ69G5FA1" })
        {
            foreach (var selector in new[] { "organization", "project", "database" })
            {
                var invalid = identity.Clone();
                switch (selector)
                {
                    case "organization": invalid.Organization = id; break;
                    case "project": invalid.Project = id; break;
                    case "database": invalid.Database = id; break;
                }

                using var rejected = client.DigestAsync(invalid, cancellationToken: cancellationToken);
                var error = await Assert.ThrowsAsync<RpcException>(async () => await rejected.ResponseAsync);
                await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
            }
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Invalid_stored_keys_do_not_change_digests_or_hide_valid_live_and_deleted_states(CancellationToken cancellationToken)
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

        var ids = Enumerable.Range(0, 130).Select(i => $"00000000000000{i:000000000000}").ToArray();
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
        StoredRegisters.Register(connection);
        using (var malformed = connection.CreateCommand())
        {
            malformed.CommandText = "INSERT INTO documents VALUES ('items', $id, tb_state('{}'), 65536, '{}');";
            malformed.Parameters.AddWithValue("$id", unicode);
            await malformed.ExecuteNonQueryAsync(cancellationToken);
        }

        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        var client = new AntiEntropy.AntiEntropyClient(channel);
        var (identity, before) = await IdentityAsync(client, tenant, cancellationToken);
        using (var malformed = connection.CreateCommand())
        {
            // More than a metadata page of managed-invalid keys, live and deleted, still exercises empty continuations;
            // empty and oversized keys stay out of the partial index altogether.
            malformed.CommandText = """
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 129)
                INSERT INTO documents SELECT 'items', '!' || replace(hex(zeroblob(252)), '00', 'd') || printf('%04d', n), tb_state('{}'), 65536, '{}' FROM numbers;
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 129)
                INSERT INTO documents SELECT 'items', '!' || replace(hex(zeroblob(252)), '00', 't') || printf('%04d', n), tb_state(NULL), 65536, NULL FROM numbers;
                INSERT INTO documents VALUES ('items', $short, tb_state('{}'), 65536, '{}'), ('items', $edge, tb_state('{}'), 65536, '{}'),
                    ('items', $long, tb_state('{}'), 65536, '{}'), ($collection, 'live', tb_state('{}'), 65536, '{}');
                INSERT INTO documents VALUES ('items', substr($short, 1, 256) || 'y', tb_state(NULL), 65536, NULL),
                    ('items', substr($edge, 1, 1023) || 'y', tb_state(NULL), 65536, NULL), ('items', $long || 'y', tb_state(NULL), 65536, NULL),
                    ($collection, 'deleted', tb_state(NULL), 65536, NULL);
                INSERT INTO documents VALUES ('items', '', tb_state('{}'), 65536, '{}'), ('', 'empty', tb_state('{}'), 65536, '{}');
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 4095)
                INSERT INTO documents SELECT 'items', '!' || replace(hex(zeroblob(1020)), '00', 'a') || printf('%04d', n), tb_state('{}'), 65536, '{}' FROM numbers;
                WITH RECURSIVE numbers(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM numbers WHERE n < 4095)
                INSERT INTO documents SELECT 'items', '!' || replace(hex(zeroblob(1020)), '00', 'b') || printf('%04d', n), tb_state(NULL), 65536, NULL FROM numbers;
                INSERT INTO documents VALUES ($prefixCollection, 'live', tb_state('{}'), 65536, '{}'),
                    ($prefixCollection, 'deleted', tb_state(NULL), 65536, NULL);
                """;
            malformed.Parameters.AddWithValue("$short", ids[126] + new string('x', 257 - ids[126].Length));
            malformed.Parameters.AddWithValue("$edge", ids[127] + new string('x', 1024 - ids[127].Length));
            malformed.Parameters.AddWithValue("$long", ids[128] + new string('x', 1025 - ids[128].Length));
            malformed.Parameters.AddWithValue("$collection", new string('z', 64));
            malformed.Parameters.AddWithValue("$prefixCollection", new string('!', 64));
            await malformed.ExecuteNonQueryAsync(cancellationToken);
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
                var changes = page.Envelope.IsEmpty ? []
                    : DeltaGroup.Decode(ApplicationEnvelope.Decode(page.Envelope.Span, ReplicationProtocol.MaxApplicationBytes), maxChanges: 128).Changes;
                await Assert.That(changes.Count <= 128).IsTrue();
                await Assert.That(Deltas.ContentBytes(changes) <= IStorageTransaction.MaxChangeGroupBytes).IsTrue();
                await Assert.That(++pages <= 5).IsTrue();
                if (page.Envelope.IsEmpty && page.HasMore)
                {
                    emptyContinuations++;
                    await Assert.That(DocumentDelta.HasValidKey(page.AfterCollection, page.AfterId)).IsFalse();
                }

                foreach (var change in changes)
                {
                    await Assert.That(DocumentDelta.HasValidKey(change.Collection, change.Id)).IsTrue();
                    await Assert.That(seen.Add((change.Collection, change.Id))).IsTrue();
                    if (change.Collection == "items" && Deltas.Json(change) is null)
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
            await Assert.That(seen).IsEquivalentTo(continuation ? expected : expected.Append((DocumentDelta.Collections, "items")));
            await Assert.That(tombstones).IsEquivalentTo(deleted);
        }

        await Assert.That(unicode.Length).IsEqualTo(256);
        await Assert.That(Encoding.UTF8.GetByteCount(unicode)).IsEqualTo(768);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Oversized_stored_registers_and_half_empty_cursors_are_rejected_by_real_repair_Rpcs(CancellationToken cancellationToken)
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
        using (var created = await writer.PutAsJsonAsync(new Uri($"{tenant.Path("repair")}/collections/items/documents/by-key?id={id}", UriKind.Relative),
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
            // A register past the most one change may carry: no page can send it.
            corrupt.CommandText = "UPDATE documents SET state = zeroblob(3 * 1024 * 1024 + 1) WHERE collection = 'items' AND id = $id;";
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
        return (identity, digest);
    }
}
