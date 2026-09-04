using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

/// <summary>
/// The ADR-0024 client-side view-engine rows: the logical session states,
/// snapshot assembly and its atomic publish, the result-delta rules
/// (duplicate, gap, removals before key reuse, move-with-update,
/// incarnation identity, top-k entry and eviction, reset), the checkpoint
/// and acknowledgement lifecycle, the authorization transitions, and the
/// reversible optimistic presentation. These rows are semantic
/// state-machine coverage: wire binding and golden-vector conformance
/// land with the owning public contract and corpus.
/// </summary>
public sealed class TerraScaleSyncV1ViewEngineTests
{
    private static readonly TerraScaleViewId ViewId = new(Guid.Parse("0123456789abcdef0123456789abcdef"));

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static TerraScaleViewIdentity Id(string documentId, ulong incarnation = 1) => new(documentId, incarnation);

    private static TerraScaleViewPosition Pos(string key) => new(key);

    private static TerraScaleViewRow Row(string documentId, string position, string raw, ulong incarnation = 1) =>
        new(Id(documentId, incarnation), Pos(position), Json(raw));

    private static TerraScaleViewSnapshotBegin Begin(
        ulong generation = 1,
        ulong baseRevision = 10,
        int rowBound = 64,
        long byteBound = 64 * 1024) =>
        new(ViewId, generation, baseRevision, new("cut-10"), schemaGeneration: 4, "orders-v1", rowBound, byteBound);

    private static TerraScaleViewSnapshotChunk Chunk(ulong generation, int ordinal, params TerraScaleViewRow[] rows) =>
        new(generation, ordinal, rows, $"chunk-{ordinal}");

    private static TerraScaleViewSnapshotEnd End(
        ulong generation = 1,
        ulong baseRevision = 10,
        string digest = "digest-10",
        string checkpoint = "checkpoint-10") =>
        new(generation, baseRevision, new(digest), new(checkpoint));

    private static TerraScaleViewResultDelta Delta(
        ulong toRevision,
        params TerraScaleViewResultOperation[] operations) =>
        Delta(toRevision, operations, fromRevision: 10, generation: 1, digest: "digest-next", checkpoint: "checkpoint-next");

    private static TerraScaleViewResultDelta Delta(
        ulong toRevision,
        TerraScaleViewResultOperation[] operations,
        ulong fromRevision,
        ulong generation,
        string digest,
        string checkpoint) =>
        new(ViewId, generation, fromRevision, toRevision, new("cut-next"), operations, new(digest), new(checkpoint));

    /// <summary>Drives one engine to <c>Live</c> through a validated two-chunk snapshot.</summary>
    private static TerraScaleSyncV1ViewEngine LiveEngine(params TerraScaleViewRow[] rows)
    {
        var engine = new TerraScaleSyncV1ViewEngine();
        engine.Authenticate();
        engine.Subscribe();
        engine.BeginSnapshot(Begin());
        var half = Math.Max(rows.Length / 2, 1);
        if (rows.Length > 0)
        {
            engine.ApplySnapshotChunk(Chunk(1, 1, rows[..half]));
            if (rows.Length > half)
            {
                engine.ApplySnapshotChunk(Chunk(1, 2, rows[half..]));
            }
        }
        else
        {
            engine.ApplySnapshotChunk(Chunk(1, 1));
        }

        engine.CompleteSnapshot(End());
        return engine;
    }

    private static async Task<TerraScaleViewProtocolException> Rejects(Action action) =>
        (await Assert.ThrowsAsync<TerraScaleViewProtocolException>(() => { action(); return Task.CompletedTask; }))!;

    /// <summary>The session states advance Connecting → Authenticated → Subscribing → Snapshotting → Live.</summary>
    [Test]
    public async Task SessionStatesAdvanceThroughSubscriptionToLive()
    {
        var engine = new TerraScaleSyncV1ViewEngine();
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Connecting);

        engine.Authenticate();
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Authenticated);

        engine.Subscribe();
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Subscribing);

        engine.BeginSnapshot(Begin());
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Snapshotting);

        engine.ApplySnapshotChunk(Chunk(1, 1, Row("a", "01", """{"name":"alpha"}""")));
        await Assert.That(engine.CurrentView).IsNull();

        var delivery = engine.CompleteSnapshot(End());
        await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SnapshotPublished);
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Live);
    }

    /// <summary>A message received in the wrong state is a typed protocol error that allocates no view work.</summary>
    [Test]
    public async Task WrongStateMessagesAreTypedProtocolErrors()
    {
        var engine = new TerraScaleSyncV1ViewEngine();
        engine.Authenticate();

        var deltaInSubscribing = await Rejects(() => engine.ApplyResultDelta(Delta(11, [new TerraScaleViewRemove(Id("a"))])));
        await Assert.That(deltaInSubscribing.Error).IsEqualTo(TerraScaleViewProtocolError.WrongState);

        var chunkWithoutBegin = await Rejects(() => engine.ApplySnapshotChunk(Chunk(1, 1)));
        await Assert.That(chunkWithoutBegin.Error).IsEqualTo(TerraScaleViewProtocolError.WrongState);

        var endWithoutBegin = await Rejects(() => engine.CompleteSnapshot(End()));
        await Assert.That(endWithoutBegin.Error).IsEqualTo(TerraScaleViewProtocolError.WrongState);

        await Assert.That(engine.CurrentView).IsNull();
    }

    /// <summary>The snapshot publishes only when the complete assembly validated, and the ack carries the adopted revision and digest.</summary>
    [Test]
    public async Task SnapshotPublishesAtomicallyWithAdoptedCheckpointAndAck()
    {
        var engine = LiveEngine(
            Row("b", "02", """{"name":"bravo"}"""),
            Row("a", "01", """{"name":"alpha"}"""),
            Row("c", "03", """{"name":"charlie"}"""));

        var view = engine.CurrentView;
        await Assert.That(view).IsNotNull();
        await Assert.That(string.Join(",", view!.Rows.Select(row => row.Identity.DocumentId))).IsEqualTo("a,b,c");
        await Assert.That(view.Revision).IsEqualTo(10ul);
        await Assert.That(view.ResultDigest.Value).IsEqualTo("digest-10");
        await Assert.That(view.Optimistic).IsFalse();

        await Assert.That(engine.Checkpoint!.Token).IsEqualTo("checkpoint-10");
        await Assert.That(engine.SnapshotsPublished).IsEqualTo(1ul);
    }

    /// <summary>An interrupted snapshot replaces nothing: a restart discards the partial assembly and publishes only its own rows.</summary>
    [Test]
    public async Task InterruptedSnapshotReplacesNothingAndRestartsSafely()
    {
        var engine = new TerraScaleSyncV1ViewEngine();
        engine.Authenticate();
        engine.Subscribe();
        engine.BeginSnapshot(Begin());
        engine.ApplySnapshotChunk(Chunk(1, 1, Row("stale", "01", """{"name":"stale"}""")));

        // The restart discards the partial assembly.
        engine.BeginSnapshot(Begin(baseRevision: 20));
        engine.ApplySnapshotChunk(Chunk(1, 1, Row("fresh", "01", """{"name":"fresh"}""")));
        var delivery = engine.CompleteSnapshot(End(baseRevision: 20, digest: "digest-20", checkpoint: "checkpoint-20"));

        await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SnapshotPublished);
        var view = engine.CurrentView!;
        await Assert.That(view.Rows.Count).IsEqualTo(1);
        await Assert.That(view.Rows[0].Identity.DocumentId).IsEqualTo("fresh");
        await Assert.That(engine.AppliedRevision).IsEqualTo(20ul);
    }

    /// <summary>A broken chunk ordinal fails closed: nothing publishes and the typed error names the snapshot.</summary>
    [Test]
    public async Task BrokenChunkOrdinalFailsClosed()
    {
        var engine = new TerraScaleSyncV1ViewEngine();
        engine.Authenticate();
        engine.Subscribe();
        engine.BeginSnapshot(Begin());
        engine.ApplySnapshotChunk(Chunk(1, 1, Row("a", "01", """{"name":"alpha"}""")));

        var broken = await Rejects(() => engine.ApplySnapshotChunk(Chunk(1, 3, Row("c", "03", """{"name":"charlie"}"""))));
        await Assert.That(broken.Error).IsEqualTo(TerraScaleViewProtocolError.MalformedSnapshot);
        await Assert.That(engine.CurrentView).IsNull();
    }

    /// <summary>A repeated identity inside one assembly fails closed.</summary>
    [Test]
    public async Task RepeatedIdentityInsideAnAssemblyFailsClosed()
    {
        var engine = new TerraScaleSyncV1ViewEngine();
        engine.Authenticate();
        engine.Subscribe();
        engine.BeginSnapshot(Begin());
        engine.ApplySnapshotChunk(Chunk(1, 1, Row("a", "01", """{"name":"alpha"}""")));

        var repeated = await Rejects(() => engine.ApplySnapshotChunk(Chunk(1, 2, Row("a", "01", """{"name":"alpha-again"}"""))));
        await Assert.That(repeated.Error).IsEqualTo(TerraScaleViewProtocolError.MalformedSnapshot);
    }

    /// <summary>A delta whose to_revision is already applied is an ignored at-least-once duplicate.</summary>
    [Test]
    public async Task DuplicateDeltaIsIgnoredWithoutTouchingTheCheckpoint()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var duplicate = engine.ApplyResultDelta(Delta(10, [new TerraScaleViewRemove(Id("a"))], fromRevision: 9, generation: 1, digest: "digest-10", checkpoint: "checkpoint-10"));

        await Assert.That(duplicate.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DuplicateDeltaIgnored);
        await Assert.That(engine.DuplicateDeltasIgnored).IsEqualTo(1ul);
        await Assert.That(engine.CurrentView!.Rows.Count).IsEqualTo(1);
        await Assert.That(engine.Checkpoint!.Token).IsEqualTo("checkpoint-10");
    }

    /// <summary>A duplicate revision with a different complete-result digest is divergence, not a harmless replay.</summary>
    [Test]
    public async Task DuplicateRevisionWithDigestMismatchRequiresReset()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var diverged = engine.ApplyResultDelta(Delta(
            10,
            [new TerraScaleViewRemove(Id("a"))],
            fromRevision: 9,
            generation: 1,
            digest: "different-result",
            checkpoint: "checkpoint-other"));

        await Assert.That(diverged.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.ViewDiverged);
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Resetting);
        await Assert.That(engine.Checkpoint).IsNull();
    }

    /// <summary>A from_revision gap applies nothing: resume or reset is required.</summary>
    [Test]
    public async Task RevisionGapAppliesNothingAndSignalsResumeOrReset()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var gap = engine.ApplyResultDelta(Delta(30, [new TerraScaleViewRemove(Id("a"))], fromRevision: 25, generation: 1, digest: "digest-30", checkpoint: "checkpoint-30"));

        await Assert.That(gap.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.RevisionGapDetected);
        await Assert.That(engine.RevisionGapsDetected).IsEqualTo(1ul);
        await Assert.That(engine.CurrentView!.Rows.Count).IsEqualTo(1);
        await Assert.That(engine.AppliedRevision).IsEqualTo(10ul);
    }

    /// <summary>One delta applies update, patch, remove, insert, and move atomically: identity selects the row and the move accompanies the update.</summary>
    [Test]
    public async Task DeltaAppliesAllOperationsAtomicallyInCanonicalOrder()
    {
        var engine = LiveEngine(
            Row("a", "02", """{"name":"alpha","score":5}"""),
            Row("b", "01", """{"name":"bravo","flags":{"x":true,"y":2}}"""),
            Row("c", "03", """{"name":"charlie"}"""));

        var delivery = engine.ApplyResultDelta(Delta(11,
            new TerraScaleViewUpdate(Id("a"), Json("""{"name":"alpha-2","score":9}""")),
            new TerraScaleViewPatch(Id("b"), [new("/name", TerraScaleViewPatchStepKind.Replace, Json("\"bravo-2\"")), new("/flags/x", TerraScaleViewPatchStepKind.Remove)]),
            new TerraScaleViewRemove(Id("c")),
            new TerraScaleViewInsert(Id("d"), Pos("00"), Json("""{"name":"delta"}""")),
            new TerraScaleViewMove(Id("a"), Pos("02"), Pos("04"))));

        await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        await Assert.That(delivery.Ack).IsNotNull();
        await Assert.That(delivery.Ack!.AppliedRevision).IsEqualTo(11ul);
        await Assert.That(delivery.Ack.ResultDigest.Value).IsEqualTo("digest-next");

        var view = engine.CurrentView!;
        await Assert.That(string.Join(",", view.Rows.Select(row => row.Identity.DocumentId))).IsEqualTo("d,b,a");
        await Assert.That(view.Rows[2].Position.Key).IsEqualTo("04");
        await Assert.That(view.Rows[2].Value.GetProperty("name").GetString()).IsEqualTo("alpha-2");
        await Assert.That(view.Rows[1].Value.GetProperty("name").GetString()).IsEqualTo("bravo-2");
        await Assert.That(view.Rows[1].Value.GetProperty("flags").TryGetProperty("x", out _)).IsFalse();
        await Assert.That(engine.AppliedRevision).IsEqualTo(11ul);
        await Assert.That(engine.Checkpoint!.Token).IsEqualTo("checkpoint-next");
    }

    /// <summary>Removals run before the inserts that reuse a visible key.</summary>
    [Test]
    public async Task RemovalsRunBeforeInsertsThatReuseAVisibleKey()
    {
        var engine = LiveEngine(Row("old", "01", """{"name":"old"}"""));

        var delivery = engine.ApplyResultDelta(Delta(11,
            new TerraScaleViewInsert(Id("new"), Pos("01"), Json("""{"name":"new"}""")),
            new TerraScaleViewRemove(Id("old"))));

        await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        var view = engine.CurrentView!;
        await Assert.That(view.Rows.Count).IsEqualTo(1);
        await Assert.That(view.Rows[0].Identity.DocumentId).IsEqualTo("new");
    }

    /// <summary>Document identity includes incarnation: a recreated document never aliases the deleted incarnation.</summary>
    [Test]
    public async Task IncarnationSeparatesRecreatedDocuments()
    {
        var engine = LiveEngine(Row("doc", "01", """{"name":"first"}""", incarnation: 1));

        var delivery = engine.ApplyResultDelta(Delta(11,
            new TerraScaleViewRemove(Id("doc", 1)),
            new TerraScaleViewInsert(Id("doc", 2), Pos("01"), Json("""{"name":"second"}"""))));

        await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        var view = engine.CurrentView!;
        await Assert.That(view.Rows.Count).IsEqualTo(1);
        await Assert.That(view.Rows[0].Identity.Incarnation).IsEqualTo(2ul);
        await Assert.That(view.Rows[0].Value.GetProperty("name").GetString()).IsEqualTo("second");
    }

    /// <summary>Top-k entry and eviction arrive as server operations: a row outside the result enters and evicts another.</summary>
    [Test]
    public async Task TopKEntryAndEvictionArriveAsServerOperations()
    {
        var engine = LiveEngine(
            Row("a", "01", """{"score":9}"""),
            Row("b", "02", """{"score":8}"""),
            Row("c", "03", """{"score":7}"""));

        var delivery = engine.ApplyResultDelta(Delta(11,
            new TerraScaleViewInsert(Id("d"), Pos("00"), Json("""{"score":10}""")),
            new TerraScaleViewRemove(Id("c"))));

        await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        var view = engine.CurrentView!;
        await Assert.That(string.Join(",", view.Rows.Select(row => row.Identity.DocumentId))).IsEqualTo("d,a,b");
    }

    /// <summary>A Reset operation replaces the complete result and adopts the new revision, digest, and checkpoint.</summary>
    [Test]
    public async Task ResetOperationReplacesTheCompleteResult()
    {
        var engine = LiveEngine(
            Row("a", "01", """{"name":"alpha"}"""),
            Row("b", "02", """{"name":"bravo"}"""));

        var delivery = engine.ApplyResultDelta(Delta(11, [new TerraScaleViewReset([Row("z", "01", """{"name":"zulu"}""")])]));

        await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.ResetApplied);
        var view = engine.CurrentView!;
        await Assert.That(view.Rows.Count).IsEqualTo(1);
        await Assert.That(view.Rows[0].Identity.DocumentId).IsEqualTo("z");
        await Assert.That(engine.ResetsApplied).IsEqualTo(1ul);
        await Assert.That(engine.Checkpoint!.Token).IsEqualTo("checkpoint-next");
    }

    /// <summary>A delta that assumes client state the engine cannot prove diverges: nothing applies and an authorized snapshot is required.</summary>
    [Test]
    public async Task UnprovableDeltaDivergesAndRequiresAnAuthorizedSnapshot()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var moved = engine.ApplyResultDelta(Delta(11, [new TerraScaleViewMove(Id("a"), Pos("wrong"), Pos("02"))]));
        await Assert.That(moved.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.ViewDiverged);
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Resetting);
        await Assert.That(engine.Checkpoint).IsNull();
        await Assert.That(engine.CurrentView!.Rows.Count).IsEqualTo(1);
        await Assert.That(engine.AppliedRevision).IsEqualTo(10ul);

        // The authorized bootstrap: a fresh snapshot is accepted from Resetting.
        engine.BeginSnapshot(Begin(generation: 2, baseRevision: 40));
        engine.ApplySnapshotChunk(Chunk(2, 1, Row("a", "01", """{"name":"alpha"}""")));
        var recovery = engine.CompleteSnapshot(End(generation: 2, baseRevision: 40, digest: "digest-40", checkpoint: "checkpoint-40"));
        await Assert.That(recovery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SnapshotPublished);
    }

    /// <summary>An update against an unknown identity diverges and applies nothing — not a partial frame.</summary>
    [Test]
    public async Task UnknownIdentityUpdateDivergesWithoutPartialApplication()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var diverged = engine.ApplyResultDelta(Delta(11,
            new TerraScaleViewUpdate(Id("a"), Json("""{"name":"alpha-2"}""")),
            new TerraScaleViewUpdate(Id("ghost"), Json("""{"name":"ghost"}"""))));

        await Assert.That(diverged.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.ViewDiverged);
        await Assert.That(engine.CurrentView!.Rows[0].Value.GetProperty("name").GetString()).IsEqualTo("alpha");
    }

    /// <summary>A server-signaled ViewDiverged closes the generation and drops the checkpoint.</summary>
    [Test]
    public async Task ServerSignaledDivergenceClosesTheGeneration()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var diverged = engine.NotifyDiverged();

        await Assert.That(diverged.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.ViewDiverged);
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Resetting);
        await Assert.That(engine.Checkpoint).IsNull();
        await Assert.That(engine.DivergencesDetected).IsEqualTo(1ul);
    }

    /// <summary>An insert targeting an existing identity is a malformed delta: updates replace existing rows.</summary>
    [Test]
    public async Task InsertOfAnExistingIdentityFailsClosed()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var malformed = await Rejects(() => engine.ApplyResultDelta(Delta(11, [new TerraScaleViewInsert(Id("a"), Pos("01"), Json("""{"name":"again"}"""))])));
        await Assert.That(malformed.Error).IsEqualTo(TerraScaleViewProtocolError.MalformedDelta);
    }

    /// <summary>The declared row bound is enforced fail-closed on both snapshots and deltas.</summary>
    [Test]
    public async Task DeclaredRowBoundFailsClosed()
    {
        var engine = new TerraScaleSyncV1ViewEngine();
        engine.Authenticate();
        engine.Subscribe();
        engine.BeginSnapshot(Begin(rowBound: 1));
        engine.ApplySnapshotChunk(Chunk(1, 1, Row("a", "01", """{"name":"alpha"}""")));

        var overBound = await Rejects(() => engine.ApplySnapshotChunk(Chunk(1, 2, Row("b", "02", """{"name":"bravo"}"""))));
        await Assert.That(overBound.Error).IsEqualTo(TerraScaleViewProtocolError.MalformedSnapshot);

        engine.CompleteSnapshot(End());
        var overflowingDelta = await Rejects(() => engine.ApplyResultDelta(Delta(11, [new TerraScaleViewInsert(Id("b"), Pos("00"), Json("""{"name":"bravo"}"""))])));
        await Assert.That(overflowingDelta.Error).IsEqualTo(TerraScaleViewProtocolError.MalformedDelta);
    }

    /// <summary>An authorization change that loses visibility opens the new generation: the removals delta applies onto the current cache.</summary>
    [Test]
    public async Task AuthorizationLossOpensTheNewGenerationWithRemovals()
    {
        var engine = LiveEngine(
            Row("a", "01", """{"name":"alpha"}"""),
            Row("b", "02", """{"name":"bravo"}"""));

        var changed = engine.NotifyAuthorizationChange(7, TerraScaleViewAuthorizationEffect.VisibilityLost);
        await Assert.That(changed.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.ViewGenerationAdvanced);
        await Assert.That(engine.AuthorizationGeneration).IsEqualTo(7ul);

        var removals = engine.ApplyResultDelta(Delta(
            toRevision: 1,
            fromRevision: 0,
            generation: 2,
            operations: [new TerraScaleViewRemove(Id("b"))],
            digest: "digest-gen2",
            checkpoint: "checkpoint-gen2"));

        await Assert.That(removals.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        await Assert.That(engine.ViewGeneration).IsEqualTo(2ul);
        await Assert.That(string.Join(",", engine.CurrentView!.Rows.Select(row => row.Identity.DocumentId))).IsEqualTo("a");
        await Assert.That(engine.Checkpoint!.Token).IsEqualTo("checkpoint-gen2");
    }

    /// <summary>A delta of an unsignaled new generation fails closed: the old generation is never resumed as current.</summary>
    [Test]
    public async Task UnsignaledGenerationChangeFailsClosed()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var malformed = await Rejects(() => engine.ApplyResultDelta(Delta(1, [new TerraScaleViewRemove(Id("a"))], fromRevision: 0, generation: 5, digest: "digest-gen5", checkpoint: "checkpoint-gen5")));
        await Assert.That(malformed.Error).IsEqualTo(TerraScaleViewProtocolError.MalformedDelta);
    }

    /// <summary>A revoked session drops its checkpoint and closes: no further view work applies.</summary>
    [Test]
    public async Task RevokedSessionDropsTheCheckpointAndCloses()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var revoked = engine.NotifyAuthorizationChange(9, TerraScaleViewAuthorizationEffect.SessionRevoked);

        await Assert.That(revoked.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SessionRevoked);
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Closing);
        await Assert.That(engine.Checkpoint).IsNull();

        var refused = await Rejects(() => engine.ApplyResultDelta(Delta(11, [new TerraScaleViewRemove(Id("a"))])));
        await Assert.That(refused.Error).IsEqualTo(TerraScaleViewProtocolError.WrongState);
    }

    /// <summary>A CheckpointExpired answer drops the unusable checkpoint and the automatic bootstrap follows.</summary>
    [Test]
    public async Task CheckpointExpiredDropsTheCheckpointAndBootstraps()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var expired = engine.NotifyCheckpointExpired();

        await Assert.That(expired.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.CheckpointExpired);
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Resetting);
        await Assert.That(engine.Checkpoint).IsNull();

        engine.BeginSnapshot(Begin(generation: 3, baseRevision: 50));
        engine.ApplySnapshotChunk(Chunk(3, 1, Row("a", "01", """{"name":"alpha"}""")));
        var bootstrap = engine.CompleteSnapshot(End(generation: 3, baseRevision: 50, digest: "digest-50", checkpoint: "checkpoint-50"));
        await Assert.That(bootstrap.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.SnapshotPublished);
        await Assert.That(engine.Checkpoint!.Token).IsEqualTo("checkpoint-50");
    }

    /// <summary>Resume requires an adopted checkpoint; replayed deltas then apply through the Resuming state back to Live.</summary>
    [Test]
    public async Task ResumeRequiresACheckpointAndAppliesReplay()
    {
        var fresh = new TerraScaleSyncV1ViewEngine();
        fresh.Authenticate();
        var noCheckpoint = await Rejects(() => fresh.BeginResume());
        await Assert.That(noCheckpoint.Error).IsEqualTo(TerraScaleViewProtocolError.WrongState);

        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));
        engine.BeginResume();
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Resuming);

        var replay = engine.ApplyResultDelta(Delta(11, [new TerraScaleViewInsert(Id("b"), Pos("02"), Json("""{"name":"bravo"}"""))]));
        await Assert.That(replay.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Live);
        await Assert.That(engine.CurrentView!.Rows.Count).IsEqualTo(2);
    }

    /// <summary>The optimistic presentation colors only the presentation: never a revision, digest, or checkpoint.</summary>
    [Test]
    public async Task OptimisticPresentationColorsOnlyThePresentation()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        var presented = engine.AttachOptimisticPresentation(
            "mutation-1",
            [new TerraScaleViewInsert(Id("b"), Pos("02"), Json("""{"name":"bravo"}"""))]);

        await Assert.That(presented.Optimistic).IsTrue();
        await Assert.That(presented.Rows.Count).IsEqualTo(2);
        await Assert.That(engine.AuthoritativeView!.Rows.Count).IsEqualTo(1);
        await Assert.That(engine.AuthoritativeView.Optimistic).IsFalse();
        await Assert.That(engine.OptimisticMutationsPending).IsEqualTo(1);
        await Assert.That(engine.AppliedRevision).IsEqualTo(10ul);
        await Assert.That(engine.ResultDigest!.Value).IsEqualTo("digest-10");
        await Assert.That(engine.Checkpoint!.Token).IsEqualTo("checkpoint-10");

        var resolved = engine.ResolveMutationOutcome("mutation-1", accepted: false);
        await Assert.That(resolved.Optimistic).IsFalse();
        await Assert.That(resolved.Rows.Count).IsEqualTo(1);
        await Assert.That(engine.OptimisticMutationsPending).IsEqualTo(0);
    }

    /// <summary>The optimistic layer rebases over authoritative result operations as they arrive.</summary>
    [Test]
    public async Task OptimisticLayerRebasesOverAuthoritativeOperations()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        _ = engine.AttachOptimisticPresentation(
            "mutation-1",
            [new TerraScaleViewUpdate(Id("a"), Json("""{"name":"alpha-optimistic"}"""))]);

        // The authoritative delta lands elsewhere on the same row: the
        // presentation layer survives and reapplies over the new state.
        var delivery = engine.ApplyResultDelta(Delta(11, [new TerraScaleViewMove(Id("a"), Pos("01"), Pos("05"))]));
        await Assert.That(delivery.Outcome).IsEqualTo(TerraScaleViewDeliveryOutcome.DeltaApplied);

        var view = engine.CurrentView!;
        await Assert.That(view.Optimistic).IsTrue();
        await Assert.That(view.Rows[0].Position.Key).IsEqualTo("05");
        await Assert.That(view.Rows[0].Value.GetProperty("name").GetString()).IsEqualTo("alpha-optimistic");
        await Assert.That(engine.AuthoritativeView!.Rows[0].Value.GetProperty("name").GetString()).IsEqualTo("alpha");
    }

    /// <summary>One stable idempotency identity per in-flight mutation: duplicates and unknown resolutions fail closed.</summary>
    [Test]
    public async Task OptimisticMutationIdentitiesAreStableAndFailClosed()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        _ = engine.AttachOptimisticPresentation("mutation-1", [new TerraScaleViewRemove(Id("a"))]);

        var duplicated = await Rejects(() => engine.AttachOptimisticPresentation("mutation-1", [new TerraScaleViewRemove(Id("a"))]));
        await Assert.That(duplicated.Error).IsEqualTo(TerraScaleViewProtocolError.InvalidOptimisticMutation);

        var unknown = await Rejects(() => engine.ResolveMutationOutcome("mutation-missing", accepted: true));
        await Assert.That(unknown.Error).IsEqualTo(TerraScaleViewProtocolError.InvalidOptimisticMutation);
    }

    /// <summary>Disconnected views retain presentation state but refuse new durable optimistic mutations.</summary>
    [Test]
    public async Task DisconnectedViewRefusesOptimisticMutationWithoutQueueing()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));
        engine.SetOnline(false);

        var refused = await Rejects(() => engine.AttachOptimisticPresentation(
            "mutation-offline",
            [new TerraScaleViewUpdate(Id("a"), Json("""{"name":"offline"}"""))]));

        await Assert.That(refused.Error).IsEqualTo(TerraScaleViewProtocolError.Offline);
        await Assert.That(engine.OptimisticMutationsPending).IsEqualTo(0);
        await Assert.That(engine.AuthoritativeView!.Rows[0].Value.GetProperty("name").GetString()).IsEqualTo("alpha");
    }

    /// <summary>Completed mutation identities cannot be attached again and repeated outcomes are idempotent.</summary>
    [Test]
    public async Task CompletedOptimisticMutationIdentityCannotBeReused()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));
        _ = engine.AttachOptimisticPresentation("mutation-completed", [new TerraScaleViewRemove(Id("a"))]);
        _ = engine.ResolveMutationOutcome("mutation-completed", accepted: false);
        _ = engine.ResolveMutationOutcome("mutation-completed", accepted: true);

        var refused = await Rejects(() => engine.AttachOptimisticPresentation(
            "mutation-completed",
            [new TerraScaleViewRemove(Id("a"))]));
        await Assert.That(refused.Error).IsEqualTo(TerraScaleViewProtocolError.InvalidOptimisticMutation);
    }

    /// <summary>The close handshake refuses new view work in Closing and terminates in Closed.</summary>
    [Test]
    public async Task CloseHandshakeRefusesViewWorkAndTerminates()
    {
        var engine = LiveEngine(Row("a", "01", """{"name":"alpha"}"""));

        engine.Close();
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Closing);

        var refused = await Rejects(() => engine.ApplyResultDelta(Delta(11, [new TerraScaleViewRemove(Id("a"))])));
        await Assert.That(refused.Error).IsEqualTo(TerraScaleViewProtocolError.WrongState);

        engine.ConfirmClosed();
        await Assert.That(engine.State).IsEqualTo(TerraScaleViewSessionState.Closed);
    }
}
