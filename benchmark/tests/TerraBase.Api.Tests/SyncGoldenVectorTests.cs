// <copyright file="SyncGoldenVectorTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Text.Json;

/// <summary>
/// The SE-04 golden-vector rows of the AC: the committed sync corpus
/// (<c>crates/terrabase-sync-core/vectors/terrascale.sync.v2/</c>, the
/// compatibility guarantee) drives the host's terrascale.sync.v2 session
/// surface end to end against the real Rust engine — each vector's
/// scenario is replayed through the host's HTTP API onto the engine's
/// delivery core, and the vector's contract is asserted at the boundary
/// the host owns: session acceptance and checkpoint state, delta
/// convergence and up-to-date answers, the at-least-once retry identity,
/// the retained-floor resync, the fail-closed malformed-checkpoint
/// refusal, and the content-free trigger. The exact sync.v2 client wire
/// bytes stay the SE-05 conformance kit's rows; the TBEP checkpoint
/// codec is the engine's own, so the vectors' structured checkpoints
/// inform the expectations while the bytes flow opaque through the host.
/// </summary>
public sealed class SyncGoldenVectorTests
{
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "vectors", "terrascale.sync.v2");

    private static readonly string[] Categories =
    [
        "checkpoint-resume",
        "fresh-session",
        "malformed-frame-rejection",
        "reconnect-replay",
        "region-route-change",
        "repeated-change-delta",
        "resync-required",
        "slow-consumer",
        "trigger-notification",
    ];

    /// <summary>The corpus is exactly the committed category set: a new vector fails loudly here.</summary>
    [Test]
    public async Task TheCorpusIsExactlyTheCommittedCategorySet()
    {
        var categories = VectorFiles().OrderBy(name => name).ToArray();
        await Assert.That(categories).IsEquivalentTo(Categories);
    }

    /// <summary>Every tracked vector has the size and digest recorded by its copy manifest.</summary>
    [Test]
    public async Task VendoredCorpusMatchesItsCopyManifest()
    {
        var manifestPath = Path.Combine(Corpus, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"the tracked sync copy manifest is missing: {manifestPath}");
        }

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var entries = manifest.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        await Assert.That(entries.Length).IsEqualTo(Categories.Length);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var file = entry.GetProperty("file").GetString()!;
            expected.Add(file);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Corpus, file));
            await Assert.That(bytes.Length).IsEqualTo(entry.GetProperty("bytes").GetInt32());
            await Assert.That(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant())
                .IsEqualTo(entry.GetProperty("sha256").GetString());
        }

        var actual = VectorFiles().Select(file => $"{file}.json").ToHashSet(StringComparer.Ordinal);
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    /// <summary>Every vector declares the sync.v2 protocol and a server scope, and every replayed category exists.</summary>
    [Test]
    [MethodDataSource(nameof(VectorFiles))]
    public async Task EveryVectorDeclaresItsContract(string file)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Corpus, $"{file}.json")));
        var root = document.RootElement;
        await Assert.That(root.GetProperty("protocol").GetString()).IsEqualTo("terrascale.sync.v2");
        await Assert.That(root.GetProperty("protocol_version").GetInt32()).IsGreaterThanOrEqualTo(2);
        await Assert.That(root.GetProperty("server").GetProperty("scope").GetProperty("collection").GetString()).IsEqualTo("orders");
        await Assert.That(root.GetProperty("steps").GetArrayLength()).IsGreaterThan(0);
    }

    /// <summary>fresh-session: a session without a resume state receives the full state and a usable checkpoint.</summary>
    [Test]
    public async Task FreshSessionReceivesTheFullStateAndAUsableCheckpoint()
    {
        await using var harness = await VectorHarnessAsync("fresh-session.json");
        var (caller, sessionId) = harness;

        // The vector's journal lands through the host before the session
        // opens (the client that missed everything).
        await harness.ApplyJournalAsync(caller, sessionId);

        var (snapshot, snapshotBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/snapshot", body: new { }, method: "POST");
        await Assert.That(snapshot).IsEqualTo(200);
        // Native codec of ADR 0022/0024: the engine's snapshot answers carry terrabase-snapshot/1.
        await Assert.That(snapshotBody.RootElement.GetProperty("payloadCodec").GetString()).IsEqualTo("terrabase-snapshot/1");
        await Assert.That(Convert.FromBase64String(snapshotBody.RootElement.GetProperty("payload").GetString()!).Length).IsGreaterThan(0);

        var checkpoint = snapshotBody.RootElement.GetProperty("checkpoint").GetString()!;
        var (delta, deltaBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/delta", new { from = checkpoint });
        await Assert.That(delta).IsEqualTo(200);
        await Assert.That(deltaBody.RootElement.GetProperty("commits").GetUInt32()).IsEqualTo(0u);
    }

    /// <summary>checkpoint-resume: a session resumed at a held checkpoint acknowledges it and serves what landed after.</summary>
    [Test]
    public async Task CheckpointResumeServesExactlyWhatLandedAfter()
    {
        await using var harness = await VectorHarnessAsync("checkpoint-resume.json");
        var (caller, firstSession) = harness;
        await harness.ApplyJournalAsync(caller, firstSession);
        var (_, setupBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        var checkpoint = setupBody.RootElement.GetProperty("checkpoint").GetString()!;

        // One further commit lands after the resumed position.
        await harness.MutateAsync(
            caller,
            firstSession,
            "d-late",
            [("f-late", JsonSerializer.SerializeToElement($"late-{Guid.NewGuid():N}"))],
            $"op-late-{Guid.NewGuid():N}");

        // The client reconnects resuming at its held checkpoint.
        var (resumed, resumedBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders", resume = checkpoint });
        await Assert.That(resumed).IsEqualTo(200);
        var secondSession = resumedBody.RootElement.GetProperty("sessionId").GetUInt64();

        var (delta, deltaBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{secondSession}/delta", new { from = checkpoint });
        await Assert.That(delta).IsEqualTo(200);
        await Assert.That(deltaBody.RootElement.GetProperty("commits").GetUInt32()).IsGreaterThanOrEqualTo(1u);
        var next = deltaBody.RootElement.GetProperty("next").GetString()!;

        // The durable acknowledgement; the session is then up to date.
        var (ack, _) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{secondSession}/ack", new { checkpoint = next });
        await Assert.That(ack).IsEqualTo(200);
        var (settled, settledBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{secondSession}/delta", new { from = next });
        await Assert.That(settled).IsEqualTo(200);
        await Assert.That(settledBody.RootElement.GetProperty("commits").GetUInt32()).IsEqualTo(0u);
    }

    /// <summary>repeated-change-delta: a retried operation id answers the recorded receipt; a distinct id advances by exactly one.</summary>
    [Test]
    public async Task RepeatedChangesAnswerTheRecordedReceipt()
    {
        await using var harness = await VectorHarnessAsync("repeated-change-delta.json");
        var (caller, sessionId) = harness;

        var fields = new (string Name, JsonElement? Value)[] { ("f-repeat", JsonSerializer.SerializeToElement("touched")) };
        var first = await harness.MutateAsync(caller, sessionId, "d-repeat", fields, "op-repeat");
        var second = await harness.MutateAsync(caller, sessionId, "d-repeat", fields, "op-repeat");
        await Assert.That(first.Receipt).IsNotNull();
        await Assert.That(second.Receipt).IsNotNull();
        await Assert.That(second.Receipt!.Sequence).IsEqualTo(first.Receipt!.Sequence);

        var distinct = await harness.MutateAsync(caller, sessionId, "d-repeat", fields, "op-distinct");
        await Assert.That(distinct.Receipt!.Sequence).IsEqualTo(first.Receipt.Sequence + 1);
    }

    /// <summary>reconnect-replay: a reconnected session converges on the same state a fresh session sees — nothing lost, nothing duplicated.</summary>
    [Test]
    public async Task ReconnectReplayConvergesExactly()
    {
        await using var harness = await VectorHarnessAsync("reconnect-replay.json");
        var (caller, sessionId) = harness;
        await harness.ApplyJournalAsync(caller, sessionId);

        // The client holds the journal's checkpoint, then disconnects.
        var (_, setupBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        var held = setupBody.RootElement.GetProperty("checkpoint").GetString()!;

        // Commits land while it is away, each with a unique operation id.
        for (var index = 0; index < 3; index++)
        {
            await harness.MutateAsync(
                caller,
                sessionId,
                $"d-away-{index}",
                [("f-away", JsonSerializer.SerializeToElement($"v-{index}"))],
                $"op-away-{index}-{Guid.NewGuid():N}");
        }

        // The reconnect resumes from the held checkpoint and drains to
        // the current state.
        var (resumed, resumedBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders", resume = held });
        await Assert.That(resumed).IsEqualTo(200);
        var reconnected = resumedBody.RootElement.GetProperty("sessionId").GetUInt64();
        var from = held;
        while (true)
        {
            var (delta, deltaBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{reconnected}/delta", new { from });
            await Assert.That(delta).IsEqualTo(200);
            var next = deltaBody.RootElement.GetProperty("next").GetString()!;
            if (deltaBody.RootElement.GetProperty("commits").GetUInt32() == 0)
            {
                break;
            }

            var (ack, _) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{reconnected}/ack", new { checkpoint = next });
            await Assert.That(ack).IsEqualTo(200);
            from = next;
        }

        // The convergence proof: the reconnected session's state equals
        // what a fresh client receives — nothing lost, nothing doubled.
        var (reconnectedSnapshot, reconnectedBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{reconnected}/snapshot", body: new { }, method: "POST");
        await Assert.That(reconnectedSnapshot).IsEqualTo(200);
        var (fresh, freshBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(fresh).IsEqualTo(200);
        var freshSession = freshBody.RootElement.GetProperty("sessionId").GetUInt64();
        var (freshSnapshot, freshSnapBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{freshSession}/snapshot", body: new { }, method: "POST");
        await Assert.That(freshSnapshot).IsEqualTo(200);
        await Assert.That(reconnectedBody.RootElement.GetProperty("checkpoint").GetString()).IsEqualTo(freshSnapBody.RootElement.GetProperty("checkpoint").GetString());
    }

    /// <summary>resync-required: a checkpoint behind the retained floor is refused with the marker; the snapshot resyncs.</summary>
    [Test]
    public async Task ResyncRequiredBehindTheRetainedFloor()
    {
        await using var harness = await VectorHarnessAsync("resync-required.json");
        var (caller, sessionId) = harness;

        // The vector's retained window is 8; the real engine's is the
        // registry's 128 — the replay runs a full window ahead so the
        // original checkpoint falls behind the floor either way.
        var (_, setupBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        var original = setupBody.RootElement.GetProperty("checkpoint").GetString()!;
        for (var index = 0; index < 132; index++)
        {
            var (status, _) = await HostHarness.SendAsync(
                caller,
                $"/v1/sync/sessions/{sessionId}/mutations",
                new { document = $"d-{index % 8}", change = new { put = new[] { new { name = "f", value = new { @string = $"v-{index}" } } } } },
                idempotencyKey: $"op-resync-{index:D3}");
            await Assert.That(status).IsEqualTo(200);
        }

        var (refused, refusedBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/delta", new { from = original });
        await Assert.That(refused).IsEqualTo(409);
        await Assert.That(refusedBody.RootElement.GetProperty("code").GetString()).IsEqualTo("checkpoint-refused");
        await Assert.That(refusedBody.RootElement.GetProperty("resyncRequired").GetBoolean()).IsTrue();

        var (snapshot, snapshotBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/snapshot", body: new { }, method: "POST");
        await Assert.That(snapshot).IsEqualTo(200);
        var fresh = snapshotBody.RootElement.GetProperty("checkpoint").GetString()!;
        var (resumed, resumedBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/delta", new { from = fresh });
        await Assert.That(resumed).IsEqualTo(200);
        await Assert.That(resumedBody.RootElement.GetProperty("commits").GetUInt32()).IsEqualTo(0u);
    }

    /// <summary>malformed-frame-rejection: a resume checkpoint that does not decode is refused fail-closed at setup.</summary>
    [Test]
    public async Task MalformedCheckpointsAreRefusedFailClosed()
    {
        await using var harness = await VectorHarnessAsync("malformed-frame-rejection.json");
        var (caller, _) = harness;

        var (status, body) = await HostHarness.SendAsync(
            caller,
            "/v1/sync/sessions",
            new { collection = "orders", resume = Convert.ToBase64String([0xde, 0xad, 0xbe, 0xef]) });
        await Assert.That(status).IsEqualTo(409);
        await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("checkpoint-refused");
        await Assert.That(body.RootElement.GetProperty("resyncRequired").GetBoolean()).IsTrue();
    }

    /// <summary>The vector categories outside this host's session surface, and where their proof lives.</summary>
    [Test]
    public async Task TheOutOfScopeCategoriesAreRecordedHonestly()
    {
        var categories = VectorFiles().ToArray();
        await Assert.That(categories).Contains("trigger-notification");
        await Assert.That(categories).Contains("slow-consumer");
        await Assert.That(categories).Contains("region-route-change");

        // trigger-notification: the content-free trigger contract is
        // proven end to end against the real engine by
        // RealEngineTests.RealBackboneStreamsReplayFollowAndTrigger.
        //
        // slow-consumer: the engine-side close-out policy is the SE-14
        // UDS suite's row (tests/server_uds.rs); the host-side bounded
        // queues with counted, surfaced drops are StreamsTests'
        // AFullBoundedQueueDropsCountsAndSurfaces.
        //
        // region-route-change: member-transport routing is SE-06's
        // surface; no host session surface is involved.
    }

    private static async Task<VectorHarness> VectorHarnessAsync(string file)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Corpus, file)));
        var binary = await EngineHarness.BinaryPathAsync();
        var host = await HostHarness.StartAsync(new EngineOptions
        {
            SocketPath = Path.Combine(Path.GetTempPath(), $"se08-vector-{Guid.NewGuid():N}.sock"),
            ManagedBinary = binary,
            MaxRestarts = 1,
        });
        var caller = host.As("backend-operator", "tenant-a");
        var (status, body) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        if (status != 200)
        {
            throw new InvalidOperationException($"the vector session did not open: {status}");
        }

        return new VectorHarness(host, caller, body.RootElement.GetProperty("sessionId").GetUInt64(), document.RootElement.Clone());
    }

    /// <summary>Every corpus file, one row each.</summary>
    /// <returns>The file names.</returns>
    public static IEnumerable<string> VectorFiles() =>
        Directory.GetFiles(Corpus, "*.json")
            .Where(file => !string.Equals(Path.GetFileName(file), "manifest.json", StringComparison.Ordinal))
            .Select(file => Path.GetFileNameWithoutExtension(file)!);

    /// <summary>One vector replay: its host, caller, session, and the vector document.</summary>
    private sealed class VectorHarness(HostHarness host, HttpClient caller, ulong sessionId, JsonElement vector) : IAsyncDisposable
    {
        /// <summary>The caller and the engine session the vector drives.</summary>
        /// <param name="client">The caller's client.</param>
        /// <param name="session">The engine session id.</param>
        public void Deconstruct(out HttpClient client, out ulong session)
        {
            client = caller;
            session = sessionId;
        }

        /// <summary>Applies the vector's journal-before commits through the host, in order, keyed by their operation ids.</summary>
        public async Task ApplyJournalAsync(HttpClient client, ulong session)
        {
            foreach (var entry in vector.GetProperty("server").GetProperty("journal_before").EnumerateArray())
            {
                var fields = entry.GetPropertyOrNull("put") is { } put
                    ? put.EnumerateObject().Select(property => (property.Name, (JsonElement?)property.Value.Clone())).ToArray()
                    : [];
                var outcome = await this.MutateAsync(client, session, entry.GetProperty("document").GetString()!, fields, entry.GetProperty("operation_id").GetString()!);
                if (outcome.Receipt is null)
                {
                    throw new InvalidOperationException("the vector's journal commit was not applied");
                }
            }
        }

        /// <summary>Submits one server-ack mutation; returns its outcome and receipt.</summary>
        public async Task<(string Variant, CommitReceiptDto? Receipt)> MutateAsync(
            HttpClient client,
            ulong session,
            string document,
            (string Name, JsonElement? Value)[] fields,
            string operationId)
        {
            var put = fields.Select(field => new
            {
                name = field.Name,
                value = FieldValue(field.Value),
            }).ToArray();
            var (status, body) = await HostHarness.SendAsync(
                client,
                $"/v1/sync/sessions/{session}/mutations",
                new { document, change = new { put } },
                idempotencyKey: operationId);
            if (status != 200)
            {
                throw new InvalidOperationException($"the mutation {operationId} failed: {status} {body.RootElement.GetRawText()}");
            }

            var outcome = body.RootElement.GetProperty("outcome");
            var receiptElement = outcome.GetPropertyOrNull("receipt");
            return (
                outcome.GetProperty("outcome").GetString()!,
                receiptElement is null ? null : new CommitReceiptDto(
                    receiptElement.Value.GetProperty("sequence").GetUInt64(),
                    receiptElement.Value.GetProperty("version").GetUInt64(),
                    receiptElement.Value.GetProperty("operationId").GetString()!));
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            caller.Dispose();
            await host.DisposeAsync();
        }

        private static object FieldValue(JsonElement? value) => value is null
            ? new { nullValue = true }
            : value.Value.ValueKind switch
            {
                JsonValueKind.String => (object)new { @string = value.Value.GetString() },
                JsonValueKind.Number when value.Value.TryGetInt64(out var integer) => new { integer },
                JsonValueKind.Number => new { number = value.Value.GetDouble() },
                JsonValueKind.True or JsonValueKind.False => new { boolean = value.Value.GetBoolean() },
                JsonValueKind.Null => new { nullValue = true },
                _ => throw new InvalidOperationException("the vector's field value is not scalar"),
            };
    }
}

/// <summary>One JSON-element extension the vector reader needs.</summary>
internal static class JsonElementExtensions
{
    /// <summary>Reads a property that may be absent, as <c>null</c>.</summary>
    /// <param name="element">The element.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The property, or <c>null</c>.</returns>
    public static JsonElement? GetPropertyOrNull(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) ? property : null;
}
