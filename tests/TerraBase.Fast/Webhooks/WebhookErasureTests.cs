using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Fast.Management;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Features.Webhooks;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Webhooks;

/// <summary>Permanent database deletion erases webhook state on every node, independent of the dispatch owner.</summary>
public sealed class WebhookErasureTests
{
    [Test]
    public async Task Another_node_sweeps_an_offline_owners_dispatch_under_purge_authority_and_keeps_the_neighbor_live()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var deleted = (await fixture.RegisterAsync(token)).GetProperty("id").GetString()!;
        var neighbor = await fixture.Api.CreateManagedDatabaseAsync(fixture.Database.Organization, fixture.Database.Project, "neighbor");
        await ManagementApi.CreateCollectionAsync(fixture.Client, neighbor, "items", token);
        var owner = fixture.Api.User;
        // The original owner goes offline for good; this node never owned the deleted database's dispatch.
        await fixture.RestartAsync(token, differentMachine: true);
        var live = (await fixture.RegisterAsync(token, WebhookApi.Route(neighbor))).GetProperty("id").GetString()!;
        await Assert.That(await DispatchesAsync(fixture, token)).Contains(deleted);

        using (var management = fixture.Api.As(owner))
        using (var response = await management.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
        {
            response.EnsureSuccessStatusCode();
        }

        await UntilAsync(fixture, async () => !(await DispatchesAsync(fixture, token)).Contains(deleted), token);
        await Assert.That(await DispatchesAsync(fixture, token)).Contains(live);
        var service = fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>();
        await Assert.That(await service.EraseAsync(Tenant(fixture.Database), token)).IsTrue();
        await Assert.That(await HasStateAsync(fixture, fixture.Database.Id, token)).IsFalse();

        await fixture.PutAsync("neighbor-write", token, neighbor);
        var request = fixture.Receiver.ReadAsync(token).AsTask();
        await UntilAsync(fixture, () => Task.FromResult(request.IsCompleted), token);
        using var payload = JsonDocument.Parse((await request).Body);
        await Assert.That(payload.RootElement.GetProperty("webhookId").GetString()).IsEqualTo(live);
    }

    [Test]
    public async Task A_dispatch_whose_scope_conflicts_with_the_purge_authority_is_held_and_blocks_verification()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var real = (await fixture.RegisterAsync(token)).GetProperty("id").GetString()!;
        var neighbor = await fixture.Api.CreateManagedDatabaseAsync(fixture.Database.Organization, fixture.Database.Project, "neighbor");
        await ManagementApi.CreateCollectionAsync(fixture.Client, neighbor, "items", token);
        var forged = EntityIds.New().ToString();
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using (var write = await system.BeginTransactionAsync(write: true, token))
        {
            // Same database id under another project: the authority does not cover it, so no node may delete it.
            var other = Tenant(fixture.Database) with { ProjectId = EntityIds.New().ToString() };
            await SystemCatalog.PutAsync(write, "webhook_dispatch", forged, new WebhookDispatch(forged, other, "items", "offline-owner"),
                TerraBaseJsonContext.Default.WebhookDispatch, token);
            _ = await write.CommitAsync(token);
        }

        using (var response = await fixture.Api.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
        {
            response.EnsureSuccessStatusCode();
        }

        // Both rows share one dispatch page, so the pass that removed the matching row also classified the forged one.
        await UntilAsync(fixture, async () => !(await DispatchesAsync(fixture, token)).Contains(real), token);
        await Assert.That(await DispatchesAsync(fixture, token)).Contains(forged);
        var service = fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>();
        await Assert.That(await service.EraseAsync(Tenant(fixture.Database), token)).IsFalse();
        await Assert.That(await DispatchesAsync(fixture, token)).Contains(forged);
        await Assert.That(await service.EraseAsync(Tenant(neighbor), token)).IsFalse();
    }

    [Test]
    public async Task A_held_send_withholds_the_purge_receipt_until_its_owner_releases_it()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var held = new HeldTransport();
        try
        {
            await using var fixture = await WebhookApi.StartAsync(services => services.AddSingleton<IWebhookTransport>(held), token);
            _ = await fixture.RegisterAsync(token);
            await fixture.PutAsync("in-flight", token);
            // The held pass never ends by itself, so wait for the send rather than the pass.
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);

            using (var response = await fixture.Api.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
            {
                response.EnsureSuccessStatusCode();
            }

            var management = fixture.Api.Server.Services.GetRequiredService<EngineManagementService>();
            var erasure = fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>();
            await management.ConvergeAsync(token);
            await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            // Logical rows are gone, but the owner still holds the payload, so this node certifies nothing yet.
            await Assert.That(await DispatchesAsync(fixture, token)).IsEmpty();
            await Assert.That(await HasStateAsync(fixture, fixture.Database.Id, token)).IsFalse();
            await Assert.That(await erasure.EraseAsync(Tenant(fixture.Database), token)).IsFalse();
            await management.ConvergeAsync(token);
            await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: false, Intent: true));

            held.Release.TrySetResult();
            // The owner logs only after its send returned and left the in-flight set.
            await UntilAsync(fixture, () => Task.FromResult(
                fixture.Logs.Text.Contains("Webhook delivery failed; persisted work will retry", StringComparison.Ordinal)), token, advance: false);
            await management.ConvergeAsync(token);
            await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: true, Intent: false));
            await Assert.That(await erasure.EraseAsync(Tenant(fixture.Database), token)).IsTrue();
        }
        finally
        {
            held.Release.TrySetResult();
        }
    }

    [Test]
    public async Task Late_replicated_webhook_state_never_reaches_storage_after_the_receipt_or_a_restart()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var neighbor = await fixture.Api.CreateManagedDatabaseAsync(fixture.Database.Organization, fixture.Database.Project, "neighbor");
        _ = await fixture.RegisterAsync(token);
        using (var response = await fixture.Api.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
        {
            response.EnsureSuccessStatusCode();
        }

        var management = fixture.Api.Server.Services.GetRequiredService<EngineManagementService>();
        await management.ConvergeAsync(token);
        await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: true, Intent: false));

        // A peer that created these before it learned of the deletion replays them now, with markers never written here before.
        var marker = "late-" + EntityIds.New().ToString().ToLowerInvariant();
        var liveMarker = "live-" + EntityIds.New().ToString().ToLowerInvariant();
        var deleted = Tenant(fixture.Database);
        var hook = EntityIds.New().ToString();
        var other = EntityIds.New().ToString();
        var states = new[]
        {
            Put("webhook_dispatch", hook, new WebhookDispatch(hook, deleted, marker, "offline-owner"), TerraBaseJsonContext.Default.WebhookDispatch, 1),
            Put($"databases/{deleted.DatabaseId}/webhooks/{marker}", hook, new WebhookRegistration(1, hook, deleted, fixture.Database.Name, marker,
                EntityIds.New().ToString(), Ulid.NewUlid().ToString(), "offline-owner", "https://" + marker + ".test/events", marker, fixture.Clock.GetUtcNow()),
                TerraBaseJsonContext.Default.WebhookRegistration, 2),
            Put($"databases/{deleted.DatabaseId}/webhook_deliveries/{hook}", EntityIds.New().ToString(),
                new WebhookDeliveryRecord(hook, new WebhookDeliveryBody(hook, fixture.Clock.GetUtcNow(), "pending", 0, null, null, null, null),
                    System.Text.Encoding.UTF8.GetBytes(marker)), TerraBaseJsonContext.Default.WebhookDeliveryRecord, 3),
            Put("webhook_dispatch", other, new WebhookDispatch(other, Tenant(neighbor), liveMarker, "offline-owner"), TerraBaseJsonContext.Default.WebhookDispatch, 4),
        };
        var sink = fixture.Api.Server.Services.GetRequiredService<IChangeGroupSink>();
        var origin = ReplicaId.Of(7, 4242, 0);
        var applied = await sink.ApplyAsync(ChangeGroups.System, origin,
            [new OriginGroup(1, [.. states.Select(state => new ChangeDelta(state.Collection, state.Id, state.State.MaxStamp, state.State))])], default, token);
        // Refused states still advance the origin, so its stream never stalls on them.
        await Assert.That(applied).IsEqualTo(new Hlc(states.Max(state => state.State.MaxStamp.Value)));
        await AssertRefusedAsync();
        // The owner's later deletion carries no payload: it joins as a tombstone and certification still holds.
        var tombstone = DocumentState.Bottom.Delete(new EntryVersion(new Hlc(applied!.Value.Value + 1), origin));
        _ = await sink.ApplyAsync(ChangeGroups.System, origin, [new OriginGroup(2, [new ChangeDelta("webhook_dispatch", hook, tombstone.MaxStamp, tombstone)])],
            applied.Value, token);
        await AssertRefusedAsync();

        await fixture.RestartAsync(token);
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await Assert.That(await fixture.Api.Server.Services.GetRequiredService<IChangeGroupSink>().RepairAsync(ChangeGroups.System, system.Incarnation, states, token))
            .IsEqualTo(0);
        await AssertRefusedAsync();
        await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: true, Intent: false));

        async Task AssertRefusedAsync()
        {
            await Assert.That(await DispatchesAsync(fixture, token)).DoesNotContain(hook);
            await Assert.That(await DispatchesAsync(fixture, token)).Contains(other);
            await Assert.That(await HasStateAsync(fixture, deleted.DatabaseId!, token)).IsFalse();
            await Assert.That(await fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>().EraseAsync(deleted, token)).IsTrue();
            var directory = fixture.Api.Server.Services.GetRequiredService<TerraBaseOptions>().DataDirectory;
            var path = System.IO.Path.Combine(directory, "_system.db");
            var persisted = await DocumentFiles.ReadAsync(path);
            // Check decrypted persisted rows and captured history; ciphertext cannot provide a positive plaintext sentinel.
            await Assert.That(persisted.Any(row => row.Contains(liveMarker, StringComparison.Ordinal))).IsTrue();
            await Assert.That(persisted.Any(row => row.Contains(marker, StringComparison.Ordinal))).IsFalse();
            await Assert.That(CapturedContains(path, marker)).IsFalse();
            // This additionally checks plaintext exposure in physical files, not decrypted free-page erasure.
            await Assert.That(StoredContains(fixture, liveMarker)).IsFalse();
            await Assert.That(StoredContains(fixture, marker)).IsFalse();
        }
    }

    [Test]
    public async Task Authority_in_the_same_group_dominates_webhook_state_ahead_of_it_and_a_conflicting_scope_is_kept()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var marker = "same-group-" + EntityIds.New().ToString().ToLowerInvariant();
        var purged = new TenantContext(fixture.Database.Organization, default, fixture.Database.Project, EntityIds.New().ToString());
        var conflicting = purged with { ProjectId = EntityIds.New().ToString() };
        var hook = EntityIds.New().ToString();
        var forged = EntityIds.New().ToString();
        var now = fixture.Clock.GetUtcNow();
        var states = new[]
        {
            Put("webhook_dispatch", hook, new WebhookDispatch(hook, purged, marker, "offline-owner"), TerraBaseJsonContext.Default.WebhookDispatch, 1),
            Put($"databases/{purged.DatabaseId}/webhook_state", hook, new WebhookState(hook, marker, "active", null), TerraBaseJsonContext.Default.WebhookState, 2),
            Put("webhook_dispatch", forged, new WebhookDispatch(forged, conflicting, "items", "offline-owner"), TerraBaseJsonContext.Default.WebhookDispatch, 3),
            // The prefix is the purged database's own namespace: even a registration naming another project is refused.
            Put($"databases/{purged.DatabaseId}/webhooks/{marker}", forged, new WebhookRegistration(1, forged, conflicting, marker, marker,
                EntityIds.New().ToString(), Ulid.NewUlid().ToString(), "offline-owner", "https://" + marker + ".test/events", marker, now),
                TerraBaseJsonContext.Default.WebhookRegistration, 5),
            Put("database_purges", purged.DatabaseId!, new DatabasePurgeRecord(purged, now, now.AddDays(30), now.AddDays(30), null, ["offline-owner"]),
                TerraBaseJsonContext.Default.DatabasePurgeRecord, 4),
        };
        _ = await fixture.Api.Server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(ChangeGroups.System, ReplicaId.Of(7, 4243, 0),
            [new OriginGroup(1, [.. states.Select(state => new ChangeDelta(state.Collection, state.Id, state.State.MaxStamp, state.State))])], default, token);

        var dispatches = await DispatchesAsync(fixture, token);
        await Assert.That(dispatches).DoesNotContain(hook);
        await Assert.That(dispatches).Contains(forged);
        await Assert.That(await HasStateAsync(fixture, purged.DatabaseId!, token)).IsFalse();
        await Assert.That(StoredContains(fixture, marker)).IsFalse();
        // The kept conflicting dispatch names the purged id, so erasure refuses to certify.
        var erasure = fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>();
        await Assert.That(await erasure.EraseAsync(purged, token)).IsFalse();
        await Assert.That(erasure.Certifies(purged)).IsFalse();
    }

    [Test]
    public async Task Malformed_tenants_or_authority_never_stall_system_replication()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var deleted = Tenant(fixture.Database);
        var neighbor = await fixture.Api.CreateManagedDatabaseAsync(fixture.Database.Organization, fixture.Database.Project, "neighbor");
        await ManagementApi.CreateCollectionAsync(fixture.Client, neighbor, "items", token);
        var live = (await fixture.RegisterAsync(token, WebhookApi.Route(neighbor))).GetProperty("id").GetString()!;
        // Before any authority exists, a claim naming only the database id is admitted; erasure must later remove it.
        var preClaim = EntityIds.New().ToString();
        var early = Raw("webhook_dispatch", preClaim, $$"""{"tenant":{"databaseId":"{{deleted.DatabaseId}}"},"collection":"items","machine":"m"}""", 0);
        _ = await fixture.Api.Server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(ChangeGroups.System, ReplicaId.Of(7, 4248, 0),
            [new OriginGroup(1, [new ChangeDelta(early.Collection, early.Id, early.State.MaxStamp, early.State)])], default, token);
        await Assert.That(await DispatchesAsync(fixture, token)).Contains(preClaim);
        using (var response = await fixture.Api.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
        {
            response.EnsureSuccessStatusCode();
        }

        var machine = fixture.Api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId;
        var (org, project, id) = (deleted.OrganizationId, deleted.ProjectId, deleted.DatabaseId);
        var (capitalized, surrogateId, surrogateName, tenantless, nullDatabase) =
            (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        var (deep, scalar, scalarState) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        var (repeated, repeatedState, repeatedLive) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        var wrapped = EntityIds.New().ToString();
        var odd = new[]
        {
            // The typed reader matches names case-insensitively, so this names the purged scope: refused.
            Raw("webhook_dispatch", capitalized, $$"""{"Tenant":{"OrganizationId":"{{org}}","ProjectId":"{{project}}","DatabaseId":"{{id}}"},"collection":"items","machine":"m"}""", 11),
            // The wire decoder and DocumentState.Put both refuse unpaired surrogates; built directly, so the reader must not throw on them.
            // An undecodable database id names no database: admitted.
            Uncanonical("webhook_dispatch", surrogateId, "tenant", $$"""{"organizationId":"{{org}}","projectId":"{{project}}","databaseId":"\ud800"}""", 12),
            // An undecodable name ahead of the ids does not hide them: refused.
            Uncanonical("webhook_dispatch", surrogateName, "tenant", $$"""{"\ud800x":1,"organizationId":"{{org}}","projectId":"{{project}}","databaseId":"{{id}}"}""", 13),
            // Rows naming no database are admitted and skipped by this node's own worker.
            Raw("webhook_dispatch", tenantless, $$"""{"collection":"items","machine":"{{machine}}"}""", 14),
            Raw("webhook_dispatch", nullDatabase, $$"""{"tenant":{"organizationId":"{{org}}","projectId":"{{project}}","databaseId":null},"collection":"items","machine":"{{machine}}"}""", 15),
            // A field nested to exactly the stored limit does not hide the tenant after it: refused.
            Raw("webhook_dispatch", deep, $$"""{"a":{{new string('[', DocumentJson.MaxStoredDepth) + new string(']', DocumentJson.MaxStoredDepth)}},"tenant":{"organizationId":"{{org}}","projectId":"{{project}}","databaseId":"{{id}}"},"collection":"items","machine":"m"}""", 16),
            // A replicated scalar where a purge belongs reads back unwrapped: no authority, so its database's state is admitted.
            Raw("database_purges", scalar, """{"\u0000":null}""", 17),
            Raw($"databases/{scalar}/webhook_state", scalarState, """{"cursor":"c","status":"active"}""", 18),
            Put("webhook_dispatch", scalarState, new WebhookDispatch(scalarState, deleted with { DatabaseId = scalar }, "items", "offline-owner"),
                TerraBaseJsonContext.Default.WebhookDispatch, 19),
            // The reserved scalar-wrapper field would hide the tenant when read back: refused whatever the scope.
            Raw("webhook_dispatch", wrapped, $$"""{"\u0000":1,"tenant":{"organizationId":"{{org}}","projectId":"{{project}}","databaseId":"{{EntityIds.New()}}"},"collection":"items","machine":"m"}""", 23),
            // Repeated nested keys pass the canonical wire decoder.
            Raw("database_purges", repeated, $$"""{"database":{"organizationId":"{{org}}","plan":"hobby","projectId":"{{project}}","databaseId":"{{repeated}}","databaseId":"{{repeated}}"},"requestedAt":"2026-10-23T00:00:00+00:00","activeDueAt":"2026-11-22T00:00:00+00:00","backupDueAt":"2026-11-22T00:00:00+00:00","backupVerifiedAt":null,"expectedHolders":["x"],"formatVersion":1}""", 20),
            Raw($"databases/{repeated}/webhook_state", repeatedState, """{"cursor":"c","status":"active"}""", 21),
            Raw("webhook_dispatch", repeatedLive, $$"""{"tenant":{"organizationId":"{{org}}","organizationId":"{{org}}","plan":"hobby","projectId":"{{project}}","databaseId":"{{EntityIds.New()}}"},"collection":"items","machine":"{{machine}}"}""", 22),
        };

        var unreadable = EntityIds.New().ToString();
        var claim = EntityIds.New().ToString();
        var kept = EntityIds.New().ToString();
        var later = EntityIds.New().ToString();
        var states = new[]
        {
            // A non-string organization claims the purged database: refused, not thrown.
            Raw("webhook_dispatch", claim, $$"""{"tenant":{"organizationId":5,"databaseId":"{{deleted.DatabaseId}}"},"collection":"items","machine":"m"}""", 1),
            // Authority no node can read is no authority: its database's webhook state stays.
            Raw("database_purges", unreadable, """{"formatVersion":"one","database":[1,2]}""", 2),
            Raw($"databases/{unreadable}/webhook_state", kept, """{"cursor":"c","status":"active"}""", 3),
            Put("webhook_dispatch", kept, new WebhookDispatch(kept, deleted with { DatabaseId = unreadable }, "items", "offline-owner"),
                TerraBaseJsonContext.Default.WebhookDispatch, 4),
        };
        var sink = fixture.Api.Server.Services.GetRequiredService<IChangeGroupSink>();
        var origin = ReplicaId.Of(7, 4245, 0);
        var applied = await sink.ApplyAsync(ChangeGroups.System, origin,
            [new OriginGroup(1, [.. states.Select(state => new ChangeDelta(state.Collection, state.Id, state.State.MaxStamp, state.State))])], default, token);
        await Assert.That(applied).IsEqualTo(new Hlc(states.Max(state => state.State.MaxStamp.Value)));
        // The origin's stream goes on from where it stood.
        var next = Put("webhook_dispatch", later, new WebhookDispatch(later, deleted with { DatabaseId = unreadable }, "items", "offline-owner"),
            TerraBaseJsonContext.Default.WebhookDispatch, 5);
        _ = await sink.ApplyAsync(ChangeGroups.System, origin, [new OriginGroup(2, [new ChangeDelta(next.Collection, next.Id, next.State.MaxStamp, next.State)])],
            applied!.Value, token);

        var dispatches = await DispatchesAsync(fixture, token);
        await Assert.That(dispatches).DoesNotContain(claim);
        await Assert.That(dispatches).Contains(kept);
        await Assert.That(dispatches).Contains(later);
        await Assert.That(await HasStateAsync(fixture, unreadable, token)).IsTrue();
        await Assert.That(await fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>().EraseAsync(deleted, token)).IsTrue();
        _ = await sink.ApplyAsync(ChangeGroups.System, origin, [new OriginGroup(3, [.. odd.Select(state => new ChangeDelta(state.Collection, state.Id, state.State.MaxStamp, state.State))])],
            new Hlc(next.State.MaxStamp.Value), token);
        var stored = await DispatchesAsync(fixture, token);
        await Assert.That(stored).DoesNotContain(capitalized);
        await Assert.That(stored).DoesNotContain(surrogateName);
        await Assert.That(stored).Contains(surrogateId);
        await Assert.That(stored).Contains(tenantless);
        await Assert.That(stored).Contains(nullDatabase);
        await Assert.That(stored).DoesNotContain(deep);
        await Assert.That(stored).DoesNotContain(preClaim);
        await Assert.That(stored).Contains(scalarState);
        await Assert.That(await HasStateAsync(fixture, scalar, token)).IsTrue();
        // A repeated key makes an authority unreadable: its prefix state stays, and the dispatch is skipped by its own worker.
        await Assert.That(await HasStateAsync(fixture, repeated, token)).IsTrue();
        await Assert.That(stored).Contains(repeatedLive);
        await Assert.That(stored).DoesNotContain(wrapped);
        await Assert.That(await fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>().EraseAsync(deleted with { DatabaseId = scalar }, token)).IsFalse();
        await Assert.That(await fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>().EraseAsync(deleted, token)).IsTrue();

        // Worker passes read every row, the odd ones and the unreadable authority included, and still deliver for the neighbor.
        await fixture.PutAsync("neighbor-write", token, neighbor);
        var request = await fixture.ReceiveAsync(token);
        using (var payload = JsonDocument.Parse(request.Body))
        {
            await Assert.That(payload.RootElement.GetProperty("webhookId").GetString()).IsEqualTo(live);
        }

        await Assert.That(fixture.Logs.Text.Contains("Webhook dispatch pass failed", StringComparison.Ordinal)).IsFalse();
        await Assert.That(await DispatchesAsync(fixture, token)).Contains(kept);

        // The surrogate rows above cannot arrive by replication: the native encoding refuses them both ways.
        using var surrogate = JsonDocument.Parse("""{"tenant":{"databaseId":"\ud800"}}""");
        await Assert.That(() => DocumentState.Bottom.Put(new EntryVersion(new Hlc(1 << 16), ReplicaId.Of(7, 1, 0)), surrogate.RootElement))
            .Throws<InvalidOperationException>();
        var unencodable = Uncanonical("webhook_dispatch", "x", "tenant", """{"\ud800x":1,"databaseId":"\ud800"}""", 1).State;
        await Assert.That(() => DocumentStateEncoding.Decode(DocumentStateEncoding.Encode(unencodable))).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_stored_row_hiding_its_tenant_behind_the_reserved_field_blocks_certification(bool objectValue)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var deleted = Tenant(fixture.Database);
        // Joined before admission refused the reserved field; it reads back unwrapped, without a readable tenant.
        // A number value reads back as a non-object; an object value reads back as one object followed by trailing fields.
        // Each case stores only its own row, so neither can mask the other.
        var tenant = "\"tenant\":{\"organizationId\":\"" + deleted.OrganizationId + "\",\"projectId\":\"" + deleted.ProjectId + "\",\"databaseId\":\"" + deleted.DatabaseId + "\"}";
        var state = objectValue
            ? Raw("webhook_dispatch", EntityIds.New().ToString(), "{\"\\u0000\":{},\"collection\":\"items\",\"machine\":\"m\"," + tenant + "}", 2)
            : Raw("webhook_dispatch", EntityIds.New().ToString(), "{\"\\u0000\":1," + tenant + ",\"collection\":\"items\",\"machine\":\"m\"}", 1);
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using (var join = await system.BeginDurableTransactionAsync(token))
        {
            _ = await join.JoinAsync(state.Collection, state.Id, state.State, new EntryVersion(state.State.MaxStamp, ReplicaId.Of(7, 4250, 0)), token);
            _ = await join.CommitAsync(token);
        }

        using (var response = await fixture.Api.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
        {
            response.EnsureSuccessStatusCode();
        }

        var erasure = fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>();
        await Assert.That(await erasure.EraseAsync(deleted, token)).IsFalse();
        await Assert.That(erasure.Certifies(deleted)).IsFalse();
        await fixture.Api.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(token);
        await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: false, Intent: true));
    }

    [Test]
    public async Task A_withheld_purge_receipt_still_lets_startup_audit_unknown_files()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var deleted = Tenant(fixture.Database);
        // A dispatch naming this database under another project keeps erasure refused, so the cleanup intent survives restart.
        var conflict = EntityIds.New().ToString();
        var state = Put("webhook_dispatch", conflict, new WebhookDispatch(conflict, deleted with { ProjectId = EntityIds.New().ToString() }, "items",
            "offline-owner"), TerraBaseJsonContext.Default.WebhookDispatch, 1);
        _ = await fixture.Api.Server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(ChangeGroups.System, ReplicaId.Of(7, 4249, 0),
            [new OriginGroup(1, [new ChangeDelta(state.Collection, state.Id, state.State.MaxStamp, state.State)])], default, token);
        using (var response = await fixture.Api.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
        {
            response.EnsureSuccessStatusCode();
        }

        await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: false, Intent: true));
        // A file in the project's directory under a database id the catalog never registered.
        var stray = EntityIds.New() + ".db";
        var project = System.IO.Path.Combine(fixture.Api.Server.Services.GetRequiredService<TerraBaseOptions>().DataDirectory,
            fixture.Database.Organization, fixture.Database.Project);
        _ = Directory.CreateDirectory(project);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(project, stray), [], token);

        await fixture.RestartAsync(token);
        await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: false, Intent: true));
        await Assert.That(fixture.Logs.Text.Contains(stray, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task An_uncommitted_replicated_conflict_cannot_slip_past_the_erasure_snapshot()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var transport = new HeldTransport();
        var conflict = EntityIds.New().ToString();
        var paused = new HeldAdmission(conflict);
        var writes = new UsageCommitHook();
        try
        {
            await using var fixture = await WebhookApi.StartAsync(services =>
            {
                writes.Install(services);
                services.AddSingleton<IWebhookTransport>(transport);
                services.AddSingleton<IWebhookReplicaAdmission>(provider => paused.Wrap(provider.GetRequiredService<WebhookReplicaAdmission>()));
            }, token);
            _ = await fixture.RegisterAsync(token);
            await fixture.PutAsync("in-flight", token);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            using (var response = await fixture.Api.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
            {
                response.EnsureSuccessStatusCode();
            }

            // The held send keeps the cleanup pending; release it so only the conflict can withhold the receipt.
            var management = fixture.Api.Server.Services.GetRequiredService<EngineManagementService>();
            await management.ConvergeAsync(token);
            transport.Release.TrySetResult();
            await fixture.ObserveAsync(_ => Task.FromResult(fixture.Logs.Text.Contains("Webhook delivery failed; persisted work will retry", StringComparison.Ordinal)), token);
            await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: false, Intent: true));

            // The receiving transaction counts the conflict, then holds its writer before commit.
            var deleted = Tenant(fixture.Database);
            var state = Put("webhook_dispatch", conflict, new WebhookDispatch(conflict, deleted with { ProjectId = EntityIds.New().ToString() }, "items",
                "offline-owner"), TerraBaseJsonContext.Default.WebhookDispatch, 1);
            var apply = fixture.Api.Server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(ChangeGroups.System, ReplicaId.Of(7, 4247, 0),
                [new OriginGroup(1, [new ChangeDelta(state.Collection, state.Id, state.State.MaxStamp, state.State)])], default, token).AsTask();
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            // Erasure's next write waits here: its leading snapshot write when correct, or its final write after a scan that
            // could not see the uncommitted row when the snapshot is taken outside the writer.
            var erasure = fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>();
            var gate = writes.PauseWrite();
            var erase = erasure.EraseAsync(deleted, token).AsTask();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            gate.Dispose();
            paused.Release.TrySetResult();
            _ = await apply.WaitAsync(TimeSpan.FromSeconds(15), token);

            await Assert.That(await erase.WaitAsync(TimeSpan.FromSeconds(15), token)).IsFalse();
            await management.ConvergeAsync(token);
            await Assert.That(erasure.Certifies(deleted)).IsFalse();
            await Assert.That(await DispatchesAsync(fixture, token)).Contains(conflict);
            await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: false, Intent: true));
        }
        finally
        {
            transport.Release.TrySetResult();
            paused.Release.TrySetResult();
        }
    }

    // Holds the receiving transaction right after admission counted the named dispatch, before the sink commits.
    private sealed class HeldAdmission(string id)
    {
        public string Id { get; } = id;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IWebhookReplicaAdmission Wrap(IWebhookReplicaAdmission inner) => new Admission(inner, this);

        private sealed class Admission(IWebhookReplicaAdmission inner, HeldAdmission held) : IWebhookReplicaAdmission
        {
            public bool Decides(string collection) => inner.Decides(collection);

            public async ValueTask<bool> AdmitAsync(IStorageTransaction transaction, string collection, string id, DocumentState state, CancellationToken cancellationToken)
            {
                var admitted = await inner.AdmitAsync(transaction, collection, id, state, cancellationToken);
                if (id == held.Id)
                {
                    held.Entered.TrySetResult();
                    await held.Release.Task;
                }

                return admitted;
            }
        }
    }

    [Test]
    public async Task A_conflict_admitted_behind_a_resumed_cursor_restarts_the_pass_and_withholds_certification()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await WebhookApi.StartAsync(token);
        var deleted = Tenant(fixture.Database);
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        // Five calls' worth of unrelated dispatches, so one pass spans several bounded calls.
        for (var batch = 0; batch < 10; batch++)
        {
            await using var write = await system.BeginTransactionAsync(write: true, token);
            for (var row = 0; row < 500; row++)
            {
                var id = EntityIds.New().ToString();
                await SystemCatalog.PutAsync(write, "webhook_dispatch", id, new WebhookDispatch(id, deleted with { DatabaseId = EntityIds.New().ToString() },
                    "items", "offline-owner"), TerraBaseJsonContext.Default.WebhookDispatch, token);
            }

            _ = await write.CommitAsync(token);
        }

        using (var response = await fixture.Api.Client.DeleteAsync(ManagementApi.Uri(fixture.Database.Path), token))
        {
            response.EnsureSuccessStatusCode();
        }

        var erasure = fixture.Api.Server.Services.GetRequiredService<IWebhookErasure>();
        await Assert.That(await erasure.EraseAsync(deleted, token)).IsFalse();
        // A peer replicates a dispatch naming this database under another project, keyed ahead of the stored cursor.
        const string conflict = "00000000000000000000000001";
        var state = Put("webhook_dispatch", conflict, new WebhookDispatch(conflict, deleted with { ProjectId = EntityIds.New().ToString() }, "items",
            "offline-owner"), TerraBaseJsonContext.Default.WebhookDispatch, 1);
        _ = await fixture.Api.Server.Services.GetRequiredService<IChangeGroupSink>().ApplyAsync(ChangeGroups.System, ReplicaId.Of(7, 4246, 0),
            [new OriginGroup(1, [new ChangeDelta(state.Collection, state.Id, state.State.MaxStamp, state.State)])], default, token);

        for (var call = 0; call < 8; call++)
        {
            await Assert.That(await erasure.EraseAsync(deleted, token)).IsFalse();
        }

        await Assert.That(erasure.Certifies(deleted)).IsFalse();
        await Assert.That(await DispatchesAsync(fixture, token)).Contains(conflict);
        await fixture.Api.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(token);
        await Assert.That(await CleanupAsync(fixture, token)).IsEqualTo((Receipt: false, Intent: true));
    }

    // The stored body exactly as the system catalog writes it, as one replicated put at a fixed future stamp.
    private static RepairState Put<T>(string collection, string id, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, ulong order)
    {
        using var body = JsonDocument.Parse(EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(value, type), id, nested: true));
        var stamp = new Hlc(((ulong)DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() << 16) + order);
        return new RepairState(collection, id, DocumentState.Bottom.Put(new EntryVersion(stamp, ReplicaId.Of(7, 4244, 0)), body.RootElement));
    }

    private static RepairState Raw(string collection, string id, string json, ulong order)
    {
        // A root object around a field of the stored depth limit, plus a set level: what a decoded state can materialize.
        using var body = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = DocumentJson.MaxStoredDepth + 2 });
        var stamp = new Hlc(((ulong)DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() << 16) + order);
        return new RepairState(collection, id, DocumentState.Bottom.Put(new EntryVersion(stamp, ReplicaId.Of(7, 4244, 0)), body.RootElement));
    }

    // One live field holding raw JSON exactly as given, bypassing the canonical re-serialization every decoded state passes.
    private static RepairState Uncanonical(string collection, string id, string field, string json, ulong order)
    {
        var at = new EntryVersion(new Hlc(((ulong)DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() << 16) + order), ReplicaId.Of(7, 4244, 0));
        var fields = ImmutableSortedDictionary.CreateRange(Utf8Ordinal.Instance,
            [KeyValuePair.Create(field, new FieldState(new BaseEntry(at, json, at.Stamp), FieldState.NoCounters, FieldState.NoDots))]);
        return new RepairState(collection, id, new DocumentState(new ClearEntry(at, false, at.Stamp), fields));
    }

    private static bool CapturedContains(string path, string marker)
    {
        using var connection = new Turso.TursoConnection(TestMasterKey.ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }

        command.CommandText = "SELECT before, after FROM turso_cdc;";
        using var rows = command.ExecuteReader();
        var needle = System.Text.Encoding.UTF8.GetBytes(marker);
        while (rows.Read())
        {
            for (var column = 0; column < 2; column++)
            {
                if (!rows.IsDBNull(column) && ((byte[])rows.GetValue(column)).AsSpan().IndexOf(needle) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool StoredContains(WebhookApi fixture, string marker)
    {
        var needle = System.Text.Encoding.UTF8.GetBytes(marker);
        var directory = fixture.Api.Server.Services.GetRequiredService<TerraBaseOptions>().DataDirectory;
        // The process lock holds no data and refuses shared reads.
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(path => System.IO.Path.GetFileName(path) != ".terrabase.lock").Any(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.GetBuffer().AsSpan(0, (int)copy.Length).IndexOf(needle) >= 0;
        });
    }

    private static async Task<(bool Receipt, bool Intent)> CleanupAsync(WebhookApi fixture, CancellationToken token)
    {
        var machine = fixture.Api.Server.Services.GetRequiredService<TerraBaseOptions>().MachineId;
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await system.BeginTransactionAsync(write: false, token);
        return (await read.GetAsync($"database_purge_receipts/{fixture.Database.Id}", machine, token) is not null,
            await read.GetAsync("pending_database_cleanup", fixture.Database.Id, token) is not null);
    }

    private static TenantContext Tenant(ManagedDatabaseRef database) => new(database.Organization, default, database.Project, database.Id);

    private static async Task<string[]> DispatchesAsync(WebhookApi fixture, CancellationToken token)
    {
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await system.BeginTransactionAsync(write: false, token);
        return [.. (await read.ListAsync("webhook_dispatch", null, 100, 1024 * 1024, token)).Documents.Select(document => document.Id)];
    }

    private static async Task<bool> HasStateAsync(WebhookApi fixture, string database, CancellationToken token)
    {
        var system = await fixture.Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        await using var read = await system.BeginTransactionAsync(write: false, token);
        return await read.HasCollectionStateAsync($"databases/{database}/webhook", prefix: true, token);
    }

    // Waits on the worker's own passes (WebhookApi.ObserveAsync), optionally after one clock tick that starts the next pass.
    private static Task UntilAsync(WebhookApi fixture, Func<Task<bool>> condition, CancellationToken token, bool advance = true) =>
        advance ? fixture.DriveAsync(_ => condition(), token) : fixture.ObserveAsync(_ => condition(), token);

    private sealed class HeldTransport : IWebhookTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<bool> ValidateAsync(string url, CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public async ValueTask<WebhookAttempt> SendAsync(string url, string secret, string deliveryId, ReadOnlyMemory<byte> body,
            DateTimeOffset timestamp, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The owner still holds the payload until it chooses to return.
                Cancelled.TrySetResult();
                await Release.Task;
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }
}
