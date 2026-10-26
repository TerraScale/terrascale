using System.Collections;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Features.Shapes;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Client responses through real listeners, CDC and peer streams (decision 0013a, B4).</summary>
public sealed class RemoteAckTests
{
    private const string Collection = "items";

    [Test]
    public async Task A_cell_of_one_answers_after_its_local_commit()
    {
        await using var api = await Documents.DocumentApi.StartAsync();
        var id = EntityIds.New().ToString();
        var response = await api.PutAsync(id, new { n = 1 });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(response.Json.GetProperty("durability").GetString()).IsEqualTo("local_durable");
        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_failed_marker_read_after_a_local_commit_answers_typed_unknown()
    {
        var armed = 0;
        await using var api = await Documents.DocumentApi.StartAsync(services: services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, _) =>
            {
                if (step == "writer.marker" && Volatile.Read(ref armed) == 1)
                {
                    throw new IOException("controlled marker read failure after COMMIT");
                }
            }));
        var token = TestContext.Current!.Execution.CancellationToken;
        // Preserve the landed copy while testing its unknown response; automatic replacement has separate coverage.
        await api.Server.Services.GetServices<IHostedService>().OfType<RebuildRecovery>().Single().StopAsync(token);
        var tenant = await api.TenantOfAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, token))!;
        // Keep a deferred forensic reader admitted before marker failure faults this copy.
        await using var forensic = await store.BeginTransactionAsync(write: false, token);
        Volatile.Write(ref armed, 1);
        var id = EntityIds.New().ToString();
        var response = await api.PutAsync(id, new { n = 1 });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Code).IsEqualTo("write_outcome_unknown");
        await Assert.That(response.Json.GetProperty("error").GetProperty("region").GetString()).IsEqualTo("test-region");
        await Assert.That(response.Json.GetProperty("error").GetProperty("machine").GetString()).IsNotNullOrEmpty();
        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await forensic.GetAsync("items", id, token)).IsNotNull()
            .Because("the commit landed despite its unknown response; ordinary reads must refuse the faulted copy");
        await Assert.That(await api.Server.Services.GetRequiredService<IReplicaCopyFaults>()
            .RefusedAsync(tenant, store.Incarnation, token)).IsTrue();
        await Assert.That(await api.Server.Services.GetRequiredService<IChangeGroupSource>()
            .AckAsync(tenant, store.Incarnation, ReplicaId.Of(99, 1, 0), 0, default, token)).IsFalse();
    }

    [Test]
    [Arguments(PlanTier.Hobby)]
    [Arguments(PlanTier.Pro)]
    public async Task A_document_waits_for_its_own_remote_ack_after_the_receiver_commits(PlanTier plan)
    {
        await using var pair = await Pair.StartAsync(plan: plan);
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(pair.WaitPlan).IsEqualTo(plan);
            await Assert.That(waiting.IsCompleted).IsFalse();
            using var local = await pair.Client.GetAsync(pair.Document(id), pair.Token);
            await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(pending.IsCompleted).IsFalse().Because("the receiver has committed, but has not sent its ACK");
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString())
                .IsEqualTo("remote_durable");
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("local_durable")]
    public async Task A_write_answers_after_its_local_commit_while_the_peer_ack_is_held_and_still_replicates(string? header)
    {
        await using var pair = await Pair.StartAsync();
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, pair.Document(id)) { Content = JsonContent.Create(new { n = 1 }) };
            if (header is not null)
            {
                request.Headers.Add("TB-Durability", header);
            }

            using var response = await pair.LocalClient.SendAsync(request, pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString())
                .IsEqualTo("local_durable");
            await Assert.That(held.Release.Task.IsCompleted).IsFalse().Because("the peer ack is still held");
        }
        finally
        {
            held.Release.TrySetResult();
        }

        await TerraBaseTestServer.EventuallyAsync(async _ =>
        {
            using var peer = await pair.PeerClient.GetAsync(pair.Document(id), pair.Token);
            return peer.StatusCode == HttpStatusCode.OK;
        }, timeout: TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task A_local_transaction_answers_local_durable_and_an_unknown_durability_is_refused()
    {
        await using var pair = await Pair.StartAsync();
        var id = EntityIds.New().ToString();
        using (var response = await pair.LocalClient.PostAsJsonAsync(new Uri($"{pair.DatabasePath}/transactions", UriKind.Relative),
            new { ops = new object[] { new { op = "put", collection = Collection, id, document = new { n = 1 } } } }, pair.Token))
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("local_durable");
        }

        using var invalid = new HttpRequestMessage(HttpMethod.Put, pair.Document(EntityIds.New().ToString())) { Content = JsonContent.Create(new { n = 1 }) };
        invalid.Headers.Add("TB-Durability", "eventual");
        using var refused = await pair.LocalClient.SendAsync(invalid, pair.Token);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await refused.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("error").GetProperty("message").GetString())
            .Contains("TB-Durability");
    }

    [Test]
    public async Task A_held_ack_answers_unknown_at_the_bound_and_the_commit_keeps_replicating()
    {
        await using var pair = await Pair.StartAsync();
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 2 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("write_outcome_unknown");
            await Assert.That(body.GetProperty("error").GetProperty("region").GetString()).IsEqualTo("gru");
            await Assert.That(body.GetProperty("error").GetProperty("machine").GetString()).IsNotNullOrEmpty();
            await Assert.That(response.Headers.RetryAfter).IsNull();
            await Assert.That(body.TryGetProperty("durability", out _)).IsFalse();
            using var local = await pair.Client.GetAsync(pair.Document(id), pair.Token);
            await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }

        using var peer = await pair.PeerClient.GetAsync(pair.Document(id), pair.Token);
        await Assert.That(peer.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task An_ack_after_the_deadline_cannot_confirm_when_the_timeout_callback_is_delayed()
    {
        var time = new DelayedAckClock();
        await using var pair = await Pair.StartAsync(ackTime: time);
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            _ = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            time.Advance(RemoteAckWaiter.Bound + TimeSpan.FromSeconds(1));
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("write_outcome_unknown");
            using var remote = await pair.PeerClient.GetAsync(pair.Document(id), pair.Token);
            await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task Cancelling_the_response_wait_keeps_the_commit_replicating()
    {
        await using var pair = await Pair.StartAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(pair.Token);
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, cancellation.Token);
        await held.Applied.Task.WaitAsync(pair.Token);
        var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
        await Assert.That(waiting.IsCompleted).IsFalse();
        await cancellation.CancelAsync();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(async () => await waiting.WaitAsync(pair.Token)).Throws<OperationCanceledException>();
        held.Release.TrySetResult();
        using var local = await pair.Client.GetAsync(pair.Document(id), pair.Token);
        using var remote = await pair.PeerClient.GetAsync(pair.Document(id), pair.Token);
        await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var next = await pair.Client.PutAsJsonAsync(pair.Document(EntityIds.New().ToString()), new { n = 2 }, pair.Token);
        await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task Losing_the_last_peer_after_local_commit_answers_unknown()
    {
        await using var pair = await Pair.StartAsync();
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var started = Stopwatch.GetTimestamp();
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        await held.Applied.Task.WaitAsync(pair.Token);
        var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
        await Assert.That(waiting.IsCompleted).IsFalse();
        await pair.StopPeerAsync();
        using var response = await pending.WaitAsync(pair.Token);
        await Assert.That(Stopwatch.GetElapsedTime(started)).IsLessThan(RemoteAckWaiter.Bound - TimeSpan.FromSeconds(2))
            .Because("losing the last peer wakes the wait before its confirmation deadline");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("error").GetProperty("code").GetString())
            .IsEqualTo("write_outcome_unknown");
        await Assert.That(response.Headers.RetryAfter).IsNull();
        using var local = await pair.Client.GetAsync(pair.Document(id), pair.Token);
        await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_down_peer_refuses_before_the_document_executes()
    {
        await using var pair = await Pair.StartAsync();
        await pair.StopPeerAsync();
        var id = EntityIds.New().ToString();
        using var response = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 3 }, pair.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("remote_region_unavailable");
        await Assert.That(response.Headers.RetryAfter).IsNotNull();
        using var absent = await pair.Client.GetAsync(pair.Document(id), pair.Token);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // Every replica region removed (the step before a delete): no retry can succeed, so the write is refused with a conflict and
    // no Retry-After, until a placement update adds a region back.
    [Test]
    public async Task A_database_left_in_its_home_region_alone_refuses_writes_with_a_conflict_until_a_region_is_added()
    {
        await using var pair = await Pair.StartAsync();
        await Assert.That(await pair.SetReplicaRegionsAsync([])).IsTrue();
        var id = EntityIds.New().ToString();
        using var response = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("error").GetProperty("code").GetString())
            .IsEqualTo("placement_needs_two_regions");
        await Assert.That(response.Headers.RetryAfter).IsNull();
        using (var absent = await pair.Client.GetAsync(pair.Document(id), pair.Token))
        {
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        await Assert.That(await pair.SetReplicaRegionsAsync(["sjc"])).IsTrue();
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var written = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 2 }, token);
            return written.IsSuccessStatusCode;
        }, TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task An_unknown_database_keeps_its_404_even_when_the_peer_is_down()
    {
        await using var pair = await Pair.StartAsync();
        await pair.StopPeerAsync();
        var uri = new Uri($"/v1/projects/{pair.Database.ProjectId}/databases/missing/collections/items/documents/by-key?id={EntityIds.New()}", UriKind.Relative);
        using var response = await pair.Client.PutAsJsonAsync(uri, new { n = 1 }, pair.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("error").GetProperty("code").GetString())
            .IsEqualTo("unknown_database");
        await Assert.That(response.Headers.RetryAfter).IsNull();
        await Assert.That(pair.ReachedWait.Task.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments("gru")]
    [Arguments("fra")]
    public async Task A_connected_same_region_or_unplaced_peer_cannot_admit_a_write(string peerRegion)
    {
        await using var pair = await Pair.StartAsync(peerRegion);
        var id = EntityIds.New().ToString();
        using var response = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("error").GetProperty("code").GetString())
            .IsEqualTo("remote_region_unavailable");
        using var absent = await pair.Client.GetAsync(pair.Document(id), pair.Token);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_transaction_waits_for_one_ack_covering_every_operation()
    {
        await using var pair = await Pair.StartAsync();
        var first = EntityIds.New().ToString();
        var second = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(first);
        var pending = pair.Client.PostAsJsonAsync(new Uri($"{pair.DatabasePath}/transactions", UriKind.Relative),
            new
            {
                ops = new[]
                {
                    new { op = "put", collection = "items", id = first, document = new { n = 1 } },
                    new { op = "put", collection = "items", id = second, document = new { n = 2 } },
                },
            }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            using var local = await pair.Client.GetAsync(pair.Document(second), pair.Token);
            await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(pending.IsCompleted).IsFalse();
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
            foreach (var id in new[] { first, second })
            {
                using var remote = await pair.PeerClient.GetAsync(pair.Document(id), pair.Token);
                await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task A_refused_remote_ack_never_confirms_the_local_commit()
    {
        await using var pair = await Pair.StartAsync();
        pair.PeerAck.Refuse = true;
        var id = EntityIds.New().ToString();
        using var response = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("error").GetProperty("code").GetString())
            .IsEqualTo("write_outcome_unknown");
        await Assert.That(pair.PeerAck.Refused.Task.IsCompleted).IsTrue().Because("a real live ACK was refused for this database");
        using var local = await pair.Client.GetAsync(pair.Document(id), pair.Token);
        using var absent = await pair.PeerClient.GetAsync(pair.Document(id), pair.Token);
        await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task An_assertion_only_transaction_answers_unchanged_while_the_peer_is_down()
    {
        await using var pair = await Pair.StartAsync();
        await pair.StopPeerAsync();
        using var response = await pair.Client.PostAsJsonAsync(new Uri($"{pair.DatabasePath}/transactions", UriKind.Relative),
            new { ops = new[] { new { op = "assert", collection = "items", id = EntityIds.New().ToString(), absent = true } } }, pair.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("unchanged");
    }

    [Test]
    public async Task Full_confirmation_admission_refuses_before_execution_and_releases_every_slot()
    {
        await using var pair = await Pair.StartAsync();
        var leases = new List<RemoteLease>();
        var id = EntityIds.New().ToString();
        try
        {
            for (var i = 0; i < RemoteAckWaiter.MaxPerDatabase; i++)
            {
                var admitted = await pair.Remote.AdmitAsync(pair.Database, pair.Token);
                leases.Add(admitted is RemoteLease lease ? lease : throw new InvalidOperationException("a free confirmation slot was refused"));
            }

            using var response = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("confirmation_busy");
            await Assert.That(response.Headers.RetryAfter).IsNotNull();
            using var absent = await pair.Client.GetAsync(pair.Document(id), pair.Token);
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        using var accepted = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 2 }, pair.Token);
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task An_ack_arriving_before_wait_registration_still_confirms_its_own_marker()
    {
        await using var pair = await Pair.StartAsync();
        pair.HoldWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(EntityIds.New().ToString()), new { n = 1 }, pair.Token);
        try
        {
            _ = await pair.ReachedWait.Task.WaitAsync(pair.Token);
            // The next sender pass follows its completed ACK handler, including confirmation-history notification.
            await pair.ReadAfterAck.Task.WaitAsync(pair.Token);
            pair.HoldWait.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
        }
        finally
        {
            pair.HoldWait.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task A_refill_covering_a_write_never_lets_a_later_live_ack_confirm_it()
    {
        await using var pair = await Pair.StartAsync();
        pair.Refill.Arm(pair.Database);
        var id = EntityIds.New().ToString();
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            var marker = await pair.ReachedWait.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);

            // Every pass from below the write refills, so only a refill's persisted ack reaches it. Once a pass reads from the
            // write on, no refill is in flight and every later batch is live.
            pair.Refill.Below(marker.ChangeId);
            await pair.Refill.Retained.Task.WaitAsync(pair.Token);
            // The pass reads from the refill's ack in memory; the sender persists that ack behind the stream.
            await TerraBaseTestServer.EventuallyAsync(async token => await pair.Source.AckedAsync(pair.Database, pair.PeerNode, token) is { } acked
                && acked.Incarnation == marker.Incarnation && acked.Acked >= marker.ChangeId);
            await Assert.That(waiting.IsCompleted).IsFalse().Because("a refill covered the write, and the next live ACK must arrive while it waits");
            using (var later = await pair.Client.PutAsJsonAsync(pair.Document(EntityIds.New().ToString()), new { n = 2 }, pair.Token))
            {
                await Assert.That(later.StatusCode).IsEqualTo(HttpStatusCode.Created);
                await Assert.That((await later.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
            }

            var store = await pair.OriginStoreAsync();
            var shape = await ShapeAsync(store);
            _ = await SealBoundedAsync(pair, pair.OriginSealer, store, shape, 0);
            await Assert.That(await ChunksAsync(pair.OriginSealer, store, shape.Handle, 0, pair.Token)).IsEmpty()
                .Because("a later live ACK cannot close the earlier unproven refill gap in the durable prefix");

            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("write_outcome_unknown");
            await Assert.That(await waiting.WaitAsync(pair.Token)).IsFalse();
            using var remote = await pair.PeerClient.GetAsync(pair.Document(id), pair.Token);
            await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("the refill holds the write; it only cannot certify it");
        }
        finally
        {
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    public async Task A_cut_log_refill_replays_unconfirmed_writes_without_certifying_the_refill(bool cutWrite, bool unregistered, bool large)
    {
        var timing = Stopwatch.StartNew();
        var output = TUnit.Core.TestContext.Current!.OutputWriter;
        void Trace(string phase)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"refill timing {timing.Elapsed.TotalSeconds:F3}s {phase}"));
        }
        // This includes building real >64 MiB history; the product ACK deadline remains ten seconds.
        await using var pair = await Pair.StartAsync(fixtureBudget: large ? TimeSpan.FromSeconds(90) : null);
        pair.ReplayTrace = Trace;
        Trace("listeners and catalog ready");
        var store = await pair.OriginTursoAsync();
        store.BackupAnchor = null;
        async Task LocalAsync(string id, byte[]? body = null)
        {
            _ = await store.WriteAsync(async (transaction, token) =>
            {
                _ = await transaction.PutAsync("items", id, body ?? Encoding.UTF8.GetBytes("{\"n\":1}"), token);
                return true;
            }, static _ => true, pair.Token);
        }

        await LocalAsync(EntityIds.New().ToString());
        var before = (await pair.Source.HeadAsync(pair.Database, store.Incarnation, pair.Token))!.Value;
        await TerraBaseTestServer.EventuallyAsync(async token =>
            await pair.Source.AckedAsync(pair.Database, pair.PeerNode, token) is { } cursor && cursor.Acked >= before);
        pair.PeerAck.Refuse = true;
        await LocalAsync(EntityIds.New().ToString());
        await pair.PeerAck.Refused.Task.WaitAsync(pair.Token);

        var head = pair.HoldDatabaseHead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage>? pending = null;
        Task<HttpResponseMessage>? later = null;
        try
        {
            await pair.AgeOriginCaptureAsync(TimeSpan.FromHours(25));
            var source = pair.OriginServer.Services.GetRequiredService<TursoChangeGroupSource>();
            await source.TrimAsync(store, pair.Token);
            await Assert.That(await pair.Source.RetainsAsync(pair.Database, store.Incarnation, before, pair.Token)).IsFalse();
            await pair.DatabaseHeadReached.Task.WaitAsync(pair.Token);
            var floor = (await source.RetainedAfterAsync(pair.Database, store.Incarnation, null, long.MaxValue, pair.Token))!.Value;
            // An older retained backlog must be repaired without forcing it into the waiting write's live replay.
            for (var n = 0; n < 3; n++)
            {
                await LocalAsync(EntityIds.New().ToString());
            }

            if (unregistered || large)
            {
                pair.HoldWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            var id = EntityIds.New().ToString();
            pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
            var marker = await pair.ReachedWait.Task.WaitAsync(pair.Token);
            Trace("write committed; retained-data setup starts");
            if (large)
            {
                var paddingId = EntityIds.New().ToString();
                var padding = Encoding.UTF8.GetBytes("{\"padding\":\"" + new string('x', 900 * 1024) + "\"}");
                // Real updates keep over 64 MiB of capture above the marker, with one document to repair in the refill.
                for (var n = 0; n < 76; n++)
                {
                    padding[^3] = (byte)('a' + n % 26);
                    await LocalAsync(paddingId, padding);
                }

                var bytes = await store.WithWriterAsync(session =>
                {
                    using var command = session.Connection.CreateCommand();
                    command.CommandText = "SELECT coalesce(sum(length(after)), 0) FROM turso_cdc WHERE change_id > $after;";
                    command.Parameters.AddWithValue("$after", marker.ChangeId);
                    return (long)command.ExecuteScalar()!;
                }, "a test measuring pending replay bytes", pair.Token);
                await Assert.That(bytes).IsGreaterThan(64L * 1024 * 1024);
                await Assert.That(await source.RetainedAfterAsync(pair.Database, store.Incarnation, null, floor, pair.Token))
                    .IsEqualTo(floor).Because("the capped fallback must not swallow writes admitted after its captured head");
            }

            Trace("retained-data setup complete");
            if (cutWrite)
            {
                await pair.AgeOriginCaptureAsync(TimeSpan.FromHours(25));
                await source.TrimAsync(store, pair.Token);
            }

            if (!unregistered)
            {
                pair.HoldWait?.TrySetResult();
                _ = await pair.WaitEntered.Task.WaitAsync(pair.Token);
                // WaitEntered precedes the async placement read inside PeerStreams.WaitAsync. Keep the sender held until
                // the real waiter has registered its marker, so this arm cannot take the unknown-position fallback.
                var acks = pair.OriginServer.Services.GetRequiredService<RemoteAckWaiter>();
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(
                    acks.ReplayThrough(pair.Database, marker.Incarnation, marker.ChangeId) == marker.ChangeId - 1), timeout: RemoteAckWaiter.Bound);
                Trace("known marker registered");
            }

            if (unregistered && large)
            {
                pair.HoldReplay = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            var replay = pair.ReplayStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.PeerAck.Refuse = false;
            Trace("release sender head");
            head.TrySetResult();
            long? captured = null;
            if (pair.HoldReplay is { } held)
            {
                captured = await pair.ReplayReached.Task.WaitAsync(pair.Token);
                pair.HoldWait!.TrySetResult();
                _ = await pair.WaitEntered.Task.WaitAsync(pair.Token);
                pair.ExpectWait();
                pair.HoldWait = null;
                later = pair.Client.PutAsJsonAsync(pair.Document(EntityIds.New().ToString()), new { n = 2 }, pair.Token);
                _ = await pair.WaitEntered.Task.WaitAsync(pair.Token);
                held.TrySetResult();
            }

            var start = await replay.Task.WaitAsync(pair.Token);
            Trace("boundary selected; refill can start");
            if (captured is { } capturedHead)
            {
                await Assert.That(start).IsLessThanOrEqualTo(capturedHead).Because("a later admission must remain above the capped refill");
            }

            var unknown = cutWrite || (unregistered && large);
            if (unknown)
            {
                await Assert.That(start).IsGreaterThanOrEqualTo(marker.ChangeId);
            }
            else if (unregistered)
            {
                await Assert.That(start).IsEqualTo(floor).Because("an admitted attempt's commit position is not registered yet");
            }
            else
            {
                await Assert.That(start).IsGreaterThan(floor).Because("known waiters do not replay the whole retained backlog");
                await Assert.That(start).IsLessThan(marker.ChangeId);
            }

            pair.HoldWait?.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            Trace("original PUT answered");
            await Assert.That(response.StatusCode).IsEqualTo(unknown ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token);
            await Assert.That(unknown ? body.GetProperty("error").GetProperty("code").GetString() : body.GetProperty("durability").GetString())
                .IsEqualTo(unknown ? "write_outcome_unknown" : "remote_durable");
            using var remote = await pair.PeerClient.GetAsync(pair.Document(id), pair.Token);
            await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await remote.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
            if (later is not null)
            {
                using var following = await later.WaitAsync(pair.Token);
                Trace("later PUT answered");
                await Assert.That(following.StatusCode).IsEqualTo(HttpStatusCode.Created);
                await Assert.That((await following.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString())
                    .IsEqualTo("remote_durable");
            }
        }
        finally
        {
            Trace("test assertions/cleanup entered");
            pair.HoldReplay?.TrySetResult();
            pair.PeerAck.Refuse = false;
            head.TrySetResult();
            pair.HoldWait?.TrySetResult();
            if (pending is not null)
            {
                using var response = await pending.WaitAsync(pair.Token);
            }

            if (later is not null)
            {
                using var response = await later.WaitAsync(pair.Token);
            }
        }
    }

    // A copy keeps the source's peer rows under the old scope: its cursor is 0 and requires a refill. A write that commits
    // before the first refill takes the copy's head must still be confirmed by a live ACK, not hidden under the refill.
    [Test]
    public async Task A_write_to_a_fresh_copy_before_its_first_refill_is_confirmed_by_a_live_ack()
    {
        await using var pair = await Pair.StartAsync();
        // Held before the copy exists, so no sender pass can take the copy's head before the write commits.
        var head = pair.HoldHead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage>? pending = null;
        HttpResponseMessage? response = null;
        try
        {
            var copy = await pair.CopyAsync("copy");
            var cursor = await pair.Source.AckedAsync(copy, pair.PeerNode, pair.Token);
            await Assert.That(cursor is { Acked: 0, RequiresRefill: true }).IsTrue().Because("the copied peer row names the source's incarnation");
            await Assert.That(await pair.PeerServer.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(copy, pair.Token)).IsNull().Because("catalog replication has not created the peer's restored file");

            var id = EntityIds.New().ToString();
            pending = pair.Client.PutAsJsonAsync(pair.Document(id, "copy"), new { n = 1 }, pair.Token);
            var marker = await pair.ReachedWait.Task.WaitAsync(pair.Token);
            await Assert.That(marker.Incarnation).IsEqualTo(cursor!.Value.Incarnation);
            await Assert.That(await pair.Source.RetainsAsync(copy, marker.Incarnation, 0, pair.Token)).IsTrue().Because("the copy's own change log is whole");
            head.TrySetResult();

            response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because("a refill at the copy's head would cover the write and never certify it");
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
            // The sender persists the ack behind the stream, after the wait it released.
            await TerraBaseTestServer.EventuallyAsync(async token => await pair.Source.AckedAsync(copy, pair.PeerNode, token) is { RequiresRefill: false } acked
                && acked.Incarnation == marker.Incarnation && acked.Acked >= marker.ChangeId);
            using var remote = await pair.PeerClient.GetAsync(pair.Document(id, "copy"), pair.Token);
            await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await remote.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        }
        finally
        {
            head.TrySetResult();
            response?.Dispose();
            if (pending is not null && response is null)
            {
                using var completed = await pending.WaitAsync(pair.Token);
            }
        }
    }

    // Batch 8 (2026-10-25): with ts-auth writing remote_durable in three regions, writers were held for seconds. A write that
    // waits for another region's ack holds neither its database's writer nor a transaction: the writer, another write of the
    // same database and the system database's durable writes all proceed while its ack is held.
    [Test]
    public async Task A_write_waiting_for_its_remote_ack_holds_no_writer_while_others_commit()
    {
        await using var pair = await Pair.StartAsync();
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        var promptly = TimeSpan.FromSeconds(5);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            _ = await pair.WaitEntered.Task.WaitAsync(pair.Token);

            // The writer is free: taking it does not wait for the held ack.
            var store = await pair.OriginTursoAsync();
            await Assert.That(await store.WithWritersHeldAsync(() => Task.FromResult(true), pair.Token, "a test taking the writer").WaitAsync(promptly, pair.Token))
                .IsTrue();

            // Another write of the same database commits and answers. (Its ack, like every later ack on this link, would wait
            // behind the held one: the gate stalls the peer's receiver, which applies in order.)
            using (var local = await pair.LocalClient.PutAsJsonAsync(pair.Document(EntityIds.New().ToString()), new { n = 2 }, pair.Token).WaitAsync(promptly, pair.Token))
            {
                await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.Created);
                await Assert.That((await local.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("local_durable");
            }

            // As the catalog anchor writes: a durable system write.
            var system = await pair.OriginServer.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(pair.Token);
            await using (var transaction = await system.BeginDurableTransactionAsync(pair.Token).AsTask().WaitAsync(promptly, pair.Token))
            {
                _ = await transaction.PutAsync("remote_wait_probe", id, Encoding.UTF8.GetBytes("""{"anchor":1}"""), pair.Token);
                _ = await transaction.CommitAsync(pair.Token).AsTask().WaitAsync(promptly, pair.Token);
            }

            await Assert.That(pending.IsCompleted).IsFalse().Because("the first write still waits for its held ack");
        }
        finally
        {
            held.Release.TrySetResult();
        }

        using var response = await pending.WaitAsync(pair.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
    }

    [Test]
    public async Task An_ack_confirms_and_the_link_ships_on_while_the_sender_writer_is_held()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.CopyAsync("copy");
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? holding = null;
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var marker = await pair.ReachedWait.Task.WaitAsync(pair.Token);
            _ = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            // The sender's own writer of db, held as a busy machine holds it: proofs and acks of db need it, the wait does not.
            holding = (await pair.OriginTursoAsync()).WithWritersHeldAsync(async () =>
            {
                taken.TrySetResult();
                await release.Task.WaitAsync(pair.Token);
                return true;
            }, pair.Token, "a test holding the sender's writer");
            await taken.Task.WaitAsync(pair.Token);

            held.Release.TrySetResult();
            using (var response = await pending.WaitAsync(pair.Token))
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
                await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
            }

            // The same link ships and confirms another database's batch while db's ack still waits for its writer.
            using (var other = await pair.Client.PutAsJsonAsync(pair.Document(EntityIds.New().ToString(), "copy"), new { n = 2 }, pair.Token))
            {
                await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.Created);
                await Assert.That((await other.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
            }

            await Assert.That(holding.IsCompleted).IsFalse();
            await Assert.That((await pair.Source.AckedAsync(pair.Database, pair.PeerNode, pair.Token))!.Value.Acked).IsLessThan(marker.ChangeId)
                .Because("the ack is persisted only once the writer is free");

            release.TrySetResult();
            await Assert.That(await holding).IsTrue();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await pair.Source.AckedAsync(pair.Database, pair.PeerNode, token) is { } cursor && cursor.Incarnation == marker.Incarnation && cursor.Acked >= marker.ChangeId);
        }
        finally
        {
            release.TrySetResult();
            held.Release.TrySetResult();
            if (holding is not null)
            {
                _ = await holding;
            }

            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task A_copy_replaced_after_ack_validation_does_not_retract_remote_durability()
    {
        await using var pair = await Pair.StartAsync();
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var marker = await pair.ReachedWait.Task.WaitAsync(pair.Token);
            _ = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            pair.Refusal.ArmPersistenceOnly(pair.Database);
            held.Release.TrySetResult();
            await pair.Refusal.Rejected.Task.WaitAsync(pair.Token);
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString())
                .IsEqualTo("remote_durable");
            await Assert.That((await pair.Source.AckedAsync(pair.Database, pair.PeerNode, pair.Token))!.Value.Acked)
                .IsLessThan(marker.ChangeId).Because("a replaced copy refuses persistence even after a valid remote confirmation");
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    [Arguments(PlanTier.Hobby)]
    [Arguments(PlanTier.Pro)]
    public async Task An_ack_for_a_replaced_copy_after_the_receiver_commits_answers_unknown(PlanTier plan)
    {
        await using var pair = await Pair.StartAsync(plan: plan);
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(pair.WaitPlan).IsEqualTo(plan);
            pair.Refusal.Arm(pair.Database);
            held.Release.TrySetResult();
            await pair.Refusal.Rejected.Task.WaitAsync(pair.Token);
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("write_outcome_unknown");
            await Assert.That(body.GetProperty("error").GetProperty("region").GetString()).IsEqualTo("gru");
            await Assert.That(body.GetProperty("error").GetProperty("machine").GetString()).IsNotNullOrEmpty();
            await Assert.That(response.Headers.RetryAfter).IsNull();
            await Assert.That(body.TryGetProperty("durability", out _)).IsFalse();
            await Assert.That(await waiting.WaitAsync(pair.Token)).IsFalse();
            using var local = await pair.Client.GetAsync(pair.Document(id), pair.Token);
            using var remote = await pair.PeerClient.GetAsync(pair.Document(id), pair.Token);
            await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task A_held_ack_keeps_its_commit_out_of_sealed_chunks()
    {
        await using var pair = await Pair.StartAsync();
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            var store = await pair.OriginStoreAsync();
            var sealer = pair.OriginSealer;
            var shape = await ShapeAsync(store);
            // Sealing is not gated; the reads below stay gated until the ACK, so the held commit is not published.
            _ = await SealBoundedAsync(pair, sealer, store, shape, 0);
            var visible = await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token);
            await Assert.That(Rows(visible).Any(row => row.Id == id)).IsFalse().Because("the peer has not acknowledged the commit");
            using var local = await pair.Client.GetAsync(pair.Document(id), pair.Token);
            await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK).Because("by-key reads show local-only writes");
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            _ = await sealer.SealUpToHeadAsync(store, shape, SealedEnd(visible), pair.Token);
            var published = await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token);
            await Assert.That(Rows(published).Single(row => row.Id == id).N).IsEqualTo(1);
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task A_refused_ack_never_publishes_its_commit()
    {
        await using var pair = await Pair.StartAsync();
        pair.PeerAck.Refuse = true;
        var id = EntityIds.New().ToString();
        using var response = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(pair.PeerAck.Refused.Task.IsCompleted).IsTrue().Because("a real live ACK was refused for this database");
        var store = await pair.OriginStoreAsync();
        var sealer = pair.OriginSealer;
        var shape = await ShapeAsync(store);
        _ = await SealBoundedAsync(pair, sealer, store, shape, 0);
        var visible = await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token);
        await Assert.That(Rows(visible).Any(row => row.Id == id)).IsFalse().Because("no ACK ever covered the commit");
    }

    [Test]
    public async Task A_local_cell_of_one_seals_its_commit_at_once()
    {
        await using var api = await Documents.DocumentApi.StartAsync();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var id = EntityIds.New().ToString();
        var response = await api.PutAsync(id, new { n = 1 });
        await Assert.That(response.Json.GetProperty("durability").GetString()).IsEqualTo("local_durable");
        var store = await OpenStoreAsync(api.Server, await api.TenantOfAsync(), token);
        var sealer = SealerOf(api.Server, new LocalDurability());
        var shape = await ShapeAsync(store);
        var boundary = await sealer.SealUpToHeadAsync(store, shape, 0, token);
        var visible = await ChunksAsync(sealer, store, shape.Handle, 0, token);
        await Assert.That(SealedEnd(visible)).IsEqualTo(boundary);
        await Assert.That(Rows(visible).Single(row => row.Id == id).N).IsEqualTo(1);
    }

    [Test]
    public async Task Missing_capture_and_frontier_never_prove_a_local_only_write()
    {
        await using var pair = await Pair.StartAsync();
        pair.PeerAck.Refuse = true;
        var id = EntityIds.New().ToString();
        using var response = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var store = await pair.OriginStoreAsync();
        var turso = await pair.OriginTursoAsync();
        await turso.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "DELETE FROM turso_cdc;";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM _tb_shape_chunk WHERE handle = '~frontier';";
            command.ExecuteNonQuery();
            return true;
        }, "a test removing durability evidence", pair.Token);
        var shape = await ShapeAsync(store);
        _ = await SealBoundedAsync(pair, pair.OriginSealer, store, shape, 0);
        await Assert.That(await ChunksAsync(pair.OriginSealer, store, shape.Handle, 0, pair.Token)).IsEmpty()
            .Because("missing capture and a missing proof cannot turn a local-only write into a remotely durable prefix");
    }

    [Test]
    public async Task An_unacked_overwrite_is_never_published_before_its_ack()
    {
        await using var pair = await Pair.StartAsync();
        var id = EntityIds.New().ToString();
        using (var created = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        var store = await pair.OriginStoreAsync();
        var sealer = pair.OriginSealer;
        var shape = await ShapeAsync(store);
        _ = await sealer.SealUpToHeadAsync(store, shape, 0, pair.Token);
        var first = await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token);
        await Assert.That(Rows(first).Single(row => row.Id == id).N).IsEqualTo(1);
        // The create's wait completed WaitEntered: the overwrite needs its own.
        pair.ExpectWait();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 2 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            _ = await SealBoundedAsync(pair, sealer, store, shape, SealedEnd(first));
            var visible = await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token);
            await Assert.That(visible.Length).IsGreaterThanOrEqualTo(first.Length);
            for (var n = 0; n < first.Length; n++)
            {
                await Assert.That(visible[n].Body.AsSpan().SequenceEqual(first[n].Body)).IsTrue().Because("sealed bytes are immutable");
            }

            await Assert.That(Rows(visible).Where(row => row.Id == id).Select(row => row.N).ToArray()).IsEquivalentTo([1])
                .Because("the unacked overwrite stays out of every readable chunk");
            await Assert.That(await pair.PersistedChunksAsync(shape.Handle)).IsEqualTo((first.Length, SealedEnd(first)))
                .Because("the gate keeps the overwrite out of the persisted chunks, not only out of reads");
            var unsealed = await ShapeAsync(store);
            _ = await SealBoundedAsync(pair, sealer, store, unsealed, 0);
            var candidate = await ChunksAsync(sealer, store, unsealed.Handle, 0, pair.Token);
            await Assert.That(Rows(candidate).Any(row => row.Id == id && row.N == 2)).IsFalse()
                .Because("materializing an older acknowledged event must not publish the newer unacknowledged value");
            await Assert.That((await pair.PersistedChunksAsync(unsealed.Handle)).Count).IsEqualTo(0)
                .Because("the first commit's document has the unacked overwrite above F, so nothing seals at all");
            using var local = await pair.Client.GetAsync(pair.Document(id), pair.Token);
            await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK);
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString())
                .IsEqualTo("remote_durable");
            _ = await sealer.SealUpToHeadAsync(store, shape, SealedEnd(visible), pair.Token);
            var published = await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token);
            await Assert.That(Rows(published).Any(row => row.Id == id && row.N == 2)).IsTrue().Because("the ACK publishes the overwrite");
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    // Under steady writes the head is never F: a commit below an unacked write of another document still publishes, at F.
    [Test]
    public async Task A_commit_below_an_unacked_write_of_another_document_publishes_at_the_frontier()
    {
        await using var pair = await Pair.StartAsync();
        var (acked, unacked) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        using (var created = await pair.Client.PutAsJsonAsync(pair.Document(acked), new { n = 1 }, pair.Token))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        var store = await pair.OriginStoreAsync();
        var sealer = pair.OriginSealer;
        var turso = await pair.OriginTursoAsync();
        var covered = await FeedHeadAsync(turso, pair.Token);
        await TerraBaseTestServer.EventuallyAsync(async token => await sealer.FrontierAsync(store, token) >= covered);
        pair.ExpectWait();
        using var held = pair.PeerAck.Hold(unacked);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(unacked), new { n = 2 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            _ = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(await FeedHeadAsync(turso, pair.Token)).IsGreaterThan(covered);
            var frontier = await sealer.FrontierAsync(store, pair.Token);
            var shape = await ShapeAsync(store);
            _ = await SealBoundedAsync(pair, sealer, store, shape, 0);
            var visible = await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token);
            await Assert.That(SealedEnd(visible)).IsEqualTo(frontier).Because("the seal covers every commit F holds");
            await Assert.That(Rows(visible).Select(row => row.Id).ToArray()).IsEquivalentTo([acked])
                .Because("the held write stays out, and does not hold back the document F covers");
            using var body = JsonDocument.Parse(visible[^1].Body);
            await Assert.That(body.RootElement.GetProperty("values_at").GetString()).IsEqualTo(frontier.ToString(CultureInfo.InvariantCulture));
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            _ = await sealer.SealUpToHeadAsync(store, shape, SealedEnd(visible), pair.Token);
            await Assert.That(Rows(await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token)).Any(row => row.Id == unacked)).IsTrue();
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task The_worker_publishes_a_commit_only_after_its_ack()
    {
        await using var pair = await Pair.StartAsync();
        var store = await pair.OriginStoreAsync();
        var sealer = pair.OriginSealer;
        var shape = await ShapeAsync(store);
        var id = EntityIds.New().ToString();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(pair.Token);
        var worker = sealer.RunDatabaseAsync(store, () => [new ShapeSealRequest(shape, 0)], stop.Token);
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            // The worker wakes on the local commit, which the peer has not acknowledged, so the row must stay unpublished.
            var visible = await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token);
            await Assert.That(Rows(visible).Any(row => row.Id == id)).IsFalse();
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            // No later commit follows the ACK, so the worker must wake on the ACK itself.
            await TerraBaseTestServer.EventuallyAsync(async token => Rows(await ChunksAsync(sealer, store, shape.Handle, 0, token)).Any(row => row.Id == id),
                timeout: TimeSpan.FromSeconds(30));
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            stop.Cancel();
            await Assert.That(async () => await worker).Throws<OperationCanceledException>();
        }
    }

    [Test]
    public async Task Http_shape_reads_serve_nothing_above_a_held_proof_and_publish_once_it_is_released()
    {
        await using var pair = await Pair.StartAsync();
        var first = EntityIds.New().ToString();
        using (var created = await pair.Client.PutAsJsonAsync(pair.Document(first), new { n = 1 }, pair.Token))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        // The bootstrap snapshot seals once the ACK's proof covers its head.
        Reply bootstrap = null!;
        await TerraBaseTestServer.EventuallyAsync(async token =>
            (bootstrap = await GetShapeAsync(pair, ShapeUri(pair, "offset=-1"), token)).Status == HttpStatusCode.TemporaryRedirect);
        var page = await GetShapeAsync(pair, new Uri(bootstrap.Location!, UriKind.Relative), pair.Token);
        await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(OpIds(page)).Contains(first);
        var handle = page.Json.GetProperty("handle").GetString()!;
        var published = page.Json.GetProperty("next_offset").GetString()!;
        var at = long.Parse(published, CultureInfo.InvariantCulture);

        pair.ExpectWait();
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 2 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            var head = await FeedHeadAsync(await pair.OriginTursoAsync(), pair.Token);
            await Assert.That(head).IsGreaterThan(at).Because("the held write committed locally");

            // Fresh at F: up to date at the published frontier, with nothing of the held commit.
            var fresh = await GetShapeAsync(pair, ShapeUri(pair, $"handle={handle}&offset={published}"), pair.Token);
            await Assert.That(fresh.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(OpIds(fresh)).DoesNotContain(id);
            await Assert.That(fresh.Json.GetProperty("next_offset").GetString()).IsEqualTo(published);
            await Assert.That(fresh.Json.GetProperty("values_at").GetString()).IsEqualTo(published);
            await Assert.That(fresh.Json.GetProperty("up_to_date").GetBoolean()).IsTrue();

            // Above F but committed: unavailable, never must_refetch. Above the committed head: must_refetch.
            var above = await GetShapeAsync(pair, ShapeUri(pair, $"handle={handle}&offset={head}"), pair.Token);
            await Assert.That(above.Status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(above.Json.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
            var beyond = await GetShapeAsync(pair, ShapeUri(pair, $"handle={handle}&offset={head + 1}"), pair.Token);
            await Assert.That(beyond.Status).IsEqualTo(HttpStatusCode.Conflict);
            // A snapshot page above F is unavailable too; above the committed head it names no snapshot.
            var pageAbove = await GetShapeAsync(pair, ShapeUri(pair, $"handle={handle}&offset={ShapeSnapshots.PageOffset(head, null)}"), pair.Token);
            await Assert.That(pageAbove.Status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(pageAbove.Json.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
            var pageBeyond = await GetShapeAsync(pair, ShapeUri(pair, $"handle={handle}&offset={ShapeSnapshots.PageOffset(head + 1, null)}"), pair.Token);
            await Assert.That(pageBeyond.Status).IsEqualTo(HttpStatusCode.Conflict);

            // Bootstrap keeps answering the published snapshot; a handle with none stays busy.
            var again = await GetShapeAsync(pair, ShapeUri(pair, "offset=-1"), pair.Token);
            await Assert.That(again.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
            await Assert.That(again.Location).IsEqualTo(bootstrap.Location);
            var filtered = await GetShapeAsync(pair, ShapeUri(pair, "offset=-1&where=" + Uri.EscapeDataString(Where)), pair.Token);
            await Assert.That(filtered.Status).IsEqualTo(HttpStatusCode.TooManyRequests);

            // A live read parks at F although the committed head is above it.
            var live = GetShapeAsync(pair, ShapeUri(pair, $"handle={handle}&offset={published}&live=true"), pair.Token);
            await TerraBaseTestServer.EventuallyAsync(async token =>
                ShapeWaiting(await pair.OriginServer.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative), token)) >= 1);
            if (live.IsCompleted)
            {
                // Another commit can wake it early: it still answers nothing of the held commit.
                await Assert.That(OpIds(await live)).DoesNotContain(id);
            }

            held.Release.TrySetResult();
            using (var response = await pending.WaitAsync(pair.Token))
            {
                await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString())
                    .IsEqualTo("remote_durable");
            }

            // The proof wakes the live read; a wake before the proof answers up to date at F and parks again, or 503 once its
            // seal's proof bound passes, which keeps the offset.
            var answer = await live;
            var offset = published;
            while (answer.Status == HttpStatusCode.ServiceUnavailable || (answer.Status == HttpStatusCode.OK && !OpIds(answer).Contains(id)))
            {
                if (answer.Status == HttpStatusCode.OK)
                {
                    offset = answer.Json.GetProperty("next_offset").GetString()!;
                }
                else
                {
                    await Assert.That(answer.Json.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
                }

                answer = await GetShapeAsync(pair, ShapeUri(pair, $"handle={handle}&offset={offset}&live=true"), pair.Token);
            }

            await Assert.That(answer.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(long.Parse(answer.Json.GetProperty("values_at").GetString()!, CultureInfo.InvariantCulture)).IsLessThanOrEqualTo(head);
            await TerraBaseTestServer.EventuallyAsync(async token =>
                (await GetShapeAsync(pair, ShapeUri(pair, "offset=-1&where=" + Uri.EscapeDataString(Where)), token)).Status == HttpStatusCode.TemporaryRedirect);
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task A_due_reseal_above_a_held_proof_answers_the_newest_published_snapshot()
    {
        await using var pair = await Pair.StartAsync();
        using (var created = await pair.Client.PutAsJsonAsync(pair.Document(EntityIds.New().ToString()), new { n = 1 }, pair.Token))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        var store = await pair.OriginStoreAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var snapshots = new ShapeSnapshots(pair.OriginServer.Services.GetRequiredService<IMemoryBudget>(), time,
            pair.OriginServer.Services.GetRequiredService<IShapeSnapshotTransactions>(), pair.Remote);
        var shape = SnapshotOf(await ShapeAsync(store));
        ShapeSnapshot first = null!;
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            try
            {
                first = await snapshots.SealIfDueAsync(store, shape, token);
                return true;
            }
            catch (StorageBusyException)
            {
                return false;
            }
        });

        pair.ExpectWait();
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 2 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            _ = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            time.Advance(ShapeSnapshots.ResealAge);
            await Assert.That(await snapshots.SealIfDueAsync(store, shape, pair.Token)).IsEqualTo(first)
                .Because("a due reseal whose head is not remote durable falls back to the published snapshot");
            await Assert.That(async () => await snapshots.SealIfDueAsync(store, SnapshotOf(await ShapeAsync(store)), pair.Token))
                .Throws<StorageBusyException>().Because("a handle without a published snapshot has nothing to fall back to");

            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                try
                {
                    return (await snapshots.SealIfDueAsync(store, shape, token)).Offset > first.Offset;
                }
                catch (StorageBusyException)
                {
                    return false;
                }
            });
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task A_captured_transaction_larger_than_one_frontier_pass_is_walked_past()
    {
        await using var pair = await Pair.StartAsync();
        var store = await pair.OriginStoreAsync();
        var sealer = pair.OriginSealer;
        var shape = await ShapeAsync(store);
        var probe = "~probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        // One autocommit statement with capture on: more images than one pass walks (64 pages of 512), one commit row, no
        // feed row. The rows sit under a handle no shape can name.
        var (images, commits, captured) = await (await pair.OriginTursoAsync()).WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT coalesce(max(change_id), 0) FROM turso_cdc;";
            var before = (long)command.ExecuteScalar()!;
            command.CommandText = """
                WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i + 1 FROM n WHERE i < 33000)
                INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) SELECT $handle, i, i, x'', 0 FROM n;
                """;
            command.Parameters.AddWithValue("$handle", probe);
            _ = command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "SELECT coalesce(sum(change_type <> 2), 0), coalesce(sum(change_type = 2), 0), max(change_id) FROM turso_cdc WHERE change_id > $before;";
            command.Parameters.AddWithValue("$before", before);
            using var reader = command.ExecuteReader();
            _ = reader.Read();
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }, "a test capturing one large transaction", pair.Token);
        await Assert.That(images).IsGreaterThan(64 * 512);
        await Assert.That(commits).IsEqualTo(1);
        // Retention passes walk past it before the write: an ack proves before it confirms, so a write's own proof walking all
        // these images could legitimately miss the 10 s bound under load (write_outcome_unknown).
        var retention = pair.OriginServer.Services.GetRequiredService<TursoChangeGroupSource>();
        try
        {
            await TerraBaseTestServer.EventuallyAsync(async token =>
            {
                await retention.TrimAsync(await pair.OriginTursoAsync(), token);
                return await pair.OriginScannedAsync(token) >= captured;
            }, TimeSpan.FromSeconds(20));
        }
        catch (OperationCanceledException) when (!pair.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"the origin's frontier stopped at the large transaction: {await pair.OriginFrontierAsync()}");
        }

        var id = EntityIds.New().ToString();
        using (var response = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token))
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString())
                .IsEqualTo("remote_durable");
        }

        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(pair.Token))
        {
            bound.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                _ = await sealer.SealUpToHeadAsync(store, shape, 0, bound.Token);
            }
            catch (OperationCanceledException) when (!pair.Token.IsCancellationRequested)
            {
                throw new TimeoutException($"the origin's frontier stopped at the large transaction: {await pair.OriginFrontierAsync()}");
            }
        }

        await Assert.That(Rows(await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token)).Any(row => row.Id == id)).IsTrue();
    }

    [Test]
    public async Task Retention_at_the_ack_never_erases_the_commit_it_proves()
    {
        await using var pair = await Pair.StartAsync();
        var store = await pair.OriginStoreAsync();
        var sealer = pair.OriginSealer;
        var shape = await ShapeAsync(store);
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            // Past the 60 s hold, recording the ACK may trim the very rows the ACK's proof walks next.
            await pair.AgeOriginCaptureAsync();
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString())
                .IsEqualTo("remote_durable");
            using (var bound = CancellationTokenSource.CreateLinkedTokenSource(pair.Token))
            {
                bound.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    _ = await sealer.SealUpToHeadAsync(store, shape, 0, bound.Token);
                }
                catch (OperationCanceledException) when (!pair.Token.IsCancellationRequested)
                {
                    throw new TimeoutException($"the origin's frontier never covered the acknowledged row: {await pair.OriginFrontierAsync()}");
                }
            }

            await Assert.That(Rows(await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token)).Any(row => row.Id == id)).IsTrue();
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task Received_rows_are_served_before_their_origin_ack()
    {
        await using var pair = await Pair.StartAsync();
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            // The peer has applied the row but has not acknowledged it, so the origin's write still waits.
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            var store = await pair.PeerStoreAsync();
            var sealer = pair.PeerSealer;
            var shape = await ShapeAsync(store);
            await SealCoveringAsync(pair, sealer, store, shape);

            var received = await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token);
            await Assert.That(Rows(received).Single(row => row.Id == id).N).IsEqualTo(1)
                .Because("received joins are proven at their local commit, not at their origin's ACK");
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task A_received_row_seals_only_under_its_exact_held_origin()
    {
        await using var pair = await Pair.StartAsync();
        // Before the row exists: the peer's own passes (retention, its stream's proofs) would rightly cover it with the real
        // held set at any moment. A pass already holding that set sees it blinded at lookup, and one in its writer ends first.
        pair.PeerProof.Blind = true;
        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            var store = await pair.PeerStoreAsync();
            var shape = await ShapeAsync(store);
            var origin = pair.OriginNode;
            var actual = await pair.PeerRemote.HeldOriginsAsync(pair.Database, pair.Token);
            await Assert.That(actual.Contains(origin)).IsTrue().Because("the peer holds the origin's applied row");
            var mismatched = ReplicaId.Of(origin.Region, origin.Member, unchecked((ushort)(origin.Epoch + 1)));
            var stale = new List<IReadOnlySet<ReplicaId>> { new HashSet<ReplicaId>(), new HashSet<ReplicaId> { mismatched } };
            var before = await pair.PeerSealer.FrontierAsync(store, pair.Token);
            foreach (var staleSet in stale)
            {
                _ = await SealBoundedAsync(pair, pair.PeerSealerWith(new StaleHeldDurability(pair.PeerRemote, staleSet)), store, shape, 0);
                await Assert.That(await pair.PeerSealer.FrontierAsync(store, pair.Token)).IsEqualTo(before)
                    .Because("a held set without the row's exact origin cannot advance or persist the frontier");
                await Assert.That(Rows(await ChunksAsync(pair.PeerSealer, store, shape.Handle, 0, pair.Token)).Any(row => row.Id == id)).IsFalse();
            }

            pair.PeerProof.Blind = false;
            await SealCoveringAsync(pair, pair.PeerSealer, store, shape);
            var received = await ChunksAsync(pair.PeerSealer, store, shape.Handle, 0, pair.Token);
            await Assert.That(Rows(received).Single(row => row.Id == id).N).IsEqualTo(1);
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task A_proof_stopped_at_a_joined_commit_still_covers_the_own_write_without_another_ack()
    {
        await using var pair = await Pair.StartAsync();
        // Also blind any delayed setup ACK, so it cannot certify the joined row before the held write's ACK.
        pair.Proof.Blind = true;
        var peerId = EntityIds.New().ToString();
        using (var joined = await pair.PeerClient.PutAsJsonAsync(pair.Document(peerId), new { n = 1 }, pair.Token))
        {
            await Assert.That(joined.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        // The peer's row is joined on the origin, and no sealer has run, so the frontier has not moved past it.
        using (var local = await pair.Client.GetAsync(pair.Document(peerId), pair.Token))
        {
            await Assert.That(local.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var id = EntityIds.New().ToString();
        using var held = pair.PeerAck.Hold(id);
        var pending = pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 2 }, pair.Token);
        try
        {
            await held.Applied.Task.WaitAsync(pair.Token);
            var waiting = await pair.WaitEntered.Task.WaitAsync(pair.Token);
            await Assert.That(waiting.IsCompleted).IsFalse();
            var actual = await ((ObservedRemote)pair.Remote).Inner.HeldOriginsAsync(pair.Database, pair.Token);
            await Assert.That(actual.Contains(pair.PeerNode)).IsTrue().Because("the origin holds the peer's joined row");
            // This ACK's proof sees the peer's joined origin as not held, so it stops at that row and must keep the own range.
            pair.Proof.Blind = true;
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString())
                .IsEqualTo("remote_durable");
            await pair.Proof.Committed.Task.WaitAsync(pair.Token);

            var store = await pair.OriginStoreAsync();
            // Force the background retention path while the proof walk must still be stopped.
            await pair.OriginServer.Services.GetRequiredService<TursoChangeGroupSource>().TrimAsync(await pair.OriginTursoAsync(), pair.Token);
            var sealer = SealerOf(pair.OriginServer, ((ObservedRemote)pair.Remote).Inner);
            var head = await (await pair.OriginTursoAsync()).WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT seq FROM _tb_feed_sequence WHERE id = 1;";
                return (long)command.ExecuteScalar()!;
            }, "a test reading the materialization head", pair.Token);
            await Assert.That(await sealer.FrontierAsync(store, pair.Token)).IsLessThan(head)
                .Because("the ACK persisted its proof while the earlier joined commit still held the walk");
            var shape = await ShapeAsync(store);
            using (var bound = CancellationTokenSource.CreateLinkedTokenSource(pair.Token))
            {
                bound.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    _ = await sealer.SealUpToHeadAsync(store, shape, 0, bound.Token);
                }
                catch (OperationCanceledException) when (!pair.Token.IsCancellationRequested)
                {
                    throw new TimeoutException($"the origin's frontier stopped before the held write: {await pair.OriginFrontierAsync()}");
                }
            }

            var rows = Rows(await ChunksAsync(sealer, store, shape.Handle, 0, pair.Token));
            await Assert.That(rows.Single(row => row.Id == peerId).N).IsEqualTo(1).Because("the joined row is held by the peer's live origin");
            await Assert.That(rows.Single(row => row.Id == id).N).IsEqualTo(2).Because("the own write is proven by the ACK range that stopped earlier");
        }
        finally
        {
            held.Release.TrySetResult();
            using var response = await pending.WaitAsync(pair.Token);
        }
    }

    [Test]
    public async Task Equal_digests_of_another_region_release_a_frontier_its_walk_stopped_at_for_good()
    {
        await using var pair = await Pair.StartAsync();
        // As after the origin restarts under a new epoch: no live link names the joined row's origin, so the peer's walk stops
        // there on every pass, and only another proof can serve the peer's shapes again.
        pair.PeerProof.Blind = true;
        var id = EntityIds.New().ToString();
        using (var put = await pair.Client.PutAsJsonAsync(pair.Document(id), new { n = 1 }, pair.Token))
        {
            await Assert.That(put.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        var store = await pair.PeerStoreAsync();
        var stopped = await pair.PeerSealer.FrontierAsync(store, pair.Token);
        var bootstrap = new Uri($"{pair.DatabasePath}/collections/{Collection}/shape?offset=-1", UriKind.Relative);
        using (var busy = await pair.PeerClient.GetAsync(bootstrap, pair.Token))
        {
            await Assert.That(busy.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests)
                .Because($"no snapshot can seal above the stopped frontier: {await pair.PeerFrontierAsync()}");
        }

        // An anti-entropy round finds the origin's digests equal: gru holds every value the peer has.
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            await pair.PeerAntiEntropy.RunRoundAsync(token);
            using var response = await pair.PeerClient.GetAsync(bootstrap, token);
            return response.StatusCode == HttpStatusCode.TemporaryRedirect;
        }, timeout: TimeSpan.FromSeconds(30));
        await Assert.That(await pair.PeerSealer.FrontierAsync(store, pair.Token)).IsGreaterThan(stopped);
    }

    [Test]
    public async Task A_frontier_hold_past_the_row_bound_ends_the_hold_while_changes_are_young()
    {
        await using var api = await Documents.DocumentApi.StartAsync();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        _ = await api.PutAsync(EntityIds.New().ToString(), new { n = 1 });
        var turso = await OpenTursoAsync(api.Server, await api.TenantOfAsync(), token);
        // One autocommit: the change log captures every row, so more than FarBehindRows (65536) unwalked changes are young.
        _ = await PadChangesAsync(turso, 65_537, token);
        var (count, oldest) = await CaptureAsync(turso, token);
        await Assert.That(count).IsGreaterThan(65_536L).Because("the standalone database captures the padded rows as real changes");
        await Assert.That(oldest).IsGreaterThanOrEqualTo(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60)
            .Because("the unwalked changes are younger than the 60 s hold, so only the row bound can end it");
        await Assert.That(await FrontierHoldAsync(turso, token)).IsNull()
            .Because("unwalked changes past the row bound end the hold, however young they are");
    }

    [Test]
    public async Task A_frontier_hold_within_the_row_bound_returns_the_scanned_frontier()
    {
        await using var api = await Documents.DocumentApi.StartAsync();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        _ = await api.PutAsync(EntityIds.New().ToString(), new { n = 1 });
        var turso = await OpenTursoAsync(api.Server, await api.TenantOfAsync(), token);
        var (count, _) = await CaptureAsync(turso, token);
        await Assert.That(count).IsGreaterThan(0L).Because("the standalone database captures its write");
        await Assert.That(await FrontierHoldAsync(turso, token)).IsEqualTo(0L)
            .Because("with no frontier row, a few young unwalked changes within the bound hold at scanned 0");
    }

    // Each seal attempt refreshes the frontier with the current held origins, so this retries until a received join is covered or the bound passes.
    private static async Task SealCoveringAsync(Pair pair, ShapeSealer sealer, IDatabaseStore store, ShapeSealDefinition shape)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(pair.Token);
        bound.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            _ = await sealer.SealUpToHeadAsync(store, shape, 0, bound.Token);
        }
        catch (OperationCanceledException) when (!pair.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"the peer's frontier never covered the received row: {await pair.PeerFrontierAsync()}");
        }
    }

    // The Turso store itself, as Pair opens it: the engine's own handle, not the IStorageEngine wrapper.
    private static async Task<TursoDatabaseStore> OpenTursoAsync(TerraBaseTestServer server, TenantContext database, CancellationToken cancellationToken) =>
        (TursoDatabaseStore)(await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, cancellationToken)
            ?? throw new InvalidOperationException("the database is missing"));

    // The change log's row count and oldest change time, read raw: proves capture happened before a hold is judged.
    private static Task<(long Count, long Oldest)> CaptureAsync(TursoDatabaseStore turso, CancellationToken cancellationToken) =>
        turso.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT count(*), coalesce(min(change_time), 0) FROM turso_cdc;";
            using var reader = command.ExecuteReader();
            _ = reader.Read();
            return (reader.GetInt64(0), reader.GetInt64(1));
        }, "a test reading the change log", cancellationToken);

    private static Task<long?> FrontierHoldAsync(TursoDatabaseStore turso, CancellationToken cancellationToken) =>
        turso.WithWriterAsync(session => TursoStorageTransaction.ShapeFrontierHold(session.Connection, null, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            "a test reading the frontier hold", cancellationToken);

    // Inserts harmless chunk rows in one statement; the change log captures each of them as a real commit.
    private static Task<int> PadChangesAsync(TursoDatabaseStore turso, long count, CancellationToken cancellationToken) =>
        turso.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE pad(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM pad WHERE n < $count)
                INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at)
                SELECT 'pad', n, n, zeroblob(0), 0 FROM pad;
                """;
            command.Parameters.AddWithValue("$count", count);
            return command.ExecuteNonQuery();
        }, "a test inserting harmless chunk rows", cancellationToken);

    private static ShapeSealer SealerOf(TerraBaseTestServer server, IRemoteDurability durability) => new(
        server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<ShapeSealer>.Instance,
        new TursoShapeStorage(server.Services.GetRequiredService<TursoStorageEngine>()), durability);

    private static async Task<IDatabaseStore> OpenStoreAsync(TerraBaseTestServer server, TenantContext database, CancellationToken cancellationToken) =>
        await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, cancellationToken)
        ?? throw new InvalidOperationException("the database is missing");

    private static async Task<ShapeSealDefinition> ShapeAsync(IDatabaseStore store)
    {
        await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var collection = await transaction.GetCollectionAsync(Collection, CancellationToken.None)
            ?? throw new InvalidOperationException("the collection is missing");
        return new ShapeSealDefinition(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            "h1-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Collection, collection.Id, RandomNumberGenerator.GetBytes(16),
            store.Incarnation);
    }

    // A seal waiting on an unacknowledged commit ends at the bound; null means it waited rather than returning a boundary.
    private static async Task<long?> SealBoundedAsync(Pair pair, ShapeSealer sealer, IDatabaseStore store, ShapeSealDefinition shape, long offset)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(pair.Token);
        bound.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            return await sealer.SealUpToHeadAsync(store, shape, offset, bound.Token);
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested && !pair.Token.IsCancellationRequested)
        {
            return null;
        }
    }

    // The chain of closed chunks from `from`, each starting where the previous one ended.
    private static async Task<SealedChunk[]> ChunksAsync(ShapeSealer sealer, IDatabaseStore store, string handle, long from, CancellationToken cancellationToken)
    {
        List<SealedChunk> chunks = [];
        while (await sealer.ReadChunkAsync(store, handle, from, cancellationToken) is { } chunk)
        {
            using (chunk)
            {
                chunks.Add(new SealedChunk(chunk.EndOffset, chunk.Body.ToArray()));
                from = chunk.EndOffset;
            }
        }

        return [.. chunks];
    }

    private static long SealedEnd(SealedChunk[] chunks) => chunks.Length == 0 ? 0 : chunks[^1].EndOffset;

    private static SealedRow[] Rows(SealedChunk[] chunks)
    {
        List<SealedRow> rows = [];
        foreach (var chunk in chunks)
        {
            using var json = JsonDocument.Parse(chunk.Body);
            foreach (var change in json.RootElement.GetProperty("changes").EnumerateArray())
            {
                foreach (var op in change.GetProperty("ops").EnumerateArray())
                {
                    if (op.GetProperty("op").GetString() == "upsert")
                    {
                        rows.Add(new SealedRow(op.GetProperty("id").GetString()!, op.GetProperty("document").GetProperty("n").GetInt32()));
                    }
                }
            }
        }

        return [.. rows];
    }

    private const string Where = """{"eq":["/n",2]}""";

    private static Uri ShapeUri(Pair pair, string query) => new($"{pair.DatabasePath}/collections/{Collection}/shape?{query}", UriKind.Relative);

    private static async Task<Reply> GetShapeAsync(Pair pair, Uri uri, CancellationToken token)
    {
        using var response = await pair.Client.GetAsync(uri, token);
        var text = await response.Content.ReadAsStringAsync(token);
        return new Reply(response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text), response.Headers.Location?.OriginalString);
    }

    private static string[] OpIds(Reply reply) =>
        [.. reply.Json.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray())
            .Select(op => op.GetProperty("id").GetString()!)];

    private static double ShapeWaiting(string scrape) => scrape.Split('\n')
        .Where(line => line.StartsWith("terrabase_shape_requests_waiting ", StringComparison.Ordinal)
            || line.StartsWith("terrabase_shape_requests_waiting{", StringComparison.Ordinal))
        .Sum(line => double.Parse(line.Split(' ')[^1], CultureInfo.InvariantCulture));

    private static Task<long> FeedHeadAsync(TursoDatabaseStore turso, CancellationToken cancellationToken) =>
        turso.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT seq FROM _tb_feed_sequence WHERE id = 1;";
            return (long)command.ExecuteScalar()!;
        }, "a test reading the feed head", cancellationToken);

    private static ShapeSnapshotDefinition SnapshotOf(ShapeSealDefinition shape) =>
        new(shape.Handle, shape.ShapeId, shape.Collection, shape.CollectionGeneration, shape.ResumeScope, shape.Incarnation);

    private sealed record Reply(HttpStatusCode Status, JsonElement Json, string? Location);

    private sealed record SealedChunk(long EndOffset, byte[] Body);

    private sealed record SealedRow(string Id, int N);

    private sealed class Pair : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        private readonly FakeMemberStore _members = new();
        private ManagementApi? _origin;
        private ManagementApi? _peer;
        private ReplicationTenant? _tenant;

        private Pair(TimeSpan? fixtureBudget)
        {
            _deadline.CancelAfter(fixtureBudget ?? TimeSpan.FromSeconds(60));
        }

        public AckGate PeerAck { get; } = new();

        public RefillGate Refill { get; } = new();

        public ProofGate Proof { get; } = new();

        // Blinds the peer's own frontier advances (retention, its stream's proofs, shape workers): none holds any origin.
        public ProofGate PeerProof { get; } = new();

        public AckRefusal Refusal { get; } = new();

        private TaskCompletionSource<Task<bool>> _waitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Task<bool>> WaitEntered => Volatile.Read(ref _waitEntered);

        // Call only once every earlier write has answered, so a later write's wait completes the new source.
        public void ExpectWait() => Volatile.Write(ref _waitEntered, new(TaskCreationOptions.RunContinuationsAsynchronously));

        public TaskCompletionSource<CommitMarker> ReachedWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadAfterAck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? HoldWait { get; set; }

        public PlanTier WaitPlan { get; set; }

        public HttpClient Client { get; private set; } = null!;

        public HttpClient PeerClient { get; private set; } = null!;

        // The origin without TB-Durability: writes answer after the local commit.
        public HttpClient LocalClient { get; private set; } = null!;

        public CancellationToken Token => _deadline.Token;

        public string DatabasePath => _tenant!.Path("db");

        public TenantContext Database => _tenant!.Database("db");

        public IRemoteDurability Remote => _origin!.Server.Services.GetRequiredService<IRemoteDurability>();

        public IChangeGroupSource Source => _origin!.Server.Services.GetRequiredService<IChangeGroupSource>();

        public ReplicaId PeerNode => _peer!.Server.Services.GetRequiredService<LocalReplica>().Id;

        public Uri Document(string id, string database = "db") => new($"{_tenant!.Path(database)}/collections/items/documents/by-key?id={id}", UriKind.Relative);

        // Holds the sender's head capture of every database of the tenant but db (a copy) until it completes.
        public TaskCompletionSource? HoldHead { get; set; }

        // Sets db's replica regions on the origin through the management service; whether it was updated.
        public async Task<bool> SetReplicaRegionsAsync(IReadOnlyList<string> regions) =>
            await _origin!.Server.Services.GetRequiredService<IManagementService>().SetDatabaseRegionsAsync(
                _tenant!.UserId, _tenant.OrganizationId, _tenant.ProjectId, Database.DatabaseId!, regions, Token) is ManagedDatabase;

        public TaskCompletionSource? HoldDatabaseHead { get; set; }

        public TaskCompletionSource DatabaseHeadReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<long>? ReplayStart { get; set; }

        public Action<string>? ReplayTrace { get; set; }

        public TaskCompletionSource ReplayLive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? HoldReplay { get; set; }

        public TaskCompletionSource<long> ReplayReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Copies db on the origin through the management service (as a restore copies its snapshot) and binds it on the peer.
        public async Task<TenantContext> CopyAsync(string name)
        {
            var management = _origin!.Server.Services.GetRequiredService<IManagementService>();
            if (await management.CopyDatabaseAsync(_tenant!.UserId, _tenant.OrganizationId, _tenant.ProjectId, Database.DatabaseId!, name, null, Token) is not ManagedDatabase)
            {
                throw new InvalidOperationException("could not copy the database");
            }

            await _tenant.CaptureDatabaseAsync(_origin.Server, name);
            await _tenant.BindCatalogAsync(_peer!.Server);
            return _tenant.Database(name);
        }

        public Task<IDatabaseStore> OriginStoreAsync() => OpenStoreAsync(_origin!.Server, Database, Token);

        public async Task<TursoDatabaseStore> OriginTursoAsync() =>
            (TursoDatabaseStore)(await _origin!.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(Database, Token)
                ?? throw new InvalidOperationException("the database is missing"));

        public TerraBaseTestServer OriginServer => _origin!.Server;

        public TerraBaseTestServer PeerServer => _peer!.Server;

        public ShapeSealer OriginSealer => SealerOf(_origin!.Server, _origin!.Server.Services.GetRequiredService<IRemoteDurability>());

        public Task<IDatabaseStore> PeerStoreAsync() => OpenStoreAsync(_peer!.Server, Database, Token);

        // The peer's persisted frontier row and the captured rows past it, for a stuck seal's failure message.
        // The origin's persisted chunks of a shape, whatever the read gate serves.
        public async Task<(int Count, long End)> PersistedChunksAsync(string handle) =>
            await (await OriginTursoAsync()).WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT count(*), coalesce(max(end_offset), 0) FROM _tb_shape_chunk WHERE handle = $handle;";
                command.Parameters.AddWithValue("$handle", handle);
                using var reader = command.ExecuteReader();
                _ = reader.Read();
                return (reader.GetInt32(0), reader.GetInt64(1));
            }, "a test reading persisted chunks", Token);

        public Task<string> PeerFrontierAsync() => FrontierStateAsync(_peer!.Server);

        public AntiEntropy PeerAntiEntropy => _peer!.Server.Services.GetRequiredService<AntiEntropy>();

        public Task<string> OriginFrontierAsync() => FrontierStateAsync(_origin!.Server);

        // The last commit change id the origin's frontier walked past (the body is "2 incarnation scanned ranges...").
        public async Task<long> OriginScannedAsync(CancellationToken cancellationToken) =>
            await (await OriginTursoAsync()).WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT CAST(body AS TEXT) FROM _tb_shape_chunk WHERE handle = '~frontier';";
                return command.ExecuteScalar() is string body ? long.Parse(body.Split(' ')[2], CultureInfo.InvariantCulture) : 0;
            }, "a test reading the scanned frontier", cancellationToken);

        // Ages every captured row of the origin past retention's 60 s hold, as if the commits were older.
        public async Task AgeOriginCaptureAsync(TimeSpan? age = null) =>
            await (await OriginTursoAsync()).WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "UPDATE turso_cdc SET change_time = $aged;";
                command.Parameters.AddWithValue("$aged", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)(age ?? TimeSpan.FromMinutes(2)).TotalSeconds);
                return command.ExecuteNonQuery();
            }, "a test ageing the change log", Token);

        // A machine's frontier state and the oldest captured rows, for a stuck seal's failure message.
        private async Task<string> FrontierStateAsync(TerraBaseTestServer server)
        {
            var turso = (TursoDatabaseStore)(await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(Database, Token)
                ?? throw new InvalidOperationException("the database is missing"));
            var self = server.Services.GetRequiredService<LocalReplica>().Id;
            return await turso.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                string Scalar(string sql)
                {
                    command.CommandText = sql;
                    return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "null";
                }

                var text = new StringBuilder();
                text.Append(CultureInfo.InvariantCulture, $"self {self.Value:x} region {self.Region}; ");
                text.Append("frontier ").Append(Scalar("SELECT end_offset || ' (' || CAST(body AS TEXT) || ')' FROM _tb_shape_chunk WHERE handle = '~frontier';"));
                text.Append("; incarnation ").Append(Scalar(TursoStatements.Incarnation));
                text.Append("; feed seq ").Append(Scalar("SELECT seq FROM _tb_feed_sequence WHERE id = 1;"));
                text.Append("; cdc min/max/count ").Append(Scalar("SELECT min(change_id) || '/' || max(change_id) || '/' || count(*) FROM turso_cdc;"));
                text.Append("; peers ").Append(Scalar("SELECT group_concat(printf('%x', node) || ' acked ' || acked || ' seen ' || seen, ', ') FROM _tb_peers;"));
                text.Append("; rows");
                command.CommandText = "SELECT change_id, change_type, table_name, after FROM turso_cdc ORDER BY change_id LIMIT 64;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var table = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    text.Append(CultureInfo.InvariantCulture, $" {reader.GetInt64(0)}:{reader.GetInt64(1)}:{table}");
                    if (reader.IsDBNull(3) || reader.GetInt64(1) == 2)
                    {
                        continue;
                    }

                    var record = SqliteRecord.Decode(TursoSession.Bytes(reader, 3));
                    if (table == "_tb_state" && record is [_, _, _, _, _, long origin, ..])
                    {
                        text.Append(CultureInfo.InvariantCulture, $":origin={unchecked((ulong)origin):x}");
                    }
                    else if (table == "_tb_feed" && record is [long seq, ..])
                    {
                        text.Append(CultureInfo.InvariantCulture, $":seq={seq}");
                    }
                }

                return text.ToString();
            }, "a test reading the shape frontier", Token);
        }

        public ShapeSealer PeerSealer => PeerSealerWith(PeerRemote);

        public ShapeSealer PeerSealerWith(IRemoteDurability durability) => SealerOf(_peer!.Server, durability);

        // The peer's real durability, which PeerProof does not blind.
        public IRemoteDurability PeerRemote => ((GatedRemote)_peer!.Server.Services.GetRequiredService<IRemoteDurability>()).Inner;

        public ReplicaId OriginNode => _origin!.Server.Services.GetRequiredService<LocalReplica>().Id;

        public static async Task<Pair> StartAsync(string peerRegion = "sjc", PlanTier plan = PlanTier.Hobby, TimeProvider? ackTime = null, TimeSpan? fixtureBudget = null)
        {
            var pair = new Pair(fixtureBudget);
            try
            {
                pair._origin = await pair.StartMachineAsync("gru", ackTime: ackTime);
                pair._peer = await pair.StartMachineAsync(peerRegion, pair.PeerAck);
                pair._tenant = await ReplicationTenant.CreateAsync(pair._origin.Server, ["db"], pair.Token, ["sjc"]);
                await pair._tenant.BindAsync(pair._peer.Server);
                var key = await pair._tenant.KeyAsync(pair._origin.Server, ["documents:read", "documents:write"], pair.Token);
                pair.LocalClient = pair._origin.DataClient(key);
                pair.Client = pair._origin.DataClient(key);
                // These tests cover the opt-in remote-durable answer (decision 0013a); the default is local_durable (0013e).
                pair.Client.DefaultRequestHeaders.Add("TB-Durability", "remote_durable");
                pair.PeerClient = pair._peer.DataClient(await pair._tenant.KeyAsync(pair._peer.Server, ["documents:read", "documents:write"], pair.Token));
                pair.PeerClient.DefaultRequestHeaders.Add("TB-Durability", "remote_durable");
                using var collection = await pair.Client.PostAsJsonAsync(new Uri($"{pair.DatabasePath}/collections", UriKind.Relative), new { name = "items" }, pair.Token);
                await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(pair._origin.Server.Services.GetRequiredService<IReplicationStatus>()
                    .Snapshot().Peers.Any(peer => peer.Region == peerRegion)), timeout: TimeSpan.FromSeconds(30));
                if (peerRegion != "fra")
                {
                    await TerraBaseTestServer.EventuallyAsync(async token =>
                    {
                        using var response = await pair.PeerClient.GetAsync(new Uri($"{pair.DatabasePath}/collections/items", UriKind.Relative), token);
                        return response.StatusCode == HttpStatusCode.OK;
                    });
                }
                if (plan != PlanTier.Hobby)
                {
                    // Bind installs the captured Hobby catalog on the peer; change the plan only after provisioning both.
                    await pair._origin.SetPlanAsync(pair._tenant.UserId, pair._tenant.OrganizationId, plan.ToString().ToLowerInvariant(), pair.Token);
                }

                return pair;
            }
            catch
            {
                await pair.DisposeAsync();
                throw;
            }
        }

        private Task<ManagementApi> StartMachineAsync(string region, AckGate? ack = null, TimeProvider? ackTime = null) => ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        }, services =>
        {
            services.AddSingleton<IMemberDirectory>(_members);
            if (ackTime is not null)
            {
                services.AddSingleton(new RemoteAckWaiter(ackTime));
            }

            if (ack is not null)
            {
                services.AddSingleton<IChangeGroupSink>(provider => new HeldSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), ack));
                var remote = services.Last(service => service.ServiceType == typeof(IRemoteDurability));
                services.AddSingleton<IRemoteDurability>(provider => new GatedRemote((IRemoteDurability)remote.ImplementationFactory!(provider), PeerProof));
                var source = services.Last(service => service.ServiceType == typeof(IChangeGroupSource));
                services.AddSingleton<IChangeGroupSource>(provider => new ControlledSource((IChangeGroupSource)source.ImplementationFactory!(provider), new AckRefusal(), new RefillGate(), PeerProof, this));
            }
            else
            {
                var remote = services.Last(service => service.ServiceType == typeof(IRemoteDurability));
                services.AddSingleton<IRemoteDurability>(provider => new ObservedRemote((IRemoteDurability)remote.ImplementationFactory!(provider), this));
                var source = services.Last(service => service.ServiceType == typeof(IChangeGroupSource));
                services.AddSingleton<IChangeGroupSource>(provider => new ControlledSource((IChangeGroupSource)source.ImplementationFactory!(provider), Refusal, Refill, Proof, this));
            }
        }, cancellationToken: Token);

        public async Task StopPeerAsync()
        {
            await _peer!.DisposeAsync();
            _peer = null;
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(_origin!.Server.Services.GetRequiredService<IReplicationStatus>()
                .Snapshot().Peers.All(peer => peer.Region != "sjc")));
        }

        public async ValueTask DisposeAsync()
        {
            PeerAck.Release();
            Client?.Dispose();
            PeerClient?.Dispose();
            LocalClient?.Dispose();
            if (_peer is not null)
            {
                await _peer.DisposeAsync();
            }

            if (_origin is not null)
            {
                await _origin.DisposeAsync();
            }

            _deadline.Dispose();
        }
    }

    // Hold timeout callbacks while monotonic time advances, as when the runtime delays timer dispatch.
    private sealed class DelayedAckClock : TimeProvider
    {
        private readonly FakeTimeProvider _time = new();

        public override long TimestampFrequency => _time.TimestampFrequency;

        public override long GetTimestamp() => _time.GetTimestamp();

        public void Advance(TimeSpan elapsed) => _time.Advance(elapsed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new HeldTimer();

        private sealed class HeldTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class AckGate
    {
        private HeldAck? _held;
        private int _refuse;

        public TaskCompletionSource Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Refuse
        {
            get => Volatile.Read(ref _refuse) != 0;
            set => Volatile.Write(ref _refuse, value ? 1 : 0);
        }

        public HeldAck Hold(string id)
        {
            var held = new HeldAck(id);
            Volatile.Write(ref _held, held);
            return held;
        }

        public void Release() => Volatile.Read(ref _held)?.Release.TrySetResult();

        public async Task BeforeAckAsync(IReadOnlyList<OriginGroup> groups, CancellationToken token)
        {
            if (Volatile.Read(ref _held) is { } held && groups.Any(group => group.Deltas.Any(delta => delta.Id == held.Id)))
            {
                held.Applied.TrySetResult();
                await held.Release.Task.WaitAsync(token);
            }
        }
    }

    private sealed class HeldAck(string id) : IDisposable
    {
        public string Id { get; } = id;

        public TaskCompletionSource Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => Release.TrySetResult();
    }

    private sealed class HeldSink(IChangeGroupSink inner, AckGate gate) : IChangeGroupSink
    {
        public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, Hlc expected, CancellationToken cancellationToken, ApplyProgress? progress = null)
        {
            if (database != ChangeGroups.System && gate.Refuse)
            {
                gate.Refused.TrySetResult();
                return null;
            }

            var applied = await inner.ApplyAsync(database, origin, groups, expected, cancellationToken, progress);
            if (applied is not null)
            {
                await gate.BeforeAckAsync(groups, cancellationToken);
            }

            return applied;
        }

        public ValueTask<int?> RepairAsync(TenantContext database, Ulid incarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            inner.RepairAsync(database, incarnation, states, cancellationToken);

        public ValueTask<Ulid?> RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, Hlc expected, Ulid? incarnation, CancellationToken cancellationToken) =>
            inner.RecordFrontierAsync(database, origin, frontier, expected, incarnation, cancellationToken);
    }

    private sealed class RefillGate
    {
        private TenantContext? _database;
        private long _below = long.MaxValue;

        // Completes at the first armed read from at or above Below, which is retained.
        public TaskCompletionSource Retained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(TenantContext database) => Volatile.Write(ref _database, database);

        public void Below(long changeId) => Volatile.Write(ref _below, changeId);

        public long Boundary => Volatile.Read(ref _below);

        // Replication tenants carry no plan: only the ids identify the database.
        public bool Refills(TenantContext database, long after)
        {
            if (Volatile.Read(ref _database) is not { } target || (target with { Plan = PlanTier.Hobby }) != (database with { Plan = PlanTier.Hobby }))
            {
                return false;
            }

            if (after < Volatile.Read(ref _below))
            {
                return true;
            }

            Retained.TrySetResult();
            return false;
        }
    }

    // Answers as if the copy in place were another: the ack's incarnation check and its persistence are refused.
    private sealed class AckRefusal
    {
        private TenantContext? _database;
        private int _head = 1;

        public TaskCompletionSource Rejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(TenantContext database) => Volatile.Write(ref _database, database);

        public void ArmPersistenceOnly(TenantContext database)
        {
            Volatile.Write(ref _head, 0);
            Arm(database);
        }

        public bool RefusesHead(TenantContext database) => Volatile.Read(ref _head) != 0 && Refuses(database);

        public bool Refuses(TenantContext database) => Volatile.Read(ref _database) is { } target && (target with { Plan = PlanTier.Hobby }) == (database with { Plan = PlanTier.Hobby });
    }

    private sealed class ProofGate
    {
        private int _blind;

        // Completes once the blinded proof of the held write's ACK has committed.
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Blind
        {
            get => Volatile.Read(ref _blind) != 0;
            set => Volatile.Write(ref _blind, value ? 1 : 0);
        }
    }

    private sealed class ControlledSource(IChangeGroupSource inner, AckRefusal refusal, RefillGate refill, ProofGate proof, Pair pair) : IChangeGroupSource
    {
        public IAsyncEnumerable<TenantContext> ListDatabasesAsync(CancellationToken cancellationToken) => inner.ListDatabasesAsync(cancellationToken);

        public async ValueTask<SourceGroups?> ReadAsync(TenantContext database, Ulid incarnation, long after, int maxGroups, int maxBytes, CancellationToken cancellationToken)
        {
            if (pair.HoldWait is not null && pair.ReachedWait.Task.IsCompletedSuccessfully)
            {
                var marker = await pair.ReachedWait.Task;
                if ((database with { Plan = PlanTier.Hobby }) == (pair.Database with { Plan = PlanTier.Hobby })
                    && incarnation == marker.Incarnation && after >= marker.ChangeId)
                {
                    pair.ReadAfterAck.TrySetResult();
                }
            }

            if (ReferenceEquals(refill, pair.Refill) && (database with { Plan = PlanTier.Hobby }) == pair.Database
                && pair.ReplayStart?.Task.IsCompletedSuccessfully == true && pair.ReplayLive.TrySetResult())
            {
                pair.ReplayTrace?.Invoke("first live read after refill ACK");
            }

            // A forced refill: the read reports the log cut past its start.
            var page = await inner.ReadAsync(database, incarnation, after, maxGroups, maxBytes, cancellationToken);
            return page is not null && refill.Refills(database, after) ? page with { Retained = false } : page;
        }

        public ValueTask<SourceCursor?> AckedAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken) =>
            inner.AckedAsync(database, peer, cancellationToken);

        public async ValueTask<bool> AckAsync(TenantContext database, Ulid incarnation, ReplicaId peer, long changeId, Hlc stable, CancellationToken cancellationToken)
        {
            if (refusal.Refuses(database))
            {
                refusal.Rejected.TrySetResult();
                return false;
            }

            return await inner.AckAsync(database, incarnation, peer, changeId, stable, cancellationToken);
        }

        public async ValueTask<bool> ProveAsync(TenantContext database, Ulid incarnation, long from, long through, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken)
        {
            if (proof.Blind && (database with { Plan = PlanTier.Hobby }) == (pair.Database with { Plan = PlanTier.Hobby }))
            {
                var blinded = await inner.ProveAsync(database, incarnation, from, through, new HashSet<ReplicaId>(), cancellationToken);
                if (pair.ReachedWait.Task.IsCompletedSuccessfully)
                {
                    var marker = await pair.ReachedWait.Task;
                    if (incarnation == marker.Incarnation && through >= marker.ChangeId)
                    {
                        proof.Committed.TrySetResult();
                    }
                }

                return blinded;
            }

            return await inner.ProveAsync(database, incarnation, from, through, new GatedHeld(held, proof), cancellationToken);
        }

        public Task ChangedAsync(CancellationToken cancellationToken) => inner.ChangedAsync(cancellationToken);
        public IDisposable Subscribe(Action<TenantContext> changed) => inner.Subscribe(changed);

        public ValueTask<bool> RecordStableAsync(TenantContext database, Ulid incarnation, ReplicaId peer, Hlc stable, CancellationToken cancellationToken) =>
            inner.RecordStableAsync(database, incarnation, peer, stable, cancellationToken);

        public ValueTask<bool?> RetainsAsync(TenantContext database, Ulid incarnation, long after, CancellationToken cancellationToken) =>
            inner.RetainsAsync(database, incarnation, after, cancellationToken);

        public async ValueTask<long?> RetainedAfterAsync(TenantContext database, Ulid incarnation, long? through, long head, CancellationToken cancellationToken)
        {
            // This gate models a cut through the marker, so the retained boundary overrides the requested replay start.
            if (refill.Refills(database, 0))
            {
                return Math.Min(head, refill.Boundary);
            }

            if (ReferenceEquals(refill, pair.Refill) && pair.HoldReplay is { } held && (database with { Plan = PlanTier.Hobby }) == pair.Database)
            {
                pair.ReplayReached.TrySetResult(head);
                await held.Task.WaitAsync(cancellationToken);
            }

            var started = Stopwatch.GetTimestamp();
            var start = await inner.RetainedAfterAsync(database, incarnation, through, head, cancellationToken);
            if (ReferenceEquals(refill, pair.Refill) && (database with { Plan = PlanTier.Hobby }) == pair.Database && start is { } found)
            {
                pair.ReplayTrace?.Invoke(string.Create(CultureInfo.InvariantCulture, $"boundary scan {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms unknown={through is null}"));
                pair.ReplayStart?.TrySetResult(found);
            }

            return start;
        }

        public async ValueTask<long?> HeadAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken)
        {
            if (refusal.RefusesHead(database))
            {
                refusal.Rejected.TrySetResult();
                return null;
            }

            if (pair.HoldHead is { } held && database.ProjectId == pair.Database.ProjectId && database.DatabaseId != pair.Database.DatabaseId)
            {
                await held.Task.WaitAsync(cancellationToken);
            }

            if (ReferenceEquals(refill, pair.Refill) && pair.HoldDatabaseHead is { } head && (database with { Plan = PlanTier.Hobby }) == pair.Database)
            {
                pair.DatabaseHeadReached.TrySetResult();
                await head.Task.WaitAsync(cancellationToken);
            }

            return await inner.HeadAsync(database, incarnation, cancellationToken);
        }
    }

    // Passes everything through except the held origins, which it answers with a fixed set: a stale or unknown view.
    private sealed class StaleHeldDurability(IRemoteDurability inner, IReadOnlySet<ReplicaId> held) : IRemoteDurability
    {
        public bool Confirms => inner.Confirms;

        public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => inner.OtherRegionsAsync(cancellationToken);

        public ValueTask<DatabasePlacement?> PlacementAsync(TenantContext database, CancellationToken cancellationToken) =>
            inner.PlacementAsync(database, cancellationToken);

        public ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken) => inner.AdmitAsync(database, cancellationToken);

        public ValueTask<IReadOnlySet<ReplicaId>> HeldOriginsAsync(TenantContext database, CancellationToken cancellationToken) =>
            ValueTask.FromResult(held);

        public ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken) =>
            inner.WaitAsync(database, marker, cancellationToken);
    }

    // Passes everything through; the held origins answer through the gate (GatedHeld).
    private sealed class GatedRemote(IRemoteDurability inner, ProofGate gate) : IRemoteDurability
    {
        public IRemoteDurability Inner => inner;

        public bool Confirms => inner.Confirms;

        public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => inner.OtherRegionsAsync(cancellationToken);

        public ValueTask<DatabasePlacement?> PlacementAsync(TenantContext database, CancellationToken cancellationToken) =>
            inner.PlacementAsync(database, cancellationToken);

        public ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken) => inner.AdmitAsync(database, cancellationToken);

        public async ValueTask<IReadOnlySet<ReplicaId>> HeldOriginsAsync(TenantContext database, CancellationToken cancellationToken) =>
            new GatedHeld(await inner.HeldOriginsAsync(database, cancellationToken), gate);

        public ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken) =>
            inner.WaitAsync(database, marker, cancellationToken);
    }

    // Held origins as the gate sees them at each lookup: once blinded none is held, even by a pass that read the set earlier.
    private sealed class GatedHeld(IReadOnlySet<ReplicaId> inner, ProofGate gate) : IReadOnlySet<ReplicaId>
    {
        private IReadOnlySet<ReplicaId> Current => gate.Blind ? FrozenSet<ReplicaId>.Empty : inner;

        public int Count => Current.Count;

        public bool Contains(ReplicaId item) => Current.Contains(item);

        public IEnumerator<ReplicaId> GetEnumerator() => Current.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool IsProperSubsetOf(IEnumerable<ReplicaId> other) => Current.IsProperSubsetOf(other);

        public bool IsProperSupersetOf(IEnumerable<ReplicaId> other) => Current.IsProperSupersetOf(other);

        public bool IsSubsetOf(IEnumerable<ReplicaId> other) => Current.IsSubsetOf(other);

        public bool IsSupersetOf(IEnumerable<ReplicaId> other) => Current.IsSupersetOf(other);

        public bool Overlaps(IEnumerable<ReplicaId> other) => Current.Overlaps(other);

        public bool SetEquals(IEnumerable<ReplicaId> other) => Current.SetEquals(other);
    }

    private sealed class ObservedRemote(IRemoteDurability inner, Pair pair) : IRemoteDurability
    {
        public IRemoteDurability Inner => inner;

        public bool Confirms => inner.Confirms;

        public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => inner.OtherRegionsAsync(cancellationToken);

        public ValueTask<DatabasePlacement?> PlacementAsync(TenantContext database, CancellationToken cancellationToken) =>
            inner.PlacementAsync(database, cancellationToken);

        public ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken) => inner.AdmitAsync(database, cancellationToken);

        public async ValueTask<IReadOnlySet<ReplicaId>> HeldOriginsAsync(TenantContext database, CancellationToken cancellationToken) =>
            new GatedHeld(await inner.HeldOriginsAsync(database, cancellationToken), pair.Proof);

        public async ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken)
        {
            pair.WaitPlan = database.Plan;
            pair.ReachedWait.TrySetResult(marker);
            if (pair.HoldWait is { } held)
            {
                await held.Task.WaitAsync(cancellationToken);
            }

            var started = Stopwatch.GetTimestamp();
            var waiting = inner.WaitAsync(database, marker, cancellationToken).AsTask();
            pair.WaitEntered.TrySetResult(waiting);
            var confirmed = await waiting;
            pair.ReplayTrace?.Invoke(string.Create(CultureInfo.InvariantCulture, $"marker {marker.ChangeId} ACK wait {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms confirmed={confirmed}"));
            return confirmed;
        }
    }
}
