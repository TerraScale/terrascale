using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

/// <summary>
/// Migration and semantic evidence for the two-directory TerraBase corpus
/// manifest @2, executed through <see cref="TerraScaleSyncV2ViewEngine"/>.
/// The rows bind owner-recorded checkpoints, errors, and journals to the
/// client state machine. They do not qualify a public transport.
/// </summary>
public sealed class TerraScaleSyncV2CorpusViewEngineConformanceTests
{
    private static readonly TerraScaleViewId ViewId = new(Guid.Parse("0123456789abcdef0123456789abcdef"));

    /// <summary>The engine corpus's required categories, each covered exactly once (the SE-05 set).</summary>
    private static readonly string[] RequiredCategories =
    [
        "authorization-reset",
        "checkpoint-resume",
        "fresh-session",
        "malformed-frame-rejection",
        "reconnect-replay",
        "region-route-change",
        "repeated-change-delta",
        "resync-required",
        "slow-consumer",
        "slow-consumer-overload",
        "trigger-notification",
    ];

    /// <summary>The prefix binding an opaque engine checkpoint token to a corpus checkpoint's canonical bytes.</summary>
    private const string TokenPrefix = "v2ck.";

    /// <summary>The generation every helper drives by default; authorization rows move to generation two explicitly.</summary>
    private const ulong BaseGeneration = 1;

    private static Task<List<TerraScaleSyncV2CorpusVector>> LoadVectorsAsync() =>
        Task.FromResult(TerraScaleSyncV2CorpusFixture.LoadVerified().Vectors.ToList());

    private static List<JsonElement> ServeSteps(JsonElement root) =>
        root.GetProperty("steps")
            .EnumerateArray()
            .Where(step => step.TryGetProperty("serve_delta", out _))
            .Select(step => step.GetProperty("serve_delta"))
            .ToList();

    private static List<JsonElement> RecordedFrames(JsonElement root, string key) =>
        root.GetProperty("steps")
            .EnumerateArray()
            .Where(step => step.TryGetProperty(key, out _))
            .Select(step => step.GetProperty(key).GetProperty("frame"))
            .ToList();

    private static (int Index, JsonElement Entry) RecordedFrameStep(
        IReadOnlyList<JsonElement> steps,
        string container,
        string kind,
        string? errorCode = null,
        int after = -1)
    {
        for (var index = after + 1; index < steps.Count; index++)
        {
            if (!steps[index].TryGetProperty(container, out var entry))
            {
                continue;
            }

            var description = entry.GetProperty("frame");
            if (description.GetProperty("kind").GetString() != kind
                || errorCode is not null && description.GetProperty("code").GetString() != errorCode)
            {
                continue;
            }

            return (index, entry);
        }

        throw new InvalidOperationException($"the corpus does not contain {container}/{kind}/{errorCode ?? "any"} after step {after}");
    }

    private static TerraScaleSyncV2Frame DecodeRecordedFrame(JsonElement entry)
    {
        var frame = TerraScaleSyncV2Frame.Decode(
            TerraScaleSyncV2GoldenVectorTests.HexToBytes(entry.GetProperty("wire").GetString()!));
        if (!frame.Verify())
        {
            throw new InvalidOperationException("the recorded frame digest does not verify");
        }

        return frame;
    }

    // --- Corpus-manifest @2 gate ------------------------------------------------

    /// <summary>The owner @2 manifest gates all 11 pairs before they reach the engine.</summary>
    [Test]
    public async Task TheCorpusManifestGatesTheViewEngineConformanceRun()
    {
        var corpus = TerraScaleSyncV2CorpusFixture.LoadVerified();
        var categories = corpus.Vectors.Select(vector => vector.Category).Order(StringComparer.Ordinal).ToArray();
        await Assert.That(categories.SequenceEqual(RequiredCategories.Order(StringComparer.Ordinal), StringComparer.Ordinal)).IsTrue();
        await Assert.That(categories.Length).IsEqualTo(RequiredCategories.Length);
        await Assert.That(corpus.Vectors.Select(vector => (vector.Corpus, vector.FileName)).Distinct().Count()).IsEqualTo(11);
    }

    // --- Checkpoint round-trip --------------------------------------------------

    /// <summary>
    /// Every corpus checkpoint description round-trips through the managed
    /// checkpoint codec — versioned bytes, exact decode, canonical ends —
    /// and every recorded delta answer's wire checkpoint equals its
    /// structured description while serving only moves the session forward.
    /// </summary>
    [Test]
    public async Task EveryCorpusCheckpointRoundTripsThroughTheClientCodec()
    {
        var found = new List<(string Category, JsonElement Checkpoint)>();
        foreach (var vector in await LoadVectorsAsync())
        {
            CollectCheckpoints(vector.Open(), vector.Category, found);
        }

        // The structural walk found checkpoints in every category that
        // carries one; a corpus without checkpoints would pass vacuously.
        await Assert.That(found.Count).IsEqualTo(64);
        foreach (var category in (string[])["authorization-reset", "checkpoint-resume", "fresh-session", "reconnect-replay", "region-route-change", "repeated-change-delta", "resync-required", "slow-consumer", "slow-consumer-overload", "trigger-notification"])
        {
            await Assert.That(found.Count(entry => entry.Category == category)).IsGreaterThanOrEqualTo(1);
        }

        foreach (var (_, element) in found)
        {
            var checkpoint = TerraScaleSyncV2GoldenVectorTests.Checkpoint(element);
            var bytes = checkpoint.ToBytes();
            await Assert.That(bytes[0]).IsEqualTo((byte)2);
            var decoded = TerraScaleSyncV2Checkpoint.FromBytes(bytes);
            await Assert.That(decoded).IsEqualTo(checkpoint);
            await Assert.That(decoded.StorageSequence).IsEqualTo(checkpoint.StorageSequence);

            // Canonicalization: the corpus's ascending writer ends survive
            // a re-create unchanged.
            var peers = checkpoint.Ends.Select(end => end.Peer).ToArray();
            await Assert.That(peers.Zip(peers.Skip(1), (lower, upper) => lower < upper).All(ascending => ascending)).IsTrue();
            var recreated = TerraScaleSyncV2Checkpoint.Create(
                checkpoint.Ends.Select(end => (end.Peer, checked((int)end.End))),
                checkpoint.StorageSequence);
            await Assert.That(recreated).IsEqualTo(checkpoint);
        }

        // Wire agreement: every recorded delta answer carries its structured
        // checkpoint in the recorded bytes, the presented position is below
        // the served next, and a stalled position is below the retained floor.
        // The expected total is counted by JSON structure alone, in a pass of
        // its own that runs no production code — the assertion gates the
        // checking loop, so a loop that skips an entry fails it.
        var expectedDeliveries = 0;
        foreach (var vector in await LoadVectorsAsync())
        {
            foreach (var serve in ServeSteps(vector.Open()))
            {
                if (serve.TryGetProperty("expect_wire", out var wire) && wire.GetString() is { Length: > 0 })
                {
                    expectedDeliveries++;
                }
            }
        }

        var deliveries = 0;
        foreach (var vector in await LoadVectorsAsync())
        {
            foreach (var serve in ServeSteps(vector.Open()))
            {
                if (!serve.TryGetProperty("expect_wire", out var wire) || wire.GetString() is not { Length: > 0 })
                {
                    continue;
                }

                var frame = TerraScaleSyncV2Frame.Decode(TerraScaleSyncV2GoldenVectorTests.HexToBytes(wire.GetString()!));
                var from = TerraScaleSyncV2GoldenVectorTests.Checkpoint(serve.GetProperty("from"));
                await Assert.That(TerraScaleSyncV2Checkpoint.FromBytes(from.ToBytes())).IsEqualTo(from);
                var described = serve.GetProperty("expect_frame");
                switch (frame.Body)
                {
                    case TerraScaleSyncV2DeltaBatchBody batch:
                        await Assert.That(batch.Next)
                            .IsEqualTo(TerraScaleSyncV2GoldenVectorTests.Checkpoint(described.GetProperty("next")));
                        await Assert.That(from.CompareTo(batch.Next)).IsLessThan(0);
                        break;
                    case TerraScaleSyncV2ResyncRequiredBody resync:
                        await Assert.That(resync.Floor)
                            .IsEqualTo(TerraScaleSyncV2GoldenVectorTests.Checkpoint(described.GetProperty("floor")));
                        await Assert.That(from.CompareTo(resync.Floor)).IsLessThan(0);
                        break;
                    default:
                        throw new InvalidOperationException($"an unexpected served kind {frame.Kind} in {vector.FileName}");
                }

                deliveries++;
            }
        }

        await Assert.That(deliveries).IsEqualTo(expectedDeliveries);
        await Assert.That(expectedDeliveries).IsGreaterThanOrEqualTo(17);
    }

    /// <summary>
    /// The checkpoint-resume vector round-trips through the engine: the
    /// resumed checkpoint is adopted at snapshot publish, the recorded
    /// checkpoint acknowledgement names that position, the further
    /// commit's next checkpoint is adopted on apply, and a reconnect
    /// replay of the same revision is an ignored at-least-once duplicate.
    /// </summary>
    [Test]
    public async Task TheCheckpointResumeVectorRoundTripsThroughTheEngine()
    {
        var vector = (await LoadVectorsAsync()).Single(entry => entry.Category == "checkpoint-resume");
        var root = vector.Open();
        var commits = JournalOf(root);
        var resume = CorpusCheckpoint(root, "session_setup", "resume");
        var next = CorpusServeNext(root);
        var acknowledged = CorpusCheckpoint(root, "checkpoint_ack", "checkpoint");

        // The resumed session: the journal_before rows at the resume
        // position, published with the corpus resume checkpoint as the
        // adopted opaque checkpoint.
        var engine = new TerraScaleSyncV2ViewEngine();
        engine.Authenticate();
        engine.Subscribe();
        var rows = FoldTo(commits, resume.StorageSequence);
        var publish = PublishSnapshot(engine, BaseGeneration, resume.StorageSequence, RowsOf(rows), TokenOf(resume));
        await Assert.That(publish.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SnapshotPublished);
        await Assert.That(publish.Ack!.AppliedRevision).IsEqualTo(resume.StorageSequence);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(resume);

        // The corpus's checkpoint_ack acknowledges exactly the adopted
        // position: the engine and the recorded client agree.
        await Assert.That(acknowledged).IsEqualTo(CheckpointOf(engine.Checkpoint!.Token));

        // The further commit (d-03's put) lands as one result delta whose
        // checkpoint is the corpus's recorded next position.
        var apply = commits.Single(commit => commit.Sequence > resume.StorageSequence);
        await Assert.That(rows.ContainsKey(apply.Document)).IsFalse();
        var delta = DeltaFor(
            resume.StorageSequence,
            next.StorageSequence,
            [new TerraScaleViewInsert(IdentityOf(apply.Document), PositionOf(apply.Document), RowValue(PutOf(apply)))],
            TokenOf(next),
            $"digest-{next.StorageSequence}");
        var delivery = engine.ApplyResultDelta(delta);
        await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        await Assert.That(delivery.Ack!.AppliedRevision).IsEqualTo(next.StorageSequence);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(next);
        await Assert.That(engine.AppliedRevision).IsEqualTo(next.StorageSequence);
        await Assert.That(engine.DeltasApplied).IsEqualTo(1ul);

        // The adopted checkpoint is resumable, and replaying the identical
        // revision (reconnect-replay's byte-identical re-delivery) is an
        // ignored duplicate that leaves the checkpoint untouched.
        engine.BeginResume();
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Resuming);
        var replay = engine.ApplyResultDelta(delta);
        await Assert.That(replay.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DuplicateDeltaIgnored);
        await Assert.That(engine.DuplicateDeltasIgnored).IsEqualTo(1ul);
        await Assert.That(engine.DeltasApplied).IsEqualTo(1ul);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(next);
    }

    /// <summary>
    /// The reconnect-replay vector through the engine: one delta batch
    /// covering the lost deliveries advances the reconnected session, the
    /// byte-identical re-delivery (a different correlation, the same
    /// content) is an ignored duplicate, and the acknowledged checkpoint
    /// is exactly the position the corpus records as owing nothing further.
    /// </summary>
    [Test]
    public async Task TheReconnectReplayVectorAppliesThenIgnoresTheRedelivery()
    {
        var vector = (await LoadVectorsAsync()).Single(entry => entry.Category == "reconnect-replay");
        var root = vector.Open();
        var commits = JournalOf(root);
        var served = ServeSteps(root);
        var from = TerraScaleSyncV2GoldenVectorTests.Checkpoint(served[0].GetProperty("from"));
        var next = TerraScaleSyncV2GoldenVectorTests.Checkpoint(served[0].GetProperty("expect_frame").GetProperty("next"));
        var reServed = TerraScaleSyncV2GoldenVectorTests.Checkpoint(served[1].GetProperty("expect_frame").GetProperty("next"));
        var acknowledged = CorpusCheckpoint(root, "checkpoint_ack", "checkpoint");
        await Assert.That(reServed).IsEqualTo(next);
        await Assert.That(from.CompareTo(next)).IsLessThan(0);

        var engine = new TerraScaleSyncV2ViewEngine();
        engine.Authenticate();
        engine.Subscribe();
        _ = PublishSnapshot(engine, BaseGeneration, from.StorageSequence, RowsOf(FoldTo(commits, from.StorageSequence)), TokenOf(from));

        // The batch covers every commit between the two checkpoints: the
        // first two documents insert and the third commit patches the
        // first document's fields (f000 replaced, f001 added).
        var covered = commits.Where(commit => commit.Sequence > from.StorageSequence && commit.Sequence <= next.StorageSequence).ToList();
        await Assert.That(covered.Count).IsEqualTo(3);
        var operations = new List<TerraScaleViewResultOperation>();
        foreach (var commit in covered)
        {
            var knownDocument = FoldBefore(commits, commit.Sequence).ContainsKey(commit.Document);
            operations.Add(commit.Delete || !knownDocument
                ? new TerraScaleViewInsert(IdentityOf(commit.Document), PositionOf(commit.Document), RowValue(PutOf(commit)))
                : PatchOf(commit));
        }

        var delta = DeltaFor(from.StorageSequence, next.StorageSequence, operations, TokenOf(next), $"digest-{next.StorageSequence}");
        var applied = engine.ApplyResultDelta(delta);
        await Assert.That(applied.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        await Assert.That(acknowledged).IsEqualTo(CheckpointOf(engine.Checkpoint!.Token));
        var patchedRow = engine.AuthoritativeView!.Rows.Single(row => row.Identity.DocumentId == "d-01");
        await Assert.That(patchedRow.Value.GetProperty("f000").GetString()).IsEqualTo("v0001b");
        await Assert.That(patchedRow.Value.GetProperty("f001").GetString()).IsEqualTo("3");

        // The second connection re-delivers identical content: ignored.
        engine.BeginResume();
        var replay = engine.ApplyResultDelta(delta);
        await Assert.That(replay.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DuplicateDeltaIgnored);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(next);

        // The corpus records that nothing further is served from the
        // acknowledged checkpoint; the engine holds exactly that position.
        var final = served[^1];
        await Assert.That(final.GetProperty("expect_wire").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(TerraScaleSyncV2GoldenVectorTests.Checkpoint(final.GetProperty("from"))).IsEqualTo(next);
    }

    // --- Patch application ------------------------------------------------------

    /// <summary>
    /// Every corpus journal replays through the view engine: each
    /// <c>apply</c> step is one result delta (a patch of the changed
    /// fields for a known document, an insert for a new one, a removal for
    /// a delete), and the final authorized result equals the corpus's own
    /// independent journal fold — the same commit stream, the same state.
    /// </summary>
    [Test]
    public async Task EveryCorpusJournalReplaysThroughTheEngineAsPatches()
    {
        var expectedEmpty = new HashSet<string>(["malformed-frame-rejection"], StringComparer.Ordinal);
        var replayed = 0;
        var patchedFields = 0L;
        foreach (var vector in await LoadVectorsAsync())
        {
            var root = vector.Open();
            var commits = JournalOf(root);
            if (commits.Count == 0)
            {
                await Assert.That(expectedEmpty.Contains(vector.Category)).IsTrue();
                continue;
            }

            var before = commits.Where(commit => commit.Source == CorpusSource.JournalBefore).ToList();
            var applies = commits.Where(commit => commit.Source == CorpusSource.Apply).ToList();
            var baseSequence = before.Count == 0 ? 0UL : before.Max(commit => commit.Sequence);
            await Assert.That(applies.Count == 0 || applies.Min(commit => commit.Sequence) > baseSequence).IsTrue();

            var engine = new TerraScaleSyncV2ViewEngine();
            engine.Authenticate();
            engine.Subscribe();
            var state = FoldTo(commits, baseSequence);
            _ = PublishSnapshot(engine, BaseGeneration, baseSequence, RowsOf(state), TokenOf(CheckpointAt(baseSequence)));
            await Assert.That(
                engine.AuthoritativeView!.Rows.Select(row => row.Identity.DocumentId).SequenceEqual(state.Keys, StringComparer.Ordinal))
                .IsTrue();

            var revision = baseSequence;
            foreach (var commit in applies)
            {
                var knownDocument = engine.AuthoritativeView!.Rows.Any(row => row.Identity.DocumentId == commit.Document);
                TerraScaleViewResultOperation operation = commit.Delete
                    ? new TerraScaleViewRemove(IdentityOf(commit.Document))
                    : knownDocument
                        ? PatchOf(commit)
                        : new TerraScaleViewInsert(IdentityOf(commit.Document), PositionOf(commit.Document), RowValue(PutOf(commit)));
                var delivery = engine.ApplyResultDelta(DeltaFor(
                    revision,
                    commit.Sequence,
                    [operation],
                    TokenOf(CheckpointAt(commit.Sequence)),
                    $"digest-{commit.Sequence}"));
                await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
                await Assert.That(delivery.Ack!.AppliedRevision).IsEqualTo(commit.Sequence);
                revision = commit.Sequence;
                replayed++;

                // The patch landed exactly: every put field of the corpus
                // commit is the row's current field value.
                if (!commit.Delete && knownDocument)
                {
                    var row = engine.AuthoritativeView!.Rows.Single(candidate => candidate.Identity.DocumentId == commit.Document);
                    foreach (var (field, value) in commit.Put)
                    {
                        await Assert.That(SameJson(row.Value.GetProperty(field), value)).IsTrue();
                        patchedFields++;
                    }
                }
            }

            // The final result is the corpus's independent fold of the same
            // journal: identical documents, identical values, canonical order.
            var final = FoldTo(commits, ulong.MaxValue);
            var view = engine.AuthoritativeView!;
            await Assert.That(view.Rows.Select(row => row.Identity.DocumentId).SequenceEqual(final.Keys, StringComparer.Ordinal)).IsTrue();
            foreach (var (document, fields) in final)
            {
                var row = view.Rows.Single(candidate => candidate.Identity.DocumentId == document);
                await Assert.That(SameJson(row.Value, RowValue(fields))).IsTrue();
            }

            await Assert.That(engine.Checkpoint!.Token).IsEqualTo(TokenOf(CheckpointAt(revision)));
        }

        // The replay loop covered the corpus's actual commit stream.
        await Assert.That(replayed).IsEqualTo(70);
        await Assert.That(patchedFields).IsGreaterThanOrEqualTo(30);
    }

    // --- Reset ------------------------------------------------------------------

    /// <summary>
    /// The resync-required vector drives the reset lifecycle through the
    /// engine: a session stalled behind the retained floor drops its
    /// checkpoint (the corpus's <c>resync_required</c> answer), adopts the
    /// recorded snapshot checkpoint as its state, owes nothing further (the
    /// corpus records no frame from that position), and a later
    /// <c>Reset</c> result operation replaces the complete result while
    /// surviving duplicate re-delivery.
    /// </summary>
    [Test]
    public async Task TheResyncRequiredVectorDrivesResetThroughTheEngine()
    {
        var vector = (await LoadVectorsAsync()).Single(entry => entry.Category == "resync-required");
        var root = vector.Open();
        var commits = JournalOf(root);
        var stalled = ServeSteps(root)
            .Single(serve => serve.TryGetProperty("expect_frame", out var described)
                && described.TryGetProperty("kind", out var kind)
                && kind.GetString() == "resync_required");
        var from = TerraScaleSyncV2GoldenVectorTests.Checkpoint(stalled.GetProperty("from"));
        var floor = TerraScaleSyncV2GoldenVectorTests.Checkpoint(stalled.GetProperty("expect_frame").GetProperty("floor"));
        var snapshot = TerraScaleSyncV2GoldenVectorTests.Checkpoint(RecordedFrames(root, "server_frame")
            .Single(frame => frame.TryGetProperty("kind", out var kind) && kind.GetString() == "snapshot_answer")
            .GetProperty("checkpoint"));

        // The stall precondition the corpus records: the presented position
        // is behind the retained floor, and the floor is behind the snapshot.
        await Assert.That(from.CompareTo(floor)).IsLessThan(0);
        await Assert.That(floor.CompareTo(snapshot)).IsLessThan(0);

        var engine = new TerraScaleSyncV2ViewEngine();
        engine.Authenticate();
        engine.Subscribe();
        _ = PublishSnapshot(engine, BaseGeneration, from.StorageSequence, RowsOf(FoldTo(commits, from.StorageSequence)), TokenOf(from));
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Live);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(from);

        // The resync answer: the checkpoint fell below the retained floor;
        // it is dropped and the automatic authorized bootstrap follows.
        var expired = engine.NotifyCheckpointExpired();
        await Assert.That(expired.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.CheckpointExpired);
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Resetting);
        await Assert.That(engine.Checkpoint).IsNull();

        // The recorded snapshot is adopted from Resetting: its checkpoint
        // becomes the session state at the snapshot's sequence.
        var snapshotRows = RowsOf(FoldTo(commits, snapshot.StorageSequence));
        var adopted = PublishSnapshot(engine, generation: 2, snapshot.StorageSequence, snapshotRows, TokenOf(snapshot));
        await Assert.That(adopted.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SnapshotPublished);
        await Assert.That(engine.AppliedRevision).IsEqualTo(snapshot.StorageSequence);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(snapshot);

        // From the adopted position the corpus serves nothing: the engine
        // holds exactly the recorded no-further-delivery position.
        var final = ServeSteps(root)[^1];
        await Assert.That(final.GetProperty("expect_wire").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(TerraScaleSyncV2GoldenVectorTests.Checkpoint(final.GetProperty("from"))).IsEqualTo(snapshot);

        // A later Reset operation replaces the complete result with the same
        // corpus state (the engine's bounded fallback), advances the
        // checkpoint, and survives an at-least-once re-delivery.
        var resetRevision = snapshot.StorageSequence + 1;
        var resetDelta = DeltaFor(
            snapshot.StorageSequence,
            resetRevision,
            [new TerraScaleViewReset(snapshotRows)],
            TokenOf(CheckpointAt(resetRevision)),
            $"digest-reset-{resetRevision}",
            generation: 2);
        var reset = engine.ApplyResultDelta(resetDelta);
        await Assert.That(reset.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.ResetApplied);
        await Assert.That(engine.ResetsApplied).IsEqualTo(1ul);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(CheckpointAt(resetRevision));
        await Assert.That(
            engine.AuthoritativeView!.Rows.Select(row => row.Identity.DocumentId)
                .SequenceEqual(FoldTo(commits, snapshot.StorageSequence).Keys, StringComparer.Ordinal))
            .IsTrue();

        var duplicate = engine.ApplyResultDelta(resetDelta);
        await Assert.That(duplicate.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DuplicateDeltaIgnored);
        await Assert.That(engine.ResetsApplied).IsEqualTo(1ul);
        await Assert.That(engine.DeltasApplied).IsEqualTo(0ul);
    }

    // --- ADR-0024 extension corpus ---------------------------------------------

    /// <summary>
    /// The owner authorization-reset vector closes the revoked generation
    /// with <c>scope_refused</c>, records the rejected stale resume, and
    /// bootstraps a new engine from the fresh snapshot before acknowledging
    /// checkpoint (1,4).
    /// </summary>
    [Test]
    public async Task TheAuthorizationResetVectorRefusesStaleResumeThenAcknowledgesTheFreshSnapshot()
    {
        var vector = (await LoadVectorsAsync()).Single(entry => entry.Category == "authorization-reset");
        await Assert.That(vector.Corpus).IsEqualTo("terrascale.sync.v2-adr0024");
        var root = vector.Open();
        var steps = root.GetProperty("steps").EnumerateArray().ToList();
        var commits = JournalOf(root);

        var refusalStep = RecordedFrameStep(steps, "server_frame", "error", "scope_refused");
        var refusalFrame = DecodeRecordedFrame(refusalStep.Entry);
        var refusal = refusalFrame.Body as TerraScaleSyncV2ErrorBody
            ?? throw new InvalidOperationException("the authorization-reset refusal is not an error body");
        await Assert.That(refusal.Code).IsEqualTo(TerraScaleSyncV2ErrorCode.ScopeRefused);
        await Assert.That(refusal.Detail).IsEqualTo("authorization-generation-change");

        var setupStep = RecordedFrameStep(steps, "client_frame", "session_setup", after: refusalStep.Index);
        var setupFrame = DecodeRecordedFrame(setupStep.Entry);
        var setup = setupFrame.Body as TerraScaleSyncV2SessionSetupBody
            ?? throw new InvalidOperationException("the authorization-reset reconnect is not a session setup");
        var staleCheckpoint = setup.Resume
            ?? throw new InvalidOperationException("the authorization-reset reconnect does not carry its stale checkpoint");
        await Assert.That(staleCheckpoint).IsEqualTo(CheckpointAt(3));

        var acceptedStep = RecordedFrameStep(steps, "server_frame", "session_accepted", after: setupStep.Index);
        var acceptedFrame = DecodeRecordedFrame(acceptedStep.Entry);
        var accepted = acceptedFrame.Body as TerraScaleSyncV2SessionAcceptedBody
            ?? throw new InvalidOperationException("the authorization-reset bootstrap is not a session acceptance");

        var snapshotStep = steps.Select((step, index) => (Step: step, Index: index))
            .Single(candidate => candidate.Index > acceptedStep.Index && candidate.Step.TryGetProperty("serve_snapshot", out _));
        var snapshotEntry = snapshotStep.Step.GetProperty("serve_snapshot");
        var snapshotFrame = TerraScaleSyncV2Frame.Decode(
            TerraScaleSyncV2GoldenVectorTests.HexToBytes(snapshotEntry.GetProperty("expect_wire").GetString()!));
        await Assert.That(snapshotFrame.Verify()).IsTrue();
        var snapshot = snapshotFrame.Body as TerraScaleSyncV2SnapshotAnswerBody
            ?? throw new InvalidOperationException("the authorization-reset bootstrap does not carry a snapshot answer");

        var ackStep = RecordedFrameStep(steps, "client_frame", "checkpoint_ack", after: snapshotStep.Index);
        var ackFrame = DecodeRecordedFrame(ackStep.Entry);
        var recordedAck = ackFrame.Body as TerraScaleSyncV2CheckpointAckBody
            ?? throw new InvalidOperationException("the authorization-reset bootstrap does not carry a checkpoint acknowledgement");

        await Assert.That(refusalStep.Index < setupStep.Index).IsTrue();
        await Assert.That(setupStep.Index < acceptedStep.Index).IsTrue();
        await Assert.That(acceptedStep.Index < snapshotStep.Index).IsTrue();
        await Assert.That(snapshotStep.Index < ackStep.Index).IsTrue();
        await Assert.That(accepted.Server).IsEqualTo(snapshot.Checkpoint);
        await Assert.That(snapshot.Checkpoint).IsEqualTo(recordedAck.Checkpoint);
        await Assert.That(snapshot.Checkpoint).IsEqualTo(CheckpointAt(4));
        await Assert.That(steps.Skip(setupStep.Index).Any(step => step.TryGetProperty("serve_delta", out _))).IsFalse();

        var revokedEngine = new TerraScaleSyncV2ViewEngine();
        revokedEngine.Authenticate();
        revokedEngine.Subscribe();
        _ = PublishSnapshot(
            revokedEngine,
            BaseGeneration,
            staleCheckpoint.StorageSequence,
            RowsOf(FoldTo(commits, staleCheckpoint.StorageSequence)),
            TokenOf(staleCheckpoint));
        var closed = revokedEngine.NotifyAuthorizationChange(2, TerraScaleViewAuthorizationEffect.SessionRevoked);
        await Assert.That(closed.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SessionRevoked);
        await Assert.That(revokedEngine.Checkpoint).IsNull();
        var refusedResume = Assert.Throws<TerraScaleViewProtocolException>(() => revokedEngine.BeginResume());
        await Assert.That(refusedResume.Error).IsEqualTo(TerraScaleViewProtocolError.WrongState);

        var freshEngine = new TerraScaleSyncV2ViewEngine();
        freshEngine.Authenticate();
        freshEngine.Subscribe();
        var published = PublishSnapshot(
            freshEngine,
            BaseGeneration,
            snapshot.Checkpoint.StorageSequence,
            RowsOf(FoldTo(commits, snapshot.Checkpoint.StorageSequence)),
            TokenOf(snapshot.Checkpoint));
        await Assert.That(published.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SnapshotPublished);
        await Assert.That(published.Ack!.AppliedRevision).IsEqualTo(recordedAck.Checkpoint.StorageSequence);
        await Assert.That(CheckpointOf(freshEngine.Checkpoint!.Token)).IsEqualTo(recordedAck.Checkpoint);
        await Assert.That(freshEngine.AuthoritativeView!.Rows.Select(row => row.Identity.DocumentId)
            .SequenceEqual(["d-01", "d-03"], StringComparer.Ordinal)).IsTrue();
    }

    /// <summary>
    /// The owner slow-consumer-overload vector records the typed overload,
    /// resumes from checkpoint (1,2), applies only commits 3 through 6, and
    /// acknowledges checkpoint (1,6) without replaying the prefix.
    /// </summary>
    [Test]
    public async Task TheSlowConsumerOverloadVectorResumesWithOnlyTheUnacknowledgedSuffix()
    {
        var vector = (await LoadVectorsAsync()).Single(entry => entry.Category == "slow-consumer-overload");
        await Assert.That(vector.Corpus).IsEqualTo("terrascale.sync.v2-adr0024");
        var root = vector.Open();
        var steps = root.GetProperty("steps").EnumerateArray().ToList();
        var commits = JournalOf(root);

        var overloadStep = RecordedFrameStep(steps, "server_frame", "error", "overloaded");
        var overloadFrame = DecodeRecordedFrame(overloadStep.Entry);
        var overload = overloadFrame.Body as TerraScaleSyncV2ErrorBody
            ?? throw new InvalidOperationException("the slow-consumer refusal is not an error body");
        await Assert.That(overload.Code).IsEqualTo(TerraScaleSyncV2ErrorCode.Overloaded);
        await Assert.That(overload.Detail).IsEqualTo("slow-consumer-unacknowledged");

        var setupStep = RecordedFrameStep(steps, "client_frame", "session_setup", after: overloadStep.Index);
        var setupFrame = DecodeRecordedFrame(setupStep.Entry);
        var setup = setupFrame.Body as TerraScaleSyncV2SessionSetupBody
            ?? throw new InvalidOperationException("the overload reconnect is not a session setup");
        var resume = setup.Resume
            ?? throw new InvalidOperationException("the overload reconnect does not carry a checkpoint");
        await Assert.That(resume).IsEqualTo(CheckpointAt(2));

        var suffixStep = steps.Select((step, index) => (Step: step, Index: index))
            .Single(candidate => candidate.Index > setupStep.Index && candidate.Step.TryGetProperty("serve_delta", out _));
        var suffixEntry = suffixStep.Step.GetProperty("serve_delta");
        var suffixFrom = TerraScaleSyncV2GoldenVectorTests.Checkpoint(suffixEntry.GetProperty("from"));
        var suffixFrame = TerraScaleSyncV2Frame.Decode(
            TerraScaleSyncV2GoldenVectorTests.HexToBytes(suffixEntry.GetProperty("expect_wire").GetString()!));
        await Assert.That(suffixFrame.Verify()).IsTrue();
        var suffixBatch = suffixFrame.Body as TerraScaleSyncV2DeltaBatchBody
            ?? throw new InvalidOperationException("the overload resume does not carry a delta batch");

        var ackStep = RecordedFrameStep(steps, "client_frame", "checkpoint_ack", after: suffixStep.Index);
        var ackFrame = DecodeRecordedFrame(ackStep.Entry);
        var recordedAck = ackFrame.Body as TerraScaleSyncV2CheckpointAckBody
            ?? throw new InvalidOperationException("the overload resume does not carry a checkpoint acknowledgement");

        await Assert.That(suffixFrom).IsEqualTo(resume);
        await Assert.That(suffixBatch.Next).IsEqualTo(CheckpointAt(6));
        await Assert.That(suffixBatch.Commits).IsEqualTo(4ul);
        await Assert.That(recordedAck.Checkpoint).IsEqualTo(suffixBatch.Next);

        var prefix = commits.Where(commit => commit.Sequence <= resume.StorageSequence).ToList();
        var suffix = commits.Where(commit => commit.Sequence > resume.StorageSequence
                && commit.Sequence <= suffixBatch.Next.StorageSequence)
            .ToList();
        await Assert.That(prefix.Select(commit => commit.Sequence)).IsEquivalentTo((ulong[])[1, 2]);
        await Assert.That(suffix.Select(commit => commit.Sequence)).IsEquivalentTo((ulong[])[3, 4, 5, 6]);
        await Assert.That(suffix.Any(commit => commit.Document is "d-01" or "d-02")).IsFalse();

        var engine = new TerraScaleSyncV2ViewEngine();
        engine.Authenticate();
        engine.Subscribe();
        _ = PublishSnapshot(
            engine,
            BaseGeneration,
            resume.StorageSequence,
            RowsOf(FoldTo(commits, resume.StorageSequence)),
            TokenOf(resume));
        engine.BeginResume();

        var operations = suffix.Select(commit => (TerraScaleViewResultOperation)new TerraScaleViewInsert(
            IdentityOf(commit.Document),
            PositionOf(commit.Document),
            RowValue(PutOf(commit)))).ToList();
        var applied = engine.ApplyResultDelta(DeltaFor(
            resume.StorageSequence,
            suffixBatch.Next.StorageSequence,
            operations,
            TokenOf(suffixBatch.Next),
            "digest-slow-consumer-overload-resume"));

        await Assert.That(applied.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        await Assert.That(applied.Ack!.AppliedRevision).IsEqualTo(recordedAck.Checkpoint.StorageSequence);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(recordedAck.Checkpoint);
        await Assert.That(engine.DeltasApplied).IsEqualTo(1ul);
        var documentIds = engine.AuthoritativeView!.Rows.Select(row => row.Identity.DocumentId).ToArray();
        await Assert.That(documentIds.SequenceEqual(["d-01", "d-02", "d-03", "d-04", "d-05", "d-06"], StringComparer.Ordinal)).IsTrue();
        await Assert.That(documentIds.Count(document => document is "d-01" or "d-02")).IsEqualTo(2);
        await Assert.That(documentIds.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(6);
    }

    // --- Authorization-generation change ----------------------------------------

    /// <summary>
    /// Authorization generation changes over a corpus-adopted checkpoint:
    /// a stale generation fails closed; a visibility loss opens the new
    /// generation whose removals delta supersedes the old checkpoint; a
    /// revoked session drops the corpus checkpoint and can no longer
    /// resume; visibility gained and projection reduced deliver their new
    /// generations onto the same corpus state.
    /// </summary>
    [Test]
    public async Task AuthorizationGenerationChangesCloseCorpusCheckpoints()
    {
        var vector = (await LoadVectorsAsync()).Single(entry => entry.Category == "checkpoint-resume");
        var root = vector.Open();
        var commits = JournalOf(root);
        var resume = CorpusCheckpoint(root, "session_setup", "resume");
        var next = CorpusServeNext(root);
        var d03 = commits.Single(commit => commit.Document == "d-03");

        TerraScaleSyncV2ViewEngine LiveAt(ulong through, TerraScaleSyncV2Checkpoint checkpoint)
        {
            var engine = new TerraScaleSyncV2ViewEngine();
            engine.Authenticate();
            engine.Subscribe();
            _ = PublishSnapshot(engine, BaseGeneration, checkpoint.StorageSequence, RowsOf(FoldTo(commits, through)), TokenOf(checkpoint));
            return engine;
        }

        // A stale authorization generation is a typed refusal.
        var engine = LiveAt(resume.StorageSequence, resume);
        var stale = Assert.Throws<TerraScaleViewProtocolException>(() =>
            engine.NotifyAuthorizationChange(engine.AuthorizationGeneration, TerraScaleViewAuthorizationEffect.VisibilityLost));
        await Assert.That(stale.Error).IsEqualTo(TerraScaleViewProtocolError.InvalidAuthorization);

        // Visibility lost: the new generation opens, the removals delta
        // applies from revision zero, and the corpus checkpoint that was
        // adopted for the old generation is superseded.
        var advanced = engine.NotifyAuthorizationChange(2, TerraScaleViewAuthorizationEffect.VisibilityLost);
        await Assert.That(advanced.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.ViewGenerationAdvanced);
        await Assert.That(engine.AuthorizationGeneration).IsEqualTo(2ul);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(resume);

        var successor = CheckpointAt(next.StorageSequence + 1);
        var removals = engine.ApplyResultDelta(DeltaFor(
            fromRevision: 0,
            toRevision: 1,
            [new TerraScaleViewRemove(IdentityOf("d-02"))],
            TokenOf(successor),
            "digest-authorization-generation-2",
            generation: 2));
        await Assert.That(removals.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        await Assert.That(engine.ViewGeneration).IsEqualTo(2ul);
        await Assert.That(engine.AuthorizationGeneration).IsEqualTo(2ul);
        await Assert.That(CheckpointOf(engine.Checkpoint!.Token)).IsEqualTo(successor);
        await Assert.That(engine.AuthoritativeView!.Rows.Select(row => row.Identity.DocumentId).SequenceEqual(["d-01"], StringComparer.Ordinal)).IsTrue();

        // Revocation: the checkpoint is dropped and the session closes; the
        // corpus-derived position is not resumable and no view work applies.
        var revoked = engine.NotifyAuthorizationChange(3, TerraScaleViewAuthorizationEffect.SessionRevoked);
        await Assert.That(revoked.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SessionRevoked);
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Closing);
        await Assert.That(engine.Checkpoint).IsNull();
        var refusedResume = Assert.Throws<TerraScaleViewProtocolException>(() => engine.BeginResume());
        await Assert.That(refusedResume.Error).IsEqualTo(TerraScaleViewProtocolError.WrongState);
        var refusedDelta = Assert.Throws<TerraScaleViewProtocolException>(() => engine.ApplyResultDelta(
            DeltaFor(1, 2, [new TerraScaleViewRemove(IdentityOf("d-01"))], TokenOf(CheckpointAt(5)), "digest-refused")));
        await Assert.That(refusedDelta.Error).IsEqualTo(TerraScaleViewProtocolError.WrongState);

        // Visibility gained: the newly authorized corpus document appears in
        // the new generation without disturbing the retained rows.
        var gained = LiveAt(resume.StorageSequence, resume);
        _ = gained.NotifyAuthorizationChange(2, TerraScaleViewAuthorizationEffect.VisibilityGained);
        var granted = gained.ApplyResultDelta(DeltaFor(
            fromRevision: 0,
            toRevision: 1,
            [new TerraScaleViewInsert(IdentityOf("d-03"), PositionOf("d-03"), RowValue(PutOf(d03)))],
            TokenOf(CheckpointAt(next.StorageSequence)),
            "digest-visibility-gained",
            generation: 2));
        await Assert.That(granted.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        await Assert.That(gained.ViewGeneration).IsEqualTo(2ul);
        await Assert.That(
            gained.AuthoritativeView!.Rows.Select(row => row.Identity.DocumentId)
                .SequenceEqual(["d-01", "d-02", "d-03"], StringComparer.Ordinal))
            .IsTrue();

        // Field projection reduced: the forbidden field leaves the corpus
        // row in the new generation while the permitted fields stay.
        var reduced = LiveAt(next.StorageSequence, next);
        _ = reduced.NotifyAuthorizationChange(2, TerraScaleViewAuthorizationEffect.FieldProjectionReduced);
        var projected = reduced.ApplyResultDelta(DeltaFor(
            fromRevision: 0,
            toRevision: 1,
            [new TerraScaleViewPatch(
                IdentityOf("d-03"),
                [new TerraScaleViewPatchStep("/f001", TerraScaleViewPatchStepKind.Remove)])],
            TokenOf(CheckpointAt(next.StorageSequence + 1)),
            "digest-projection-reduced",
            generation: 2));
        await Assert.That(projected.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        var projectedRow = reduced.AuthoritativeView!.Rows.Single(row => row.Identity.DocumentId == "d-03");
        await Assert.That(projectedRow.Value.TryGetProperty("f001", out _)).IsFalse();
        await Assert.That(projectedRow.Value.GetProperty("f000").GetString()).IsEqualTo("v0003");
    }

    // --- Corpus helpers ----------------------------------------------------------

    private enum CorpusSource
    {
        JournalBefore,
        Apply,
    }

    private sealed record CorpusCommit(
        ulong Sequence,
        string Document,
        string OperationId,
        CorpusSource Source,
        bool Delete,
        IReadOnlyList<(string Field, JsonElement Value)> Put);

    /// <summary>The vector's full journal: the server's <c>journal_before</c> commits plus every <c>apply</c> step, in corpus order.</summary>
    private static List<CorpusCommit> JournalOf(JsonElement root)
    {
        var commits = new List<CorpusCommit>();
        if (root.GetProperty("server").TryGetProperty("journal_before", out var before) && before.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in before.EnumerateArray())
            {
                commits.Add(CommitFrom(entry, CorpusSource.JournalBefore));
            }
        }

        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            if (step.TryGetProperty("apply", out var apply))
            {
                commits.Add(CommitFrom(apply, CorpusSource.Apply));
            }
        }

        return commits;
    }

    private static CorpusCommit CommitFrom(JsonElement entry, CorpusSource source)
    {
        var put = new List<(string Field, JsonElement Value)>();
        if (entry.TryGetProperty("put", out var fields) && fields.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in fields.EnumerateObject())
            {
                put.Add((field.Name, field.Value.Clone()));
            }
        }

        var delete = entry.TryGetProperty("delete", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new CorpusCommit(
            entry.GetProperty("sequence").GetUInt64(),
            entry.GetProperty("document").GetString()!,
            entry.GetProperty("operation_id").GetString()!,
            source,
            delete,
            put);
    }

    /// <summary>The independent corpus fold: last commit per document wins, deletes remove, ordinal document order.</summary>
    private static SortedDictionary<string, Dictionary<string, JsonElement>> FoldTo(IReadOnlyList<CorpusCommit> commits, ulong throughSequence) =>
        FoldWhere(commits, commit => commit.Sequence <= throughSequence);

    /// <summary>The corpus fold of everything strictly before a storage sequence (the state a commit applies onto).</summary>
    private static SortedDictionary<string, Dictionary<string, JsonElement>> FoldBefore(IReadOnlyList<CorpusCommit> commits, ulong sequence) =>
        FoldWhere(commits, commit => commit.Sequence < sequence);

    private static SortedDictionary<string, Dictionary<string, JsonElement>> FoldWhere(IReadOnlyList<CorpusCommit> commits, Func<CorpusCommit, bool> scope)
    {
        var documents = new SortedDictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var commit in commits.Where(scope))
        {
            if (commit.Delete)
            {
                documents.Remove(commit.Document);
                continue;
            }

            if (!documents.TryGetValue(commit.Document, out var fields))
            {
                documents[commit.Document] = fields = [];
            }

            foreach (var (field, value) in commit.Put)
            {
                fields[field] = value;
            }
        }

        return documents;
    }

    private static Dictionary<string, JsonElement> PutOf(CorpusCommit commit) =>
        commit.Put.ToDictionary(field => field.Field, field => field.Value);

    private static JsonElement RowValue(IReadOnlyDictionary<string, JsonElement> fields)
    {
        var node = new JsonObject();
        foreach (var field in fields.OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            node[field.Key] = JsonNode.Parse(field.Value.GetRawText());
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static List<TerraScaleViewRow> RowsOf(SortedDictionary<string, Dictionary<string, JsonElement>> documents) =>
        documents.Select(entry => new TerraScaleViewRow(IdentityOf(entry.Key), PositionOf(entry.Key), RowValue(entry.Value))).ToList();

    private static TerraScaleViewIdentity IdentityOf(string document) => new(document, incarnation: 1);

    private static TerraScaleViewPosition PositionOf(string document) => new(document);

    /// <summary>A corpus journal position's checkpoint: the single server writer (peer 1) through the named storage sequence.</summary>
    private static TerraScaleSyncV2Checkpoint CheckpointAt(ulong storageSequence) =>
        TerraScaleSyncV2Checkpoint.Create([(1ul, checked((int)storageSequence))], storageSequence);

    private static string TokenOf(TerraScaleSyncV2Checkpoint checkpoint) => TokenPrefix + Convert.ToBase64String(checkpoint.ToBytes());

    private static TerraScaleSyncV2Checkpoint CheckpointOf(string token) =>
        TerraScaleSyncV2Checkpoint.FromBytes(Convert.FromBase64String(token[TokenPrefix.Length..]));

    private static TerraScaleViewResultDelta DeltaFor(
        ulong fromRevision,
        ulong toRevision,
        IReadOnlyList<TerraScaleViewResultOperation> operations,
        string checkpointToken,
        string digest,
        ulong generation = BaseGeneration) =>
        new(
            ViewId,
            generation,
            fromRevision,
            toRevision,
            new TerraScaleViewCausalCut($"cut-{toRevision}"),
            operations,
            new TerraScaleViewDigest(digest),
            new TerraScaleViewCheckpoint(checkpointToken));

    private static TerraScaleViewPatch PatchOf(CorpusCommit commit) => new(
        IdentityOf(commit.Document),
        [.. commit.Put.Select(field => new TerraScaleViewPatchStep(
            $"/{field.Field}",
            TerraScaleViewPatchStepKind.Replace,
            field.Value))]);

    /// <summary>Publishes one validated chunked snapshot and returns the delivery.</summary>
    private static TerraScaleViewDelivery PublishSnapshot(
        TerraScaleSyncV2ViewEngine engine,
        ulong generation,
        ulong baseRevision,
        IReadOnlyList<TerraScaleViewRow> rows,
        string checkpointToken)
    {
        engine.BeginSnapshot(new TerraScaleViewSnapshotBegin(
            ViewId,
            generation,
            baseRevision,
            new TerraScaleViewCausalCut($"cut-{baseRevision}"),
            schemaGeneration: 1,
            "corpus-orders-v1",
            declaredRowBound: Math.Max(64, rows.Count),
            declaredByteBound: 64 * 1024 * 1024));
        var chunks = rows.Chunk(8).ToList();
        if (chunks.Count == 0)
        {
            engine.ApplySnapshotChunk(new TerraScaleViewSnapshotChunk(generation, 1, [], "chunk-1"));
        }
        else
        {
            for (var index = 0; index < chunks.Count; index++)
            {
                engine.ApplySnapshotChunk(new TerraScaleViewSnapshotChunk(generation, index + 1, chunks[index], $"chunk-{index + 1}"));
            }
        }

        return engine.CompleteSnapshot(new TerraScaleViewSnapshotEnd(
            generation,
            baseRevision,
            new TerraScaleViewDigest($"digest-{generation}-{baseRevision}"),
            new TerraScaleViewCheckpoint(checkpointToken)));
    }

    /// <summary>The checkpoint a recorded client frame of one kind carries in one named property.</summary>
    private static TerraScaleSyncV2Checkpoint CorpusCheckpoint(JsonElement root, string kind, string property) =>
        TerraScaleSyncV2GoldenVectorTests.Checkpoint(RecordedFrames(root, "client_frame")
            .Single(frame => frame.GetProperty("kind").GetString() == kind)
            .GetProperty(property));

    /// <summary>The next checkpoint the vector's first recorded delta answer serves.</summary>
    private static TerraScaleSyncV2Checkpoint CorpusServeNext(JsonElement root) => TerraScaleSyncV2GoldenVectorTests.Checkpoint(
        ServeSteps(root)
            .First(serve => serve.TryGetProperty("expect_frame", out var described) && described.TryGetProperty("next", out _))
            .GetProperty("expect_frame")
            .GetProperty("next"));

    /// <summary>Recursively collects every structured checkpoint description (exactly the <c>ends</c> and <c>storage_sequence</c> pair) in one vector.</summary>
    private static void CollectCheckpoints(JsonElement node, string category, List<(string Category, JsonElement Checkpoint)> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                var names = node.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
                if (names.Count == 2
                    && names[0] == "ends" && names[1] == "storage_sequence"
                    && node.GetProperty("ends").ValueKind == JsonValueKind.Array
                    && node.GetProperty("storage_sequence").ValueKind == JsonValueKind.Number)
                {
                    found.Add((category, node.Clone()));
                    return;
                }

                foreach (var property in node.EnumerateObject())
                {
                    CollectCheckpoints(property.Value, category, found);
                }

                break;
            case JsonValueKind.Array:
                foreach (var entry in node.EnumerateArray())
                {
                    CollectCheckpoints(entry, category, found);
                }

                break;
        }
    }

    /// <summary>Structural JSON equality: key order and formatting are not state.</summary>
    private static bool SameJson(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftNames = left.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToList();
                var rightNames = right.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToList();
                return leftNames.SequenceEqual(rightNames, StringComparer.Ordinal)
                    && leftNames.All(name => SameJson(left.GetProperty(name), right.GetProperty(name)));
            case JsonValueKind.Array:
                return left.GetArrayLength() == right.GetArrayLength()
                    && left.EnumerateArray().Zip(right.EnumerateArray(), SameJson).All(same => same);
            default:
                return JsonElement.DeepEquals(left, right);
        }
    }
}
