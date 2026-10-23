using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Webhooks;

/// <summary>Lost local feed history becomes an explicit public status, without silently resetting the cursor.</summary>
public sealed class WebhookFeedGapTests
{
    [Test]
    public async Task Metadata_pages_admit_all_origin_buffers_before_materializing_excluded_collection_backlogs()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(services =>
        {
            var budgetRegistration = services.Last(service => service.ServiceType == typeof(IMemoryBudget));
            services.AddSingleton<IMemoryBudget>(provider => new FeedMemory((IMemoryBudget)budgetRegistration.ImplementationFactory!(provider)));
        }, token);
        await ManagementApi.CreateCollectionAsync(fixture.Client, fixture.Database, "other", token);
        var registration = await fixture.RegisterAsync(token);
        var memory = (FeedMemory)fixture.Api.Server.Services.GetRequiredService<IMemoryBudget>();
        var tenant = new TenantContext(fixture.Database.Organization, PlanTier.Hobby, fixture.Database.Project, fixture.Database.Id);
        var store = (await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, token))!;
        using var value = JsonDocument.Parse("""{"joined":true}""");
        await using (var write = await store.BeginTransactionAsync(write: true, token))
        {
            // Thirty-two independent origins, each with a whole 32-event transaction; only the final origin matches.
            for (var node = 1; node <= 32; node++)
            {
                var origin = ReplicaId.Of(4518, (byte)node, 1);
                var at = new EntryVersion(new Hlc(FeedCursor.At(fixture.Clock.GetUtcNow() + TimeSpan.FromSeconds(1)).Value + (ulong)node), origin);
                var delta = DocumentState.Bottom.Put(at, value.RootElement).DeltaAt(at.Stamp);
                for (var item = 0; item < 32; item++)
                {
                    _ = await write.JoinAsync(node == 32 ? "items" : "other", EntityIds.New().ToString(), delta, at, token);
                }
            }
            _ = await write.CommitAsync(token);
        }

        memory.Refuse = true;
        await fixture.DriveAsync(_ => Task.FromResult(memory.Refusals > 0), token);
        await Assert.That(fixture.Receiver.Received).IsEqualTo(0);
        await Assert.That((await fixture.JsonAsync($"{fixture.Path}/{registration.GetProperty("id").GetString()}/deliveries", token)).GetArrayLength()).IsEqualTo(0);
        memory.Refuse = false;
        var captured = await fixture.ReceiveAsync(token);
        using var body = JsonDocument.Parse(captured.Body);
        await Assert.That(body.RootElement.GetProperty("events").GetArrayLength()).IsEqualTo(32);
        await Assert.That(memory.Admitted).IsGreaterThanOrEqualTo(32);
        await WebhookDeliveryTests.SignedAsync(captured, registration.GetProperty("secret").GetString()!);
    }

    [Test]
    public async Task A_dropped_collection_keeps_its_registration_manageable_and_recreation_does_not_reset_its_gap()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var hook = await fixture.RegisterAsync(token);
        var id = hook.GetProperty("id").GetString()!;
        using (var dropped = await fixture.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Data + "/collections/items"), token))
        {
            dropped.EnsureSuccessStatusCode();
        }

        await fixture.DriveAsync(async cancellation =>
            (await fixture.JsonAsync(fixture.Path, cancellation))[0].GetProperty("status").GetString() == "feed_gap", token);
        await Assert.That((await fixture.JsonAsync($"{fixture.Path}/{id}/deliveries", token)).GetArrayLength()).IsEqualTo(0);
        await ManagementApi.CreateCollectionAsync(fixture.Client, fixture.Database, "items", token);
        var control = await fixture.RegisterAsync(token);
        await fixture.PutAsync("recreated", token);
        var captured = await fixture.ReceiveAsync(token);
        using var body = JsonDocument.Parse(captured.Body);
        await Assert.That(body.RootElement.GetProperty("webhookId").GetString()).IsEqualTo(control.GetProperty("id").GetString());
        _ = await fixture.DeliveryAsync(control.GetProperty("id").GetString()!, "delivered", token);
        var list = await fixture.JsonAsync(fixture.Path, token);
        await Assert.That(list.EnumerateArray().Single(item => item.GetProperty("id").GetString() == id)
            .GetProperty("status").GetString()).IsEqualTo("feed_gap");
        await Assert.That(fixture.Receiver.Received).IsEqualTo(1);
        using var deleted = await fixture.Client.DeleteAsync(ManagementApi.Uri($"{fixture.Path}/{id}"), token);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Retention_or_a_trimmed_prefix_pauses_delivery_until_the_registration_is_recreated(bool expireOffline)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        await fixture.PutAsync("head", token);
        var hook = await fixture.RegisterAsync(token);
        var id = hook.GetProperty("id").GetString()!;
        if (expireOffline)
        {
            await fixture.StopAsync();
            fixture.Clock.Advance(FeedCursor.Retention + TimeSpan.FromMinutes(1));
            await fixture.RestartAsync(token);
        }
        else
        {
            await fixture.PutAsync("lost", token);
            var tenant = new TenantContext(fixture.Database.Organization, PlanTier.Hobby, fixture.Database.Project, fixture.Database.Id);
            var store = (await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, token))!;
            await using var transaction = await store.BeginTransactionAsync(write: true, token);
            var head = await transaction.GetFeedHeadAsync(token);
            var afterHead = new Hlc(head.Values.Max(stamp => stamp.Value) + 1);
            await Assert.That(await transaction.TrimFeedAsync(afterHead, 1024, token)).IsGreaterThan(0);
            _ = await transaction.CommitAsync(token);
        }

        await fixture.DriveAsync(async cancellation =>
            (await fixture.JsonAsync(fixture.Path, cancellation))[0].GetProperty("status").GetString() == "feed_gap", token);
        await Assert.That((await fixture.JsonAsync($"{fixture.Path}/{id}/deliveries", token)).GetArrayLength()).IsEqualTo(0);
        await Assert.That(fixture.Receiver.Received).IsEqualTo(0);

        // A newly owned hook proves a worker pass completed while the old hook remains paused.
        await fixture.PutAsync("no-reset", token);
        var control = await fixture.RegisterAsync(token);
        var controlId = control.GetProperty("id").GetString()!;
        await fixture.PutAsync("control", token);
        var captured = await fixture.ReceiveAsync(token);
        using (var body = JsonDocument.Parse(captured.Body))
        {
            await Assert.That(body.RootElement.GetProperty("webhookId").GetString()).IsEqualTo(controlId);
            await Assert.That(body.RootElement.GetProperty("events").GetArrayLength()).IsEqualTo(1);
        }

        _ = await fixture.DeliveryAsync(controlId, "delivered", token);
        await Assert.That((await fixture.JsonAsync($"{fixture.Path}/{id}/deliveries", token)).GetArrayLength()).IsEqualTo(0);
        foreach (var remove in new[] { id, controlId })
        {
            using var deleted = await fixture.Client.DeleteAsync(ManagementApi.Uri($"{fixture.Path}/{remove}"), token);
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        var recreated = await fixture.RegisterAsync(token);
        await fixture.PutAsync("recovered", token);
        var recovered = await fixture.ReceiveAsync(token);
        using var recoveredBody = JsonDocument.Parse(recovered.Body);
        await Assert.That(recoveredBody.RootElement.GetProperty("webhookId").GetString()).IsEqualTo(recreated.GetProperty("id").GetString());
        var events = recoveredBody.RootElement.GetProperty("events");
        await Assert.That(events.GetArrayLength()).IsEqualTo(1);
        await Assert.That(events[0].GetProperty("id").GetString()).IsEqualTo(ManagementApi.DocumentId("recovered"));
    }

    [Test]
    public async Task An_already_captured_batch_retries_after_retention_then_reports_the_feed_gap()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        fixture.Receiver.FirstStatus = 503;
        var hook = await fixture.RegisterAsync(token);
        var id = hook.GetProperty("id").GetString()!;
        await fixture.PutAsync("captured", token);
        var pending = await fixture.ReceiveAsync(token);
        _ = await fixture.DeliveryAsync(id, "pending", token, "http_error");
        await fixture.StopAsync();
        fixture.Clock.Advance(FeedCursor.Retention + TimeSpan.FromMinutes(1));
        await fixture.RestartAsync(token);

        var replay = await fixture.ReceiveAsync(token);
        await Assert.That(replay.Delivery).IsEqualTo(pending.Delivery);
        await Assert.That(replay.Body.SequenceEqual(pending.Body)).IsTrue();
        await WebhookDeliveryTests.SignedAsync(replay, hook.GetProperty("secret").GetString()!);
        _ = await fixture.DeliveryAsync(id, "delivered", token);
        await fixture.DriveAsync(async cancellation =>
            (await fixture.JsonAsync(fixture.Path, cancellation))[0].GetProperty("status").GetString() == "feed_gap", token);
        await Assert.That(fixture.Receiver.Received).IsEqualTo(2);
    }

    private sealed class FeedMemory(IMemoryBudget inner) : IMemoryBudget
    {
        private int _refusals;
        private int _admitted;
        public bool Refuse { get => Volatile.Read(ref field); set => Volatile.Write(ref field, value); }
        public int Refusals => Volatile.Read(ref _refusals);
        public int Admitted => Volatile.Read(ref _admitted);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            if (lane == MemoryLane.Foreground && tenant is not null && bytes is >= 8192 and < 65536)
            {
                if (Refuse)
                {
                    Interlocked.Increment(ref _refusals);
                    return null;
                }

                Interlocked.Increment(ref _admitted);
            }

            return inner.TryReserve(lane, bytes, tenant);
        }
    }
}
