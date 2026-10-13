using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Client responses through real listeners, CDC and peer streams (decision 0013a, B4).</summary>
public sealed class RemoteAckTests
{
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
        Volatile.Write(ref armed, 1);
        var id = EntityIds.New().ToString();
        var response = await api.PutAsync(id, new { n = 1 });
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Code).IsEqualTo("write_outcome_unknown");
        await Assert.That(response.Json.GetProperty("error").GetProperty("region").GetString()).IsEqualTo("test-region");
        await Assert.That(response.Json.GetProperty("error").GetProperty("machine").GetString()).IsNotNullOrEmpty();
        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.OK);
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
            var persisted = await pair.Source.AckedAsync(pair.Database, pair.PeerNode, pair.Token);
            await Assert.That(persisted is { } acked && acked.Incarnation == marker.Incarnation && acked.Acked >= marker.ChangeId).IsTrue();
            await Assert.That(waiting.IsCompleted).IsFalse().Because("a refill covered the write, and the next live ACK must arrive while it waits");
            using (var later = await pair.Client.PutAsJsonAsync(pair.Document(EntityIds.New().ToString()), new { n = 2 }, pair.Token))
            {
                await Assert.That(later.StatusCode).IsEqualTo(HttpStatusCode.Created);
                await Assert.That((await later.Content.ReadFromJsonAsync<JsonElement>(pair.Token)).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
            }

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
    [Arguments(PlanTier.Hobby)]
    [Arguments(PlanTier.Pro)]
    public async Task A_refused_ack_persistence_after_the_receiver_commits_answers_unknown(PlanTier plan)
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

    private sealed class Pair : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        private readonly FakeMemberStore _members = new();
        private ManagementApi? _origin;
        private ManagementApi? _peer;
        private ReplicationTenant? _tenant;

        private Pair()
        {
            _deadline.CancelAfter(TimeSpan.FromSeconds(60));
        }

        public AckGate PeerAck { get; } = new();

        public RefillGate Refill { get; } = new();

        public AckRefusal Refusal { get; } = new();

        public TaskCompletionSource<Task<bool>> WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CommitMarker> ReachedWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadAfterAck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? HoldWait { get; set; }

        public PlanTier WaitPlan { get; set; }

        public HttpClient Client { get; private set; } = null!;

        public HttpClient PeerClient { get; private set; } = null!;

        public CancellationToken Token => _deadline.Token;

        public string DatabasePath => _tenant!.Path("db");

        public TenantContext Database => _tenant!.Database("db");

        public IRemoteDurability Remote => _origin!.Server.Services.GetRequiredService<IRemoteDurability>();

        public IChangeGroupSource Source => _origin!.Server.Services.GetRequiredService<IChangeGroupSource>();

        public ReplicaId PeerNode => _peer!.Server.Services.GetRequiredService<LocalReplica>().Id;

        public Uri Document(string id) => new($"{DatabasePath}/collections/items/documents/by-key?id={id}", UriKind.Relative);

        public static async Task<Pair> StartAsync(string peerRegion = "sjc", PlanTier plan = PlanTier.Hobby)
        {
            var pair = new Pair();
            try
            {
                pair._origin = await pair.StartMachineAsync("gru");
                pair._peer = await pair.StartMachineAsync(peerRegion, pair.PeerAck);
                pair._tenant = await ReplicationTenant.CreateAsync(pair._origin.Server, ["db"], pair.Token, ["sjc"]);
                await pair._tenant.BindAsync(pair._peer.Server);
                pair.Client = pair._origin.DataClient(await pair._tenant.KeyAsync(pair._origin.Server, ["documents:read", "documents:write"], pair.Token));
                pair.PeerClient = pair._peer.DataClient(await pair._tenant.KeyAsync(pair._peer.Server, ["documents:read", "documents:write"], pair.Token));
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

        private Task<ManagementApi> StartMachineAsync(string region, AckGate? ack = null) => ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
        }, services =>
        {
            services.AddSingleton<IMemberDirectory>(_members);
            if (ack is not null)
            {
                services.AddSingleton<IChangeGroupSink>(provider => new HeldSink(ActivatorUtilities.CreateInstance<StorageChangeGroupSink>(provider), ack));
            }
            else
            {
                var remote = services.Last(service => service.ServiceType == typeof(IRemoteDurability));
                services.AddSingleton<IRemoteDurability>(provider => new ObservedRemote((IRemoteDurability)remote.ImplementationFactory!(provider), this));
                var source = services.Last(service => service.ServiceType == typeof(IChangeGroupSource));
                services.AddSingleton<IChangeGroupSource>(provider => new ControlledSource((IChangeGroupSource)source.ImplementationFactory!(provider), Refusal, Refill, this));
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
        public async ValueTask<Hlc?> ApplyAsync(TenantContext database, ReplicaId origin, IReadOnlyList<OriginGroup> groups, CancellationToken cancellationToken)
        {
            if (database != ChangeGroups.System && gate.Refuse)
            {
                gate.Refused.TrySetResult();
                return null;
            }

            var applied = await inner.ApplyAsync(database, origin, groups, cancellationToken);
            if (applied is not null)
            {
                await gate.BeforeAckAsync(groups, cancellationToken);
            }

            return applied;
        }

        public ValueTask<int?> RepairAsync(TenantContext database, IReadOnlyList<RepairState> states, CancellationToken cancellationToken) =>
            inner.RepairAsync(database, states, cancellationToken);

        public ValueTask RecordFrontierAsync(TenantContext database, ReplicaId origin, Hlc frontier, CancellationToken cancellationToken) =>
            inner.RecordFrontierAsync(database, origin, frontier, cancellationToken);
    }

    private sealed class RefillGate
    {
        private TenantContext? _database;
        private long _below = long.MaxValue;

        // Completes at the first armed read from at or above Below, which is retained.
        public TaskCompletionSource Retained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(TenantContext database) => Volatile.Write(ref _database, database);

        public void Below(long changeId) => Volatile.Write(ref _below, changeId);

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

    private sealed class AckRefusal
    {
        private TenantContext? _database;

        public TaskCompletionSource Rejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(TenantContext database) => Volatile.Write(ref _database, database);

        public bool Refuses(TenantContext database) => Volatile.Read(ref _database) is { } target && (target with { Plan = PlanTier.Hobby }) == (database with { Plan = PlanTier.Hobby });
    }

    private sealed class ControlledSource(IChangeGroupSource inner, AckRefusal refusal, RefillGate refill, Pair pair) : IChangeGroupSource
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

            return await inner.ReadAsync(database, incarnation, after, maxGroups, maxBytes, cancellationToken);
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

        public Task ChangedAsync(CancellationToken cancellationToken) => inner.ChangedAsync(cancellationToken);

        public ValueTask<bool> RecordStableAsync(TenantContext database, Ulid incarnation, ReplicaId peer, Hlc stable, CancellationToken cancellationToken) =>
            inner.RecordStableAsync(database, incarnation, peer, stable, cancellationToken);

        public async ValueTask<bool?> RetainsAsync(TenantContext database, Ulid incarnation, long after, CancellationToken cancellationToken) =>
            refill.Refills(database, after) ? false : await inner.RetainsAsync(database, incarnation, after, cancellationToken);

        public ValueTask<long?> HeadAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken) =>
            inner.HeadAsync(database, incarnation, cancellationToken);
    }

    private sealed class ObservedRemote(IRemoteDurability inner, Pair pair) : IRemoteDurability
    {
        public bool Confirms => inner.Confirms;

        public ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) => inner.OtherRegionsAsync(cancellationToken);

        public ValueTask<RemoteAdmission> AdmitAsync(TenantContext database, CancellationToken cancellationToken) => inner.AdmitAsync(database, cancellationToken);

        public async ValueTask<bool> WaitAsync(TenantContext database, CommitMarker marker, CancellationToken cancellationToken)
        {
            pair.WaitPlan = database.Plan;
            pair.ReachedWait.TrySetResult(marker);
            if (pair.HoldWait is { } held)
            {
                await held.Task.WaitAsync(cancellationToken);
            }

            var waiting = inner.WaitAsync(database, marker, cancellationToken).AsTask();
            pair.WaitEntered.TrySetResult(waiting);
            return await waiting;
        }
    }
}
