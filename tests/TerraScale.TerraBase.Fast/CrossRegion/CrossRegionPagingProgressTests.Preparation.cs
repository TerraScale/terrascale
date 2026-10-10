using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Errors;
using TerraBase.Features.CrossRegion;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.CrossRegion;

public sealed partial class CrossRegionPagingProgressTests
{
    [Test]
    [Arguments(1024, 8)]
    [Arguments(32, 1)]
    [Arguments(1, 0)]
    public async Task Cold_targets_prepare_ahead_of_a_held_creation_only_as_far_as_the_storage_queue_allows(int storageQueue, int ahead)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var recorder = new WanRecorder(bus);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-prepare-").FullName;
        try
        {
            using var steps = new LifecycleSteps();
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            // The target answers no other region, so only its own puller and this test take its storage slots.
            network.Mute("sfo-a");
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartTargetAsync(credentials, storageQueue, recorder, clock, steps);
            try
            {
                await HeadsAskedAsync(recorder, clock, token);
                var failures = await FailuresAsync(sfo, token);
                var puller = sfo.Server.Services.GetServices<IHostedService>().OfType<WanPuller>().Single();
                await Assert.That(puller.TargetsAhead).IsEqualTo(ahead);
                var (databases, documents, values, versions) = await SeedAsync(gru, sfo, token);
                using var target = await sfo.CreateClientAsync(databases[0].Organization, "documents:read", "documents:write");
                var held = databases[0].Id;
                string[] others = [.. databases.Skip(1).Select(database => database.Id)];
                var early = others.Take(ahead).ToHashSet(StringComparer.Ordinal);
                steps.Held = held;
                bus.Begin.TrySetResult();
                await steps.WaitAsync(seen => seen.Contains(("create.prepare", held)), token);
                // While the first head's creation waits, exactly the targets the queue admits behind it are created.
                await steps.WaitAsync(seen => early.All(id => seen.Contains(("create.registered", id))), token);
                steps.Release();
                await clock.PausedAsync(token);
                var log = steps.Steps;
                var released = log.IndexOf(("test.released", held));
                await Assert.That(log.Take(released).Where(step => step.Step == "create.registered").Select(step => step.Database)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(early)).IsTrue();
                await Assert.That(log.Skip(released).Where(step => step.Step == "create.prepare").Select(step => step.Database)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(others.Except(early))).IsTrue();
                await Assert.That(log.Where(step => step.Step == "create.registered").Select(step => step.Database).Order(StringComparer.Ordinal)
                    .SequenceEqual(databases.Select(database => database.Id))).IsTrue();
                if (ahead == 0)
                {
                    // Inline: each creation commits before the next one prepares, in head order.
                    await Assert.That(log.Where(step => step.Step is "create.prepare" or "create.committed")
                        .SequenceEqual(databases.SelectMany(database => new[] { ("create.prepare", database.Id), ("create.committed", database.Id) }))).IsTrue();
                }

                // Preparation runs ahead; pulls keep head order.
                await Assert.That(recorder.Pulled.Distinct().SequenceEqual(databases.Select(database => WanProtocol.Id(database.Id)))).IsTrue();
                await Assert.That(steps.HoldExpired).IsFalse();
                await Assert.That(await FailuresAsync(sfo, token)).IsEqualTo(failures);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_unregistered", GruLabel, token)).IsEqualTo(0);
                for (var index = 0; index < databases.Count; index++)
                {
                    await AssertAdmittedReadAsync(target, documents[index], values[index], versions[index]);
                }
            }
            finally
            {
                steps.Release();
                bus.Begin.TrySetResult();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("forgotten", false)]
    [Arguments("unplaced", false)]
    [Arguments("dropped", false)]
    [Arguments("dropped", true)]
    public async Task A_target_prepared_ahead_is_checked_again_before_its_pull(string change, bool delayDeletion)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var recorder = new WanRecorder(bus);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-prepared-").FullName;
        try
        {
            using var steps = new LifecycleSteps();
            using var deletionRelease = new ManualResetEventSlim();
            var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool>? deletion = null;
            EventHandler<IDatabaseStore>? beforeDeleted = delayDeletion ? (_, _) =>
            {
                deleting.TrySetResult();
                _ = deletionRelease.Wait(TimeSpan.FromSeconds(30));
            }
            : null;
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            network.Mute("sfo-a");
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartTargetAsync(credentials, 1024, recorder, clock, steps, beforeDeleted);
            try
            {
                await HeadsAskedAsync(recorder, clock, token);
                var failures = await FailuresAsync(sfo, token);
                var puller = sfo.Server.Services.GetServices<IHostedService>().OfType<WanPuller>().Single();
                var (databases, documents, values, versions) = await SeedAsync(gru, sfo, token);
                using var target = await sfo.CreateClientAsync(databases[0].Organization, "documents:read", "documents:write");
                var held = databases[0].Id;
                var changed = databases[3];
                var file = Path.Combine(sfo.Server.DataDirectory, changed.Organization, changed.Project, changed.Id + ".db");
                steps.Held = held;
                bus.Begin.TrySetResult();
                await steps.WaitAsync(seen => seen.Contains(("create.prepare", held)) && seen.Contains(("wan.targeted", changed.Id)), token);

                // Its store is prepared and queued behind the held head when this region's catalog or storage changes.
                var tenants = sfo.Server.Services.GetRequiredService<ITenantDirectory>();
                var engine = sfo.Server.Services.GetRequiredService<IStorageEngine>();
                var database = await tenants.FindDatabaseAsync(changed.Organization, changed.Project, changed.Name, token) switch
                {
                    TenantContext found => found,
                    _ => throw new InvalidOperationException("the prepared database was not found"),
                };
                // Preparing it pinned the store it opened and the frontier it read there.
                var prepared = (await engine.OpenDatabaseAsync(database, token))!.Incarnation;
                await Assert.That(puller.Holds(prepared)).IsTrue();
                if (change == "forgotten")
                {
                    // The reconciliation of a deletion at its home region (WanPuller.ForgetDeletedAsync); no HTTP API forgets a replica.
                    await Assert.That(await tenants.ForgetDatabaseAsync(database, token) is DatabaseForgotten { Forgotten: true }).IsTrue();
                }
                else if (change == "unplaced")
                {
                    // A newer placement from its home region without this one, as a later head brings it.
                    var placement = await tenants.DatabaseRecordAsync(database, token) switch
                    {
                        TenantDatabaseRecord found => found.Placement,
                        _ => throw new InvalidOperationException("the prepared database placement was not found"),
                    };
                    await Assert.That(await tenants.UpdatePlacementAsync(database, new DatabasePlacement(placement.Home, [], placement.Version + 1), token)
                        is PlacementUpdate { Updated: true }).IsTrue();
                    var updated = await tenants.DatabaseRecordAsync(database, token) switch
                    {
                        TenantDatabaseRecord found => found.Placement,
                        _ => throw new InvalidOperationException("the prepared database placement was not found"),
                    };
                    await Assert.That(updated.Version).IsEqualTo(placement.Version + 1);
                    await Assert.That(updated.Includes("sfo-1")).IsFalse();
                }
                else
                {
                    // The local copy goes while the catalog still places the database here.
                    if (delayDeletion)
                    {
                        // Notify runs after the engine unlocks; hold it while the queued pull opens a replacement.
                        deletion = Task.Run(async () => await engine.DeleteDatabaseAsync(database, token), token);
                        await deleting.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                    }
                    else
                    {
                        await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
                    }
                }

                await Assert.That(await tenants.RegisteredAsync(database, token) is bool registered && registered == (change == "dropped")).IsTrue();
                await Assert.That(await tenants.DatabaseRecordAsync(database, token) switch
                {
                    TenantDatabaseRecord => change != "forgotten",
                    NotFound => change == "forgotten",
                    Unavailable => false,
                }).IsTrue();
                await Assert.That(File.Exists(file)).IsEqualTo(change == "unplaced");
                steps.Release();
                await clock.PausedAsync(token);

                var log = steps.Steps;
                var released = log.IndexOf(("test.released", held));
                // Only a closed store of a database still placed here is targeted again, and only that one is pulled.
                await Assert.That(log.Skip(released).Count(step => step == ("wan.targeted", changed.Id))).IsEqualTo(change == "dropped" ? 1 : 0);
                await Assert.That(recorder.Pulled.Contains(WanProtocol.Id(changed.Id))).IsEqualTo(change == "dropped");
                await Assert.That(recorder.Pulled.Distinct().SequenceEqual(databases.Where(other => other != changed || change == "dropped")
                    .Select(other => WanProtocol.Id(other.Id)))).IsTrue();
                await Assert.That(steps.HoldExpired).IsFalse();
                await Assert.That(await FailuresAsync(sfo, token)).IsEqualTo(failures);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_unregistered", GruLabel, token)).IsEqualTo(change == "dropped" ? 0 : 1);
                if (delayDeletion)
                {
                    await Assert.That(deletion!.IsCompleted).IsFalse();
                    deletionRelease.Set();
                    await Assert.That(await deletion!).IsTrue();
                }

                // A deleted store and its frontier are released; a copy no longer placed here stays pinned until it is deleted.
                await Assert.That(puller.Holds(prepared)).IsEqualTo(change == "unplaced");
                if (change == "dropped")
                {
                    var recreated = (await engine.OpenDatabaseAsync(database, token))!.Incarnation;
                    await Assert.That(recreated).IsNotEqualTo(prepared);
                    await Assert.That(puller.Holds(recreated)).IsTrue();
                }

                for (var index = 0; index < databases.Count; index++)
                {
                    if (databases[index] != changed || change == "dropped")
                    {
                        await AssertAdmittedReadAsync(target, documents[index], values[index], versions[index]);
                    }
                }

                await Assert.That(File.Exists(file)).IsEqualTo(change != "forgotten");
                if (change != "dropped")
                {
                    using var refused = await target.GetAsync(documents[3], token);
                    await Assert.That(refused.StatusCode).IsEqualTo(change == "forgotten" ? HttpStatusCode.NotFound : HttpStatusCode.MisdirectedRequest);
                }

                if (change == "forgotten")
                {
                    using var absent = await sfo.Client.GetAsync(ManagementApi.Uri(changed.Path), token);
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                if (delayDeletion)
                {
                    clock.Advance(WanPuller.Interval);
                    await clock.PausedAsync(token);
                    // The old notification must leave the replacement cached, rather than targeting it again.
                    await Assert.That(steps.Steps.Count(step => step == ("wan.targeted", changed.Id))).IsEqualTo(2);
                    await AssertAdmittedReadAsync(target, documents[3], values[3], versions[3]);
                }
            }
            finally
            {
                steps.Release();
                bus.Begin.TrySetResult();
                deletionRelease.Set();
                if (deletion is not null)
                {
                    _ = await deletion;
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task A_replacement_resets_old_origin_state_across_deletion_and_keeps_its_own(bool oldDiverged, bool deleteWhilePullPending)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var recorder = new WanRecorder(bus);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-replaced-").FullName;
        try
        {
            using var steps = new LifecycleSteps();
            using var deletionRelease = new ManualResetEventSlim();
            var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingPull = new TaskCompletionSource<PullRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pullRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool>? deletion = null;
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            network.Mute("sfo-a");
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            EventHandler<IDatabaseStore>? beforeDeleted = deleteWhilePullPending ? null : (_, _) =>
            {
                deleting.TrySetResult();
                _ = deletionRelease.Wait(TimeSpan.FromSeconds(30));
            };
            await using var sfo = await StartTargetAsync(credentials, 1024, recorder, clock, steps, beforeDeleted);
            try
            {
                await HeadsAskedAsync(recorder, clock, token);
                var (databases, documents, values, versions) = await SeedAsync(gru, sfo, token);
                var changed = databases[3];
                var id = WanProtocol.Id(changed.Id);
                using var source = await gru.CreateClientAsync(changed.Organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(changed.Organization, "documents:read", "documents:write");
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                await AssertAdmittedReadAsync(target, documents[3], values[3], versions[3]);
                var tenants = sfo.Server.Services.GetRequiredService<ITenantDirectory>();
                var engine = sfo.Server.Services.GetRequiredService<IStorageEngine>();
                var puller = sfo.Server.Services.GetServices<IHostedService>().OfType<WanPuller>().Single();
                var meter = sfo.Server.Services.GetRequiredService<UsageMeter>();
                var database = await tenants.FindDatabaseAsync(changed.Organization, changed.Project, changed.Name, token) switch
                {
                    TenantContext found => found,
                    _ => throw new InvalidOperationException("the replicated database was not found"),
                };
                var original = (await engine.OpenDatabaseAsync(database, token))!.Incarnation;
                var applied = (long)bus.LastAdvertisement!.Heads.Single(head => head.DatabaseId == id).Sequence;
                await Assert.That(applied).IsGreaterThan(0);

                async Task AssertStateAsync(long remaining, bool diverged)
                {
                    var (lifetime, _) = await meter.GetObservationsAsync(database,
                        new UsageWindow(UsageWindow.Hour, clock.GetUtcNow().ToUnixTimeSeconds() / 3600, 1), token) switch
                    {
                        UsageCounts counts => counts,
                        Unavailable error => throw new InvalidOperationException(error.Message),
                    };
                    var state = lifetime.Meters!["replication_status|sfo-1|gru-1|inbound"].State!;
                    await Assert.That(state.Remaining).IsEqualTo(remaining);
                    await Assert.That(state.Diverged).IsEqualTo(diverged);
                }

                values[3] += " " + new Faker().Lorem.Sentence();
                using (var written = await source.PutAsJsonAsync(documents[3], new { value = values[3] }, token))
                {
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    versions[3] = written.Headers.ETag!.Tag;
                }

                // The old copy pulls after its verified prefix; delay its response or the deletion notification.
                bus.AheadOf = oldDiverged ? id : null;
                bus.FailPullsOf = oldDiverged || deleteWhilePullPending ? null : id;
                if (deleteWhilePullPending)
                {
                    recorder.BeforePullReply = async (pull, cancellation) =>
                    {
                        if (pull.DatabaseId == id)
                        {
                            pendingPull.TrySetResult(pull);
                            await pullRelease.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
                            if (!oldDiverged)
                            {
                                throw new IOException("injected delayed pull failure");
                            }
                        }
                    };
                }

                clock.Advance(WanPuller.Interval);
                if (deleteWhilePullPending)
                {
                    var pull = await pendingPull.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                    await Assert.That((long)pull.After).IsEqualTo(applied);
                    await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
                    await Assert.That(puller.Holds(original)).IsFalse();
                    // Both eviction callbacks completed before the old response can update retained state.
                    pullRelease.TrySetResult();
                    await clock.PausedAsync(token);
                    recorder.BeforePullReply = null;
                    await AssertStateAsync(0, false);
                    await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(0);

                    // Without a fresh advertisement, the deleted copy's late response must leave no retry to create a replacement.
                    var pulls = recorder.Pulled.Length;
                    bus.Slow = true;
                    bus.Unanswered = true;
                    clock.Advance(WanPuller.Interval);
                    await clock.PausedAsync(token);
                    await Assert.That(recorder.Pulled.Length).IsEqualTo(pulls);
                    await Assert.That(steps.Steps.Count(step => step == ("wan.targeted", changed.Id))).IsEqualTo(1);
                    bus.Unanswered = false;
                    bus.Slow = false;
                }
                else
                {
                    await clock.PausedAsync(token);
                    await AssertStateAsync(1, oldDiverged);
                    deletion = Task.Run(async () => await engine.DeleteDatabaseAsync(database, token), token);
                    await deleting.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                }

                var advertised = (long)bus.LastAdvertisement!.Heads.Single(head => head.DatabaseId == id).Sequence;
                await Assert.That(advertised).IsEqualTo(applied + 1);
                bus.AheadOf = null;
                bus.FailPullsOf = id;
                clock.Advance(WanPuller.Interval);
                await clock.PausedAsync(token);
                var replacement = (await engine.OpenDatabaseAsync(database, token))!.Incarnation;
                await Assert.That(replacement).IsNotEqualTo(original);
                // A failed pull applied nothing to the replacement: all advertised groups are missing, without old divergence.
                await AssertStateAsync(advertised, false);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo((double)advertised);
                using (var absent = await target.GetAsync(documents[3], token))
                {
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                bus.FailPullsOf = null;
                bus.AheadOf = id;
                clock.Advance(WanPuller.Interval);
                await clock.PausedAsync(token);
                await AssertStateAsync(advertised, true);
                if (deletion is not null)
                {
                    await Assert.That(deletion.IsCompleted).IsFalse();
                    deletionRelease.Set();
                    await Assert.That(await deletion).IsTrue();
                }

                await Assert.That(puller.Holds(original)).IsFalse();
                await Assert.That(puller.Holds(replacement)).IsTrue();

                // A transport failure verifies nothing: the replacement's own divergence survives the old notification.
                bus.AheadOf = null;
                bus.FailPullsOf = id;
                clock.Advance(WanPuller.Interval);
                await clock.PausedAsync(token);
                await AssertStateAsync(advertised, true);
                await Assert.That(steps.Steps.Count(step => step == ("wan.targeted", changed.Id))).IsEqualTo(2);

                bus.FailPullsOf = null;
                clock.Advance(WanPuller.Interval);
                await clock.PausedAsync(token);
                await AssertStateAsync(0, false);
                await AssertAdmittedReadAsync(target, documents[3], values[3], versions[3]);
            }
            finally
            {
                bus.Begin.TrySetResult();
                pullRelease.TrySetResult();
                deletionRelease.Set();
                if (deletion is not null)
                {
                    _ = await deletion;
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task A_deleted_processed_head_cannot_publish_old_backlog_or_retry_its_copy(bool oldDiverged, bool deleteAfterRound)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var recorder = new WanRecorder(bus);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-processed-delete-").FullName;
        try
        {
            using var steps = new LifecycleSteps();
            var pendingPull = new TaskCompletionSource<PullRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pullRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            network.Mute("sfo-a");
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartTargetAsync(credentials, 1024, recorder, clock, steps);
            try
            {
                await HeadsAskedAsync(recorder, clock, token);
                var (databases, documents, values, versions) = await SeedAsync(gru, sfo, token);
                var changed = databases[3];
                var id = WanProtocol.Id(changed.Id);
                var later = WanProtocol.Id(databases[4].Id);
                using var source = await gru.CreateClientAsync(changed.Organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(changed.Organization, "documents:read", "documents:write");
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                await AssertReadAsync(target, documents[3], values[3], versions[3], token);

                var tenants = sfo.Server.Services.GetRequiredService<ITenantDirectory>();
                var engine = sfo.Server.Services.GetRequiredService<IStorageEngine>();
                var puller = sfo.Server.Services.GetServices<IHostedService>().OfType<WanPuller>().Single();
                var meter = sfo.Server.Services.GetRequiredService<UsageMeter>();
                var database = await tenants.FindDatabaseAsync(changed.Organization, changed.Project, changed.Name, token) switch
                {
                    TenantContext found => found,
                    _ => throw new InvalidOperationException("the replicated database was not found"),
                };
                var original = (await engine.OpenDatabaseAsync(database, token))!.Incarnation;
                var file = Path.Combine(sfo.Server.DataDirectory, changed.Organization, changed.Project, changed.Id + ".db");
                async Task AssertStateAsync(long remaining, bool diverged)
                {
                    var (lifetime, _) = await meter.GetObservationsAsync(database,
                        new UsageWindow(UsageWindow.Hour, clock.GetUtcNow().ToUnixTimeSeconds() / 3600, 1), token) switch
                    {
                        UsageCounts counts => counts,
                        Unavailable error => throw new InvalidOperationException(error.Message),
                    };
                    var state = lifetime.Meters!["replication_status|sfo-1|gru-1|inbound"].State!;
                    await Assert.That(state.Remaining).IsEqualTo(remaining);
                    await Assert.That(state.Diverged).IsEqualTo(diverged);
                }

                foreach (var index in new[] { 3, 4 })
                {
                    values[index] += " " + new Faker().Lorem.Sentence();
                    using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    versions[index] = written.Headers.ETag!.Tag;
                }

                bus.AheadOf = oldDiverged ? id : null;
                bus.FailPullsOf = oldDiverged ? null : id;
                recorder.BeforePullReply = async (pull, cancellation) =>
                {
                    if (pull.DatabaseId == later)
                    {
                        pendingPull.TrySetResult(pull);
                        await pullRelease.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
                        throw new IOException("injected later-head pull failure");
                    }
                };

                clock.Advance(WanPuller.Interval);
                _ = await pendingPull.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                // Pulls retain head order: the earlier head has already retained its backlog and entered the round's retry list.
                await AssertStateAsync(1, false);
                if (!deleteAfterRound)
                {
                    await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
                }

                pullRelease.TrySetResult();
                await clock.PausedAsync(token);
                recorder.BeforePullReply = null;
                await AssertStateAsync(deleteAfterRound ? 1 : 0, deleteAfterRound && oldDiverged);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, token)).IsEqualTo(deleteAfterRound ? 2 : 1);
                if (deleteAfterRound)
                {
                    // The retry has been published before deletion; consuming it still requires the original incarnation.
                    await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
                }

                await Assert.That(puller.Holds(original)).IsFalse();
                await AssertStateAsync(0, false);
                await Assert.That(File.Exists(file)).IsFalse();
                await Assert.That(await tenants.RegisteredAsync(database, token) is true).IsTrue();
                var pulls = recorder.Pulled.Length;
                bus.AheadOf = null;
                bus.FailPullsOf = null;
                bus.Slow = true;
                bus.Unanswered = true;
                clock.Advance(WanPuller.Interval);
                await clock.PausedAsync(token);
                // No new advertisement: the deleted copy stays absent, while the other database's valid retry succeeds.
                await Assert.That(recorder.Pulled.Length).IsEqualTo(pulls + 1);
                await Assert.That(recorder.Pulled[^1]).IsEqualTo(later);
                await Assert.That(steps.Steps.Count(step => step == ("wan.targeted", changed.Id))).IsEqualTo(1);
                await Assert.That(await engine.OpenDatabaseAsync(database, token) is null).IsTrue();
                await Assert.That(File.Exists(file)).IsFalse();
                await AssertStateAsync(0, false);
                using (var absent = await target.GetAsync(documents[3], token))
                {
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                await AssertReadAsync(target, documents[4], values[4], versions[4], token);
            }
            finally
            {
                bus.Begin.TrySetResult();
                pullRelease.TrySetResult();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(true, false)]
    public async Task An_unavailable_catalog_retry_preserves_its_admitted_copy_lifetime(bool cold, bool deleteCopy)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var network = new FakeRegionNetwork();
        var clock = new ProgressClock(DateTimeOffset.UtcNow);
        var bus = new ProgressBus(network.Join("sfo", "sfo-a"), fullMembership: false);
        var recorder = new WanRecorder(bus);
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-catalog-retry-delete-").FullName;
        try
        {
            using var steps = new LifecycleSteps();
            Func<IDatabaseStore, CancellationToken, ValueTask>? afterOpened = null;
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            network.Mute("sfo-a");
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartTargetAsync(credentials, 1024, recorder, clock, steps,
                afterOpened: (store, cancellation) => afterOpened?.Invoke(store, cancellation) ?? ValueTask.CompletedTask);
            try
            {
                await HeadsAskedAsync(recorder, clock, token);
                var (databases, documents, values, versions) = await SeedAsync(gru, sfo, token);
                var changed = databases[3];
                var later = WanProtocol.Id(databases[4].Id);
                using var source = await gru.CreateClientAsync(changed.Organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(changed.Organization, "documents:read", "documents:write");
                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                await AssertReadAsync(target, documents[3], values[3], versions[3], token);

                var tenants = sfo.Server.Services.GetRequiredService<ITenantDirectory>();
                var engine = sfo.Server.Services.GetRequiredService<IStorageEngine>();
                var puller = sfo.Server.Services.GetServices<IHostedService>().OfType<WanPuller>().Single();
                var database = await tenants.FindDatabaseAsync(changed.Organization, changed.Project, changed.Name, token) switch
                {
                    TenantContext found => found,
                    _ => throw new InvalidOperationException("the replicated database was not found"),
                };
                var original = (await engine.OpenDatabaseAsync(database, token))!.Incarnation;
                var file = Path.Combine(sfo.Server.DataDirectory, changed.Organization, changed.Project, changed.Id + ".db");
                async Task<byte[]> WriteCatalogVersionAsync(byte[] version, CancellationToken cancellation)
                {
                    var system = await engine.OpenSystemDatabaseAsync(cancellation);
                    await using var write = await system.BeginTransactionAsync(write: true, cancellation);
                    var previous = (await write.GetAsync("catalog", "version", cancellation))!.Json.ToArray();
                    await write.PutAsync("catalog", "version", version, cancellation);
                    await write.CommitAsync(cancellation);
                    return previous;
                }

                foreach (var index in new[] { 3, 4 })
                {
                    values[index] += " " + new Faker().Lorem.Sentence();
                    using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    versions[index] = written.Headers.ETag!.Tag;
                }

                byte[]? supportedVersion = null;
                var beforeRefusal = original;
                if (cold)
                {
                    // The next fresh head must open a new copy; refuse only after the real journal returns its store.
                    await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
                    await Assert.That(puller.Holds(original)).IsFalse();
                    await Assert.That(File.Exists(file)).IsFalse();
                    afterOpened = async (store, cancellation) =>
                    {
                        if (store.Tenant?.DatabaseId == changed.Id)
                        {
                            original = store.Incarnation;
                            supportedVersion = await WriteCatalogVersionAsync("\"99\""u8.ToArray(), cancellation);
                        }
                    };
                }
                else
                {
                    // The round already read its catalog page; invalidate cached placements before it consumes fresh heads.
                    recorder.BeforeHeadsReply = async cancellation =>
                    {
                        supportedVersion = await WriteCatalogVersionAsync("\"99\""u8.ToArray(), cancellation);
                    };
                }
                var pulls = recorder.Pulled.Length;
                var failures = await FailuresAsync(sfo, token);
                clock.Advance(WanPuller.Interval);
                await clock.PausedAsync(token);
                afterOpened = null;
                if (cold)
                {
                    await Assert.That(original).IsNotEqualTo(beforeRefusal);
                }
                recorder.BeforeHeadsReply = null;
                await Assert.That(supportedVersion is not null).IsTrue();
                await Assert.That(await tenants.DatabaseRecordAsync(database, token) is Unavailable).IsTrue();
                await Assert.That(await FailuresAsync(sfo, token)).IsGreaterThan(failures);
                await Assert.That(recorder.Pulled.Length).IsEqualTo(pulls);
                if (!cold)
                {
                    await Assert.That(puller.Holds(original)).IsTrue();
                }
                using (var unreadable = await target.GetAsync(documents[3], token))
                {
                    await Assert.That(unreadable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                }

                if (deleteCopy)
                {
                    await Assert.That(await engine.DeleteDatabaseAsync(database, token)).IsTrue();
                    await Assert.That(puller.Holds(original)).IsFalse();
                    await Assert.That(File.Exists(file)).IsFalse();
                }
                _ = await WriteCatalogVersionAsync(supportedVersion!, token);
                await Assert.That(await tenants.RegisteredAsync(database, token) is true).IsTrue();
                bus.Slow = true;
                bus.Unanswered = true;
                // Expire the ten-second WAN refusal using virtual time; discovery supplies no fresh advertisements.
                clock.Advance(TimeSpan.FromSeconds(11));
                await clock.PausedAsync(token);
                await Assert.That(recorder.Pulled.Length).IsEqualTo(pulls + (deleteCopy ? 1 : 2));
                await Assert.That(recorder.Pulled[^1]).IsEqualTo(later);
                await Assert.That(steps.Steps.Count(step => step == ("wan.targeted", changed.Id))).IsEqualTo(1);
                if (deleteCopy)
                {
                    await Assert.That(await engine.OpenDatabaseAsync(database, token) is null).IsTrue();
                    await Assert.That(File.Exists(file)).IsFalse();
                    using var absent = await target.GetAsync(documents[3], token);
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }
                else
                {
                    await Assert.That((await engine.OpenDatabaseAsync(database, token))!.Incarnation).IsEqualTo(original);
                    await Assert.That(puller.Holds(original)).IsTrue();
                    await AssertReadAsync(target, documents[3], values[3], versions[3], token);
                }

                await AssertReadAsync(target, documents[4], values[4], versions[4], token);
            }
            finally
            {
                afterOpened = null;
                recorder.BeforeHeadsReply = null;
                bus.Begin.TrySetResult();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // The receiving member: sfo-a with its storage queue, the test's bus and clock, and its database lifecycle steps recorded.
    // Its other clocks stand still, so no periodic usage or pressure work competes for a small queue's slots.
    private static Task<ManagementApi> StartTargetAsync(
        string credentials, int storageQueue, IRegionBus bus, ProgressClock clock, LifecycleSteps steps,
        EventHandler<IDatabaseStore>? beforeDeleted = null, Func<IDatabaseStore, CancellationToken, ValueTask>? afterOpened = null)
    {
        var frozen = new FakeTimeProvider(clock.GetUtcNow());
        return ManagementApi.StartAsync(settings =>
        {
            settings["region"] = "sfo-1";
            settings["node-name"] = "sfo-a";
            settings["storage-queue"] = storageQueue.ToString(CultureInfo.InvariantCulture);
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }, services =>
        {
            services.AddSingleton(bus);
            services.AddSingleton<IMemberStore>(new FakeMemberStore(frozen));
            services.AddSingleton<DatabaseLifecycleCheckpoint>(steps.Record);
            if (afterOpened is not null)
            {
                services.AddSingleton<IReplicationJournal>(provider => new OpeningJournal(
                    ActivatorUtilities.CreateInstance<EngineReplicationJournal>(provider), afterOpened));
            }

            var registration = services.Single(service => service.ImplementationType == typeof(WanPuller));
            services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(
                provider =>
                {
                    if (beforeDeleted is not null)
                    {
                        provider.GetRequiredService<IStorageEngine>().DatabaseDeleted += beforeDeleted;
                    }

                    return ActivatorUtilities.CreateInstance<WanPuller>(provider, clock);
                });
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<WanMetrics>(provider, clock));
        }, frozen, managementCredential: true);
    }

    private sealed class OpeningJournal(IReplicationJournal inner, Func<IDatabaseStore, CancellationToken, ValueTask> afterOpened) : IReplicationJournal
    {
        public async ValueTask<(IDatabaseStore Store, ReadOnlyMemory<byte> Receiver, long Applied)> OpenAsync(
            TenantContext database, ReadOnlyMemory<byte> origin, CancellationToken cancellationToken)
        {
            var opened = await inner.OpenAsync(database, origin, cancellationToken);
            await afterOpened(opened.Store, cancellationToken);
            return opened;
        }

        public ValueTask<JournalAppend> AppendAsync(
            IDatabaseStore store, ReadOnlyMemory<byte> origin, long sequence, CapturedGroup group, CancellationToken cancellationToken) =>
            inner.AppendAsync(store, origin, sequence, group, cancellationToken);
    }

    // The first round reads this region's catalog page before it asks for heads. With one storage slot, startup work can
    // refuse that read: the round fails before heads and pauses, and the next one asks.
    private static async Task HeadsAskedAsync(WanRecorder recorder, ProgressClock clock, CancellationToken token)
    {
        for (var round = 0; round < 3; round++)
        {
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pause = clock.PausedAsync(reading.Token);
            if (await Task.WhenAny(recorder.Asked.Task, pause).WaitAsync(TimeSpan.FromSeconds(30), token) == recorder.Asked.Task)
            {
                await reading.CancelAsync();
                try
                {
                    await pause;
                }
                catch (OperationCanceledException) when (reading.IsCancellationRequested)
                {
                    // Join the unused read so it cannot take the pause of the round that pulls.
                }

                return;
            }

            await pause;
            clock.Advance(WanPuller.Interval);
        }

        throw new TimeoutException("the target never asked for heads");
    }

    // One organization and project registered in both regions, then ten databases at home in gru placed in sfo, each with
    // one document, sorted as their heads are: a held head, the most targets a queue prepares ahead of it, and one more.
    private static async Task<(List<ManagedDatabaseRef> Databases, Uri[] Documents, string[] Values, string[] Versions)> SeedAsync(
        ManagementApi gru, ManagementApi sfo, CancellationToken token)
    {
        var faker = new Faker();
        var organization = EntityIds.New().ToString();
        var project = EntityIds.New().ToString();
        var organizationName = faker.Company.CompanyName();
        var projectName = faker.Random.AlphaNumeric(12);
        foreach (var api in new[] { gru, sfo })
        {
            using var service = api.Service(api.User);
            using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
            {
                id = EntityIds.New().ToString(),
                ops = new object[]
                {
                    new { op = "create_organization", id = organization, name = organizationName },
                    new { op = "create_project", organization, id = project, name = projectName },
                },
            }, token);
            registered.EnsureSuccessStatusCode();
        }

        var databases = await CreatePlacedAsync(gru, organization, project,
            Enumerable.Range(0, 10).Select(index => "prepare-" + index.ToString(CultureInfo.InvariantCulture)), token);
        // Within one project, canonical ULID text orders like the heads' binary ids.
        databases.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        var documents = databases.Select(database => ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "saved"))).ToArray();
        var values = databases.Select(_ => faker.Lorem.Sentence()).ToArray();
        var versions = new string[databases.Count];
        using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
        for (var index = 0; index < databases.Count; index++)
        {
            await ManagementApi.CreateCollectionAsync(source, databases[index], "items", token);
            using var written = await source.PutAsJsonAsync(documents[index], new { value = values[index] }, token);
            await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
            versions[index] = written.Headers.ETag!.Tag;
        }

        return (databases, documents, values, versions);
    }

    // With one storage slot a usage flush can refuse a foreground read: retry only that backpressure.
    private static Task AssertAdmittedReadAsync(HttpClient client, Uri document, string value, string version) =>
        TerraBaseTestServer.EventuallyAsync(async probe =>
        {
            using var response = await client.GetAsync(document, probe);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return false;
            }

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Headers.ETag?.Tag).IsEqualTo(version);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(probe);
            await Assert.That(body.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
            return true;
        });

    // Failures of every reason: a round that pulled into a closed store fails a pull and heals only in a later round. Tests
    // compare against the count once heads are asked, after any startup round HeadsAskedAsync retried.
    private static async Task<double> FailuresAsync(ManagementApi api, CancellationToken token)
    {
        try
        {
            return await api.Server.MetricAsync("terrabase_wan_failures_total", GruLabel, token);
        }
        catch (InvalidOperationException)
        {
            // Prometheus exposes the counter only after its first failure.
            return 0;
        }
    }

    // The target's database lifecycle steps in the order they ran; the creation of Held waits at create.prepare, before it
    // takes any storage admission, until Release. Waits complete on the global thread-pool queue: the step that completes
    // one may block its own thread next.
    private sealed class LifecycleSteps : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<(string Step, string Database)> _steps = [];
        private readonly List<(Func<List<(string Step, string Database)>, bool> Reached, TaskCompletionSource Signal)> _waiters = [];
        private readonly ManualResetEventSlim _release = new();

        // Set before the first heads arrive, so no creation can miss it.
        public string? Held
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public bool HoldExpired
        {
            get => Volatile.Read(ref field);
            private set => Volatile.Write(ref field, value);
        }

        public List<(string Step, string Database)> Steps
        {
            get
            {
                lock (_gate)
                {
                    return [.. _steps];
                }
            }
        }

        public void Record(string step, TenantContext database)
        {
            if (step is not ("create.prepare" or "create.committed" or "create.registered" or "wan.targeted") || database.DatabaseId is not { } id)
            {
                return;
            }

            Add(step, id);
            // Bounded, so a test that never releases cannot strand the host's thread.
            if (step == "create.prepare" && id == Held && !_release.Wait(TimeSpan.FromSeconds(30)))
            {
                HoldExpired = true;
            }
        }

        // Marks the release in the step order, then lets the held creation continue.
        public void Release()
        {
            if (!_release.IsSet)
            {
                Add("test.released", Held ?? "");
                _release.Set();
            }
        }

        public Task WaitAsync(Func<List<(string Step, string Database)>, bool> reached, CancellationToken token)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (reached(_steps))
                {
                    return Task.CompletedTask;
                }

                _waiters.Add((reached, signal));
            }

            return signal.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }

        public void Dispose() => _release.Dispose();

        private void Add(string step, string database)
        {
            List<TaskCompletionSource> reached = [];
            lock (_gate)
            {
                _steps.Add((step, database));
                _ = _waiters.RemoveAll(waiter =>
                {
                    if (!waiter.Reached(_steps))
                    {
                        return false;
                    }

                    reached.Add(waiter.Signal);
                    return true;
                });
            }

            foreach (var signal in reached)
            {
                _ = ThreadPool.QueueUserWorkItem(static pending => pending.TrySetResult(), signal, preferLocal: false);
            }
        }
    }

    // The target's bus: tells when its puller first asks for heads, and records the database of every pull it sends.
    private sealed class WanRecorder(IRegionBus inner) : IRegionBus
    {
        private readonly ConcurrentQueue<ByteString> _pulled = new();

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ByteString[] Pulled => [.. _pulled];

        public Func<CancellationToken, ValueTask>? BeforeHeadsReply
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public Func<PullRequest, CancellationToken, ValueTask>? BeforePullReply
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(
            string subject, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Asked.TrySetResult();
            await foreach (var reply in inner.RequestManyAsync(subject, request, cancellationToken))
            {
                if (BeforeHeadsReply is { } before)
                {
                    await before(cancellationToken);
                }

                yield return reply;
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            var pull = WanRequest.Parser.ParseFrom(request.Span).Pull;
            if (pull is not null)
            {
                _pulled.Enqueue(pull.DatabaseId);
            }

            var reply = await inner.RequestAsync(subject, request, cancellationToken);
            if (pull is not null && BeforePullReply is { } before)
            {
                await before(pull, cancellationToken);
            }

            return reply;
        }

        public Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken) =>
            inner.ServeAsync(subject, answer, cancellationToken);
    }
}
