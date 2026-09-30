using System.Collections.Immutable;
using System.Text.Json;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeCodecTests
{
    internal const string Tenant = "018f0000-0000-7000-8000-000000000001";
    internal const string Database = "018f0000-0000-7000-8000-000000000002";
    internal const string Collection = "018f0000-0000-7000-8000-000000000003";
    internal const string Document = "018f0000-0000-7000-8000-000000000004";
    internal const string Replica = "018f0000-0000-7000-8000-000000000005";
    internal static readonly NativeScope Scope = new(Tenant, Database, 0);
    internal static readonly string SchemaDigest = new('a', 64);

    internal static NativeCommit CreateCommit(string text = "first note") => new(
        new NativeTransactionId(1790452800000, new string('1', 32)), new NativeAuthority.Leaderless(),
        [new NativeWrite(Collection, Document, 1, SchemaDigest, new NativeCondition.Absent(), [],
            new NativeWriteValue.Live([new NativeField(1, new NativeValue.Text(text))]))]);

    [Test]
    public async Task PublishedCreateUpdateDeleteEffectsMatchOpenApiVectors()
    {
        // Producer encoder values are pinned by the accepted correction in contracts/native/provenance.json.
        var create = CreateCommit();
        var created = NativeCodec.EncodeCommit(Scope, create);
        await Assert.That(created.Identity.EffectDigest).IsEqualTo("e0434f6594433812ae108095baaddb29e2365b63e40d3b0147076668ae12b57f");

        var original = create.Writes[0];
        var observed = ImmutableArray.Create(new NativeObserved(Replica, new string('1', 32), 1));
        var update = create with
        {
            TransactionId = new NativeTransactionId(1790452800000, new string('2', 32)),
            Writes = [original with
            {
                Condition = new NativeCondition.Version(new string('d', 64)), Observed = observed,
                Value = new NativeWriteValue.Live([new NativeField(1, new NativeValue.Text("revised note"))])
            }]
        };
        await Assert.That(NativeCodec.EncodeCommit(Scope, update).Identity.EffectDigest)
            .IsEqualTo("dc301f7cd4f7f458bb4ae6436b8cfde1d732f9d7994f85bb47e31eec966722be");
        var delete = update with
        {
            TransactionId = new NativeTransactionId(1790452800000, new string('3', 32)),
            Writes = [update.Writes[0] with { Value = new NativeWriteValue.Tombstone() }]
        };
        await Assert.That(NativeCodec.EncodeCommit(Scope, delete).Identity.EffectDigest)
            .IsEqualTo("61f146edbd4c6a1bb18f8f3ced5c1b03884f17798e0ab0a0c35e757e4a745fd4");
        var newIdentity = create with { TransactionId = new NativeTransactionId(ulong.MaxValue, new string('f', 32)) };
        await Assert.That(NativeCodec.EncodeCommit(Scope, newIdentity).Identity.EffectDigest).IsEqualTo(created.Identity.EffectDigest);
    }

    [Test]
    public async Task PublishedAtomicBoundaryAndNestedValueVectorsPreserveEffectsAndConditions()
    {
        // Exact vector inputs and expected digests from contracts/native/atomic_vectors.json.
        var scope = new NativeScope("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", ulong.MaxValue);
        var write = new NativeWrite("33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444",
            ulong.MaxValue, new string('5', 64), new NativeCondition.Absent(), [],
            new NativeWriteValue.Live([new NativeField(1, new NativeValue.Integer(long.MinValue))]));
        var boundary = new NativeCommit(new NativeTransactionId(ulong.MaxValue, new string('6', 32)),
            new NativeAuthority.Leaderless(), [write]);
        var nested = boundary with
        {
            Writes = [write with
            {
                Condition = new NativeCondition.Version(new string('7', 64)),
                Observed = [new NativeObserved("88888888-8888-8888-8888-888888888888", new string('9', 32), ulong.MaxValue)],
                Value = new NativeWriteValue.Live(
                [
                    new NativeField(1, new NativeValue.Null()),
                    new NativeField(2, new NativeValue.Boolean(true)),
                    new NativeField(3, new NativeValue.Integer(long.MaxValue)),
                    new NativeField(4, new NativeValue.Text("Olá🌍")),
                    new NativeField(5, new NativeValue.Bytes([0, 1, 255])),
                    new NativeField(6, new NativeValue.Map([new NativeField(7, new NativeValue.Array(
                        [new NativeValue.Null(), new NativeValue.Boolean(false), new NativeValue.Text("nested")]))])),
                    new NativeField(7, new NativeValue.Array(
                        [new NativeValue.Map([new NativeField(2, new NativeValue.Integer(-1))]), new NativeValue.Bytes([])]))
                ])
            }]
        };
        var tombstone = boundary with
        {
            Authority = new NativeAuthority.SingleWriter(ulong.MaxValue,
                new NativeWriter("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", new string('b', 32))),
            Writes = [write with { Condition = new NativeCondition.Version(new string('c', 64)), Value = new NativeWriteValue.Tombstone() }]
        };
        (NativeCommit Commit, string Digest)[] vectors =
        [
            (boundary, "05610fc6b661ac9ad17a1206f46496715401c9230e090bfb963639f49717cb29"),
            (nested, "e0d25da3a40ac4e5cf2e380105258864021f270b925b77205e9901fae53b96a0"),
            (tombstone, "9710ab23e2fa3ae2df3f48a7d54603c88c3227d51fcf5ebb2e1f796d36849f6b")
        ];
        foreach (var vector in vectors)
        {
            var encoded = NativeCodec.EncodeCommit(scope, vector.Commit);
            await Assert.That(encoded.Identity.EffectDigest).IsEqualTo(vector.Digest);
            using var document = NativeCodec.Parse(encoded.Body);
            var decoded = NativeCodec.DecodeCommit(scope, document.RootElement);
            await Assert.That(decoded.Authority).IsEqualTo(vector.Commit.Authority);
            await Assert.That(decoded.Writes[0].Condition).IsEqualTo(vector.Commit.Writes[0].Condition);
            await Assert.That(NativeCodec.EncodeCommit(scope, decoded).Identity.EffectDigest).IsEqualTo(vector.Digest);
        }
    }

    [Test]
    public async Task PublishedNativeErrorsStayClosedAndUseFixedDiagnosticsForUnknownIdentifiers()
    {
        foreach (var code in new[]
        {
            "invalid_request", "invalid_cursor", "cursor_expired", "invalid_key", "forbidden_scope", "unknown_route",
            "method_not_allowed", "schema_changed", "idempotency_conflict", "idempotency_expired", "rate_limited",
            "capacity", "storage_unavailable", "stored_corruption", "commit_uncertain", "precondition_failed",
            "authority_changed", "authority_unavailable"
        })
        {
            using var published = NativeCodec.Parse("{\"error\":{\"code\":\"" + code + "\"}}");
            await Assert.That(NativeCodec.DecodeError(published.RootElement)).IsEqualTo(code);
        }
        const string canary = "secret_canary";
        using var document = NativeCodec.Parse("{\"error\":{\"code\":\"" + canary + "\"}}");
        InvalidDataException? rejection = null;
        try { NativeCodec.DecodeError(document.RootElement); }
        catch (InvalidDataException exception) { rejection = exception; }
        await Assert.That(rejection is not null).IsTrue();
        await Assert.That(rejection!.ToString()).DoesNotContain(canary);
    }

    [Test]
    public async Task CanonicalScopeAndExactIntegersRejectCoercionAndPreserveShardZero()
    {
        await Assert.That(NativeCodec.ScopePath(Scope)).Contains("/shards/0");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
        {
            NativeCodec.ScopePath(Scope with { Tenant = Tenant.ToUpperInvariant() });
            return Task.CompletedTask;
        });
        var commit = CreateCommit() with { TransactionId = new NativeTransactionId(ulong.MaxValue, new string('1', 32)) };
        var encoded = NativeCodec.EncodeCommit(Scope, commit);
        await Assert.That(encoded.Body).Contains("\"18446744073709551615\"");
        using var raw = NativeCodec.Parse(encoded.Body.Replace("\"18446744073709551615\"", "18446744073709551615", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
        {
            NativeCodec.DecodeCommit(Scope, raw.RootElement);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task DuplicateUnknownFieldsAndContradictoryPointStatesAreRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
        {
            using var duplicate = NativeCodec.Parse("{\"version\":1,\"version\":1}");
            return Task.CompletedTask;
        });
        foreach (var json in new[]
        {
            "{\"version\":1,\"state\":\"absent\",\"state_version\":null,\"context\":[],\"siblings\":[],\"legacy\":true}",
            "{\"version\":1,\"state\":\"value\",\"state_version\":null,\"context\":[],\"siblings\":[]}",
            "{\"version\":1,\"state\":\"conflict\",\"state_version\":null,\"context\":[],\"siblings\":[]}"
        })
        {
            using var document = NativeCodec.Parse(json);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
            {
                NativeCodec.DecodePoint(document.RootElement);
                return Task.CompletedTask;
            });
        }
    }

    [Test]
    public async Task TypedPointOutputPreservesNullBytesArraysAndCausalContext()
    {
        var fields = ImmutableArray.Create(
            new NativeField(1, new NativeValue.Null()),
            new NativeField(2, new NativeValue.Integer(long.MinValue)),
            new NativeField(3, new NativeValue.Bytes([0, 255])),
            new NativeField(4, new NativeValue.Map([new NativeField(1, new NativeValue.Text("nested"))])),
            new NativeField(5, new NativeValue.Array([new NativeValue.Boolean(true), new NativeValue.Text("array")])));
        var point = new NativePoint(NativePointState.Value, new string('d', 64),
            [new NativeObserved(Replica, new string('1', 32), ulong.MaxValue)],
            [new NativeSibling(new NativeEvent(Database, 0, Replica, new string('1', 32), ulong.MaxValue), 1,
                SchemaDigest, new NativeWriteValue.Live(fields))]);
        var encoded = NativeCodec.EncodePoint(point);
        using var document = NativeCodec.Parse(encoded);
        var decoded = NativeCodec.DecodePoint(document.RootElement);
        await Assert.That(decoded.State).IsEqualTo(NativePointState.Value);
        await Assert.That(decoded.StateVersion).IsEqualTo(point.StateVersion);
        await Assert.That(decoded.Context[0].Sequence).IsEqualTo(ulong.MaxValue);
        await Assert.That(NativeCodec.EncodePoint(decoded)).IsEqualTo(encoded);
        await Assert.That(new NativeValue.Text("sensitive-value").ToString()).DoesNotContain("sensitive-value");
    }

    [Test]
    public async Task NestedArraysAndUnsortedFieldOrWriteIdsAreRejectedBeforePreparation()
    {
        var original = CreateCommit();
        foreach (var value in new NativeWriteValue[]
        {
            new NativeWriteValue.Live([new NativeField(2, new NativeValue.Null()), new NativeField(1, new NativeValue.Null())]),
            new NativeWriteValue.Live([new NativeField(1, new NativeValue.Array([
                new NativeValue.Map([new NativeField(1, new NativeValue.Array([]))])]))])
        })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
            {
                NativeCodec.EncodeCommit(Scope, original with { Writes = [original.Writes[0] with { Value = value }] });
                return Task.CompletedTask;
            });
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
        {
            NativeCodec.EncodeCommit(Scope, original with { Writes = [original.Writes[0], original.Writes[0]] });
            return Task.CompletedTask;
        });
    }
}
