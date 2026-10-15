using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

// Apply after A4/A5, trusted-state validation, and the admission draft. Each case owns a real host and listener.
// Keep the existing RebuildCertificationTests >2 MiB/<4 MiB page case; it already reads every refilled document.
public sealed class StateAdmissionTests
{
    private const long BudgetBytes = 128L * 1024 * 1024;
    private const long SourceBytes = 16L * ReplicationProtocol.MaxApplicationBytes;
    private const long HashHeadroom = 64 * 1024;
    private static CancellationToken Token => TUnit.Core.TestContext.Current!.Execution.CancellationToken;

    [Test]
    public async Task Source_pressure_refuses_before_paging_and_releasing_pressure_allows_the_same_pin()
    {
        await using var fixture = await Fixture.StartAsync();
        var pin = fixture.Store.Incarnation;
        var before = fixture.Probe.PageReads;
        using (fixture.Pressure(SourceBytes / 2))
        {
            var error = await Assert.ThrowsAsync<RpcException>(async () => _ = await fixture.ReadAsync(pin, Token));
            await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
            await Assert.That(fixture.Probe.PageReads).IsEqualTo(before);
            await Assert.That(fixture.Probe.SourceReserved).IsEqualTo(0);
        }

        await fixture.HealthyAsync();
        await Assert.That(await fixture.ReadAsync(pin, Token)).IsGreaterThan(0);
        await Assert.That(fixture.Probe.WritesUnderLease).IsGreaterThan(0);
        await fixture.SourceReleasedAsync();
    }

    [Test]
    [Arguments("complete")]
    [Arguments("cancel")]
    [Arguments("source-error")]
    [Arguments("write-error")]
    public async Task Source_lease_survives_the_page_and_write_and_releases_on_every_exit(string exit)
    {
        await using var fixture = await Fixture.StartAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Probe.AfterPage = async token =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
            if (exit == "source-error")
            {
                throw new IOException("injected after the production source read");
            }
        };
        fixture.Probe.FailWrite = exit == "write-error";
        var reading = fixture.ReadAsync(fixture.Store.Incarnation, stop.Token);
        try
        {
            await reached.Task.WaitAsync(Token);
            await Assert.That(fixture.Probe.SourceReserved).IsEqualTo(SourceBytes);
            if (exit == "cancel")
            {
                await stop.CancelAsync();
            }

            release.TrySetResult();
            if (exit == "complete")
            {
                await Assert.That(await reading).IsGreaterThan(0);
                await Assert.That(fixture.Probe.WritesUnderLease).IsGreaterThan(0);
            }
            else
            {
                _ = await Assert.ThrowsAsync<RpcException>(async () => _ = await reading);
            }

            await fixture.SourceReleasedAsync();
            await fixture.HealthyAsync();
        }
        finally
        {
            release.TrySetResult();
            await stop.CancelAsync();
            await ((Task)reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [Arguments("collection")]
    [Arguments("id")]
    public async Task Oversized_stored_keys_fault_before_the_key_payload_query(string column)
    {
        await using var fixture = await Fixture.StartAsync();
        var pin = fixture.Store.Incarnation;
        var keyQueries = 0;
        fixture.Probe.Checkpoint = (step, _) =>
        {
            if (step == "repair.keys")
            {
                Interlocked.Increment(ref keyQueries);
            }
        };
        // The only requested bucket has this damaged first row; no healthy earlier bucket can produce a page.
        await fixture.SqlAsync(column == "collection"
            ? "UPDATE _tb_state SET collection = $value WHERE collection = 'items' AND id = $id;"
            : "UPDATE _tb_state SET id = $value WHERE collection = 'items' AND id = $id;", new string('x', 1024 * 1024));
        var error = await Assert.ThrowsAsync<RpcException>(async () => _ = await fixture.ReadAsync(pin, Token));
        await Assert.That(error!.StatusCode).IsNotEqualTo(StatusCode.OK);
        await Assert.That(keyQueries).IsEqualTo(0).Because("scalar key preflight failed before the payload SELECT");
        await Assert.That(fixture.Probe.WritesUnderLease).IsEqualTo(0);
        await fixture.SourceReleasedAsync();
        await Assert.That(await fixture.Service<IReplicaCopyFaults>().RefusedAsync(fixture.Database, pin, Token)).IsTrue();
    }

    [Test]
    [Arguments("state")]
    [Arguments("document")]
    [Arguments("conflict")]
    public async Task Stored_payload_pressure_refuses_before_state_selection_without_marking_damage(string column)
    {
        await using var fixture = await Fixture.StartAsync();
        // The inaccessible payload is deliberately not decoded: its size alone is capacity, not proof of corruption.
        var large = new string('x', 4 * 1024 * 1024);
        await fixture.SqlAsync(column switch
        {
            "state" => "UPDATE _tb_state SET state = $value WHERE collection = 'items' AND id = $id;",
            "document" => "UPDATE items SET doc = $value WHERE id = $id;",
            _ => "INSERT INTO _tb_unique_conflicts(collection, id, body) VALUES('items', $id, $value);",
        }, large);
        var payloadReads = 0;
        fixture.Probe.Checkpoint = (step, _) =>
        {
            if (step == "state.payload")
            {
                Interlocked.Increment(ref payloadReads);
            }
        };
        using (fixture.Pressure(SourceBytes + 1024 * 1024))
        {
            var error = await Assert.ThrowsAsync<RpcException>(async () => _ = await fixture.ReadAsync(fixture.Store.Incarnation, Token));
            await Assert.That(error!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
            await Assert.That(payloadReads).IsEqualTo(0);
            await Assert.That(fixture.Probe.WritesUnderLease).IsEqualTo(0);
            await fixture.SourceReleasedAsync();
        }

        await fixture.HealthyAsync();
    }

    [Test]
    public async Task Small_incoming_refresh_admits_old_history_through_join_and_releases_after_refusal_and_commit()
    {
        await using var fixture = await Fixture.StartAsync();
        var origin = ReplicaId.Of(1, 7, 0);
        var senderPin = Ulid.NewUlid();
        var stamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16;
        using var channel = PeerChannel.Create(fixture.Api.Server.BaseAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        using var call = client.Push(cancellationToken: Token);
        await call.RequestStream.WriteAsync(new PushFrame { Hello = new Hello { Node = origin.Value, Region = "sjc" } }, Token);
        await Assert.That(await call.ResponseStream.MoveNext(Token)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        // Eight genuine applied refreshes retain 256 removed dots while the JSON remains small.
        var faker = new Faker();
        for (var batch = 0; batch < 8; batch++)
        {
            var history = DocumentState.Bottom;
            for (var dot = 0; dot < 32; dot++)
            {
                var element = JsonElement.Parse('"' + faker.Random.String2(1000, "abcdef") + '"');
                var added = new EntryVersion(new Hlc(++stamp), origin);
                history = history.Add(added, "gone", element).Remove(new EntryVersion(new Hlc(++stamp), origin), "gone", element);
            }

            await Assert.That((await SendAsync(history, (ulong)batch + 1)).Refused).IsFalse();
        }

        var (oldState, oldBody) = await fixture.SqlSizeAsync();
        var oldCharge = 16 * (3L * (oldState / 4) + 3 + oldBody) + 2 * (5 + fixture.Id.Length) + 2048;
        var next = DocumentState.Bottom.Set(new EntryVersion(new Hlc(++stamp), origin), "n", JsonElement.Parse("2"));
        await Assert.That(oldCharge).IsGreaterThan(1024 * 1024);
        await Assert.That(DocumentStateEncoding.Encode(next).Length).IsLessThan(1024);
        var payloadReads = 0;
        var heldAtReset = false;
        fixture.Probe.Checkpoint = (step, _) =>
        {
            if (step == "state.payload")
            {
                Interlocked.Increment(ref payloadReads);
            }

            if (step == "writer.reset")
            {
                heldAtReset |= Volatile.Read(ref fixture.Probe.ForegroundReserved) >= oldCharge;
            }
        };
        var committed = fixture.Store.CommitSequence;
        using (fixture.Pressure(1024 * 1024))
        {
            await Assert.That((await SendAsync(next, 9)).Refused).IsTrue();
            await Assert.That(payloadReads).IsEqualTo(1)
                .Because("the healthy collection-drop state is projected before the document admission refuses");
            await Assert.That(fixture.Store.CommitSequence).IsEqualTo(committed);
        }

        await fixture.HealthyAsync();
        await Assert.That((await SendAsync(next, 10)).Refused).IsFalse();
        await Assert.That(payloadReads).IsEqualTo(3)
            .Because("the healthy retry projects collection-drop metadata and the admitted target document");
        await Assert.That(heldAtReset).IsTrue().Because("the old state is still admitted after join and commit, before disposal");
        var (afterState, _) = await fixture.SqlSizeAsync();
        await Assert.That(afterState).IsGreaterThanOrEqualTo(oldState).Because("the refresh preserved retained removed dots");
        await fixture.SourceReleasedAsync();
        await call.RequestStream.CompleteAsync();

        async Task<Ack> SendAsync(DocumentState state, ulong sequence)
        {
            OriginGroup[] groups = [new((long)sequence, [new ChangeDelta("items", fixture.Id, state.MaxStamp, state)])];
            await call.RequestStream.WriteAsync(new PushFrame
            {
                Batch = new Batch { Database = ChangeGroups.Wire(fixture.Database, senderPin), LastChangeId = sequence, Groups = ChangeGroups.Encode(groups) },
            }, Token);
            await Assert.That(await call.ResponseStream.MoveNext(Token)).IsTrue();
            return call.ResponseStream.Current.Ack;
        }
    }

    [Test]
    [Arguments(false, "h1", "blob")]
    [Arguments(false, "h2", "blob")]
    [Arguments(false, "h1", "text")]
    [Arguments(false, "h2", "text")]
    [Arguments(false, "h1", "large-blob")]
    [Arguments(false, "h2", "large-blob")]
    [Arguments(true, "h1", "blob")]
    [Arguments(true, "h2", "blob")]
    [Arguments(true, "h1", "text")]
    [Arguments(true, "h2", "text")]
    [Arguments(true, "h1", "large-blob")]
    [Arguments(true, "h2", "large-blob")]
    public async Task Invalid_hash_storage_types_fault_before_projection_and_refuse_the_exact_source_or_receiver_copy(
        bool receiver, string column, string damage)
    {
        await using var fixture = await Fixture.StartAsync();
        var original = fixture.Store.Incarnation;
        var source = fixture.Service<IChangeGroupSource>();
        var peer = ReplicaId.Of(99, 1, 0);
        var repair = fixture.Service<IRebuildRepair>();
        var incoming = DocumentState.Bottom.Set(new EntryVersion(new Hlc(ulong.MaxValue / 2), peer), "n", JsonElement.Parse("2"));
        RepairState[] refresh = [new("items", fixture.Id, incoming)];
        var sourcePin = Ulid.NewUlid();
        if (receiver)
        {
            await fixture.SwapAndSalvageAsync();
            await Assert.That(await repair.RepairRebuildAsync(fixture.Database, fixture.Store.Incarnation, refresh, Token)).IsEqualTo(1);
            await Assert.That(await repair.RecordRefillAsync(fixture.Database, fixture.Store.Incarnation, "hash-source", sourcePin, 1, Token)).IsTrue();
        }
        else
        {
            await fixture.HealthyAsync();
            await Assert.That(await fixture.ReadAsync(original, Token)).IsGreaterThan(0);
            await Assert.That(await source.AckAsync(fixture.Database, original, peer, 0, default, Token)).IsTrue();
            await fixture.SourceReleasedAsync();
        }

        var copy = fixture.Store;
        var pin = copy.Incarnation;
        var committed = copy.CommitSequence;
        var value = HashValue(damage);
        await fixture.SqlAsync($"UPDATE _tb_state SET {column} = $value WHERE collection = 'items' AND id = $id;", value);
        var sizes = await fixture.HashSizeAsync(column);
        await Assert.That(sizes.Type).IsEqualTo(damage == "text" ? "text" : "blob");
        await Assert.That(sizes.State).IsGreaterThan(0);
        await Assert.That(sizes.State).IsLessThan(1024);
        await Assert.That(sizes.Body).IsLessThan(1024);
        await Assert.That(sizes.Keys).IsLessThan(128);
        await Assert.That(16 * (3 * (sizes.State / 4) + 3 + sizes.Body) + 2 * sizes.Keys + 2048).IsLessThan(HashHeadroom);
        var keys = 0;
        var payloads = 0;
        var immediate = false;
        var reservedBefore = Volatile.Read(ref fixture.Probe.Reserved);
        var beforeWrites = fixture.Probe.WritesUnderLease;
        var beforePages = fixture.Probe.PageReads;
        fixture.Probe.Checkpoint = (step, _) =>
        {
            if (step == "repair.keys")
            {
                Interlocked.Increment(ref keys);
            }
            if (step == "state.payload")
            {
                Interlocked.Increment(ref payloads);
            }
            if (step == (receiver ? "writer.reset" : "reader.reset"))
            {
                immediate |= copy.CopyFault is not null && copy.Incarnation == pin;
            }
        };
        using (damage == "large-blob" ? fixture.Pressure((receiver ? 0 : SourceBytes) + HashHeadroom) : null)
        {
            if (damage == "large-blob")
            {
                await Assert.That(sizes.Hash).IsEqualTo(2 * 1024 * 1024);
                await Assert.That(sizes.Hash).IsGreaterThan(BudgetBytes - Volatile.Read(ref fixture.Probe.Reserved) - (receiver ? 0 : SourceBytes));
            }
            if (receiver)
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                    _ = await repair.RepairRebuildAsync(fixture.Database, pin, refresh, Token));
                await Assert.That(StoredDataCorruption.Is(error!)).IsTrue();
            }
            else
            {
                var error = await Assert.ThrowsAsync<RpcException>(async () => _ = await fixture.ReadAsync(pin, Token));
                await Assert.That(error!.StatusCode).IsNotEqualTo(StatusCode.ResourceExhausted)
                    .Because("invalid stored hash types are corruption, even under real memory pressure");
                await Assert.That(fixture.Probe.PageReads).IsEqualTo(beforePages + 1);
                if (damage == "large-blob")
                {
                    await Assert.That(fixture.Probe.PageAvailable).IsGreaterThanOrEqualTo(HashHeadroom);
                    await Assert.That(sizes.Hash).IsGreaterThan(fixture.Probe.PageAvailable);
                }
            }
            await fixture.SourceReleasedAsync();
        }
        fixture.Probe.Checkpoint = null;
        await Assert.That(keys).IsEqualTo(0);
        await Assert.That(payloads).IsEqualTo(receiver ? 1 : 0)
            .Because("receiver repair projects healthy collection-drop metadata before rejecting the document hash types");
        await Assert.That(immediate).IsTrue().Because("the exact copy is faulted before transaction cleanup records it durably");
        await Assert.That(fixture.Probe.WritesUnderLease).IsEqualTo(beforeWrites).Because("no damaged source page is served");
        await Assert.That(copy.Incarnation).IsEqualTo(pin);
        await Assert.That(copy.CopyFault).IsNotNull();
        await Assert.That(copy.CommitSequence).IsEqualTo(committed);
        await fixture.AssertPendingAsync(original, receiver ? pin : null);
        await Assert.That(await fixture.Service<IReplicaCopyFaults>().RefusedAsync(fixture.Database, pin, Token)).IsTrue();
        await AssertOrdinaryWriteRefusedAsync(copy);
        await Assert.That(await source.AckAsync(fixture.Database, pin, peer, 1, default, Token)).IsFalse();
        await Assert.That(await source.ReadAsync(fixture.Database, pin, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, Token)).IsNull();
        await Assert.That(await repair.RepairRebuildAsync(fixture.Database, pin, refresh, Token)).IsNull();
        await Assert.That(await repair.RecordRefillAsync(fixture.Database, pin, "hash-source", sourcePin, 2, Token)).IsFalse();
        await Assert.That(await repair.CompleteRebuildAsync(fixture.Database, original, pin, Token)).IsFalse();
        await Assert.That(fixture.Service<SqliteStorageEngine>().Ledger.FailedClosed).IsNull();
        if (receiver)
        {
            await Assert.That(await fixture.Service<IRebuildStorage>().FinalizeAsync(fixture.Database, pin, fixture.Side!, Token)).IsFalse();
            await fixture.AssertSideKeptAsync();
        }
        await Assert.That(await fixture.HashSizeAsync(column)).IsEqualTo(sizes).Because("the refused join preserves the damaged row too");
        await fixture.SourceReleasedAsync();
        await fixture.AllReleasedAsync(reservedBefore);
    }

    [Test]
    [Arguments("h1", "blob")]
    [Arguments("h2", "text")]
    public async Task Invalid_hash_types_on_a_key_lookahead_row_refuse_the_whole_real_source_page(string column, string damage)
    {
        await using var fixture = await Fixture.StartAsync();
        // Distinct buckets let the first valid state fill the page before the next bucket's key-only lookahead.
        var other = EntityIds.New().ToString();
        while (DocumentStateEncoding.Bucket("items", other) == DocumentStateEncoding.Bucket("items", fixture.Id)
            || DocumentStateEncoding.Bucket("items", other) == DocumentStateEncoding.Bucket(DocumentDelta.Collections, "items"))
        {
            Token.ThrowIfCancellationRequested();
            other = EntityIds.New().ToString();
        }
        await using (var write = await fixture.Store.BeginDurableTransactionAsync(Token))
        {
            _ = await write.PutAsync("items", other, "{\"n\":1}"u8.ToArray(), Token);
            _ = await write.CommitAsync(Token);
        }
        var firstBucket = DocumentStateEncoding.Bucket("items", fixture.Id);
        var otherBucket = DocumentStateEncoding.Bucket("items", other);
        int[] buckets = [firstBucket, otherBucket];
        var source = fixture.Service<IReplicaStates>();
        var pin = fixture.Store.Incarnation;
        var healthy = await source.ReadBucketsAsync(fixture.Database, pin, buckets, null, 1, ChangeGroups.MaxBatchBytes, Token);
        await Assert.That(healthy?.States.Count).IsEqualTo(1);
        await Assert.That(healthy?.Next).IsNotNull();
        var last = firstBucket > otherBucket ? fixture.Id : other;
        var damaged = await fixture.Store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = $"UPDATE _tb_state SET {column} = $value WHERE collection = 'items' AND id = $id;";
            command.Parameters.AddWithValue("$value", HashValue(damage));
            command.Parameters.AddWithValue("$id", last);
            return command.ExecuteNonQuery();
        }, "an admission test damaging only a key lookahead hash", Token);
        await Assert.That(damaged).IsEqualTo(1);
        var keyQueries = 0;
        var payloads = 0;
        fixture.Probe.Checkpoint = (step, _) =>
        {
            if (step == "repair.keys")
            {
                Interlocked.Increment(ref keyQueries);
            }
            if (step == "state.payload")
            {
                Interlocked.Increment(ref payloads);
            }
        };
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            _ = await source.ReadBucketsAsync(fixture.Database, pin, buckets, null, 1, ChangeGroups.MaxBatchBytes, Token));
        fixture.Probe.Checkpoint = null;
        await Assert.That(StoredDataCorruption.Is(error!)).IsTrue();
        await Assert.That(keyQueries).IsEqualTo(1).Because("the valid first bucket is projected, the damaged lookahead bucket is not");
        await Assert.That(payloads).IsEqualTo(1).Because("the valid first state was admitted before the lookahead refusal");
        await Assert.That(fixture.Probe.WritesUnderLease).IsEqualTo(0);
        await fixture.AssertPendingAsync(pin, null);
        await Assert.That(fixture.Store.CopyFault).IsNotNull();
        await AssertOrdinaryWriteRefusedAsync(fixture.Store);
        await Assert.That(await fixture.Service<IChangeGroupSource>().AckAsync(fixture.Database, pin, ReplicaId.Of(99, 1, 0), 0, default, Token)).IsFalse();
        await fixture.SourceReleasedAsync();
    }

    [Test]
    [Arguments("h1", "blob")]
    [Arguments("h2", "blob")]
    [Arguments("h1", "text")]
    [Arguments("h2", "text")]
    public async Task Invalid_SYSTEM_hash_types_refuse_before_state_projection_and_fail_the_node_closed(string column, string damage)
    {
        await using var fixture = await Fixture.StartAsync();
        var engine = fixture.Service<SqliteStorageEngine>();
        var system = engine.SystemStore ?? throw new InvalidOperationException("SYSTEM is missing");
        var id = EntityIds.New().ToString();
        await using (var seed = await system.BeginDurableTransactionAsync(Token))
        {
            _ = await seed.CreateCollectionAsync(new CollectionInfo("hashitems", 1), Token);
            _ = await seed.PutAsync("hashitems", id, "{\"n\":1}"u8.ToArray(), Token);
            _ = await seed.CommitAsync(Token);
        }
        var peer = ReplicaId.Of(99, 1, 0);
        var source = fixture.Service<IChangeGroupSource>();
        await Assert.That(await source.AckAsync(fixture.Database, fixture.Store.Incarnation, peer, 0, default, Token)).IsTrue();
        var payloads = 0;
        var reservedBefore = Volatile.Read(ref fixture.Probe.Reserved);
        fixture.Probe.ObservedDatabase = ChangeGroups.System;
        fixture.Probe.Checkpoint = (step, _) =>
        {
            if (step == "state.payload")
            {
                Interlocked.Increment(ref payloads);
            }
        };
        await using (var healthy = await system.BeginDurableTransactionAsync(Token))
        {
            await Assert.That(await healthy.JoinAsync("hashitems", id, DocumentState.Bottom, default, Token)).IsFalse();
        }
        await Assert.That(payloads).IsGreaterThan(0).Because("the connected SYSTEM observer sees the healthy stored-state projection");
        await fixture.AllReleasedAsync(reservedBefore);
        Interlocked.Exchange(ref payloads, 0);
        var damaged = await system.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = $"UPDATE _tb_state SET {column} = $value WHERE collection = 'hashitems' AND id = $id;";
            command.Parameters.AddWithValue("$value", HashValue(damage));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery();
        }, "an admission test damaging a SYSTEM hash type", Token);
        await Assert.That(damaged).IsEqualTo(1);
        await using (var join = await system.BeginDurableTransactionAsync(Token))
        {
            var at = new EntryVersion(new Hlc(ulong.MaxValue / 2), peer);
            var delta = DocumentState.Bottom.Set(at, "n", JsonElement.Parse("2"));
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () => _ = await join.JoinAsync("hashitems", id, delta, at, Token));
            await Assert.That(StoredDataCorruption.Is(error!)).IsTrue();
            await Assert.That(system.CopyFault).IsNotNull();
            await Assert.That(engine.Ledger.FailedClosed).IsNotNull().Because("SYSTEM fails closed immediately, before join disposal");
        }
        fixture.Probe.Checkpoint = null;
        await Assert.That(payloads).IsEqualTo(0);
        await Assert.That(fixture.Probe.WritesUnderLease).IsEqualTo(0);
        await Assert.That(fixture.Store.CopyFault).IsNull().Because("the healthy tenant is refused by node health, not misidentified as damaged");
        await AssertOrdinaryWriteRefusedAsync(fixture.Store);
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            _ = await source.AckAsync(fixture.Database, fixture.Store.Incarnation, peer, 1, default, Token));
        using var ready = await fixture.Api.Client.GetAsync("/readyz", Token);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        _ = await Assert.ThrowsAsync<RpcException>(async () => _ = await fixture.ReadAsync(fixture.Store.Incarnation, Token));
        await Assert.That(fixture.Probe.WritesUnderLease).IsEqualTo(0);
        await fixture.SourceReleasedAsync();
        await fixture.AllReleasedAsync(reservedBefore);
    }

    [Test]
    [Arguments("h1", "large-blob", false)]
    [Arguments("h1", "large-blob", true)]
    [Arguments("h2", "large-text", false)]
    [Arguments("h2", "large-text", true)]
    [Arguments("h1", "text", false)]
    [Arguments("h1", "text", true)]
    [Arguments("h2", "blob", false)]
    [Arguments("h2", "blob", true)]
    [Arguments("bucket", "large-blob", false)]
    [Arguments("bucket", "large-blob", true)]
    [Arguments("bucket", "large-text", false)]
    [Arguments("bucket", "large-text", true)]
    [Arguments("bucket", "outside", false)]
    [Arguments("bucket", "outside", true)]
    public async Task Invalid_digest_scalars_fault_discovery_and_pinned_listener_reads(string column, string damage, bool pinned)
    {
        await using var fixture = await Fixture.StartAsync();
        var pin = fixture.Store.Incarnation;
        await Assert.That((await fixture.DigestAsync(pinned ? pin : null)).Held).IsTrue();
        await fixture.SqlAsync($"UPDATE _tb_state SET {column} = $value WHERE collection = 'items' AND id = $id;",
            damage == "outside" ? (object)4096L : HashValue(damage));
        var committed = fixture.Store.CommitSequence;
        var immediate = false;
        fixture.Probe.Checkpoint = (step, _) =>
        {
            if (step == "reader.reset")
            {
                immediate |= fixture.Store.CopyFault is not null && fixture.Store.Incarnation == pin;
            }
        };
        using (damage.StartsWith("large-", StringComparison.Ordinal) ? fixture.Pressure(HashHeadroom) : null)
        {
            var error = await Assert.ThrowsAsync<RpcException>(async () => _ = await fixture.DigestAsync(pinned ? pin : null));
            await Assert.That(error!.StatusCode).IsNotEqualTo(StatusCode.ResourceExhausted)
                .Because("invalid storage types are corruption, even when their payload exceeds available memory");
        }
        fixture.Probe.Checkpoint = null;
        await Assert.That(immediate).IsTrue();
        await Assert.That(fixture.Store.CopyFault).IsNotNull();
        await Assert.That(fixture.Store.CommitSequence).IsEqualTo(committed);
        await fixture.AssertPendingAsync(pin, null);
        await Assert.That(await fixture.Service<IReplicaCopyFaults>().RefusedAsync(fixture.Database, pin, Token)).IsTrue();
        await AssertOrdinaryWriteRefusedAsync(fixture.Store);
        await Assert.That(await fixture.Service<IChangeGroupSource>().AckAsync(fixture.Database, pin, ReplicaId.Of(99, 1, 0), 0, default, Token)).IsFalse();
        await Assert.That((await fixture.DigestAsync(null)).Held).IsFalse();
        await Assert.That((await fixture.DigestAsync(pin)).Held).IsFalse();
        await Assert.That(fixture.Service<SqliteStorageEngine>().Ledger.FailedClosed).IsNull();
        await fixture.SourceReleasedAsync();
    }

    [Test]
    public async Task Healthy_digest_discovery_and_pin_return_the_same_fixed_digest_and_head()
    {
        await using var fixture = await Fixture.StartAsync();
        var pin = fixture.Store.Incarnation;
        var discovery = await fixture.DigestAsync(null);
        var pinned = await fixture.DigestAsync(pin);
        await Assert.That(discovery.Held).IsTrue();
        await Assert.That(pinned.Held).IsTrue();
        await Assert.That(new Ulid(discovery.Incarnation.Span)).IsEqualTo(pin);
        await Assert.That(pinned.Incarnation).IsEqualTo(discovery.Incarnation);
        await Assert.That(pinned.HeadChangeId).IsEqualTo(discovery.HeadChangeId);
        await Assert.That(pinned.Buckets).IsEqualTo(discovery.Buckets);
        await Assert.That(ApplicationEnvelope.Decode(pinned.Buckets.Span, ReplicationProtocol.MaxApplicationBytes).Length).IsEqualTo(4096 * 16);
        var head = await fixture.Service<IChangeGroupSource>().HeadAsync(fixture.Database, pin, Token)
            is long found ? found : throw new InvalidOperationException("the healthy source head was refused");
        await Assert.That(pinned.HeadChangeId).IsEqualTo((ulong)head);
        await fixture.HealthyAsync();
        await fixture.SourceReleasedAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Invalid_SYSTEM_digest_scalars_fail_the_node_closed(bool pinned)
    {
        await using var fixture = await Fixture.StartAsync();
        var engine = fixture.Service<SqliteStorageEngine>();
        var system = engine.SystemStore ?? throw new InvalidOperationException("SYSTEM is missing");
        var id = EntityIds.New().ToString();
        await using (var seed = await system.BeginDurableTransactionAsync(Token))
        {
            _ = await seed.CreateCollectionAsync(new CollectionInfo("digestitems", 1), Token);
            _ = await seed.PutAsync("digestitems", id, "{\"n\":1}"u8.ToArray(), Token);
            _ = await seed.CommitAsync(Token);
        }
        var source = fixture.Service<IReplicaStates>();
        await Assert.That(await source.DigestAsync(ChangeGroups.System, pinned ? system.Incarnation : null, Token)).IsNotNull();
        var changed = await system.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "UPDATE _tb_state SET h2 = $value WHERE collection = 'digestitems' AND id = $id;";
            command.Parameters.AddWithValue("$value", HashValue("large-text"));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery();
        }, "an admission test damaging SYSTEM digest storage", Token);
        await Assert.That(changed).IsEqualTo(1);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            _ = await source.DigestAsync(ChangeGroups.System, pinned ? system.Incarnation : null, Token));
        await Assert.That(StoredDataCorruption.Is(error!)).IsTrue();
        await Assert.That(system.CopyFault).IsNotNull();
        await Assert.That(engine.Ledger.FailedClosed).IsNotNull();
        await Assert.That(fixture.Store.CopyFault).IsNull();
        using var ready = await fixture.Api.Client.GetAsync("/readyz", Token);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await AssertOrdinaryWriteRefusedAsync(fixture.Store);
        _ = await Assert.ThrowsAsync<RpcException>(async () => _ = await fixture.DigestAsync(fixture.Store.Incarnation));
    }

    private static object HashValue(string damage) => damage switch
    {
        "text" => "not-an-integer",
        "large-blob" => new byte[2 * 1024 * 1024],
        "large-text" => new string('x', 2 * 1024 * 1024),
        _ => new byte[] { 1, 2, 3 },
    };

    private static async Task AssertOrdinaryWriteRefusedAsync(SqliteDatabaseStore copy) =>
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var write = await copy.BeginDurableTransactionAsync(Token);
        });

    private sealed class Fixture(ManagementApi api, TenantContext database, SqliteDatabaseStore store, Probe probe, string id) : IAsyncDisposable
    {
        private static readonly string[] SideSuffixes = ["", "-wal", "-shm", "-journal"];
        private readonly long _foregroundBaseline = Volatile.Read(ref probe.ForegroundReserved);
        public ManagementApi Api => api;
        public TenantContext Database => database;
        public SqliteDatabaseStore Store => store;
        public Probe Probe => probe;
        public string Id => id;
        public string? Side { get; private set; }
        private Dictionary<string, byte[]> SideHashes { get; } = [];

        public T Service<T>() where T : notnull => api.Server.Services.GetRequiredService<T>();

        public static async Task<Fixture> StartAsync()
        {
            var probe = new Probe();
            var api = await ManagementApi.StartAsync(settings => settings["memory-budget"] = BudgetBytes.ToString(CultureInfo.InvariantCulture), services =>
            {
                services.AddSingleton<IReplicaRebuild, BusyRebuild>();
                services.AddSingleton(probe);
                services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
                {
                    if (probe.ObservedDatabase is { } observed ? database == observed : database.DatabaseId == probe.Database?.DatabaseId)
                    {
                        probe.Checkpoint?.Invoke(step, database);
                    }
                });
                var memory = services.Last(service => service.ServiceType == typeof(IMemoryBudget));
                services.AddSingleton<IMemoryBudget>(provider => new MemoryProbe(
                    (IMemoryBudget)(memory.ImplementationInstance ?? memory.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, memory.ImplementationType!)), probe));
                var states = services.Last(service => service.ServiceType == typeof(IReplicaStates));
                services.AddSingleton<IReplicaStates>(provider => new SourceProbe(
                    (IReplicaStates)(states.ImplementationInstance ?? states.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, states.ImplementationType!)), probe));
                services.AddSingleton<WriteProbe>();
                services.AddGrpc(options => options.Interceptors.Add<WriteProbe>());
            }, time: new FakeTimeProvider());
            try
            {
                var managed = await api.CreateManagedDatabaseAsync();
                using var data = await api.CreateClientAsync(managed.Organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(data, managed, "items");
                var database = await api.Server.Services.GetRequiredService<ITenantDirectory>()
                    .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, Token)
                    is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");
                var store = (SqliteDatabaseStore)(await api.Server.Services.GetRequiredService<SqliteStorageEngine>()
                    .OpenDatabaseAsync(database, Token))!;
                var id = EntityIds.New().ToString();
                while (DocumentStateEncoding.Bucket("items", id) == DocumentStateEncoding.Bucket(DocumentDelta.Collections, "items"))
                {
                    Token.ThrowIfCancellationRequested();
                    id = EntityIds.New().ToString();
                }
                await using (var write = await store.BeginTransactionAsync(write: true, Token))
                {
                    _ = await write.PutAsync("items", id, Encoding.UTF8.GetBytes("{\"n\":1}"), Token);
                    _ = await write.CommitAsync(Token);
                }

                probe.Database = database;
                return new Fixture(api, database, store, probe, id);
            }
            catch
            {
                await api.DisposeAsync();
                throw;
            }
        }

        public IMemoryReservation Pressure(long leave) => Service<IMemoryBudget>()
            .TryReserve(MemoryLane.Bulk, BudgetBytes - Volatile.Read(ref probe.Reserved) - leave, tenant: null)
            ?? throw new InvalidOperationException("the fixture could not reserve pressure");

        public async Task<int> ReadAsync(Ulid pin, CancellationToken token)
        {
            using var channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
            var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
            var request = new BucketRequest { Database = ChangeGroups.Wire(database, pin) };
            request.Buckets.Add((uint)DocumentStateEncoding.Bucket("items", id));
            using var call = client.ReadBucket(request, cancellationToken: token);
            var count = 0;
            while (await call.ResponseStream.MoveNext(token))
            {
                await Assert.That(new Ulid(call.ResponseStream.Current.Incarnation.Span)).IsEqualTo(pin);
                count += StateList.Parser.ParseFrom(ApplicationEnvelope.Decode(call.ResponseStream.Current.States.Span,
                    ReplicationProtocol.MaxApplicationBytes)).States.Count;
            }

            return count;
        }

        public async Task<DigestReply> DigestAsync(Ulid? pin)
        {
            using var channel = PeerChannel.Create(api.Server.BaseAddress, PeerTls.Credential);
            var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
            return await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database, pin) }, cancellationToken: Token);
        }

        public async Task HealthyAsync()
        {
            await Assert.That(await Service<IReplicaCopyFaults>().RefusedAsync(database, store.Incarnation, Token)).IsFalse();
            await Assert.That(await Service<IRebuildLedger>().GetAsync(database, Token)).IsNull();
        }

        public Task SourceReleasedAsync() => TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(
            Volatile.Read(ref probe.SourceReserved) == 0 && Volatile.Read(ref probe.ForegroundReserved) == _foregroundBaseline), TimeSpan.FromSeconds(10));

        public Task AllReleasedAsync(long baseline) => TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(
            Volatile.Read(ref probe.Reserved) == baseline), TimeSpan.FromSeconds(10));

        public async Task SqlAsync(string sql, object value) => _ = await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery();
        }, "an admission test editing a stored value", Token);

        public Task<(string Type, long Hash, long State, long Body, long Keys)> HashSizeAsync(string column) => store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = $"SELECT typeof(s.{column}), octet_length(s.{column}), octet_length(s.state), octet_length(d.doc), octet_length(s.collection) + octet_length(s.id) FROM _tb_state s JOIN items d ON s.id = d.id WHERE s.collection = 'items' AND s.id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("the damaged row is missing");
            }
            return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        }, "an admission test measuring scalar hash and payload sizes", Token);

        public async Task SwapAndSalvageAsync()
        {
            var original = store.Incarnation;
            var storage = Service<IRebuildStorage>();
            var ledger = Service<IRebuildLedger>();
            await Service<IReplicaCopyFaults>().RecordAsync(database, original, "the admission fixture owns the original fault", Token);
            Side = storage.SidePath(database, original);
            var rebuilt = await storage.SwapAsync(database, original, Side, Token)
                ?? throw new InvalidOperationException("the admission fixture did not swap");
            var pending = await ledger.GetAsync(database, Token) ?? throw new InvalidOperationException("the fault row is missing");
            await ledger.SaveAsync(pending with { Phase = RebuildPhase.Swapped, NewIncarnation = rebuilt }, Token);
            store = (SqliteDatabaseStore)(await Service<SqliteStorageEngine>().OpenDatabaseAsync(database, Token))!;
            var working = await storage.CopyForSalvageAsync(Side, Token);
            try
            {
                var rows = await storage.MeasureSideAsync(working, string.Empty, string.Empty, 1024, 2 * 1024 * 1024, Token);
                var states = await storage.ReadSideAsync(working, rows, Token);
                var repaired = await Service<IRebuildRepair>().RepairRebuildAsync(database, rebuilt, states, Token)
                    ?? throw new InvalidOperationException("the admission fixture salvage was refused");
                await Assert.That(repaired).IsGreaterThan(0);
            }
            finally
            {
                await storage.DiscardWorkingCopyAsync(working, Token);
            }
            foreach (var suffix in SideSuffixes.Where(suffix => File.Exists(Side + suffix)))
            {
                await using var stream = File.OpenRead(Side + suffix);
                SideHashes.Add(suffix, await SHA256.HashDataAsync(stream, Token));
            }
        }

        public async Task AssertPendingAsync(Ulid original, Ulid? additional)
        {
            var pending = await Service<IRebuildLedger>().GetAsync(database, Token);
            await Assert.That(pending?.Phase).IsEqualTo(RebuildPhase.Pending);
            await Assert.That(pending?.OldIncarnation).IsEqualTo(original);
            await Assert.That(pending?.AdditionalIncarnation).IsEqualTo(additional);
            await Assert.That(pending?.NewIncarnation).IsNull();
            await Assert.That(store.RebuildRow?.Refill).IsNull();
            var system = Service<SqliteStorageEngine>().SystemStore ?? throw new InvalidOperationException("SYSTEM is missing");
            await using var read = (SqliteStorageTransaction)await system.BeginTransactionAsync(write: false, Token);
            var count = await read.LedgerAsync(command =>
            {
                using var select = command("""
                    SELECT count(*) FROM _tb_rebuild WHERE organization = $organization AND project = $project AND database = $database
                        AND phase = $phase AND old_incarnation = $old AND additional_incarnation IS $additional AND new_incarnation IS NULL
                        AND source_machine IS NULL AND source_incarnation IS NULL AND source_head IS NULL;
                    """);
                RebuildLedger.Key.Of(database).Bind(select);
                select.Parameters.AddWithValue("$phase", (long)RebuildPhase.Pending);
                select.Parameters.AddWithValue("$old", original.ToString());
                select.Parameters.AddWithValue("$additional", additional is { } pin ? pin.ToString() : DBNull.Value);
                return Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
            }, Token);
            await Assert.That(count).IsEqualTo(1).Because("the durable SYSTEM row names exactly this copy and carries no refill or certificate");
        }

        public async Task AssertSideKeptAsync()
        {
            await Assert.That(SideHashes.ContainsKey(string.Empty)).IsTrue();
            foreach (var (suffix, hash) in SideHashes)
            {
                await Assert.That(File.Exists(Side + suffix)).IsTrue();
                await using var stream = File.OpenRead(Side + suffix);
                await Assert.That((await SHA256.HashDataAsync(stream, Token)).SequenceEqual(hash)).IsTrue();
            }
        }

        public Task<(long State, long Body)> SqlSizeAsync() => store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT octet_length(s.state), octet_length(d.doc) FROM _tb_state s JOIN items d ON s.id = d.id WHERE s.collection = 'items' AND s.id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("the history is missing");
            }

            return (reader.GetInt64(0), reader.GetInt64(1));
        }, "an admission test measuring retained history", Token);

        public async ValueTask DisposeAsync()
        {
            probe.Checkpoint = null;
            await api.DisposeAsync();
            await api.CleanupCompletion;
        }
    }

    private sealed class Probe
    {
        public TenantContext? Database { get; set; }
        public TenantContext? ObservedDatabase { get; set; }
        public Action<string, TenantContext>? Checkpoint { get; set; }
        public Func<CancellationToken, Task>? AfterPage { get; set; }
        public bool FailWrite { get; set; }
        public long Reserved;
        public long SourceReserved;
        public long ForegroundReserved;
        public long PageAvailable;
        public int PageReads;
        public int WritesUnderLease;
    }

    // Observation only: all acceptance/refusal decisions and reservations come from the production budget.
    private sealed class MemoryProbe(IMemoryBudget inner, Probe probe) : IMemoryBudget
    {
        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            if (inner.TryReserve(lane, bytes, tenant) is not { } lease)
            {
                return null;
            }

            Interlocked.Add(ref probe.Reserved, bytes);
            if (lane == MemoryLane.Replication && bytes == SourceBytes)
            {
                Interlocked.Add(ref probe.SourceReserved, bytes);
            }

            if (lane == MemoryLane.Foreground)
            {
                Interlocked.Add(ref probe.ForegroundReserved, bytes);
            }

            return new Observed(lease, lane, probe);
        }

        private sealed class Observed(IMemoryReservation lease, MemoryLane lane, Probe probe) : IMemoryReservation
        {
            private int _released;
            public long Bytes => lease.Bytes;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0)
                {
                    return;
                }

                lease.Dispose();
                Interlocked.Add(ref probe.Reserved, -Bytes);
                if (lane == MemoryLane.Replication && Bytes == SourceBytes)
                {
                    Interlocked.Add(ref probe.SourceReserved, -Bytes);
                }

                if (lane == MemoryLane.Foreground)
                {
                    Interlocked.Add(ref probe.ForegroundReserved, -Bytes);
                }
            }
        }
    }

    private sealed class SourceProbe(IReplicaStates inner, Probe probe) : IReplicaStates
    {
        public ValueTask<bool> MatchedAsync(TenantContext database, ReplicaDigest digest, CancellationToken cancellationToken) =>
            inner.MatchedAsync(database, digest, cancellationToken);

        public ValueTask<ReplicaDigest?> DigestAsync(TenantContext database, Ulid? expected, CancellationToken token) => inner.DigestAsync(database, expected, token);

        public async ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)?> ReadBucketsAsync(
            TenantContext database, Ulid incarnation, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken token)
        {
            if (database != probe.Database)
            {
                return await inner.ReadBucketsAsync(database, incarnation, buckets, after, maxStates, maxBytes, token);
            }

            Interlocked.Increment(ref probe.PageReads);
            Volatile.Write(ref probe.PageAvailable, BudgetBytes - Volatile.Read(ref probe.Reserved));
            var page = await inner.ReadBucketsAsync(database, incarnation, buckets, after, maxStates, maxBytes, token);
            if (probe.AfterPage is { } hook)
            {
                await hook(token);
            }

            return page;
        }

        public ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken token) => inner.StableAsync(database, token);
        public ValueTask<Hlc> HorizonAsync(TenantContext database, CancellationToken token) => inner.HorizonAsync(database, token);
        public ValueTask<int?> ForgetAsync(TenantContext database, Ulid incarnation, IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken token) =>
            inner.ForgetAsync(database, incarnation, keys, horizon, token);
    }

    private sealed class WriteProbe(Probe probe) : Interceptor
    {
        public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> writer,
            ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation) =>
            continuation(request, request is BucketRequest bucket && probe.Database is { } database && bucket.Database.DatabaseId == database.DatabaseId
                ? new ObservedWriter<TResponse>(writer, probe) : writer, context);

        private sealed class ObservedWriter<T>(IServerStreamWriter<T> inner, Probe probe) : IServerStreamWriter<T>
        {
            public WriteOptions? WriteOptions { get => inner.WriteOptions; set => inner.WriteOptions = value; }

            public Task WriteAsync(T message) => WriteAsync(message, CancellationToken.None);

            public async Task WriteAsync(T message, CancellationToken cancellationToken)
            {
                await Assert.That(Volatile.Read(ref probe.SourceReserved)).IsEqualTo(SourceBytes);
                Interlocked.Increment(ref probe.WritesUnderLease);
                if (probe.FailWrite)
                {
                    throw new IOException("injected failure in the real listener's page write");
                }

                await inner.WriteAsync(message, cancellationToken);
                await Assert.That(Volatile.Read(ref probe.SourceReserved)).IsEqualTo(SourceBytes);
            }
        }
    }

    private sealed class BusyRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Refused("the admission fixture owns this rebuild"));
    }
}
