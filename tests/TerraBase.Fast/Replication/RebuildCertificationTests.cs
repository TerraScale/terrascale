using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Certified refill of a rebuilt copy (docs/terrabase/decisions/0013a, section 1) on real hosts: a copy fault recorded in
/// the engine's ledger wakes the production <see cref="RebuildRecovery"/> worker, whose <see cref="ReplicaRebuild"/> refills
/// the new incarnation through <see cref="RebuildCertification"/> over the real peer listener, salvages the side copy and
/// passes the FULL completion barrier. Source/wire decorators inject faults; a budget decorator holds real pressure
/// leases at receiver admission. Certification, repair, storage and peer listeners remain the production implementations.
/// </summary>
public sealed class RebuildCertificationTests
{
    private const string NoRefill = "no certified refill from an intact source";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static CancellationToken TestToken => TUnit.Core.TestContext.Current!.Execution.CancellationToken;

    [Test]
    public async Task An_authenticated_owner_retries_certified_recovery_without_reviving_a_deleted_document_and_cannot_replay_completion()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var deleted = EntityIds.New().ToString();
        await pair.Gru.PutAsync(deleted, "{\"n\":5}");
        await Assert.That(await pair.Sjc.UntilAsync(deleted, "{\"n\":5}")).IsTrue();
        await pair.Gru.WriteAsync(async transaction => { _ = await transaction.DeleteAsync("items", deleted, pair.Token); });
        await pair.WaitAsync(async _ => await pair.Sjc.ReadAsync(deleted) is null);
        var faulted = await pair.Sjc.IncarnationAsync();
        pair.Gru.Source.Arm(SourceFault.OmitsState, afterPage: null, omittedId: kept);
        await Assert.That(await pair.Sjc.FaultAsync(faulted)).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        var rebuilt = await pair.Sjc.SwappedAsync(faulted);
        pair.Gru.Source.Disarm();
        await pair.Sjc.Api.VerifyCatalogAsync(pair.Token);
        var peer = (await pair.Members.ListAsync(pair.Token)).Single(member => member.Name == pair.Gru.Name);
        await Assert.That(await pair.Sjc.Service<AntiEntropy>().PullAsync(ChangeGroups.System, peer, pair.Token)).IsNotNull();
        var gate = pair.Sjc.Service<ICatalogGate>();
        var digest = await gate.DigestAsync(pair.Token);
        using var owner = pair.Sjc.Api.Service(pair.User);
        var path = ManagementApi.Uri($"/v1/management/organizations/{pair.Database.OrganizationId}/projects/{pair.Database.ProjectId}/databases/{pair.Database.DatabaseId}/recovery/retry");
        var request = new { faultedIncarnation = faulted.ToString(), reason = "Healthy peer restored after a refused certified refill" };

        using var response = await owner.PostAsJsonAsync(path, request, pair.Token);

        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NoContent);
        await Assert.That(await pair.Sjc.IncarnationAsync()).IsEqualTo(rebuilt);
        await pair.Sjc.CertifiedAsync(faulted, rebuilt, pair.Gru);
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("{\"n\":2}");
        await Assert.That(await pair.Sjc.ReadAsync(deleted)).IsNull();
        var attempts = pair.Sjc.Rebuilds.Outcomes.Count;
        using var replay = await owner.PostAsJsonAsync(path, request, pair.Token);
        await Assert.That(replay.StatusCode).IsEqualTo(System.Net.HttpStatusCode.Conflict);
        await Assert.That(pair.Sjc.Rebuilds.Outcomes.Count).IsEqualTo(attempts);
        await Assert.That(gate.IsOpen && gate.Trusted).IsTrue();
        await Assert.That(await gate.DigestAsync(pair.Token)).IsEqualTo(digest);
    }

    /// <summary>How the source misbehaves while a refill reads it.</summary>
    public enum SourceFault
    {
        /// <summary>A write lands after every refill page, including refreshes: the newest digest never equals every bucket.</summary>
        Writes,

        /// <summary>The pinned incarnation is no longer the source's copy: its pinned read is refused.</summary>
        Replaced,

        /// <summary>A page leaves out a state its digest counts.</summary>
        OmitsState,

        /// <summary>The digests do not match the states the source streams.</summary>
        DigestDiffers,

        /// <summary>The first stream ends at its deadline after delivering a nonempty page.</summary>
        StreamDeadline,

        /// <summary>Every stream ends at its deadline before delivering any state.</summary>
        NoProgressDeadline,
    }

    [Test]
    public async Task A_faulted_copy_is_refilled_from_a_pinned_source_keeps_its_unique_verified_state_and_certifies()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        // Production's 512 MB machine admits 128 MB process memory and an 8 MB Hobby foreground share.
        using var refusedForeground = pair.Sjc.Service<IMemoryBudget>()
            .TryReserve(MemoryLane.Foreground, 3L * 4096 * 16 + 4L * ReplicationProtocol.MaxApplicationBytes, pair.Database);
        await Assert.That(refusedForeground).IsNull();
        // A document only gru holds: once sjc's faulted copy goes aside, only the refill can bring it.
        var refilled = EntityIds.New().ToString();
        await pair.Gru.PutAsync(refilled, """{"n":3}""");
        await Assert.That(await pair.Sjc.UntilAsync(refilled, """{"n":3}""")).IsTrue();
        await pair.Sjc.LoseAsync(refilled);
        var faulted = await pair.Sjc.IncarnationAsync();

        var outcome = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        var rebuilt = ((RebuildOutcome.Completed)outcome).NewIncarnation;
        await Assert.That(rebuilt).IsNotEqualTo(faulted);
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""").Because("a verified state only the side copy held survives");
        await Assert.That(await pair.Sjc.ReadAsync(refilled)).IsEqualTo("""{"n":3}""").Because("only the refill held it");
        await pair.Sjc.CertifiedAsync(faulted, rebuilt, pair.Gru);

        // The certified copy is a live replica again: gru's next write streams into the new incarnation.
        var after = EntityIds.New().ToString();
        await pair.Gru.PutAsync(after, """{"n":4}""");
        await Assert.That(await pair.Sjc.UntilAsync(after, """{"n":4}""")).IsTrue();
    }

    [Test]
    public async Task A_certified_rebuild_invalidates_a_client_document_cached_before_the_copy_fault()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, _) = await pair.SeedAsync();
        pair.Members.Map = (viewer, member) => viewer == pair.Sjc.Name && member.Name == pair.Gru.Name
            ? member with { PeerAddress = new UriBuilder(member.PeerAddress) { Port = 1 }.Uri.ToString() }
            : member;
        await pair.Sjc.RefreshMembershipAsync();
        await pair.WaitAsync(_ => Task.FromResult(pair.Sjc.Service<PeerStreams>().Snapshot().Peers.Count == 0));
        using var data = await pair.Gru.Api.CreateClientAsync(pair.Database.OrganizationId, "documents:read");
        var path = new Uri($"/v1/projects/{pair.Database.ProjectId}/databases/main/collections/items/documents/by-key?id={kept}", UriKind.Relative);
        using (var before = await data.GetAsync(path, pair.Token))
        {
            await Assert.That(before.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await before.Content.ReadAsStringAsync(pair.Token));
            await Assert.That(body.RootElement.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        }

        var oldState = await pair.Gru.StateAsync(kept);
        var faulted = await pair.Gru.IncarnationAsync();
        var rebuilds = pair.Gru.Rebuilds;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rebuilds.Gate = gate;
        try
        {
            await pair.Gru.Service<IReplicaCopyFaults>().RecordAsync(pair.Database, faulted, "a test fault", pair.Token);
            await rebuilds.Entered.Task.WaitAsync(Deadline, pair.Token);
            await pair.Sjc.PutAsync(kept, """{"n":2}""");
            var updated = await pair.Sjc.StateAsync(kept);
            await Assert.That(updated.State.MaxStamp.Value).IsGreaterThan(oldState.State.MaxStamp.Value);
            gate.SetResult();
            await pair.WaitAsync(_ => Task.FromResult(rebuilds.Outcomes.Count > 0));
            var outcome = rebuilds.Outcomes[0];
            await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
            await Assert.That(await pair.Gru.ReadAsync(kept)).IsEqualTo("""{"n":2}""");

            using var after = await data.GetAsync(path, pair.Token);
            await Assert.That(after.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
            using var result = JsonDocument.Parse(await after.Content.ReadAsStringAsync(pair.Token));
            await Assert.That(result.RootElement.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(2)
                .Because("the certified replacement contains the newer state, including for a previously cached client GET");
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Test]
    public async Task A_retained_materialized_document_without_its_state_refuses_certification_and_keeps_the_side_copy()
    {
        await using var pair = await Pair.StartAsync();
        var (_, only) = await pair.SeedAsync();
        await Assert.That(await pair.Gru.ReadAsync(only)).IsNull();
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""");
        var faulted = await pair.Sjc.IncarnationAsync();
        var rebuilds = pair.Sjc.Rebuilds;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rebuilds.Gate = gate;
        try
        {
            await pair.Sjc.Service<IReplicaCopyFaults>().RecordAsync(pair.Database, faulted, "a test fault", pair.Token);
            await rebuilds.Entered.Task.WaitAsync(Deadline, pair.Token);
            await pair.Sjc.RemoveStateKeepingDocumentAsync(only);
            await Assert.That(await pair.Gru.ReadAsync(only)).IsNull();
            gate.SetResult();
            await pair.WaitAsync(_ => Task.FromResult(rebuilds.Outcomes.Count > 0));

            var outcome = rebuilds.Outcomes[0];
            await Assert.That(outcome is RebuildOutcome.Refused).IsTrue().Because(outcome.ToString());
            await Assert.That(((RebuildOutcome.Refused)outcome).Reason.Length).IsGreaterThan(0);
            _ = await pair.Sjc.RetainedRefusalAsync(faulted, pair.Gru.Name);
            await pair.Sjc.AssertNoSalvageFilesAsync(faulted);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Test]
    public async Task A_live_batch_refused_while_the_copy_is_faulted_streams_into_the_certified_copy()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var refusedBefore = Volatile.Read(ref pair.Sjc.Buckets.RefusedPushes);
        var faulted = await pair.Sjc.IncarnationAsync();
        var rebuilds = pair.Sjc.Rebuilds;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rebuilds.Gate = release;
        await pair.Sjc.Service<IReplicaCopyFaults>().RecordAsync(pair.Database, faulted, "a test fault", pair.Token);
        await rebuilds.Entered.Task.WaitAsync(Deadline, pair.Token);

        // gru's live batch meets the faulted copy in place: sjc refuses it, and gru's cursor waits out its retry.
        var during = EntityIds.New().ToString();
        await pair.Gru.PutAsync(during, """{"n":5}""");
        await pair.WaitAsync(_ => Task.FromResult(Volatile.Read(ref pair.Sjc.Buckets.RefusedPushes) > refusedBefore));
        rebuilds.Gate = null;
        release.SetResult();
        await pair.WaitAsync(_ => Task.FromResult(rebuilds.Outcomes.Count > 0));

        var outcome = rebuilds.Outcomes[0];
        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await pair.Sjc.CertifiedAsync(faulted, ((RebuildOutcome.Completed)outcome).NewIncarnation, pair.Gru);
        var after = EntityIds.New().ToString();
        await pair.Gru.PutAsync(after, """{"n":4}""");
        await Assert.That(await pair.Sjc.UntilAsync(after, """{"n":4}""")).IsTrue();
    }

    [Test]
    [Arguments("h1", "blob", false)]
    [Arguments("h2", "blob", false)]
    [Arguments("h1", "text", false)]
    [Arguments("h2", "text", false)]
    [Arguments("h1", "large-blob", false)]
    [Arguments("h2", "large-blob", false)]
    [Arguments("h1", "blob", true)]
    [Arguments("h2", "blob", true)]
    [Arguments("h1", "text", true)]
    [Arguments("h2", "text", true)]
    [Arguments("h1", "large-blob", true)]
    [Arguments("h2", "large-blob", true)]
    [Arguments("h1", "healthy", false)]
    public async Task Retained_hash_types_skip_their_state_under_real_pressure_complete_the_rebuild_and_release_leases(
        string column, string damage, bool additional)
    {
        const long headroom = 8L * 1024 * 1024 + 64 * 1024;
        const int largeHashBytes = 16 * 1024 * 1024;
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var original = await pair.Sjc.IncarnationAsync();
        var damagedIncarnation = original;
        var damagedId = only;
        if (additional)
        {
            pair.Members.Map = (viewer, member) => viewer == pair.Sjc.Name && member.Name == pair.Gru.Name
                ? member with { PeerAddress = new UriBuilder(member.PeerAddress) { Port = 1 }.Uri.ToString() }
                : member;
            await pair.Sjc.RefreshMembershipAsync();
            await Assert.That(await pair.Sjc.FaultAsync(original)).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
            damagedIncarnation = await pair.Sjc.SwappedAsync(original);
            damagedId = EntityIds.New().ToString();
            var seed = await pair.Gru.StateAsync(kept);
            await Assert.That(await pair.Sjc.Service<IRebuildRepair>().RepairRebuildAsync(pair.Database,
                damagedIncarnation, [new RepairState(seed.Collection, damagedId, seed.State)], pair.Token)).IsEqualTo(1);
        }

        // Hold the real worker behind the copy-fault guard before editing the file that will go aside.
        var rebuilds = pair.Sjc.Rebuilds;
        var before = rebuilds.Outcomes.Count;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rebuilds.Gate = release;
        Dictionary<string, byte[]>? additionalBytes = null;
        IMemoryReservation? pressure = null;
        var available = 0L;
        pair.AfterRefill = async () =>
        {
            if (additional)
            {
                additionalBytes = await pair.Sjc.SideBytesAsync(damagedIncarnation);
            }
            lock (pair.Sjc.Admission.Gate)
            {
                var probe = pair.Sjc.Admission;
                pressure = pair.Sjc.Service<IMemoryBudget>().TryReserve(MemoryLane.Bulk,
                    AdmissionProbe.BudgetBytes - probe.Reserved - headroom, tenant: null)
                    ?? throw new InvalidOperationException("the real budget did not admit salvage pressure");
                available = AdmissionProbe.BudgetBytes - probe.Reserved;
                probe.SalvageActive = true;
            }
        };
        try
        {
            await pair.Sjc.Service<IReplicaCopyFaults>().RecordAsync(pair.Database, damagedIncarnation, "a retained hash fixture fault", pair.Token);
            await rebuilds.Entered.Task.WaitAsync(Deadline, pair.Token);
            if (damage != "healthy")
            {
                await pair.Sjc.CorruptHashAsync(damagedId, column, damage == "text" ? "not-an-integer"
                    : damage == "large-blob" ? new byte[largeHashBytes] : new byte[] { 1, 2, 3 });
            }
            pair.Members.Map = null;
            if (additional)
            {
                await pair.Sjc.RefreshMembershipAsync();
            }
            release.SetResult();
            await pair.WaitAsync(_ => Task.FromResult(rebuilds.Outcomes.Count > before));
            var outcome = rebuilds.Outcomes[before];
            await Assert.That(pressure).IsNotNull().Because("the intact source completed real pinned refill before salvage");
            await Assert.That(available).IsEqualTo(headroom);
            await Assert.That(available).IsLessThan(largeHashBytes);
            await Assert.That(pair.Sjc.Admission.SalvageMetadataRequests).IsGreaterThan(0);
            await Assert.That(pair.Sjc.Admission.SalvagePageRequests).IsGreaterThan(0);
            await Assert.That(pair.Sjc.Admission.SalvageDeniedRequests).IsEqualTo(0).Because("the small measured page is admitted despite the oversized hash");
            await Assert.That(pair.Sjc.Admission.SalvagePeak).IsGreaterThan(8L * 1024 * 1024).And.IsLessThanOrEqualTo(headroom);
            // The probe counts every Bulk lease from the salvage on: the completed rebuild's later ledger pages drain too.
            await pair.WaitAsync(_ => Task.FromResult(Volatile.Read(ref pair.Sjc.Admission.SalvageReserved) == 0));
            await Assert.That(Volatile.Read(ref pair.Sjc.Admission.SalvageReserved)).IsEqualTo(0).Because("both salvage leases release while pressure remains held");
            (pressure ?? throw new InvalidOperationException("salvage pressure was not reached")).Dispose();
            pressure = null;
            // A state whose retained hash does not verify is skipped (it is not trusted); every other one joins.
            await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
            await Assert.That(await pair.Sjc.ReadAsync(damagedId)).IsEqualTo(damage == "healthy" ? "{\"n\":2}" : null);
            await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo(damage != "healthy" && !additional ? null : "{\"n\":2}");
            await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("{\"n\":1}");
            await pair.Sjc.CertifiedAsync(original, ((RebuildOutcome.Completed)outcome).NewIncarnation, pair.Gru);
            await pair.Sjc.AssertNoSalvageFilesAsync(original);
            if (additional)
            {
                await pair.Sjc.AssertNoSalvageFilesAsync(damagedIncarnation);
            }
            await pair.AdmissionsReleasedAsync();
        }
        finally
        {
            release.TrySetResult();
            pair.AfterRefill = null;
            pressure?.Dispose();
        }
    }

    [Test]
    public async Task A_source_write_during_the_first_pass_is_covered_by_a_bounded_pinned_bucket_refresh()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var during = EntityIds.New().ToString();
        pair.Gru.Source.Arm(fault: null, afterPage: () => pair.Gru.PutAsync(during, "{\"n\":7}"));

        var outcome = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await Assert.That(pair.Gru.Source.Fired).IsTrue();
        await Assert.That(pair.Gru.Source.HookCalls).IsEqualTo(1).Because("the source write hook is one-shot");
        var requests = pair.Gru.Source.RefillRequests[0];
        await Assert.That(requests.Length).IsGreaterThanOrEqualTo(2).Because("the first-pass write requires another bucket request");
        await Assert.That(requests[0]).IsEqualTo(TursoStorageTransaction.Buckets);
        await Assert.That(requests[1]).IsGreaterThan(0).And.IsLessThan(TursoStorageTransaction.Buckets).Because("the second request refreshes a subset under the same pin");
        await Assert.That(await pair.Sjc.ReadAsync(during)).IsEqualTo("{\"n\":7}");
        await pair.Sjc.CertifiedAsync(faulted, ((RebuildOutcome.Completed)outcome).NewIncarnation, pair.Gru);
    }

    [Test]
    public async Task A_refill_stream_deadline_retries_only_uncovered_buckets_and_certifies_every_document()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var ids = Enumerable.Range(0, 1025).Select(_ => EntityIds.New().ToString()).ToArray();
        foreach (var chunk in ids.Chunk(256))
        {
            await pair.Gru.WriteAsync(async transaction =>
            {
                foreach (var id in chunk)
                {
                    _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes("""{"n":7}"""), pair.Token);
                }
            });
        }

        var faulted = await pair.Sjc.IncarnationAsync();
        pair.Gru.Source.Arm(SourceFault.StreamDeadline, afterPage: null);

        var outcome = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(pair.Gru.Source.Fired).IsTrue().Because("the first stream delivered a page before its injected deadline");
        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        var requests = pair.Gru.Source.RefillRequests[0];
        await Assert.That(requests.Length).IsEqualTo(2);
        await Assert.That(requests[0]).IsEqualTo(TursoStorageTransaction.Buckets);
        await Assert.That(requests[1]).IsGreaterThan(0);
        await Assert.That(requests[1]).IsLessThan(TursoStorageTransaction.Buckets);
        foreach (var id in ids)
        {
            await Assert.That(await pair.Sjc.ReadAsync(id)).IsEqualTo("""{"n":7}""");
        }

        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""");
        await pair.Sjc.CertifiedAsync(faulted, ((RebuildOutcome.Completed)outcome).NewIncarnation, pair.Gru);
        await pair.AdmissionsReleasedAsync();
    }

    [Test]
    public async Task Refill_stream_deadlines_without_progress_exhaust_the_bounded_rounds_and_keep_the_side_copy()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        pair.Gru.Source.Arm(SourceFault.NoProgressDeadline, afterPage: null);

        var outcome = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(outcome).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        await Assert.That(pair.Gru.Source.Fired).IsTrue();
        var requests = pair.Gru.Source.RefillRequests[0];
        await Assert.That(requests.Length).IsEqualTo(8);
        await Assert.That(requests[0]).IsEqualTo(TursoStorageTransaction.Buckets);
        await Assert.That(requests.Skip(1).All(count => count is > 0 and < TursoStorageTransaction.Buckets)).IsTrue();
        _ = await pair.Sjc.RetainedRefusalAsync(faulted);
        await pair.AdmissionsReleasedAsync();
    }

    // Liveness (RT2 review 2.1-5): a matched bucket stays matched, so a source written after every page, refreshes included,
    // still certifies; writes after a bucket's match reach the rebuilt copy by the stream, and it takes writes at once.
    [Test]
    public async Task A_source_written_after_every_refill_page_certifies_and_streams_each_write_into_the_rebuilt_copy()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var written = new ConcurrentQueue<string>();
        pair.Gru.Source.Arm(SourceFault.Writes, async () =>
        {
            var id = EntityIds.New().ToString();
            await pair.Gru.PutAsync(id, """{"n":9}""");
            written.Enqueue(id);
        });

        var outcome = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await Assert.That(pair.Gru.Source.HookCalls).IsGreaterThanOrEqualTo(2).Because("the source was written during the first pass and its refresh");
        pair.Gru.Source.Disarm();
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""");
        await pair.Sjc.CertifiedAsync(faulted, ((RebuildOutcome.Completed)outcome).NewIncarnation, pair.Gru);
        foreach (var id in written)
        {
            await Assert.That(await pair.Sjc.UntilAsync(id, """{"n":9}""")).IsTrue().Because($"{id} was written during the refill");
        }
    }

    [Test]
    [Arguments(SourceFault.Replaced)]
    [Arguments(SourceFault.OmitsState)]
    [Arguments(SourceFault.DigestDiffers)]
    public async Task A_refill_from_a_source_that_changes_or_does_not_cover_its_digest_is_refused_keeps_the_record_and_side_copy_and_resumes(SourceFault fault)
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        pair.Gru.Source.Arm(fault, afterPage: null, omittedId: kept);

        var refused = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(refused).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        await Assert.That(pair.Gru.Source.Fired).IsTrue().Because("the refusal came from the injected source fault");
        if (fault is SourceFault.OmitsState or SourceFault.DigestDiffers)
        {
            var requests = pair.Gru.Source.RefillRequests[0];
            await Assert.That(requests.Length).IsEqualTo(8).Because("persistent mismatches exhaust the bounded refresh rounds");
            await Assert.That(requests[0]).IsEqualTo(TursoStorageTransaction.Buckets);
            await Assert.That(requests.Skip(1).All(count => count is > 0 and < TursoStorageTransaction.Buckets)).IsTrue();
        }

        var rebuilt = await pair.Sjc.SwappedAsync(faulted);

        pair.Gru.Source.Disarm();
        await Assert.That(await pair.Sjc.ResumeAsync(faulted)).IsEqualTo(rebuilt).Because("the resumed rebuild certifies the incarnation it swapped in");
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""");
        await pair.Sjc.CertifiedAsync(faulted, rebuilt, pair.Gru);
    }

    [Test]
    public async Task A_listed_source_that_cannot_be_reached_refuses_the_refill_and_the_rebuilt_copy_takes_repairs_only_for_its_exact_incarnation()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, _) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var source = await pair.Gru.DigestAsync();
        // sjc still lists gru, at a peer address nothing listens on.
        pair.Members.Map = (viewer, member) => viewer == pair.Sjc.Name && member.Name == pair.Gru.Name
            ? member with { PeerAddress = new UriBuilder(member.PeerAddress) { Port = 1 }.Uri.ToString() }
            : member;
        await pair.Sjc.RefreshMembershipAsync();

        var refused = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(refused).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        var rebuilt = await pair.Sjc.SwappedAsync(faulted);
        var page = new[] { await pair.Gru.StateAsync(kept) };
        var repair = pair.Sjc.Service<IRebuildRepair>();
        var none = pair.Token;
        await Assert.That(await repair.RepairRebuildAsync(pair.Database, faulted, page, none)).IsNull().Because("the faulted incarnation");
        await Assert.That(await repair.RepairRebuildAsync(pair.Database, Ulid.NewUlid(), page, none)).IsNull().Because("an incarnation not in place");
        await Assert.That(await repair.RecordRefillAsync(pair.Database, Ulid.NewUlid(), pair.Gru.Name, source.Incarnation, source.Head, source.CollectedBefore, none)).IsFalse();
        await Assert.That(await repair.CompleteRebuildAsync(pair.Database, faulted, rebuilt, none)).IsFalse().Because("no refill is recorded");
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await pair.Sjc.Service<IChangeGroupSink>().RepairAsync(pair.Database, rebuilt, page, none));
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsNull().Because("no refused write reached the rebuilt copy");
        await Assert.That(await repair.RepairRebuildAsync(pair.Database, rebuilt, page, none)).IsEqualTo(1);
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");

        pair.Members.Map = null;
        await pair.Sjc.RefreshMembershipAsync();
        await Assert.That(await pair.Sjc.ResumeAsync(faulted)).IsEqualTo(rebuilt);
        await pair.Sjc.CertifiedAsync(faulted, rebuilt, pair.Gru);
    }

    [Test]
    public async Task An_uncertain_replacement_is_retained_and_its_unique_verified_state_is_salvaged_into_another_incarnation()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, _) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        pair.Members.Map = (viewer, member) => viewer == pair.Sjc.Name && member.Name == pair.Gru.Name
            ? member with { PeerAddress = new UriBuilder(member.PeerAddress) { Port = 1 }.Uri.ToString() }
            : member;
        await pair.Sjc.RefreshMembershipAsync();
        await Assert.That(await pair.Sjc.FaultAsync(faulted)).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        var firstReplacement = await pair.Sjc.SwappedAsync(faulted);
        var unique = EntityIds.New().ToString();
        var sourceState = await pair.Gru.StateAsync(kept);
        var repair = new RepairState(sourceState.Collection, unique, sourceState.State);
        await Assert.That(await pair.Sjc.Service<IRebuildRepair>()
            .RepairRebuildAsync(pair.Database, firstReplacement, [repair], pair.Token)).IsEqualTo(1);

        await Assert.That(await pair.Sjc.FaultAsync(firstReplacement)).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        RebuildRecord? retained = null;
        await pair.WaitAsync(async token =>
        {
            retained = await pair.Sjc.Service<IRebuildLedger>().GetAsync(pair.Database, token);
            return retained is { Phase: RebuildPhase.Swapped } && retained.NewIncarnation != firstReplacement;
        });
        await Assert.That(retained!.AdditionalIncarnation).IsEqualTo(firstReplacement);
        await Assert.That(pair.Sjc.SideExists(faulted)).IsTrue();
        await Assert.That(pair.Sjc.SideExists(firstReplacement)).IsTrue();

        pair.Members.Map = null;
        await pair.Sjc.RefreshMembershipAsync();
        var rebuilt = await pair.Sjc.ResumeAsync(faulted);
        await Assert.That(rebuilt).IsNotEqualTo(firstReplacement);
        await Assert.That(await pair.Sjc.ReadAsync(unique)).IsEqualTo("""{"n":1}""");
        await pair.Sjc.CertifiedAsync(faulted, rebuilt, pair.Gru);
        await Assert.That(pair.Sjc.SideExists(firstReplacement)).IsFalse();
    }

    [Test]
    public async Task A_rebuilt_copy_whose_persisted_incarnation_changes_while_refill_streams_refuses_and_keeps_the_side_copy()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        // The cached receiver identity stays unchanged: only the same-writer persisted identity check can refuse this page.
        var changed = Ulid.NewUlid();
        pair.Gru.Source.Arm(fault: null, afterPage: () => pair.Sjc.ChangePersistedIncarnationAsync(changed));

        var refused = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(refused).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        await Assert.That(pair.Gru.Source.Fired).IsTrue();
        var record = await pair.Sjc.Service<IRebuildLedger>().GetAsync(pair.Database, pair.Token);
        await Assert.That(record?.Phase).IsEqualTo(RebuildPhase.Swapped);
        await Assert.That(await pair.Sjc.PersistedIncarnationAsync()).IsEqualTo(changed);
        await Assert.That(await pair.Sjc.InPlaceAsync()).IsEqualTo(record!.NewIncarnation).Because("the cached identity cannot authorize the repair");
        await Assert.That(pair.Sjc.SideExists(faulted)).IsTrue();
        await Assert.That(await pair.Sjc.RefillSourceAsync()).IsNull();
    }

    [Test]
    public async Task A_refill_page_that_decodes_past_two_megabytes_within_the_protocol_bound_certifies_and_keeps_every_document()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        // Larger documents fill the same 2 MiB page with fewer writes; keys and framing exceed that bound below.
        var json = $$"""{"v":"{{new string('v', 8192)}}"}""";
        var ids = Enumerable.Range(0, 256).Select(index => $"{EntityIds.New()}-{index:D4}-{new string('k', 968)}").ToArray();
        foreach (var chunk in ids.Chunk(64))
        {
            await pair.Gru.WriteAsync(async transaction =>
            {
                foreach (var id in chunk)
                {
                    _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes(json), pair.Token);
                }
            });
        }

        var faulted = await pair.Sjc.IncarnationAsync();

        var outcome = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(outcome is RebuildOutcome.Completed).IsTrue().Because(outcome.ToString());
        await Assert.That(pair.Gru.Source.LargestPage).IsGreaterThan(ChangeGroups.MaxBatchBytes).Because("the page the receiver decoded");
        await Assert.That(pair.Gru.Source.LargestPage).IsLessThanOrEqualTo(ReplicationProtocol.MaxApplicationBytes);
        var rebuilt = ((RebuildOutcome.Completed)outcome).NewIncarnation;
        foreach (var id in ids)
        {
            await Assert.That(await pair.Sjc.ReadAsync(id)).IsEqualTo(json);
        }

        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("""{"n":1}""");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("""{"n":2}""");
        await pair.Sjc.CertifiedAsync(faulted, rebuilt, pair.Gru);
    }

    [Test]
    public async Task A_faulted_or_swapped_but_uncertified_copy_answers_no_digest_and_refuses_pinned_buckets_over_the_peer_listener()
    {
        await using var pair = await Pair.StartAsync();
        _ = await pair.SeedAsync();
        var held = await pair.Gru.IncarnationAsync();
        var address = (await pair.Members.ListAsync(pair.Token)).Single(member => member.Name == pair.Gru.Name).PeerAddress;
        using var channel = PeerChannel.Create(new Uri(address), PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        await AnswersAsync(client, pair.Database, held, held: true);

        // gru's copy faults; its rebuild waits, so the faulted file stays in place behind the source's fault guard. gru
        // cannot reach sjc, so once released its rebuild swaps and stays uncertified.
        var rebuilds = pair.Gru.Rebuilds;
        rebuilds.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Members.Map = (viewer, member) => viewer == pair.Gru.Name && member.Name == pair.Sjc.Name
            ? member with { PeerAddress = new UriBuilder(member.PeerAddress) { Port = 1 }.Uri.ToString() }
            : member;
        await pair.Gru.RefreshMembershipAsync();
        await pair.Gru.Service<IReplicaCopyFaults>().RecordAsync(pair.Database, held, "a test fault", pair.Token);
        await rebuilds.Entered.Task.WaitAsync(Deadline, pair.Token);
        await Assert.That(await pair.Gru.InPlaceAsync()).IsEqualTo(held).Because("the faulted file is still in place");
        await AnswersAsync(client, pair.Database, held, held: false);

        rebuilds.Gate.SetResult();
        await pair.WaitAsync(_ => Task.FromResult(rebuilds.Outcomes.Count > 0));
        await Assert.That(rebuilds.Outcomes[0]).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        var swapped = await pair.Gru.SwappedAsync(held);
        await AnswersAsync(client, pair.Database, swapped, held: false);
    }

    [Test]
    [Arguments(false, false, "document")]
    [Arguments(true, false, "document")]
    [Arguments(false, true, "document")]
    [Arguments(true, true, "document")]
    [Arguments(false, false, "query")]
    [Arguments(true, false, "query")]
    [Arguments(false, true, "query")]
    [Arguments(true, true, "query")]
    [Arguments(false, false, "collection")]
    [Arguments(false, true, "collection")]
    [Arguments(false, false, "collections")]
    [Arguments(false, true, "collections")]
    [Arguments(false, false, "schema")]
    [Arguments(false, true, "schema")]
    [Arguments(false, false, "changes")]
    [Arguments(false, true, "changes")]
    public async Task Client_reads_refuse_faulted_and_swapped_uncertified_copies(bool cached, bool swapped, string route)
    {
        await using var pair = await Pair.StartAsync();
        var (kept, _) = await pair.SeedAsync();
        using var data = await pair.Gru.Api.CreateClientAsync(pair.Database.OrganizationId, "documents:read");
        var collections = $"/v1/projects/{pair.Database.ProjectId}/databases/main/collections";
        var path = new Uri(route switch
        {
            "document" => $"{collections}/items/documents/by-key?id={kept}",
            "query" => $"{collections}/items/documents",
            "collection" => $"{collections}/items",
            "collections" => collections,
            "schema" => $"{collections}/items/schema",
            "changes" => $"/v1/projects/{pair.Database.ProjectId}/databases/main/changes?cursor=now",
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        }, UriKind.Relative);
        if (cached)
        {
            using var before = await data.GetAsync(path, pair.Token);
            await Assert.That(before.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        }

        var held = await pair.Gru.IncarnationAsync();
        var rebuilds = pair.Gru.Rebuilds;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rebuilds.Gate = gate;
        pair.Members.Map = (viewer, member) => viewer == pair.Gru.Name && member.Name == pair.Sjc.Name
            ? member with { PeerAddress = new UriBuilder(member.PeerAddress) { Port = 1 }.Uri.ToString() }
            : member;
        await pair.Gru.RefreshMembershipAsync();
        try
        {
            await pair.Gru.Service<IReplicaCopyFaults>().RecordAsync(pair.Database, held, "a test fault", pair.Token);
            await rebuilds.Entered.Task.WaitAsync(Deadline, pair.Token);
            if (swapped)
            {
                gate.SetResult();
                await pair.WaitAsync(_ => Task.FromResult(rebuilds.Outcomes.Count > 0));
                await Assert.That(rebuilds.Outcomes[0]).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
                _ = await pair.Gru.SwappedAsync(held);
            }
            else
            {
                await Assert.That(await pair.Gru.InPlaceAsync()).IsEqualTo(held);
                await Assert.That(await pair.Gru.Service<IReplicaCopyFaults>().RefusedAsync(pair.Database, held, pair.Token)).IsTrue();
            }

            using var response = await data.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, pair.Token);
            await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync(pair.Token);
            using var error = JsonDocument.Parse(body);
            await Assert.That(error.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("copy_faulted");
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_source_page_with_a_decodable_state_that_disagrees_with_its_stored_digest_faults_before_it_is_served(bool consistentMaterialization)
    {
        await using var pair = await Pair.StartAsync();
        var (kept, _) = await pair.SeedAsync();
        var incarnation = await pair.Gru.IncarnationAsync();
        pair.Gru.Rebuilds.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (consistentMaterialization)
        {
            await pair.Gru.CorruptStateAsync(kept);
        }
        else
        {
            await pair.Gru.CorruptDocumentAsync(kept);
        }
        var address = (await pair.Members.ListAsync(pair.Token)).Single(member => member.Name == pair.Gru.Name).PeerAddress;
        using var channel = PeerChannel.Create(new Uri(address), PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var request = new BucketRequest { Database = ChangeGroups.Wire(pair.Database, incarnation) };
        request.Buckets.AddRange(Enumerable.Range(0, TursoStorageTransaction.Buckets).Select(bucket => (uint)bucket));
        using var call = client.ReadBucket(request, deadline: DateTime.UtcNow + Deadline, cancellationToken: pair.Token);
        _ = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await call.ResponseStream.MoveNext(pair.Token))
            {
                throw new InvalidOperationException("the damaged source served a page");
            }
        });
        await Assert.That(await pair.Gru.Service<IReplicaCopyFaults>()
            .RefusedAsync(pair.Database, incarnation, pair.Token)).IsTrue();
        var row = await pair.Gru.Service<IRebuildLedger>().GetAsync(pair.Database, pair.Token);
        await Assert.That(row?.OldIncarnation).IsEqualTo(incarnation);
        await Assert.That(row?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(await pair.Gru.FaultReasonAsync()).IsEqualTo("a stored value is corrupt").Because("the keyed read check faulted the copy");
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("{\"n\":1}");
        await AnswersAsync(client, pair.Database, incarnation, held: false);
    }

    [Test]
    public async Task A_decodable_receiver_state_that_disagrees_with_its_stored_digest_faults_during_join_and_keeps_every_copy()
    {
        await using var pair = await Pair.StartAsync();
        var (kept, _) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var intact = await pair.Gru.StateAsync(kept);
        var source = await pair.Gru.IncarnationAsync();
        var repair = pair.Sjc.Service<IRebuildRepair>();
        var corrupted = Ulid.Empty;
        pair.Gru.Source.Arm(fault: null, afterPage: async () =>
        {
            corrupted = await pair.Sjc.IncarnationAsync();
            await Assert.That(corrupted).IsNotEqualTo(faulted);
            await Assert.That(await repair.RepairRebuildAsync(pair.Database, corrupted, [intact], pair.Token)).IsEqualTo(1);
            // Hold subsequent recovery attempts so the faulted receiver and Pending row remain observable.
            pair.Sjc.Rebuilds.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await pair.Sjc.CorruptDocumentAsync(kept);
        });

        var refused = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(refused).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        await Assert.That(pair.Gru.Source.HookCalls).IsEqualTo(1);
        await Assert.That(pair.Gru.Source.Fired).IsTrue();
        var row = await pair.Sjc.Service<IRebuildLedger>().GetAsync(pair.Database, pair.Token);
        await Assert.That(row?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(row!.OldIncarnation).IsEqualTo(faulted);
        await Assert.That(row.AdditionalIncarnation).IsEqualTo(corrupted).Because("the exact damaged receiver is retained for recovery");
        await Assert.That(row.NewIncarnation).IsNull();
        await Assert.That(await pair.Sjc.InPlaceAsync()).IsEqualTo(corrupted);
        await Assert.That(await pair.Sjc.Service<IReplicaCopyFaults>().RefusedAsync(pair.Database, corrupted, pair.Token)).IsTrue();
        await Assert.That(await pair.Sjc.CertificateAsync()).IsEmpty();
        await Assert.That(await pair.Sjc.RefillSourceAsync()).IsNull();
        await Assert.That(await repair.RepairRebuildAsync(pair.Database, corrupted, [intact], pair.Token)).IsNull();
        await Assert.That(await repair.RecordRefillAsync(pair.Database, corrupted, pair.Gru.Name, source, 0, default, pair.Token)).IsFalse();
        await Assert.That(await repair.CompleteRebuildAsync(pair.Database, faulted, corrupted, pair.Token)).IsFalse();
        var storage = pair.Sjc.Service<IRebuildStorage>();
        await Assert.That(await storage.FinalizeAsync(pair.Database, corrupted, storage.SidePath(pair.Database, faulted), pair.Token)).IsFalse();
        await Assert.That(pair.Sjc.SideExists(faulted)).IsTrue();
        await Assert.That(await pair.Sjc.InPlaceAsync()).IsEqualTo(corrupted).Because("the damaged receiver stays in place");
        await Assert.That(await pair.Gru.Service<IReplicaCopyFaults>().RefusedAsync(pair.Database, source, pair.Token)).IsFalse();
        await Assert.That(await pair.Gru.ReadAsync(kept)).IsEqualTo("{\"n\":1}");
    }

    /// <summary>The exact receiver reservation to exhaust, after the pass and source page are admitted.</summary>
    public enum ReceiverCapacity
    {
        /// <summary>The receiver's decoded-page reservation.</summary>
        Decode,

        /// <summary>The receiver's retained old-state reservation inside its production join.</summary>
        ReadState,
    }

    /// <summary>Wire pages whose aggregate XOR equals the original complete source page.</summary>
    public enum WireDisorder
    {
        /// <summary>The last key appears on three separate pages, so its aggregate XOR is unchanged.</summary>
        TripleDuplicate,

        /// <summary>Each page is sorted, but the last key is sent before the preceding keys.</summary>
        ReversedAcrossPages,
    }

    [Test]
    [Arguments(ReceiverCapacity.Decode)]
    [Arguments(ReceiverCapacity.ReadState)]
    public async Task Receiver_capacity_refusal_stops_before_a_second_eligible_real_source_and_releases_admission_before_retry(ReceiverCapacity point)
    {
        await using var pair = await Pair.StartAsync(secondSource: true);
        var (kept, only) = await pair.SeedAsync();
        var fallback = pair.SecondSource!;
        await Assert.That(await fallback.UntilAsync(kept, "{\"n\":1}")).IsTrue();
        await pair.EligibleSourcesAsync();
        await pair.AdmissionsReleasedAsync();
        var secondBefore = fallback.Buckets.DatabaseRequests;
        var faulted = await pair.Sjc.IncarnationAsync();
        var probe = pair.Sjc.Admission;
        string? historyId = null;
        pair.Gru.Source.BeforePage = async (states, token) =>
        {
            pair.Gru.Source.BeforePage = null;
            if (point == ReceiverCapacity.Decode)
            {
                var bytes = ApplicationEnvelope.Decode(AntiEntropy.Encode(states, await pair.Gru.IncarnationAsync()).States.Span,
                    ReplicationProtocol.MaxApplicationBytes).Length;
                probe.Exhaust(MemoryLane.Bulk, 16L * bytes);
                return;
            }

            // Seed genuine retained history in the new receiver through its production repair, before the source page
            // arrives. A preceding old row must be admitted before the large old row refuses in the same transaction.
            var target = states.Last(state => state.Collection == "items");
            historyId = target.Id;
            var preceding = states.TakeWhile(state => state != target).ToArray();
            await Assert.That(preceding.Length).IsGreaterThan(0);
            var history = target.State;
            var stamp = history.MaxStamp.Value;
            var origin = ReplicaId.Of(1, 7, 0);
            var faker = new Faker();
            for (var index = 0; index < 256; index++)
            {
                var element = JsonElement.Parse('"' + faker.Random.String2(1000, "abcdef") + index + '"');
                history = history.Add(new EntryVersion(new Hlc(++stamp), origin), "gone", element)
                    .Remove(new EntryVersion(new Hlc(++stamp), origin), "gone", element);
            }

            var receiver = await pair.Sjc.IncarnationAsync();
            var repair = pair.Sjc.Service<IRebuildRepair>();
            await Assert.That(await repair.RepairRebuildAsync(pair.Database, receiver,
                [.. preceding, target with { State = history }], token)).IsNotNull();
            var oldCharge = await pair.Sjc.StateReadChargeAsync(target.Collection, target.Id);
            await Assert.That(oldCharge).IsGreaterThan(1024 * 1024);
            await Assert.That(DocumentStateEncoding.Encode(target.State).Length).IsLessThan(1024);
            probe.Exhaust(MemoryLane.Foreground, oldCharge);
        };

        try
        {
            var refused = await pair.Sjc.FaultAsync(faulted);

            await Assert.That(refused).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
            await Assert.That(probe.DeniedRequests).IsEqualTo(1).Because("the real production budget refused the targeted receiver reservation");
            await Assert.That(probe.DeniedLane).IsEqualTo((MemoryLane?)(point == ReceiverCapacity.Decode ? MemoryLane.Bulk : MemoryLane.Foreground));
            await Assert.That(probe.PressureBytes).IsGreaterThan(0).Because("capacity came from a held real reservation");
            await Assert.That(probe.PassPeak).IsEqualTo(AdmissionProbe.PassBytes);
            await Assert.That(pair.Gru.Source.RefillRequests[0].Length).IsEqualTo(1);
            await Assert.That(pair.Gru.Buckets.CertificationRequests).IsEqualTo(1);
            await Assert.That(fallback.Buckets.CertificationRequests).IsEqualTo(0).Because("local receiver capacity stops all peers");
            await Assert.That(fallback.Source.RefillRequests[0]).IsEmpty();
            if (point == ReceiverCapacity.ReadState)
            {
                await Assert.That(probe.JoinHeldAtDenial).IsGreaterThan(0).Because("an earlier old row was admitted in the refusing transaction");
                await Assert.That(probe.JoinHeldAtReset).IsGreaterThan(0).Because("old-state leases survive rollback until actual writer disposal");
            }

            var rebuilt = await pair.Sjc.RetainedRefusalAsync(faulted);
            await pair.AdmissionsReleasedAsync();
            await Assert.That(fallback.Buckets.DatabaseRequests).IsEqualTo(secondBefore)
                .Because("the second real source received no watched bucket RPC, including outside the wrapper scope");
            await Assert.That(probe.PressureBytes).IsGreaterThan(0).Because("only the deliberate pressure lease remains");
            probe.RemovePressure();
            await Assert.That(probe.PressureBytes).IsEqualTo(0);
            await Assert.That(await pair.Sjc.ResumeAsync(faulted)).IsEqualTo(rebuilt);
            await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("{\"n\":1}");
            await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("{\"n\":2}");
            if (historyId is not null)
            {
                await Assert.That(await pair.Sjc.StateReadChargeAsync("items", historyId)).IsGreaterThan(1024 * 1024)
                    .Because("retry joined the small source state without dropping the old removed-dot history");
            }

            await pair.Sjc.CertifiedAsync(faulted, rebuilt, pair.Gru);
            await pair.AdmissionsReleasedAsync();
        }
        finally
        {
            pair.Gru.Source.BeforePage = null;
            probe.RemovePressure();
        }
    }

    [Test]
    [Arguments(WireDisorder.TripleDuplicate)]
    [Arguments(WireDisorder.ReversedAcrossPages)]
    public async Task Same_pin_pages_with_matching_XOR_but_duplicate_or_reversed_cross_page_keys_refuse_and_resume(WireDisorder disorder)
    {
        await using var pair = await Pair.StartAsync();
        var (kept, only) = await pair.SeedAsync();
        var faulted = await pair.Sjc.IncarnationAsync();
        var sourcePin = await pair.Gru.IncarnationAsync();
        pair.Gru.Buckets.Disorder = disorder;

        var refused = await pair.Sjc.FaultAsync(faulted);

        await Assert.That(refused).IsEqualTo(new RebuildOutcome.Refused(NoRefill));
        await Assert.That(pair.Gru.Buckets.MutatedStreams).IsEqualTo(1);
        await Assert.That(pair.Gru.Buckets.PreparedPages).IsEqualTo(disorder == WireDisorder.TripleDuplicate ? 3 : 2);
        await Assert.That(pair.Gru.Buckets.PreparedPin).IsEqualTo(sourcePin);
        await Assert.That(pair.Gru.Buckets.PreservesCoverage).IsTrue().Because("digest mismatch alone cannot reject these pages");
        await Assert.That(pair.Gru.Buckets.CertificationRequests).IsEqualTo(1).Because("ordering refuses before a digest refresh");
        var rebuilt = await pair.Sjc.RetainedRefusalAsync(faulted);
        await pair.AdmissionsReleasedAsync();

        pair.Gru.Buckets.Disorder = null;
        await Assert.That(await pair.Sjc.ResumeAsync(faulted)).IsEqualTo(rebuilt);
        await Assert.That(await pair.Sjc.ReadAsync(kept)).IsEqualTo("{\"n\":1}");
        await Assert.That(await pair.Sjc.ReadAsync(only)).IsEqualTo("{\"n\":2}");
        await pair.Sjc.CertifiedAsync(faulted, rebuilt, pair.Gru);
        await pair.AdmissionsReleasedAsync();
    }

    // What the real peer listener answers for `incarnation`: discovery and pinned digests held or not, and a pinned bucket
    // read that streams or fails with FailedPrecondition.
    private static async Task AnswersAsync(Features.Replication.V1.Replication.ReplicationClient client, TenantContext database, Ulid incarnation, bool held)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        stop.CancelAfter(Deadline);
        var deadline = DateTime.UtcNow + Deadline;
        var discovery = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database, null) }, deadline: deadline, cancellationToken: stop.Token);
        var pinned = await client.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database, incarnation) }, deadline: deadline, cancellationToken: stop.Token);
        await Assert.That(discovery.Held).IsEqualTo(held).Because("a discovery digest");
        await Assert.That(pinned.Held).IsEqualTo(held).Because("a digest pinned to the incarnation");
        if (held)
        {
            await Assert.That(new Ulid(discovery.Incarnation.Span)).IsEqualTo(incarnation);
        }

        var request = new BucketRequest { Database = ChangeGroups.Wire(database, incarnation) };
        request.Buckets.AddRange(Enumerable.Range(0, TursoStorageTransaction.Buckets).Select(bucket => (uint)bucket));
        async Task<int> ReadAsync()
        {
            using var call = client.ReadBucket(request, deadline: deadline, cancellationToken: stop.Token);
            var pages = 0;
            while (await call.ResponseStream.MoveNext(stop.Token))
            {
                pages++;
            }

            return pages;
        }

        if (held)
        {
            await Assert.That(await ReadAsync()).IsGreaterThan(0);
        }
        else
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => _ = await ReadAsync());
            await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        }
    }

    /// <summary>gru and sjc, each holding one database placed in both; sjc's copy faults and is rebuilt from gru.</summary>
    private sealed class Pair : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        public CancellationToken Token => _stop.Token;
        public FakeMemberStore Members { get; } = new();
        public string User { get; } = EntityIds.New().ToString();
        public Machine? SecondSource { get; private set; }
        public Dictionary<string, byte[]>? SideBeforeRefill { get; set; }
        public Func<Task>? AfterRefill { get; set; }

        public Task WaitAsync(Func<CancellationToken, Task<bool>> condition) =>
            TerraBaseTestServer.EventuallyAsync(_ =>
            {
                Token.ThrowIfCancellationRequested();
                return condition(Token);
            }, Deadline);

        public async Task EligibleSourcesAsync()
        {
            var view = await Sjc.Service<IMembership>().RefreshAsync(Token);
            var eligible = view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere)
                .Where(member => member.Name != Sjc.Name && member.PeerAddress.Length > 0 && member.Region == "gru").ToArray();
            await Assert.That(eligible.Select(member => member.Name).ToArray()).IsEquivalentTo([Gru.Name, SecondSource!.Name]);
            await Assert.That(eligible[0].Name).IsEqualTo(Gru.Name).Because("the first source is deterministic in the real membership view");
            foreach (var member in eligible)
            {
                var machine = member.Name == Gru.Name ? Gru : SecondSource!;
                using var channel = PeerChannel.Create(new Uri(member.PeerAddress), PeerTls.Credential);
                var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
                await AnswersAsync(client, Database, await machine.IncarnationAsync(), held: true);
            }
        }

        public Task AdmissionsReleasedAsync() => WaitAsync(_ => Task.FromResult(
            Sjc.Admission.Released && Gru.Admission.Released && Volatile.Read(ref Gru.Buckets.ActiveCertificationRequests) == 0
            && (SecondSource is null || SecondSource.Admission.Released && Volatile.Read(ref SecondSource.Buckets.ActiveCertificationRequests) == 0)));

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        public Machine Gru { get; private set; } = null!;

        public Machine Sjc { get; private set; } = null!;

        public static async Task<Pair> StartAsync(bool secondSource = false)
        {
            var pair = new Pair();
            pair._stop.CancelAfter(Deadline);
            try
            {
                pair.Gru = new Machine(pair, "gru", ["sjc"], "0");
                await pair.Gru.StartAsync();
                if (secondSource)
                {
                    pair.SecondSource = new Machine(pair, "gru", ["sjc"], "f");
                    await pair.SecondSource.StartAsync();
                }

                pair.Sjc = new Machine(pair, "sjc", ["gru"], "8");
                await pair.Sjc.StartAsync();
                foreach (var machine in new[] { pair.Gru, pair.SecondSource, pair.Sjc })
                {
                    if (machine is not null)
                    {
                        machine.Clock.Advance(DirectoryMembership.Heartbeat);
                        _ = await machine.Service<IMembership>().RefreshAsync(pair.Token);
                    }
                }

                return pair;
            }
            catch (Exception startupError)
            {
                try
                {
                    await pair.DisposeAsync();
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(startupError, cleanupError);
                }

                throw;
            }
        }

        // Three documents on both machines, then gru loses one behind the streams' back: only sjc's copy holds it.
        public async Task<(string Kept, string Only)> SeedAsync()
        {
            var (kept, also, only) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
            await Gru.WriteAsync(async transaction =>
            {
                _ = await transaction.CreateCollectionAsync(new CollectionInfo("items", 1), Token);
                _ = await transaction.PutAsync("items", kept, Encoding.UTF8.GetBytes("""{"n":1}"""), Token);
                _ = await transaction.PutAsync("items", also, Encoding.UTF8.GetBytes("""{"n":1}"""), Token);
                _ = await transaction.PutAsync("items", only, Encoding.UTF8.GetBytes("""{"n":2}"""), Token);
            });
            await Assert.That(await Sjc.UntilAsync(only, """{"n":2}""")).IsTrue();
            await Assert.That(await Sjc.UntilAsync(also, """{"n":1}""")).IsTrue();
            await Gru.LoseAsync(only);
            return (kept, only);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            var failures = new List<Exception>();
            foreach (var machine in new[] { Sjc, SecondSource, Gru })
            {
                if (machine is not null)
                {
                    try
                    {
                        await machine.DisposeAsync();
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }
            }

            _stop.Dispose();
            if (failures.Count > 0)
            {
                throw new AggregateException("certification fixture cleanup failed", failures);
            }
        }
    }

    private sealed class Machine(Pair pair, string region, string[] replicas, string namePrefix) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-certify-").FullName;
        private ManagementApi? _api;

        public string Name { get; } = namePrefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..13];
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public AdmissionProbe Admission { get; } = new();
        public BucketReads Buckets => Service<BucketReads>();

        public ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

        public Source Source => (Source)Service<IReplicaStates>();

        public Rebuilds Rebuilds => (Rebuilds)Service<IReplicaRebuild>();

        private TursoStorageEngine Engine => Service<TursoStorageEngine>();

        public T Service<T>()
            where T : notnull => Api.Server.Services.GetRequiredService<T>();

        public async Task StartAsync()
        {
            _api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = region;
                settings["node-name"] = Name;
                settings["data-directory"] = _directory;
                settings["memory-budget"] = (128L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }, services =>
            {
                services.AddSingleton<IMemberDirectory>(pair.Members);
                // Keep periodic anti-entropy and recovery retries from consuming this test's pinned refill hooks.
                // Durable fault wakes still run. Peer streams keep the system clock, as in production: a batch refused while
                // a copy is faulted retries after real time instead of waiting forever on this frozen clock.
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PeerStreams>(provider, TimeProvider.System));
                services.AddSingleton(Admission);
                services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
                {
                    if (database.DatabaseId == pair.Database.DatabaseId && step == "writer.reset")
                    {
                        Admission.NoteWriterReset();
                    }
                });
                var memory = services.Last(service => service.ServiceType == typeof(IMemoryBudget));
                services.AddSingleton<IMemoryBudget>(provider => new ObservedBudget(
                    (IMemoryBudget)(memory.ImplementationInstance ?? memory.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, memory.ImplementationType!)), Admission));
                var repair = services.Last(service => service.ServiceType == typeof(IRebuildRepair));
                services.AddSingleton<IRebuildRepair>(provider => new ObservedRepair(
                    (IRebuildRepair)(repair.ImplementationInstance ?? repair.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, repair.ImplementationType!)), Admission, pair.Database));
                services.AddSingleton<BucketReads>();
                services.AddGrpc(options => options.Interceptors.Add<BucketReads>());
                // What the peer listener and anti-entropy read: the production source (its copy-fault guard included), with a
                // fault the test may inject.
                var production = services.Last(service => service.ServiceType == typeof(IReplicaStates));
                services.AddSingleton<IReplicaStates>(provider => new Source(
                    (IReplicaStates)(production.ImplementationInstance ?? production.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, production.ImplementationType!)), pair.Database, provider.GetRequiredService<BucketReads>()));
                var certification = services.Last(service => service.ServiceType == typeof(IRebuildCertification));
                services.AddSingleton<IRebuildCertification>(provider => new Refills(
                    (IRebuildCertification)(certification.ImplementationInstance ?? certification.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, certification.ImplementationType!)), pair, this));
                // The worker's rebuild: the real one, its outcomes recorded.
                services.AddSingleton<IReplicaRebuild>(provider => new Rebuilds(ActivatorUtilities.CreateInstance<ReplicaRebuild>(provider), pair.Token));
                if (!services.Any(service => service.ImplementationType == typeof(RebuildRecovery)))
                {
                    services.AddHostedService<RebuildRecovery>();
                }
            }, cancellationToken: TestToken, userId: pair.User);
            Buckets.WatchedDatabaseId = pair.Database.DatabaseId
                ?? throw new InvalidOperationException("the certification fixture database ID is missing");
            Buckets.IsCertification = request => Source.IsRefill(request);
            var database = pair.Database;
            await CatalogFixture.RegisterAsync(Api, Api.User, database, "main", replicas);
        }

        public async Task RefreshMembershipAsync()
        {
            // DirectoryMembership caches its view for a heartbeat; Map changes alone leave the old peer address in use.
            Clock.Advance(DirectoryMembership.Heartbeat);
            _ = await Service<IMembership>().RefreshAsync(pair.Token);
        }

        // Records a copy fault of `incarnation` through the engine's ledger; the worker's first rebuild outcome.
        public async Task<RebuildOutcome> FaultAsync(Ulid incarnation)
        {
            var before = Rebuilds.Outcomes.Count;
            await Service<IReplicaCopyFaults>().RecordAsync(pair.Database, incarnation, "a test fault", pair.Token);
            await pair.WaitAsync(_ => Task.FromResult(Rebuilds.Outcomes.Count > before));
            return Rebuilds.Outcomes[before];
        }

        // Runs the rebuild again (the worker's own retry tick may run it too; a busy answer means it is): the one completion.
        public async Task<Ulid> ResumeAsync(Ulid faulted)
        {
            await pair.WaitAsync(async token => await Rebuilds.RebuildAsync(pair.Database, faulted, token) is not RebuildOutcome.Busy);
            var completed = Rebuilds.Outcomes.OfType<RebuildOutcome.Completed>().ToList();
            await Assert.That(completed.Count).IsEqualTo(1).Because(string.Join(", ", Rebuilds.Outcomes));
            return completed[0].NewIncarnation;
        }

        // A refused refill's state: the record swapped, the side copy kept, no refill proof, the new incarnation refused.
        public async Task<Ulid> SwappedAsync(Ulid faulted, string? refillSource = null)
        {
            var record = await Service<IRebuildLedger>().GetAsync(pair.Database, pair.Token);
            await Assert.That(record?.Phase).IsEqualTo(RebuildPhase.Swapped);
            await Assert.That(record!.OldIncarnation).IsEqualTo(faulted);
            var rebuilt = record.NewIncarnation!.Value;
            await Assert.That(await IncarnationAsync()).IsEqualTo(rebuilt);
            await Assert.That(SideExists(faulted)).IsTrue();
            await Assert.That(await RefillSourceAsync()).IsEqualTo(refillSource).Because("the refill proof matches the reached phase");
            await Assert.That(await Service<IReplicaCopyFaults>().RefusedAsync(pair.Database, rebuilt, pair.Token)).IsTrue();
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(DurableWriteAsync);
            return rebuilt;
        }

        // A finished rebuild: the record and the side copy gone, the certificate naming the source's copy, writes accepted.
        public async Task CertifiedAsync(Ulid faulted, Ulid rebuilt, Machine source)
        {
            await Assert.That(await IncarnationAsync()).IsEqualTo(rebuilt);
            await Assert.That(await Service<IRebuildLedger>().GetAsync(pair.Database, pair.Token)).IsNull();
            await Assert.That(SideExists(faulted)).IsFalse();
            await Assert.That(await Service<IReplicaCopyFaults>().RefusedAsync(pair.Database, rebuilt, pair.Token)).IsFalse();
            await Assert.That(await Service<IReplicaCopyFaults>().RefusedAsync(pair.Database, faulted, pair.Token)).IsTrue();
            var digest = await source.DigestAsync();
            var certificate = await CertificateAsync();
            await Assert.That(certificate.Count).IsEqualTo(1);
            await Assert.That(certificate[0].Origin).IsEqualTo(source.Name);
            await Assert.That(certificate[0].Incarnation).IsEqualTo(digest.Incarnation.ToString());
            await Assert.That(certificate[0].Head).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(digest.Head);
            await DurableWriteAsync();
        }

        public async Task<ReplicaDigest> DigestAsync() =>
            await Service<TursoChangeGroupSource>().DigestAsync(pair.Database, expected: null, pair.Token)
                ?? throw new InvalidOperationException($"{region} does not hold the database");

        // The whole state of one document of this machine's copy, as the source reads it.
        public async Task<RepairState> StateAsync(string id)
        {
            var source = Service<TursoChangeGroupSource>();
            var digest = await DigestAsync();
            var page = await source.ReadBucketsAsync(pair.Database, digest.Incarnation, [.. Enumerable.Range(0, TursoStorageTransaction.Buckets)], null,
                ChangeGroups.MaxDeltas, ChangeGroups.MaxBatchBytes, pair.Token);
            return page?.States.Single(state => state.Id == id) ?? throw new InvalidOperationException($"{region} does not hold {id}");
        }

        public async Task<Ulid> IncarnationAsync() => await InPlaceAsync() ?? throw new InvalidOperationException($"the database is missing on {region}");

        public async Task<Ulid?> InPlaceAsync() => await Engine.IncarnationAsync(pair.Database, pair.Token);

        public bool SideExists(Ulid incarnation) => Engine.SideExists(Engine.SidePath(pair.Database, incarnation));

        public async Task<Dictionary<string, byte[]>> SideBytesAsync(Ulid incarnation)
        {
            var path = Engine.SidePath(pair.Database, incarnation);
            var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                if (File.Exists(path + suffix))
                {
                    await using var stream = File.OpenRead(path + suffix);
                    snapshot.Add(suffix, await SHA256.HashDataAsync(stream, pair.Token));
                }
            }

            await Assert.That(snapshot.ContainsKey(string.Empty)).IsTrue();
            return snapshot;
        }

        public async Task AssertSideBytesAsync(Ulid incarnation, Dictionary<string, byte[]> before)
        {
            var after = await SideBytesAsync(incarnation);
            await Assert.That(after.Keys.ToArray()).IsEquivalentTo(before.Keys.ToArray());
            foreach (var (suffix, hash) in before)
            {
                await Assert.That(after[suffix].AsSpan().SequenceEqual(hash)).IsTrue().Because("retained side files stay byte-identical");
            }
        }

        public async Task AssertNoSalvageFilesAsync(Ulid incarnation)
        {
            var working = Engine.SidePath(pair.Database, incarnation) + ".salvage";
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                await Assert.That(File.Exists(working + suffix)).IsFalse();
            }
        }

        public async Task CorruptHashAsync(string id, string column, object value)
        {
            if (column is not ("h1" or "h2"))
            {
                throw new ArgumentOutOfRangeException(nameof(column));
            }
            var store = (TursoDatabaseStore)await StoreAsync();
            var changed = await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = $"UPDATE _tb_state SET {column} = $value WHERE collection = 'items' AND id = $id;";
                command.Parameters.AddWithValue("$value", value);
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteNonQuery();
            }, "a test damaging a retained hash behind the copy-fault guard", pair.Token);
            await Assert.That(changed).IsEqualTo(1);
            var stored = await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = $"SELECT typeof(s.{column}), octet_length(s.{column}), octet_length(s.state), octet_length(d.doc) FROM _tb_state s JOIN items d ON d.id = s.id WHERE s.collection = 'items' AND s.id = $id;";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidOperationException("the retained hash fixture row is missing");
                }
                return (Type: reader.GetString(0), Hash: reader.GetInt64(1), State: reader.GetInt64(2), Body: reader.GetInt64(3));
            }, "a test checking scalar hash and document lengths", pair.Token);
            await Assert.That(stored.Type).IsEqualTo(value is string ? "text" : "blob");
            await Assert.That(stored.Hash).IsEqualTo(value is byte[] bytes ? bytes.LongLength : Encoding.UTF8.GetByteCount((string)value));
            await Assert.That(stored.State + stored.Body).IsGreaterThan(0).And.IsLessThan(4096);
        }

        public async Task<Ulid> RetainedRefusalAsync(Ulid faulted, string? refillSource = null)
        {
            var rebuilt = await SwappedAsync(faulted, refillSource);
            await Assert.That(await CertificateAsync()).IsEmpty();
            var before = pair.SideBeforeRefill ?? throw new InvalidOperationException("the pre-stream side bytes were not captured");
            var after = await SideBytesAsync(faulted);
            await Assert.That(after.Keys.ToArray()).IsEquivalentTo(before.Keys.ToArray());
            foreach (var (suffix, hash) in before)
            {
                await Assert.That(after[suffix].AsSpan().SequenceEqual(hash)).IsTrue().Because("the retained file and sidecars stay byte-identical");
            }

            return rebuilt;
        }

        public async Task<long> StateReadChargeAsync(string collection, string id)
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            return await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT octet_length(s.state), coalesce(octet_length(d.doc), 0) FROM _tb_state s LEFT JOIN items d ON d.id = s.id WHERE s.collection = $collection AND s.id = $id;";
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidOperationException("the seeded old state is missing");
                }

                var decoded = 3L * (reader.GetInt64(0) / 4) + 3;
                return 16 * (decoded + reader.GetInt64(1)) + 2L * (Encoding.UTF8.GetByteCount(collection) + Encoding.UTF8.GetByteCount(id)) + 2048;
            }, "an admission test measuring a genuine retained old state", pair.Token);
        }

        public async Task ChangePersistedIncarnationAsync(Ulid incarnation)
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            _ = await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "UPDATE commit_offset SET incarnation = $incarnation WHERE id = 1;";
                command.Parameters.AddWithValue("$incarnation", incarnation.ToString());
                return command.ExecuteNonQuery();
            }, "a test changing persisted receiver identity", pair.Token);
        }

        public async Task<Ulid> PersistedIncarnationAsync()
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            return await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT incarnation FROM commit_offset WHERE id = 1;";
                return Ulid.Parse((string)command.ExecuteScalar()!);
            }, "a test reading persisted receiver identity", pair.Token);
        }

        public async Task WriteAsync(Func<IStorageTransaction, Task> work)
        {
            var store = await StoreAsync();
            _ = await store.WriteAsync(async (transaction, _) =>
            {
                await work(transaction);
                return true;
            }, static _ => true, pair.Token);
        }

        public Task PutAsync(string id, string json) =>
            WriteAsync(async transaction => _ = await transaction.PutAsync("items", id, Encoding.UTF8.GetBytes(json), pair.Token));

        public async Task<string?> ReadAsync(string id)
        {
            var store = await StoreAsync();
            await using var transaction = await store.BeginTransactionAsync(write: false, pair.Token);
            return await transaction.GetAsync("items", id, pair.Token) is { Deleted: false } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
        }

        public async Task<bool> UntilAsync(string id, string expected)
        {
            await pair.WaitAsync(async _ => await ReadAsync(id) == expected);
            return true;
        }

        public async Task RemoveStateKeepingDocumentAsync(string id)
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            var changed = await store.WithWriterAsync(session => Uncaptured(session, command =>
            {
                command.CommandText = "DELETE FROM _tb_state WHERE collection = 'items' AND id = $id;";
                command.Parameters.AddWithValue("$id", id);
                var removed = command.ExecuteNonQuery();
                command.CommandText = "SELECT doc FROM \"items\" WHERE id = $id;";
                if (command.ExecuteScalar() as string != """{"n":2}""")
                {
                    throw new InvalidOperationException("the orphan fixture did not retain its materialized document");
                }

                return removed;
            }), "a test removing state while retaining its materialized document", pair.Token);
            await Assert.That(changed).IsEqualTo(1);
        }

        public async Task CorruptStateAsync(string id)
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            await store.WithWriterAsync(session => Uncaptured(session, command =>
            {
                command.CommandText = "UPDATE _tb_state SET state = (SELECT state FROM _tb_state WHERE collection = 'items' AND id != $id LIMIT 1) WHERE collection = 'items' AND id = $id;";
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException("the keyed state corruption did not change one row");
                }
                // Its materialization remains valid for the donor state; only the original keyed h1/h2 reveals damage.
                command.CommandText = "UPDATE \"items\" SET doc = (SELECT doc FROM \"items\" WHERE id != $id LIMIT 1) WHERE id = $id;";
                return command.ExecuteNonQuery();
            }), "a test corrupting state while preserving materialization", pair.Token);
        }

        public async Task CorruptDocumentAsync(string id)
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            var changed = await store.WithWriterAsync(session => Uncaptured(session, command =>
            {
                command.CommandText = "UPDATE \"items\" SET doc = '{\"n\":9}' WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteNonQuery();
            }), "a test corrupting a materialized document", pair.Token);
            await Assert.That(changed).IsEqualTo(1).Because("the stored document changed while its keyed digest stayed intact");
        }

        // Damage at rest, not a write: with capture on, the CDC tail would decode the raw edit and fault the copy first, in
        // memory ahead of its durable record, so the read under test would be refused before the keyed check ever ran.
        private static int Uncaptured(TursoSession session, Func<SqliteCommand, int> corrupt)
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT coalesce(max(change_id), 0) FROM turso_cdc;";
            var head = (long)command.ExecuteScalar()!;
            command.CommandText = TursoSession.CaptureOff;
            _ = command.ExecuteNonQuery();
            int changed;
            try
            {
                changed = corrupt(command);
            }
            finally
            {
                command.Parameters.Clear();
                command.CommandText = TursoSession.CaptureOn;
                _ = command.ExecuteNonQuery();
            }

            command.CommandText = "SELECT coalesce(max(change_id), 0) FROM turso_cdc;";
            return (long)command.ExecuteScalar()! == head ? changed : throw new InvalidOperationException("the corruption reached the change log");
        }

        // Deletes the document's rows from the file directly, as a lost apply would leave it.
        public async Task LoseAsync(string id)
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            _ = await store.WithWriterAsync(session =>
            {
                using var transaction = session.Connection.BeginTransaction();
                using var command = session.Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM \"items\" WHERE id = $id; DELETE FROM _tb_state WHERE collection = 'items' AND id = $id;";
                command.Parameters.AddWithValue("$id", id);
                var changed = command.ExecuteNonQuery();
                MerkleFixture.Rebuild(session, transaction);
                transaction.Commit();
                return changed;
            }, "a test editing rows directly", pair.Token);
            store.ForgetCollections();
            await Assert.That(await ReadAsync(id)).IsNull();
        }

        // The reason on the database's ledger row: which check faulted the copy first, or null.
        public async Task<string?> FaultReasonAsync()
        {
            var system = (TursoDatabaseStore)await Engine.OpenSystemDatabaseAsync(pair.Token);
            return await system.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT reason FROM _tb_rebuild WHERE database = $database;";
                command.Parameters.AddWithValue("$database", pair.Database.DatabaseId);
                return command.ExecuteScalar() as string;
            }, "a test reading the rebuild ledger", pair.Token);
        }

        // The refill proof on the database's ledger row: the source member's name, or null.
        public async Task<string?> RefillSourceAsync()
        {
            var system = (TursoDatabaseStore)await Engine.OpenSystemDatabaseAsync(pair.Token);
            return await system.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT source_machine FROM _tb_rebuild WHERE database = $database;";
                command.Parameters.AddWithValue("$database", pair.Database.DatabaseId);
                return command.ExecuteScalar() as string;
            }, "a test reading the rebuild ledger", pair.Token);
        }

        public async ValueTask DisposeAsync()
        {
            Admission.RemovePressure();
            if (_api is not null)
            {
                var api = _api;
                try
                {
                    await api.DisposeAsync();
                }
                finally
                {
                    // The harness can return with native cleanup still running. Delete our directory only after it ends.
                    try
                    {
                        using var cleanupDeadline = new CancellationTokenSource(Deadline);
                        await api.CleanupCompletion.WaitAsync(cleanupDeadline.Token);
                    }
                    finally
                    {
                        _api = null;
                        if (api.CleanupCompletion.IsCompleted)
                        {
                            Directory.Delete(_directory, recursive: true);
                        }
                    }
                }
            }
            else
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        // The rebuilt file's certificate rows (decision 0013a's _tb_refill).
        public async Task<List<(string Origin, string Incarnation, long Head)>> CertificateAsync()
        {
            var store = (TursoDatabaseStore)await StoreAsync();
            return await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT origin, incarnation, head FROM _tb_refill;";
                using var reader = command.ExecuteReader();
                var rows = new List<(string, string, long)>();
                while (reader.Read())
                {
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
                }

                return rows;
            }, "a test reading the refill certificate", pair.Token);
        }

        private async Task<IDatabaseStore> StoreAsync() =>
            await Engine.OpenDatabaseAsync(pair.Database, pair.Token) ?? throw new InvalidOperationException($"the database is missing on {region}");

        // A durable (replication-class) write of one new document through the host's engine.
        public async Task DurableWriteAsync()
        {
            var store = await Service<IStorageEngine>().OpenDatabaseAsync(pair.Database, pair.Token)
                ?? throw new InvalidOperationException($"the database is missing on {region}");
            await using var transaction = await store.BeginDurableTransactionAsync(pair.Token);
            _ = await transaction.PutAsync("items", EntityIds.New().ToString(), Encoding.UTF8.GetBytes("""{"n":5}"""), pair.Token);
            _ = await transaction.CommitAsync(pair.Token);
        }
    }

    // Only this test seam chooses where to reserve pressure. The production budget decides the actual refusal.
    private sealed class AdmissionProbe
    {
        public const long BudgetBytes = 128L * 1024 * 1024;
        public const long PassBytes = 3L * 4096 * 16 + 4L * ReplicationProtocol.MaxApplicationBytes;
        public const long SourceBytes = 16L * ReplicationProtocol.MaxApplicationBytes;
        public readonly Lock Gate = new();
        public bool CertificationActive;
        public bool SalvageActive;
        public long SalvageReserved;
        public long SalvagePeak;
        public int SalvageMetadataRequests;
        public int SalvagePageRequests;
        public int SalvageDeniedRequests;
        public int ActiveJoins;
        public int ActiveBucketReads;
        public long Reserved;
        public long PassReserved;
        public long PassPeak;
        public long DecodeReserved;
        public long JoinReserved;
        public long SourceReserved;
        public long SourceReadReserved;
        public long JoinHeldAtDenial;
        public long JoinHeldAtReset;
        public int DeniedRequests;
        public MemoryLane? DeniedLane;
        public MemoryLane? TargetLane;
        public long TargetBytes;
        private IMemoryReservation? _pressure;

        public bool Released
        {
            get
            {
                lock (Gate)
                {
                    return PassReserved == 0 && DecodeReserved == 0 && JoinReserved == 0 && SourceReserved == 0
                        && SourceReadReserved == 0 && SalvageReserved == 0 && ActiveJoins == 0 && ActiveBucketReads == 0;
                }
            }
        }

        public long PressureBytes
        {
            get
            {
                lock (Gate)
                {
                    return _pressure?.Bytes ?? 0;
                }
            }
        }

        public void Exhaust(MemoryLane lane, long bytes)
        {
            lock (Gate)
            {
                TargetLane = lane;
                TargetBytes = bytes;
            }
        }

        public void ReservePressure(IMemoryBudget budget, long requested)
        {
            // Leave one byte less than the target needs. Do not allocate a payload or replace an admission exception.
            var bytes = BudgetBytes - Reserved - (requested - 1);
            _pressure = budget.TryReserve(MemoryLane.Bulk, bytes, tenant: null)
                ?? throw new InvalidOperationException("the real budget did not admit the controlled pressure lease");
        }

        public void NoteWriterReset()
        {
            lock (Gate)
            {
                if (_pressure is not null && DeniedRequests > 0)
                {
                    JoinHeldAtReset = Math.Max(JoinHeldAtReset, JoinReserved);
                }
            }
        }

        public void RemovePressure()
        {
            lock (Gate)
            {
                TargetLane = null;
                _pressure?.Dispose();
                _pressure = null;
            }
        }
    }

    private enum LeaseKind
    {
        Other,
        Pass,
        Decode,
        Join,
        Source,
        SourceRead,
        Salvage,
    }

    // Every lease forwards to, and disposes, the original production reservation.
    private sealed class ObservedBudget(IMemoryBudget inner, AdmissionProbe probe) : IMemoryBudget
    {
        private AdmissionProbe Probe => probe;
        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            lock (probe.Gate)
            {
                var kind = lane == MemoryLane.Replication && bytes == AdmissionProbe.SourceBytes ? LeaseKind.Source
                    : lane == MemoryLane.Bulk && probe.CertificationActive
                        ? bytes == AdmissionProbe.PassBytes ? LeaseKind.Pass : LeaseKind.Decode
                    : lane == MemoryLane.Bulk && probe.SalvageActive ? LeaseKind.Salvage
                    : lane == MemoryLane.Foreground && probe.ActiveJoins > 0 ? LeaseKind.Join
                    : lane == MemoryLane.Foreground && probe.ActiveBucketReads > 0 ? LeaseKind.SourceRead : LeaseKind.Other;
                var targeted = probe.CertificationActive && lane == probe.TargetLane && bytes == probe.TargetBytes
                    && (kind == LeaseKind.Decode || kind == LeaseKind.Join);
                if (targeted && probe.PressureBytes == 0)
                {
                    probe.ReservePressure(inner, bytes);
                }

                if (kind == LeaseKind.Salvage)
                {
                    if (bytes == ChangeGroups.MaxDeltas * 8192L)
                    {
                        probe.SalvageMetadataRequests++;
                    }
                    else
                    {
                        probe.SalvagePageRequests++;
                    }
                }

                if (inner.TryReserve(lane, bytes, tenant) is not { } lease)
                {
                    if (kind == LeaseKind.Salvage)
                    {
                        probe.SalvageDeniedRequests++;
                    }
                    if (targeted)
                    {
                        probe.DeniedRequests++;
                        probe.DeniedLane = lane;
                        probe.JoinHeldAtDenial = probe.JoinReserved;
                    }

                    return null;
                }

                probe.Reserved += lease.Bytes;
                Adjust(kind, lease.Bytes);
                return new Observed(lease, this, kind);
            }
        }

        private void Adjust(LeaseKind kind, long bytes)
        {
            switch (kind)
            {
                case LeaseKind.Salvage:
                    probe.SalvageReserved += bytes;
                    probe.SalvagePeak = Math.Max(probe.SalvagePeak, probe.SalvageReserved);
                    break;
                case LeaseKind.Pass:
                    probe.PassReserved += bytes;
                    probe.PassPeak = Math.Max(probe.PassPeak, probe.PassReserved);
                    break;
                case LeaseKind.Decode:
                    probe.DecodeReserved += bytes;
                    break;
                case LeaseKind.Join:
                    probe.JoinReserved += bytes;
                    break;
                case LeaseKind.Source:
                    probe.SourceReserved += bytes;
                    break;
                case LeaseKind.SourceRead:
                    probe.SourceReadReserved += bytes;
                    break;
            }
        }

        private sealed class Observed(IMemoryReservation inner, ObservedBudget owner, LeaseKind kind) : IMemoryReservation
        {
            private int _released;
            public long Bytes => inner.Bytes;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0)
                {
                    return;
                }

                lock (owner.Probe.Gate)
                {
                    inner.Dispose();
                    owner.Probe.Reserved -= Bytes;
                    owner.Adjust(kind, -Bytes);
                }
            }
        }
    }

    // A global host-local observation scope also reaches the dedicated Turso writer (which restores QoS, not arbitrary AsyncLocals).
    private sealed class ObservedRepair(IRebuildRepair inner, AdmissionProbe probe, TenantContext watched) : IRebuildRepair
    {
        public async ValueTask<int?> RefillRebuildAsync(TenantContext database, Ulid newIncarnation, IReadOnlyList<int> buckets,
            BucketCursor? after, IReadOnlyList<RepairState> states, Hlc collectedBefore, CancellationToken cancellationToken)
        {
            lock (probe.Gate)
            {
                probe.ActiveJoins++;
            }

            try
            {
                return await inner.RefillRebuildAsync(database, newIncarnation, buckets, after, states, collectedBefore, cancellationToken);
            }
            finally
            {
                lock (probe.Gate)
                {
                    probe.ActiveJoins--;
                }
            }
        }

        public async ValueTask<int?> RepairRebuildAsync(TenantContext database, Ulid newIncarnation, IReadOnlyList<RepairState> states,
            CancellationToken cancellationToken)
        {
            if (database != watched)
            {
                return await inner.RepairRebuildAsync(database, newIncarnation, states, cancellationToken);
            }

            lock (probe.Gate)
            {
                probe.ActiveJoins++;
            }

            try
            {
                return await inner.RepairRebuildAsync(database, newIncarnation, states, cancellationToken);
            }
            finally
            {
                lock (probe.Gate)
                {
                    probe.ActiveJoins--;
                }
            }
        }

        public ValueTask<bool> RecordRefillAsync(TenantContext database, Ulid newIncarnation, string sourceMachine,
            Ulid sourceIncarnation, long head, Hlc collectedBefore, CancellationToken cancellationToken) =>
            inner.RecordRefillAsync(database, newIncarnation, sourceMachine, sourceIncarnation, head, collectedBefore, cancellationToken);

        public ValueTask<bool> CompleteRebuildAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation,
            CancellationToken cancellationToken) => inner.CompleteRebuildAsync(database, oldIncarnation, newIncarnation, cancellationToken);
    }

    // The worker's rebuild: the real one, each outcome recorded in order.
    private sealed class Rebuilds(IReplicaRebuild real, CancellationToken testToken) : IReplicaRebuild
    {
        private readonly ConcurrentQueue<RebuildOutcome> _outcomes = new();

        public IReadOnlyList<RebuildOutcome> Outcomes => [.. _outcomes];

        // While set, each rebuild signals Entered and waits on it before starting: the faulted copy stays in place.
        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, testToken);
            if (Gate is { } gate)
            {
                _ = Entered.TrySetResult();
                await gate.Task.WaitAsync(stop.Token);
            }

            var outcome = await real.RebuildAsync(database, faultedIncarnation, stop.Token);
            _outcomes.Enqueue(outcome);
            return outcome;
        }
    }

    // Scope only incoming bucket streams; local background scans never consume the source hooks.
    private sealed class BucketReads(AdmissionProbe admission) : Interceptor
    {
        public AsyncLocal<BucketRequest?> Current { get; } = new();
        public Func<BucketRequest, bool>? IsCertification { get; set; }
        public WireDisorder? Disorder { get; set; }
        public string WatchedDatabaseId { get; set; } = string.Empty;
        public int DatabaseRequests;
        public int CertificationRequests;
        public int ActiveCertificationRequests;
        public int StreamFailures;
        public int MutatedStreams;
        public int PreparedPages;
        public Ulid PreparedPin;
        public bool PreservesCoverage;
        public int RefusedPushes;

        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
            IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
            continuation(requestStream, new RefusalWriter<TResponse>(responseStream, this, context.CancellationToken), context);

        // Counts the watched database's refused acks this receiver sends; every frame passes through unchanged.
        private sealed class RefusalWriter<T>(IServerStreamWriter<T> inner, BucketReads probe, CancellationToken callToken) : IServerStreamWriter<T>
        {
            public WriteOptions? WriteOptions { get => inner.WriteOptions; set => inner.WriteOptions = value; }
            public Task WriteAsync(T message) => WriteAsync(message, callToken);

            public async Task WriteAsync(T message, CancellationToken cancellationToken)
            {
                await inner.WriteAsync(message, cancellationToken);
                if (message is AckFrame { Ack: { Refused: true } ack } && ack.Database?.DatabaseId == probe.WatchedDatabaseId)
                {
                    _ = Interlocked.Increment(ref probe.RefusedPushes);
                }
            }
        }

        public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
            TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
            ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            var previous = Current.Value;
            Current.Value = request as BucketRequest;
            if (request is BucketRequest watched && watched.Database.DatabaseId == WatchedDatabaseId)
            {
                Interlocked.Increment(ref DatabaseRequests);
            }

            var targeted = request is BucketRequest bucket && IsCertification?.Invoke(bucket) == true;
            if (targeted)
            {
                Interlocked.Increment(ref CertificationRequests);
                Interlocked.Increment(ref ActiveCertificationRequests);
                lock (admission.Gate)
                {
                    admission.ActiveBucketReads++;
                }
            }

            try
            {
                var writer = targeted ? new LeaseWriter<TResponse>(responseStream, admission, context.CancellationToken) : responseStream;
                await continuation(request, targeted && Disorder is { } disorder
                    ? new DisorderWriter<TResponse>(writer, this, disorder, context.CancellationToken) : writer, context);
            }
            catch (Exception) when (context.CancellationToken.IsCancellationRequested)
            {
                // Receiver cancellation after refusal is expected and is not a source admission failure.
                throw;
            }
            catch
            {
                if (targeted)
                {
                    Interlocked.Increment(ref StreamFailures);
                }

                throw;
            }
            finally
            {
                if (targeted)
                {
                    Interlocked.Decrement(ref ActiveCertificationRequests);
                    lock (admission.Gate)
                    {
                        admission.ActiveBucketReads--;
                    }
                }

                Current.Value = previous;
            }
        }

        private sealed class LeaseWriter<T>(IServerStreamWriter<T> inner, AdmissionProbe probe, CancellationToken callToken) : IServerStreamWriter<T>
        {
            public WriteOptions? WriteOptions { get => inner.WriteOptions; set => inner.WriteOptions = value; }
            public Task WriteAsync(T message) => WriteAsync(message, callToken);

            public async Task WriteAsync(T message, CancellationToken cancellationToken)
            {
                await Assert.That(Volatile.Read(ref probe.SourceReserved)).IsEqualTo(AdmissionProbe.SourceBytes);
                await inner.WriteAsync(message, cancellationToken);
                await Assert.That(Volatile.Read(ref probe.SourceReserved)).IsEqualTo(AdmissionProbe.SourceBytes);
            }
        }

        private sealed class DisorderWriter<T>(IServerStreamWriter<T> inner, BucketReads probe, WireDisorder disorder,
            CancellationToken callToken) : IServerStreamWriter<T>
        {
            private bool _seen;
            public WriteOptions? WriteOptions { get => inner.WriteOptions; set => inner.WriteOptions = value; }
            public Task WriteAsync(T message) => WriteAsync(message, callToken);

            public async Task WriteAsync(T message, CancellationToken cancellationToken)
            {
                if (message is not StatePage original || _seen)
                {
                    throw new InvalidOperationException("the small seed must produce exactly one production StatePage");
                }

                _seen = true;
                var list = StateList.Parser.ParseFrom(ApplicationEnvelope.Decode(original.States.Span, ReplicationProtocol.MaxApplicationBytes));
                if (list.States.Count < 2)
                {
                    throw new InvalidOperationException("the ordering regression needs at least two real source keys");
                }

                var last = list.States[^1];
                StatePage[] pages = disorder == WireDisorder.TripleDuplicate
                    ? [original.Clone(), Page([last]), Page([last])]
                    : [Page([last]), Page(list.States.Take(list.States.Count - 1))];
                probe.PreparedPin = new Ulid(original.Incarnation.Span);
                probe.PreparedPages = pages.Length;
                probe.PreservesCoverage = pages.All(page => page.Incarnation.Equals(original.Incarnation))
                    && Coverage(pages).AsSpan().SequenceEqual(Coverage([original]));
                Interlocked.Increment(ref probe.MutatedStreams);
                foreach (var page in pages)
                {
                    await inner.WriteAsync((T)(object)page, cancellationToken);
                }

                StatePage Page(IEnumerable<StateEntry> entries)
                {
                    var states = new StateList();
                    states.States.AddRange(entries.Select(entry => entry.Clone()));
                    var page = original.Clone();
                    page.States = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(states.ToByteArray()));
                    return page;
                }
            }

            private static byte[] Coverage(IEnumerable<StatePage> pages)
            {
                var coverage = new byte[4096 * 16];
                foreach (var page in pages)
                {
                    var list = StateList.Parser.ParseFrom(ApplicationEnvelope.Decode(page.States.Span, ReplicationProtocol.MaxApplicationBytes));
                    foreach (var entry in list.States)
                    {
                        var state = DocumentStateEncoding.Decode(entry.State.Span);
                        var digest = DocumentStateEncoding.Digest(entry.Collection, entry.Id, state);
                        var bucket = DocumentStateEncoding.Bucket(entry.Collection, entry.Id);
                        var offset = bucket * 16;
                        for (var index = 0; index < 8; index++)
                        {
                            coverage[offset + index] ^= (byte)((ulong)(digest >> 64) >> (8 * index));
                            coverage[offset + 8 + index] ^= (byte)((ulong)digest >> (8 * index));
                        }
                    }
                }

                return coverage;
            }
        }
    }

    // The real certification sets the source scope only while its receiver is refused to background repairs.
    private sealed class Refills(IRebuildCertification real, Pair pair, Machine receiver) : IRebuildCertification
    {
        public async ValueTask<bool> RefillAsync(TenantContext database, Ulid newIncarnation, CancellationToken cancellationToken)
        {
            if (database != pair.Database)
            {
                return await real.RefillAsync(database, newIncarnation, cancellationToken);
            }

            var sources = new[] { pair.Gru, pair.SecondSource }.OfType<Machine>().ToArray();
            var begun = new List<Source>();
            bool refilled;
            try
            {
                if (receiver == pair.Sjc && pair.SideBeforeRefill is null)
                {
                    var row = await receiver.Service<IRebuildLedger>().GetAsync(database, cancellationToken)
                        ?? throw new InvalidOperationException("the swapped rebuild record is missing");
                    pair.SideBeforeRefill = await receiver.SideBytesAsync(row.OldIncarnation);
                }

                foreach (var machine in sources)
                {
                    await machine.Source.BeginRefillAsync(cancellationToken);
                    begun.Add(machine.Source);
                }

                lock (receiver.Admission.Gate)
                {
                    receiver.Admission.CertificationActive = true;
                }
                refilled = await real.RefillAsync(database, newIncarnation, cancellationToken);
            }
            finally
            {
                lock (receiver.Admission.Gate)
                {
                    receiver.Admission.CertificationActive = false;
                }
                foreach (var source in begun)
                {
                    source.EndRefill();
                }
            }
            if (refilled && receiver == pair.Sjc && pair.AfterRefill is { } after)
            {
                await after();
            }
            return refilled;
        }

        public ValueTask<bool> CompleteAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken) =>
            real.CompleteAsync(database, oldIncarnation, newIncarnation, cancellationToken);
    }

    // The machine's real replica states; faults apply to every page of the watched pinned certification, including subsets.
    private sealed class Source(IReplicaStates inner, TenantContext watched, BucketReads requests) : IReplicaStates
    {
        public ValueTask<bool> MatchedAsync(TenantContext database, ReplicaDigest digest, CancellationToken cancellationToken) =>
            inner.MatchedAsync(database, digest, cancellationToken);

        private const int Disarmed = -1;
        private int _fault = Disarmed;
        private Func<Task>? _afterPage;
        private int _fired;
        private int _largestPage;
        private int _hookCalls;
        private string? _omittedId;
        private Refill? _refill;
        private readonly ConcurrentQueue<int[]> _refillRequests = new();

        public Func<IReadOnlyList<RepairState>, CancellationToken, Task>? BeforePage { get; set; }
        public bool IsRefill(BucketRequest request) => request.Database.DatabaseId == watched.DatabaseId
            && ChangeGroups.Incarnation(request.Database) == Volatile.Read(ref _refill)?.Pin;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public int HookCalls => Volatile.Read(ref _hookCalls);

        // The largest decoded StateList of an unarmed refill page read so far: the bytes a receiver's Decode must accept.
        public int LargestPage => Volatile.Read(ref _largestPage);

        public IReadOnlyList<int[]> RefillRequests => [.. _refillRequests];

        public async Task BeginRefillAsync(CancellationToken cancellationToken)
        {
            var digest = await inner.DigestAsync(watched, expected: null, cancellationToken);
            Volatile.Write(ref _refill, new Refill(digest?.Incarnation ?? Ulid.Empty));
        }

        public void EndRefill()
        {
            if (Interlocked.Exchange(ref _refill, null) is { } refill)
            {
                _refillRequests.Enqueue([.. refill.BucketCounts]);
            }
        }

        // Writes repeat on every targeted page until Disarm; any other afterPage hook runs once.
        public void Arm(SourceFault? fault, Func<Task>? afterPage, string? omittedId = null)
        {
            if (fault == SourceFault.OmitsState)
            {
                ArgumentException.ThrowIfNullOrEmpty(omittedId);
            }

            Volatile.Write(ref _omittedId, omittedId);
            Volatile.Write(ref _afterPage, afterPage);
            Volatile.Write(ref _fault, fault is { } chosen ? (int)chosen : Disarmed);
        }

        public void Disarm()
        {
            Volatile.Write(ref _fault, Disarmed);
            Volatile.Write(ref _afterPage, null);
        }

        public async ValueTask<ReplicaDigest?> DigestAsync(TenantContext database, Ulid? expected, CancellationToken cancellationToken)
        {
            var digest = await inner.DigestAsync(database, expected, cancellationToken);
            if (digest is null || database != watched || expected != Volatile.Read(ref _refill)?.Pin
                || expected is null || Volatile.Read(ref _fault) != (int)SourceFault.DigestDiffers)
            {
                return digest;
            }

            Fire();
            var buckets = (byte[])digest.Buckets.Clone();
            buckets[0] ^= 1;
            return digest with { Buckets = buckets };
        }

        public async ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)?> ReadBucketsAsync(
            TenantContext database, Ulid incarnation, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken)
        {
            var fault = Volatile.Read(ref _fault);
            var refill = Volatile.Read(ref _refill);
            if (database != watched || refill is null || incarnation != refill.Pin || buckets.Count == 0
                || requests.Current.Value is not { } request || ChangeGroups.Incarnation(request.Database) != incarnation)
            {
                return await inner.ReadBucketsAsync(database, incarnation, buckets, after, maxStates, maxBytes, cancellationToken);
            }

            if (after is null)
            {
                refill.BucketCounts.Enqueue(buckets.Count);
            }

            if (fault == (int)SourceFault.NoProgressDeadline
                || fault == (int)SourceFault.StreamDeadline && after is not null && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
            {
                Fire();
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "the test source reached its refill stream deadline"));
            }

            if (fault == Disarmed && Volatile.Read(ref _afterPage) is null && BeforePage is null)
            {
                return Measure(await inner.ReadBucketsAsync(database, incarnation, buckets, after, maxStates, maxBytes, cancellationToken), incarnation);
            }

            if (fault == (int)SourceFault.Replaced)
            {
                Fire();
                return null;
            }

            var page = await inner.ReadBucketsAsync(database, incarnation, buckets, after, maxStates, maxBytes, cancellationToken);
            if (fault == (int)SourceFault.OmitsState && page is { } read
                && read.States.Any(state => state.Collection == "items" && state.Id == Volatile.Read(ref _omittedId)))
            {
                Fire();
                page = ([.. read.States.Where(state => state.Collection != "items" || state.Id != Volatile.Read(ref _omittedId))], read.Next);
            }

            if (page is { States.Count: > 0 }
                && (fault == (int)SourceFault.Writes ? Volatile.Read(ref _afterPage) : Interlocked.Exchange(ref _afterPage, null)) is { } hook)
            {
                await hook();
                _ = Interlocked.Increment(ref _hookCalls);
                Fire();
            }

            if (page is { States.Count: > 0 } ready && BeforePage is { } before)
            {
                await before(ready.States, cancellationToken);
            }

            return Measure(page, incarnation);
        }

        public ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken cancellationToken) => inner.StableAsync(database, cancellationToken);

        public ValueTask<Hlc> HorizonAsync(TenantContext database, CancellationToken cancellationToken) => inner.HorizonAsync(database, cancellationToken);

        public ValueTask<int?> ForgetAsync(
            TenantContext database, Ulid incarnation, IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken) =>
            inner.ForgetAsync(database, incarnation, keys, horizon, cancellationToken);

        private void Fire() => Volatile.Write(ref _fired, 1);

        private sealed record Refill(Ulid Pin)
        {
            public ConcurrentQueue<int> BucketCounts { get; } = new();
        }

        // Encodes the page as the peer listener does and decodes it without a cap, recording its decoded size.
        private (IReadOnlyList<RepairState> States, BucketCursor? Next)? Measure((IReadOnlyList<RepairState> States, BucketCursor? Next)? page, Ulid incarnation)
        {
            if (page is { States.Count: > 0 } read)
            {
                var size = ApplicationEnvelope.Decode(AntiEntropy.Encode(read.States, incarnation).States.Span, int.MaxValue).Length;
                int largest;
                while (size > (largest = Volatile.Read(ref _largestPage)) && Interlocked.CompareExchange(ref _largestPage, size, largest) != largest)
                {
                }
            }

            return page;
        }
    }
}
