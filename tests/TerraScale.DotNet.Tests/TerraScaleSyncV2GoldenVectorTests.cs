using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

/// <summary>
/// The vendored <c>terrascale.sync.v2</c> golden-vector conformance run
/// (SE-10): the corpus committed by SE-04 (with SE-07's regenerated
/// slow-consumer file) and the SE-05 corpus-manifest gate, executed
/// against the SDK's native managed codec. For every recorded frame the
/// structured description must seal and encode to the recorded bytes and
/// the bytes must decode back; for every <c>decode_rejects</c> entry the
/// typed rejection must match; for every budget step the class, budget,
/// and payload accounting are recounted from the committed wire bytes.
/// </summary>
public sealed class TerraScaleSyncV2GoldenVectorTests
{
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

    private static string CorpusDirectory()
    {
        var candidate = AppContext.BaseDirectory;
        for (var depth = 0; depth < 6; depth++)
        {
            var vectors = Path.Combine(candidate, "vectors", "terrascale.sync.v2");
            if (Directory.Exists(vectors))
            {
                return vectors;
            }

            candidate = Path.GetDirectoryName(candidate)!;
        }

        throw new InvalidOperationException("the vendored terrascale.sync.v2 corpus directory was not found");
    }

    /// <summary>The corpus header identities are exact before any step runs.</summary>
    [Test]
    public async Task TheCorpusHeaderIdentitiesAreExact()
    {
        foreach (var file in CorpusFiles())
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file.Path));
            var root = document.RootElement;
            await Assert.That(root.GetProperty("schema").GetString())
                .IsEqualTo("terrascale.sync.v2/vector-corpus@1");
            await Assert.That(root.GetProperty("protocol").GetString()).IsEqualTo("terrascale.sync.v2");
            await Assert.That(root.GetProperty("protocol_version").GetInt32()).IsEqualTo(2);
            await Assert.That(root.GetProperty("category").GetString()).IsEqualTo(file.Category);
        }
    }

    /// <summary>The SE-05 manifest gates the run: every file digest and size matches, and the category set is exact.</summary>
    [Test]
    public async Task TheCorpusManifestVerifiesEveryVector()
    {
        var directory = CorpusDirectory();
        var manifestPath = Path.Combine(directory, "manifest.json");
        await Assert.That(File.Exists(manifestPath)).IsTrue();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = manifest.RootElement;
        await Assert.That(root.GetProperty("schema").GetString())
            .IsEqualTo("terrascale.sync.v2/corpus-manifest@1");
        await Assert.That(root.GetProperty("protocol").GetString()).IsEqualTo("terrascale.sync.v2");
        await Assert.That(root.GetProperty("protocol_version").GetInt32()).IsEqualTo(2);

        var entries = root.GetProperty("vectors").EnumerateArray().ToList();
        await Assert.That(entries.Count).IsEqualTo(Categories.Length);
        var filesOnDisk = Directory.GetFiles(directory, "*.json")
            .Where(path => Path.GetFileName(path) != "manifest.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        await Assert.That(filesOnDisk.Length).IsEqualTo(Categories.Length);

        var manifestFiles = entries.Select(entry => entry.GetProperty("file").GetString()).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        await Assert.That(manifestFiles.SequenceEqual(filesOnDisk.Select(Path.GetFileName), StringComparer.Ordinal)).IsTrue();

        foreach (var entry in entries)
        {
            var file = entry.GetProperty("file").GetString()!;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, file));
            await Assert.That(bytes.LongLength).IsEqualTo(entry.GetProperty("bytes").GetInt64());
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await Assert.That(digest).IsEqualTo(entry.GetProperty("sha256").GetString());
            await Assert.That(Categories.Contains(entry.GetProperty("category").GetString())).IsTrue();
        }
    }

    /// <summary>Every recorded frame encodes byte-exact from its description and decodes back with its envelope identity intact.</summary>
    [Test]
    public async Task EveryRecordedFrameEncodesByteExactAndDecodesBack()
    {
        var framesChecked = 0;
        foreach (var file in CorpusFiles())
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file.Path));
            var steps = document.RootElement.GetProperty("steps");
            foreach (var step in steps.EnumerateArray())
            {
                foreach (var key in (string[])["client_frame", "server_frame"])
                {
                    if (step.TryGetProperty(key, out var entry))
                    {
                        await CheckRecordedFrame(file.Category, entry);
                        framesChecked++;
                    }
                }

                if (step.TryGetProperty("serve_delta", out var delta) && delta.TryGetProperty("expect_frame", out var deltaFrame))
                {
                    await CheckRecordedFrame(file.Category, Wrap(deltaFrame, delta.GetProperty("expect_wire").GetString()!));
                    framesChecked++;
                }

                if (step.TryGetProperty("serve_snapshot", out var snapshot))
                {
                    await CheckRecordedFrame(file.Category, Wrap(snapshot.GetProperty("expect_frame"), snapshot.GetProperty("expect_wire").GetString()!));
                    framesChecked++;
                }

                if (step.TryGetProperty("serve_trigger", out var trigger))
                {
                    await CheckRecordedFrame(file.Category, Wrap(trigger.GetProperty("expect_frame"), trigger.GetProperty("expect_wire").GetString()!));
                    framesChecked++;
                }
            }
        }

        await Assert.That(framesChecked).IsGreaterThan(10);
    }

    /// <summary>Served expectations decode byte-exact and match their structured descriptions (the client-side serve checks).</summary>
    [Test]
    public async Task ServeExpectationsDecodeByteExactWithTheirDescriptions()
    {
        var checkedExpectations = 0;
        foreach (var file in CorpusFiles())
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file.Path));
            foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
            {
                foreach (var key in (string[])["serve_delta", "serve_snapshot", "serve_trigger"])
                {
                    if (!step.TryGetProperty(key, out var entry) ||
                        entry.TryGetProperty("expect_wire", out var wire) == false ||
                        wire.GetString() is not { Length: > 0 })
                    {
                        continue;
                    }

                    var bytes = HexToBytes(wire.GetString()!);
                    var decoded = TerraScaleSyncV2Frame.Decode(bytes);
                    await Assert.That(decoded.Verify()).IsTrue();
                    var described = FrameFromDescription(entry.GetProperty("expect_frame"));
                    await Assert.That(decoded.ToByteArray().AsSpan().SequenceEqual(described.ToByteArray())).IsTrue();
                    checkedExpectations++;
                }
            }
        }

        await Assert.That(checkedExpectations).IsEqualTo(20);
    }

    /// <summary>Trigger notifications stay content-free and within their written budget.</summary>
    [Test]
    public async Task TriggerFramesStayContentFreeAndWithinBudget()
    {
        var triggers = 0;
        foreach (var file in CorpusFiles())
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file.Path));
            foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
            {
                if (!step.TryGetProperty("serve_trigger", out var trigger))
                {
                    continue;
                }

                var described = trigger.GetProperty("expect_frame");
                var keys = described.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                await Assert.That(keys)
                    .IsEquivalentTo((string[])["causation", "collection", "correlation", "kind", "operation_id", "storage_sequence"]);
                var frame = FrameFromDescription(described);
                await Assert.That(frame.ToByteArray().LongLength)
                    .IsLessThanOrEqualTo((long)TerraScaleSyncV2Protocol.BudgetBytes(TerraScaleSyncV2FrameClass.Trigger));
                triggers++;
            }
        }

        await Assert.That(triggers).IsEqualTo(1);
    }

    /// <summary>Fail-closed decode: every recorded rejection answers its exact typed error name.</summary>
    [Test]
    public async Task MalformedFramesAreRejectedWithTheRecordedTypedError()
    {
        var rejections = 0;
        foreach (var file in CorpusFiles())
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file.Path));
            foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
            {
                if (!step.TryGetProperty("decode_rejects", out var entry))
                {
                    continue;
                }

                var bytes = HexToBytes(entry.GetProperty("wire").GetString()!);
                var expected = entry.GetProperty("error").GetString()!;
                var failure = Assert.Throws<TerraScaleSyncV2ProtocolException>(() => TerraScaleSyncV2Frame.Decode(bytes));
                await Assert.That(failure.WireName).IsEqualTo(expected);
                rejections++;
            }
        }

        await Assert.That(rejections).IsEqualTo(10);
    }

    /// <summary>Budget steps recount class, budget, and payload accounting from the committed wire bytes.</summary>
    [Test]
    public async Task BudgetStepsRecountFromClassWireAndPayload()
    {
        var budgets = 0;
        foreach (var file in CorpusFiles())
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file.Path));
            foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
            {
                if (!step.TryGetProperty("budget", out var entry))
                {
                    continue;
                }

                var bytes = HexToBytes(entry.GetProperty("wire").GetString()!);
                var decoded = TerraScaleSyncV2Frame.Decode(bytes);
                var recordedClass = entry.GetProperty("class").GetString()!;
                await Assert.That(decoded.Body.FrameClass.ToString().ToLowerInvariant()).IsEqualTo(recordedClass);
                var budget = TerraScaleSyncV2Protocol.BudgetBytes(decoded.Body.FrameClass);
                var oversizeException = decoded.Body is TerraScaleSyncV2DeltaBatchBody
                {
                    SingleCommitOversize: true,
                    Commits: 1,
                };
                await Assert.That(bytes.LongLength <= budget || oversizeException).IsTrue();
                if (entry.TryGetProperty("payload_bytes", out var payloadBytes))
                {
                    await Assert.That((long)decoded.PayloadLength).IsEqualTo(payloadBytes.GetInt64());
                    await Assert.That((long)(bytes.LongLength - decoded.PayloadLength))
                        .IsEqualTo(entry.GetProperty("envelope_overhead_bytes").GetInt64());
                }

                if (entry.TryGetProperty("bytes_per_delivered_change", out var perChange))
                {
                    var batch = (TerraScaleSyncV2DeltaBatchBody)decoded.Body;
                    await Assert.That(batch.Commits).IsGreaterThan(0ul);
                    await Assert.That(bytes.LongLength / (long)batch.Commits).IsEqualTo(perChange.GetInt64());
                }

                if (entry.TryGetProperty("delta_bytes", out var deltaBytes))
                {
                    await Assert.That(deltaBytes.GetInt64())
                        .IsLessThan(entry.GetProperty("snapshot_bytes").GetInt64());
                }

                budgets++;
            }
        }

        await Assert.That(budgets).IsEqualTo(17);
    }

    /// <summary>The fixed envelope cost is exactly the heartbeat's wire size: 38 bytes.</summary>
    [Test]
    public async Task AHeartbeatEncodesToExactlyTheFixedEnvelopeBytes()
    {
        var heartbeat = TerraScaleSyncV2Frame.Seal(
            new TerraScaleSyncV2HeartbeatBody(),
            correlation: 1,
            causation: 0);
        await Assert.That(heartbeat.ToByteArray().LongLength)
            .IsEqualTo((long)TerraScaleSyncV2Protocol.FixedEnvelopeBytes);
    }

    /// <summary>The idempotency pairing is a wire rule on both seal and decode.</summary>
    [Test]
    public async Task TheIdempotencyPairingIsEnforcedOnSealAndDecode()
    {
        var key = new TerraScaleSyncV2IdempotencyKey("op-01");
        var mutation = new TerraScaleSyncV2MutationRequestBody(
            "orders",
            "d-01",
            new TerraScaleSyncV2DocumentWrite.Put(new Dictionary<string, TerraScaleSyncV2Value>
            {
                ["qty"] = TerraScaleSyncV2Value.Of(3L),
            }));
        _ = await Assert.ThrowsAsync<TerraScaleSyncV2ProtocolException>(() =>
            Task.Run(() => TerraScaleSyncV2Frame.Seal(mutation, 1, 0)));
        _ = await Assert.ThrowsAsync<TerraScaleSyncV2ProtocolException>(() =>
            Task.Run(() => TerraScaleSyncV2Frame.Seal(new TerraScaleSyncV2HeartbeatBody(), 1, 0, key)));

        var sealedFrame = TerraScaleSyncV2Frame.Seal(mutation, 1, 0, key);
        var wire = sealedFrame.ToByteArray();
        var decoded = TerraScaleSyncV2Frame.Decode(wire);
        await Assert.That(decoded.IdempotencyKey!.Value.Value).IsEqualTo("op-01");
    }

    /// <summary>Checkpoint encoding is versioned, canonical, and fails closed on unknown versions.</summary>
    [Test]
    public async Task CheckpointsEncodeCanonicallyAndFailClosed()
    {
        var checkpoint = TerraScaleSyncV2Checkpoint.Create(
            [(1ul, 9)],
            storageSequence: 42);
        var bytes = checkpoint.ToBytes();
        await Assert.That(bytes[0]).IsEqualTo((byte)1);
        var decoded = TerraScaleSyncV2Checkpoint.FromBytes(bytes);
        await Assert.That(decoded).IsEqualTo(checkpoint);

        var canonical = TerraScaleSyncV2Checkpoint.Create([(2ul, 4), (1ul, 7), (1ul, 3)], 5);
        await Assert.That(canonical.Ends.Select(end => (end.Peer, end.End)).ToArray())
            .IsEquivalentTo([(1ul, 7), (2ul, 4)]);

        var failure = Assert.Throws<TerraScaleSyncV2ProtocolException>(() => TerraScaleSyncV2Checkpoint.FromBytes([2]));
        await Assert.That(failure.Error).IsEqualTo(TerraScaleSyncV2ProtocolError.MalformedFrame);
        await Assert.That(failure.Reason).Contains("checkpoint");
    }

    private static IEnumerable<(string Category, string Path)> CorpusFiles()
    {
        var directory = CorpusDirectory();
        return Directory.GetFiles(directory, "*.json")
            .Where(path => Path.GetFileName(path) != "manifest.json")
            .Select(path => (Path.GetFileNameWithoutExtension(path), path))
            .OrderBy(entry => entry.Item1, StringComparer.Ordinal);
    }

    private static async Task CheckRecordedFrame(string category, JsonElement entry)
    {
        var expectedWire = HexToBytes(entry.GetProperty("wire").GetString()!);
        var frame = FrameFromDescription(entry.GetProperty("frame"));
        var encoded = frame.ToByteArray();
        await Assert.That(encoded.AsSpan().SequenceEqual(expectedWire))
            .IsTrue();

        var decoded = TerraScaleSyncV2Frame.Decode(expectedWire);
        await Assert.That(decoded.Kind).IsEqualTo(frame.Kind);
        await Assert.That(decoded.Verify()).IsTrue();
        await Assert.That(decoded.Correlation).IsEqualTo(frame.Correlation);
        await Assert.That(decoded.Causation).IsEqualTo(frame.Causation);
        await Assert.That(decoded.IdempotencyKey?.Value).IsEqualTo(frame.IdempotencyKey?.Value);
    }

    private static JsonElement Wrap(JsonElement frame, string wire) => JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
    {
        ["frame"] = frame,
        ["wire"] = JsonSerializer.SerializeToElement(wire),
    });

    internal static TerraScaleSyncV2Frame FrameFromDescription(JsonElement value)
    {
        var kind = value.GetProperty("kind").GetString()!;
        var correlation = value.GetProperty("correlation").GetUInt64();
        var causation = value.TryGetProperty("causation", out var causationElement) ? causationElement.GetUInt64() : 0ul;
        var idempotency = value.TryGetProperty("idempotency", out var idempotencyElement)
            ? new TerraScaleSyncV2IdempotencyKey(idempotencyElement.GetString()!)
            : (TerraScaleSyncV2IdempotencyKey?)null;

        TerraScaleSyncV2Body body = kind switch
        {
            "session_setup" => new TerraScaleSyncV2SessionSetupBody(
                Strings(value.GetProperty("collections")),
                value.GetProperty("resume").ValueKind == JsonValueKind.Object
                    ? Checkpoint(value.GetProperty("resume"))
                    : null),
            "session_accepted" => new TerraScaleSyncV2SessionAcceptedBody(
                value.GetProperty("session").GetUInt64(),
                Checkpoint(value.GetProperty("server_checkpoint")),
                Strings(value.GetProperty("collections"))),
            "subscription_modify" => new TerraScaleSyncV2SubscriptionModifyBody(
                Strings(value.GetProperty("add")),
                Strings(value.GetProperty("remove"))),
            "subscription_changed" => new TerraScaleSyncV2SubscriptionChangedBody(
                Strings(value.GetProperty("active"))),
            "checkpoint_ack" => new TerraScaleSyncV2CheckpointAckBody(Checkpoint(value.GetProperty("checkpoint"))),
            "heartbeat" => new TerraScaleSyncV2HeartbeatBody(),
            "delta_batch" => new TerraScaleSyncV2DeltaBatchBody(
                Codec(value.GetProperty("codec")),
                HexToBytes(value.GetProperty("payload").GetString()!),
                Checkpoint(value.GetProperty("next")),
                value.GetProperty("within_budget").GetBoolean(),
                value.GetProperty("single_commit_oversize").GetBoolean(),
                value.GetProperty("commits").GetUInt64()),
            "snapshot_answer" => new TerraScaleSyncV2SnapshotAnswerBody(
                Codec(value.GetProperty("codec")),
                HexToBytes(value.GetProperty("payload").GetString()!),
                Checkpoint(value.GetProperty("checkpoint"))),
            "trigger" => new TerraScaleSyncV2TriggerBody(
                value.GetProperty("collection").GetString()!,
                value.GetProperty("storage_sequence").GetUInt64(),
                value.GetProperty("operation_id").GetString()!),
            "resync_required" => new TerraScaleSyncV2ResyncRequiredBody(
                Checkpoint(value.GetProperty("floor")),
                value.GetProperty("reason").GetString() switch
                {
                    "behind-retained-floor" => TerraScaleSyncV2ResyncReason.BehindRetainedFloor,
                    "history-compacted" => TerraScaleSyncV2ResyncReason.HistoryCompacted,
                    var other => throw new InvalidOperationException($"unknown resync reason {other}"),
                }),
            "region_route_change" => new TerraScaleSyncV2RegionRouteChangeBody(value.GetProperty("region").GetString()!),
            "error" => new TerraScaleSyncV2ErrorBody(
                ErrorCode(value.GetProperty("code")),
                value.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                    ? detail.GetString()
                    : null),
            "mutation_request" => new TerraScaleSyncV2MutationRequestBody(
                value.GetProperty("collection").GetString()!,
                value.GetProperty("document").GetString()!,
                value.TryGetProperty("put", out var put) && put.ValueKind == JsonValueKind.Object
                    ? new TerraScaleSyncV2DocumentWrite.Put(put.EnumerateObject().ToDictionary(
                        field => field.Name,
                        field => Value(field.Value)))
                    : new TerraScaleSyncV2DocumentWrite.Delete()),
            "mutation_ack" => new TerraScaleSyncV2MutationAckBody(
                value.GetProperty("outcome").GetString() switch
                {
                    "applied" => new TerraScaleSyncV2MutationApplied(
                        value.GetProperty("storage_sequence").GetUInt64(),
                        value.GetProperty("operation_id").GetString()!),
                    "rejected" => new TerraScaleSyncV2MutationRejected(ErrorCode(value.GetProperty("rejection"))),
                    var other => throw new InvalidOperationException($"unknown mutation outcome {other}"),
                }),
            var other => throw new InvalidOperationException($"unknown frame kind {other}"),
        };
        return TerraScaleSyncV2Frame.Seal(body, correlation, causation, idempotency);
    }

    internal static TerraScaleSyncV2Checkpoint Checkpoint(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return TerraScaleSyncV2Checkpoint.Initial;
        }

        var ends = value.GetProperty("ends").EnumerateArray()
            .Select(pair => (pair[0].GetUInt64(), pair[1].GetInt32()))
            .ToArray();
        return TerraScaleSyncV2Checkpoint.Create(ends, value.GetProperty("storage_sequence").GetUInt64());
    }

    private static TerraScaleSyncV2PayloadCodec Codec(JsonElement value) => value.GetString() switch
    {
        "loro-delta/1" => TerraScaleSyncV2PayloadCodec.LoroDelta1,
        "loro-snapshot/1" => TerraScaleSyncV2PayloadCodec.LoroSnapshot1,
        var other => throw new InvalidOperationException($"unknown payload codec {other}"),
    };

    private static TerraScaleSyncV2ErrorCode ErrorCode(JsonElement value) => value.GetString() switch
    {
        "unsupported_version" => TerraScaleSyncV2ErrorCode.UnsupportedVersion,
        "malformed_frame" => TerraScaleSyncV2ErrorCode.MalformedFrame,
        "checkpoint_refused" => TerraScaleSyncV2ErrorCode.CheckpointRefused,
        "scope_refused" => TerraScaleSyncV2ErrorCode.ScopeRefused,
        "session_closed" => TerraScaleSyncV2ErrorCode.SessionClosed,
        "overloaded" => TerraScaleSyncV2ErrorCode.Overloaded,
        var other => throw new InvalidOperationException($"unknown error code {other}"),
    };

    private static TerraScaleSyncV2Value Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => TerraScaleSyncV2Value.Null,
        JsonValueKind.True => TerraScaleSyncV2Value.Of(true),
        JsonValueKind.False => TerraScaleSyncV2Value.Of(false),
        JsonValueKind.String => TerraScaleSyncV2Value.Of(value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => TerraScaleSyncV2Value.Of(integer),
        JsonValueKind.Number => TerraScaleSyncV2Value.Of(value.GetDouble()),
        var other => throw new InvalidOperationException($"unsupported vector value {other}"),
    };

    private static List<string> Strings(JsonElement value) =>
        value.EnumerateArray().Select(entry => entry.GetString()!).ToList();

    internal static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }

        return bytes;
    }
}
