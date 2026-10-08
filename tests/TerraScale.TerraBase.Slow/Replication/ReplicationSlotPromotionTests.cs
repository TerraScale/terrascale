using System.Collections.Concurrent;
using System.Globalization;
using Bogus;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

public sealed class ReplicationSlotPromotionTests
{
    [Test]
    [Arguments("welcome_digest")]
    [Arguments("peer_ahead")]
    [Arguments("ack_digest")]
    public async Task Promoting_an_omitted_conflict_preserves_divergence_until_full_valid_catchup(string reason)
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        var retryHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var partialWelcomeHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePartialAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalAckHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestAckHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLatestAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefixes = new ConcurrentDictionary<ByteString, (ulong Sequence, ByteString Digest)>();
        var invalidDigest = ByteString.CopyFrom(new byte[JournalDigest.Length]);
        var recoveryPrefix = (Sequence: 0UL, Digest: ByteString.Empty);
        ByteString? omittedId = null;
        var attempts = 0;
        await using var peer = await FakePeer.StartAsync(tls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            var hello = requests.Current.Hello;
            var origin = hello.LocalIdentity.MemberIncarnation;
            var attempt = hello.LocalIdentity.DatabaseId == Volatile.Read(ref omittedId)
                ? Interlocked.Increment(ref attempts)
                : 0;
            if (attempt >= 2)
            {
                // Keep all subsequent attempts valid: another detection must not hide lost conflict state.
                retryHeld.TrySetResult();
                await releaseRetry.Task.WaitAsync(context.CancellationToken);
            }

            prefixes.TryGetValue(hello.LocalIdentity.DatabaseId, out var prefix);
            if (attempt >= 2 || (attempt == 1 && reason == "ack_digest"))
            {
                prefix = recoveryPrefix;
            }

            var welcome = FakePeer.Welcome(hello, out var member);
            welcome.Welcome.Frontiers.Add(new PeerFrontier
            {
                Origin = new OriginIdentity { ReplicaId = origin, Incarnation = origin },
                DurableSequence = attempt == 1 && reason == "peer_ahead" ? (ulong)long.MaxValue : prefix.Sequence,
                JournalDigest = attempt == 1 && reason == "welcome_digest"
                    ? invalidDigest
                    : prefix.Digest ?? ByteString.CopyFrom(JournalDigest.Initial(origin.Span)),
            });
            await responses.WriteAsync(welcome, context.CancellationToken);
            if (attempt == 1 && reason != "ack_digest")
            {
                return;
            }

            while (await requests.MoveNext(context.CancellationToken))
            {
                var batch = requests.Current.CommittedGroup;
                if (attempt == 1)
                {
                    var invalid = FakePeer.Ack(member, batch, batch.EventId.Sequence);
                    invalid.Durable.JournalDigest = invalidDigest;
                    await responses.WriteAsync(invalid, context.CancellationToken);
                    // Closing with unread batches can reset the stream before the sender validates this ACK.
                    while (await requests.MoveNext(context.CancellationToken))
                    {
                    }

                    return;
                }

                if (attempt >= 2 && batch.EventId.Sequence == 2)
                {
                    partialWelcomeHeld.TrySetResult();
                    await releasePartialAck.Task.WaitAsync(context.CancellationToken);
                }

                if (attempt >= 2 && batch.EventId.Sequence == 3)
                {
                    finalAckHeld.TrySetResult();
                    await releaseFinalAck.Task.WaitAsync(context.CancellationToken);
                }

                if (attempt >= 2 && batch.EventId.Sequence == 4)
                {
                    latestAckHeld.TrySetResult();
                    await releaseLatestAck.Task.WaitAsync(context.CancellationToken);
                }

                prefixes[hello.LocalIdentity.DatabaseId] = (batch.EventId.Sequence, batch.JournalDigest);
                await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
            }
        });
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
            settings["write-wait-ms"] = "0";
        });
        var token = TestContext.Current!.Execution.CancellationToken;
        try
        {
            var databases = new Dictionary<string, (TenantContext Context, ReplicationTenant Tenant, string Name)>();
            var faker = new Faker();
            for (var offset = 0; offset < 129; offset += 32)
            {
                var names = Enumerable.Range(offset, Math.Min(32, 129 - offset)).Select(index => $"db-{index}").ToArray();
                var tenant = await ReplicationTenant.CreateAsync(server, names, token);
                using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:write"));
                foreach (var name in names)
                {
                    using var created = await writer.PostAsJsonAsync(new Uri($"{tenant.Path(name)}/collections", UriKind.Relative),
                        new { name = faker.Random.String2(12, "abcdefghijklmnopqrstuvwxyz") }, token);
                    created.EnsureSuccessStatusCode();
                    var database = tenant.Database(name);
                    databases.Add(database.DatabaseId!, (database, tenant, name));
                }
            }

            await TerraBaseTestServer.ReplicatedAsync(server);
            var initial = await StatusAsync(token);
            await Assert.That(initial.DatabaseCount).IsEqualTo(129);
            await Assert.That(initial.OmittedDatabases).IsEqualTo(1);
            await Assert.That(initial.Peers[0].Databases.Count).IsEqualTo(128);
            var observedIds = initial.Peers[0].Databases.Select(database => database.DatabaseId).ToHashSet(StringComparer.Ordinal);
            var omitted = databases.Single(entry => !observedIds.Contains(entry.Key));
            var databaseId = ByteString.CopyFrom(Guid.Parse(omitted.Key).ToByteArray(bigEndian: true));
            recoveryPrefix = prefixes[databaseId];
            await Assert.That(recoveryPrefix.Sequence).IsEqualTo(1UL);
            using (var writer = clientTls.CreateClient(server.ClientAddress!, await omitted.Value.Tenant.KeyAsync(server, "documents:write")))
            {
                for (var index = 0; index < 2; index++)
                {
                    using var created = await writer.PostAsJsonAsync(new Uri($"{omitted.Value.Tenant.Path(omitted.Value.Name)}/collections", UriKind.Relative),
                        new { name = faker.Random.String2(12, "abcdefghijklmnopqrstuvwxyz") }, token);
                    created.EnsureSuccessStatusCode();
                }
            }

            await TerraBaseTestServer.ReplicatedAsync(server);
            Volatile.Write(ref omittedId, databaseId);
            await retryHeld.Task.WaitAsync(token);
            // The first conflict happened without a slot; this retry is parked before its Welcome.
            await Assert.That(await DetectionsAsync()).IsEqualTo(1);
            var conflicted = await StatusAsync(token);
            await Assert.That(conflicted.OmittedDatabases).IsEqualTo(1);
            await Assert.That(conflicted.Peers[0].Databases.Any(database => database.DatabaseId == omitted.Key)).IsFalse();
            await Assert.That(await server.MetricAsync("terrabase_replication_database_diverged", "", token)).IsEqualTo(0);

            var removed = conflicted.Peers[0].Databases[0];
            var slotLabel = $"database=\"{removed.Slot.ToString(CultureInfo.InvariantCulture)}\"";
            var engine = server.Services.GetRequiredService<IStorageEngine>();
            await Assert.That(await engine.DeleteDatabaseAsync(databases[removed.DatabaseId].Context, token)).IsTrue();
            // No write to the omitted database can allocate the slot: promotion must come from discovery.
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                (await StatusAsync(cancellationToken)).Peers[0].Databases.Any(database => database.DatabaseId == omitted.Key));
            var promoted = await AssertConflictAsync();
            await Assert.That(promoted.Slot).IsEqualTo(removed.Slot);
            await Assert.That(promoted.Head).IsEqualTo(3);
            await Assert.That(promoted.Acknowledged).IsEqualTo(0);

            releaseRetry.TrySetResult();
            await partialWelcomeHeld.Task.WaitAsync(token);
            await WaitForPrefixAsync(1);
            await AssertConflictAsync();
            releasePartialAck.TrySetResult();
            await finalAckHeld.Task.WaitAsync(token);
            await WaitForPrefixAsync(2);
            await AssertConflictAsync();

            // The stream captured head 3. A newer commit must prevent that stream's final ACK from clearing the conflict.
            using (var writer = clientTls.CreateClient(server.ClientAddress!, await omitted.Value.Tenant.KeyAsync(server, "documents:write")))
            {
                using var created = await writer.PostAsJsonAsync(new Uri($"{omitted.Value.Tenant.Path(omitted.Value.Name)}/collections", UriKind.Relative),
                    new { name = faker.Random.String2(12, "abcdefghijklmnopqrstuvwxyz") }, token);
                created.EnsureSuccessStatusCode();
            }

            releaseFinalAck.TrySetResult();
            await latestAckHeld.Task.WaitAsync(token);
            await WaitForPrefixAsync(3);
            var stillConflicted = await AssertConflictAsync();
            await Assert.That(stillConflicted.Head).IsEqualTo(4);
            await Assert.That(stillConflicted.DurableLag).IsEqualTo(1);
            releaseLatestAck.TrySetResult();
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
            {
                var recovered = (await StatusAsync(cancellationToken)).Peers[0].Databases.Single(database => database.DatabaseId == omitted.Key);
                return recovered.Acknowledged == recovered.Head && recovered.DurableLag == 0 && !recovered.Diverged;
            });
            await Assert.That(await server.MetricAsync("terrabase_replication_database_diverged", slotLabel, token)).IsEqualTo(0);
            await Assert.That(await DetectionsAsync()).IsEqualTo(1);

            // Retiring the promoted identity leaves explicit zero samples in its previously allocated slot.
            await Assert.That(await engine.DeleteDatabaseAsync(omitted.Value.Context, token)).IsTrue();
            var retired = await StatusAsync(token);
            await Assert.That(retired.DatabaseCount).IsEqualTo(127);
            await Assert.That(retired.OmittedDatabases).IsEqualTo(0);
            await Assert.That(retired.Peers[0].Databases.Any(database => database.Slot == removed.Slot)).IsFalse();
            await Assert.That(await server.MetricAsync("terrabase_replication_database_diverged", slotLabel, token)).IsEqualTo(0);
            await Assert.That(await server.MetricAsync("terrabase_replication_database_lag", slotLabel, token)).IsEqualTo(0);
            await Assert.That(await server.MetricAsync("terrabase_replication_database_catchup_seconds", slotLabel, token)).IsEqualTo(0);

            async Task<ReplicationDatabaseStatus> AssertConflictAsync()
            {
                var status = await StatusAsync(token);
                await Assert.That(status.DatabaseCount).IsEqualTo(128);
                await Assert.That(status.OmittedDatabases).IsEqualTo(0);
                var database = status.Peers[0].Databases.Single(database => database.DatabaseId == omitted.Key);
                await Assert.That(database.Diverged).IsTrue();
                await Assert.That(await server.MetricAsync("terrabase_replication_database_diverged", slotLabel, token)).IsEqualTo(1);
                await Assert.That(await DetectionsAsync()).IsEqualTo(1);
                return database;
            }

            Task WaitForPrefixAsync(long prefix)
            {
                return TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                    (await StatusAsync(cancellationToken)).Peers[0].Databases.Single(database => database.DatabaseId == omitted.Key).Acknowledged == prefix);
            }
        }
        finally
        {
            releaseRetry.TrySetResult();
            releasePartialAck.TrySetResult();
            releaseFinalAck.TrySetResult();
            releaseLatestAck.TrySetResult();
        }

        async Task<ReplicationStatus> StatusAsync(CancellationToken cancellationToken)
        {
            return (await server.Client.GetFromJsonAsync<ReplicationStatus>(new Uri("/replication", UriKind.Relative), cancellationToken))!;
        }

        Task<double> DetectionsAsync()
        {
            return server.MetricAsync("terrabase_replication_divergence_detections_total", $"reason=\"{reason}\"", token);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Promoting_a_caught_up_database_does_not_record_convergence(bool revalidationInProgress)
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        var primaryHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrimary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promotedHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePromoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promotionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefixes = new ConcurrentDictionary<ByteString, (ulong Sequence, ByteString Digest)>();
        ByteString? heldId = null;
        ByteString? omittedId = null;
        await using var peer = await FakePeer.StartAsync(tls, async (requests, responses, context) =>
        {
            await requests.MoveNext(context.CancellationToken);
            var hello = requests.Current.Hello;
            if (hello.LocalIdentity.DatabaseId == Volatile.Read(ref heldId) && !releasePrimary.Task.IsCompleted)
            {
                primaryHeld.TrySetResult();
                await releasePrimary.Task.WaitAsync(context.CancellationToken);
            }

            if (promotionReady.Task.IsCompleted && hello.LocalIdentity.DatabaseId == omittedId && !releasePromoted.Task.IsCompleted)
            {
                promotedHeld.TrySetResult();
                await releasePromoted.Task.WaitAsync(context.CancellationToken);
            }

            var origin = hello.LocalIdentity.MemberIncarnation;
            var welcome = FakePeer.Welcome(hello, out var member);
            prefixes.TryGetValue(origin, out var prefix);
            welcome.Welcome.Frontiers.Add(new PeerFrontier
            {
                Origin = new OriginIdentity { ReplicaId = origin, Incarnation = origin },
                DurableSequence = prefix.Sequence,
                JournalDigest = prefix.Digest ?? ByteString.CopyFrom(JournalDigest.Initial(origin.Span)),
            });
            await responses.WriteAsync(welcome, context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                var batch = requests.Current.CommittedGroup;
                prefixes[origin] = (batch.EventId.Sequence, batch.JournalDigest);
                await responses.WriteAsync(FakePeer.Ack(member, batch, batch.EventId.Sequence), context.CancellationToken);
            }
        });
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            clientTls.Apply(settings);
            settings["peer-addresses"] = peer.Address.ToString();
            settings["write-wait-ms"] = "0";
        });
        var token = TestContext.Current!.Execution.CancellationToken;
        try
        {
            var databases = new Dictionary<string, TenantContext>();
            var faker = new Faker();
            for (var offset = 0; offset < 129; offset += 32)
            {
                var names = Enumerable.Range(offset, Math.Min(32, 129 - offset)).Select(index => $"db-{index}").ToArray();
                var tenant = await ReplicationTenant.CreateAsync(server, names, token);
                using var writer = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:write"));
                foreach (var name in names)
                {
                    using var created = await writer.PostAsJsonAsync(new Uri($"{tenant.Path(name)}/collections", UriKind.Relative),
                        new { name = faker.Random.String2(12, "abcdefghijklmnopqrstuvwxyz") }, token);
                    created.EnsureSuccessStatusCode();
                    var database = tenant.Database(name);
                    databases.Add(database.DatabaseId!, database);
                }
            }

            await TerraBaseTestServer.ReplicatedAsync(server);
            var initial = await server.Client.GetFromJsonAsync<ReplicationStatus>(new Uri("/replication", UriKind.Relative), token);
            await Assert.That(initial!.DatabaseCount).IsEqualTo(129);
            await Assert.That(initial.OmittedDatabases).IsEqualTo(1);
            await Assert.That(initial.Peers[0].Databases.Count).IsEqualTo(128);
            var observed = initial.Peers[0].Databases;
            var observedIds = observed.Select(database => database.DatabaseId).ToHashSet(StringComparer.Ordinal);
            var omitted = databases.Single(entry => !observedIds.Contains(entry.Key));
            omittedId = ByteString.CopyFrom(Guid.Parse(omitted.Key).ToByteArray(bigEndian: true));
            var removed = observed[0];
            var slotLabel = $"database=\"{removed.Slot.ToString(CultureInfo.InvariantCulture)}\"";
            var convergences = await server.MetricAsync("terrabase_replication_convergence_seconds_count", "", token);
            var slotConvergences = await server.MetricAsync("terrabase_replication_convergence_seconds_count", slotLabel, token);
            await Assert.That(slotConvergences).IsGreaterThan(0);

            // Park the single sender worker before deletion: either the omitted database is revalidating,
            // or another database is, leaving the omitted database's trusted ACK intact.
            Volatile.Write(ref heldId, revalidationInProgress
                ? omittedId
                : ByteString.CopyFrom(Guid.Parse(observed[1].DatabaseId).ToByteArray(bigEndian: true)));
            await primaryHeld.Task.WaitAsync(token);
            var engine = server.Services.GetRequiredService<IStorageEngine>();
            await Assert.That(await engine.DeleteDatabaseAsync(databases[removed.DatabaseId], token)).IsTrue();
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
            {
                var status = await server.Client.GetFromJsonAsync<ReplicationStatus>(new Uri("/replication", UriKind.Relative), cancellationToken);
                return status!.Peers[0].Databases.Any(database => database.DatabaseId == omitted.Key);
            });

            await AssertPromotedAsync();
            promotionReady.TrySetResult();
            releasePrimary.TrySetResult();
            await promotedHeld.Task.WaitAsync(token);
            // The promoted database now has conservative lag while its unchanged valid Welcome is held.
            await AssertPromotedAsync();
            releasePromoted.TrySetResult();
            await TerraBaseTestServer.ReplicatedAsync(server);
            await AssertPromotedAsync();

            // No omitted database remains to reuse this slot before its retired samples are scraped.
            var beforeDeletion = await server.Client.GetFromJsonAsync<ReplicationStatus>(new Uri("/replication", UriKind.Relative), token);
            var retired = beforeDeletion!.Peers[0].Databases.MaxBy(database => database.Slot)!;
            await Assert.That(await engine.DeleteDatabaseAsync(databases[retired.DatabaseId], token)).IsTrue();
            var afterDeletion = await server.Client.GetFromJsonAsync<ReplicationStatus>(new Uri("/replication", UriKind.Relative), token);
            await Assert.That(afterDeletion!.DatabaseCount).IsEqualTo(127);
            await Assert.That(afterDeletion.OmittedDatabases).IsEqualTo(0);
            await Assert.That(afterDeletion.Peers[0].Databases.Any(database => database.Slot == retired.Slot)).IsFalse();
            var retiredLabel = $"database=\"{retired.Slot.ToString(CultureInfo.InvariantCulture)}\"";
            // MetricAsync throws if a sample is absent: retirement must retain explicit zeroes.
            await Assert.That(await server.MetricAsync("terrabase_replication_database_lag", retiredLabel, token)).IsEqualTo(0);
            await Assert.That(await server.MetricAsync("terrabase_replication_database_diverged", retiredLabel, token)).IsEqualTo(0);
            await Assert.That(await server.MetricAsync("terrabase_replication_database_catchup_seconds", retiredLabel, token)).IsEqualTo(0);

            async Task AssertPromotedAsync()
            {
                var status = await server.Client.GetFromJsonAsync<ReplicationStatus>(new Uri("/replication", UriKind.Relative), token);
                await Assert.That(status!.DatabaseCount).IsEqualTo(128);
                await Assert.That(status.OmittedDatabases).IsEqualTo(0);
                var promoted = status.Peers[0].Databases.Single(database => database.DatabaseId == omitted.Key);
                await Assert.That(promoted.Slot).IsEqualTo(removed.Slot);
                await Assert.That(promoted.CatchupSeconds).IsEqualTo(0);
                await Assert.That(promoted.ConvergenceSeconds).IsNull();
                await Assert.That(await server.MetricAsync("terrabase_replication_database_catchup_seconds", slotLabel, token)).IsEqualTo(0);
                // Histogram samples remain attached to the reused slot, but promotion adds no sample.
                await Assert.That(await server.MetricAsync("terrabase_replication_convergence_seconds_count", "", token)).IsEqualTo(convergences);
                await Assert.That(await server.MetricAsync("terrabase_replication_convergence_seconds_count", slotLabel, token)).IsEqualTo(slotConvergences);
            }
        }
        finally
        {
            releasePrimary.TrySetResult();
            releasePromoted.TrySetResult();
        }
    }
}
