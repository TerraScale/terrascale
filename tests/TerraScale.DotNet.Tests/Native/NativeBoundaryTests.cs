using System.Collections.Immutable;
using TerraScale.DotNet.Native;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativeBoundaryTests
{
    [Test]
    public async Task OversizedSharedByteWritesAreRejectedBeforeAggregateWireAllocation()
    {
        var shared = new NativeWriteValue.Live([new NativeField(1,
            new NativeValue.Bytes(ImmutableArray.Create(new byte[256 * 1024])))]);
        var commit = NativeCodecTests.CreateCommit();
        commit = commit with { Writes = Enumerable.Range(0, 1024).Select(index => commit.Writes[0] with
        {
            Document = $"018f0000-0000-7000-8000-{index:x12}", Value = shared
        }).ToImmutableArray() };
        using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected."));
        using var client = NativeClientTests.Client(handler);
        var before = GC.GetAllocatedBytesForCurrentThread();
        try { client.PrepareCommit(commit); throw new InvalidOperationException("Oversized commit was accepted."); }
        catch (InvalidDataException) { }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(64L * 1024 * 1024);
    }

    [Test]
    public async Task JsonEncodingEnforcesWireLimitWhenTextEscapingExpandsAValidEffect()
    {
        var commit = NativeCodecTests.CreateCommit(new string('é', 400_000));
        commit = commit with { Writes = [commit.Writes[0], commit.Writes[0] with
        {
            Document = "018f0000-0000-7000-8000-000000000006"
        }] };
        using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected."));
        using var client = NativeClientTests.Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
        {
            client.PrepareCommit(commit);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task QueryEncoderRejectsUninitializedOversizedDuplicateAndDescendingRows()
    {
        var point = new NativePoint(NativePointState.Absent, null, [], []);
        var first = new NativeQueryRow(NativeCodecTests.Document, point);
        var second = first with { Document = "018f0000-0000-7000-8000-000000000006" };
        foreach (var rows in new ImmutableArray<NativeQueryRow>[]
        {
            default, Enumerable.Repeat(first, 129).ToImmutableArray(), [first, first], [second, first]
        })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
            {
                NativeCodec.EncodeQueryPage(new NativeQueryPage(rows, null));
                return Task.CompletedTask;
            });
        }
        using var encoded = NativeCodec.Parse(NativeCodec.EncodeQueryPage(new NativeQueryPage([first, second], null)));
        await Assert.That(NativeCodec.DecodeQueryPage(encoded.RootElement, 2).Rows.Length).IsEqualTo(2);
    }

    [Test]
    public async Task MalformedWireSurrogateReturnsInvalidResponse()
    {
        var point = new NativePoint(NativePointState.Value, new string('d', 64),
            [new NativeObserved(NativeCodecTests.Replica, new string('1', 32), 1)],
            [new NativeSibling(new NativeEvent(NativeCodecTests.Database, 0, NativeCodecTests.Replica, new string('1', 32), 1),
                1, NativeCodecTests.SchemaDigest, new NativeWriteValue.Live([new NativeField(1, new NativeValue.Text("marker"))]))]);
        var body = NativeCodec.EncodePoint(point).Replace("marker", "\\uD800", StringComparison.Ordinal);
        using var handler = new NativeClientTests.Handler((_, _) => Task.FromResult(NativeClientTests.Json(body)));
        using var client = NativeClientTests.Client(handler);
        var result = await client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document);
        await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code).IsEqualTo("invalid_response");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidCredentialAndFinalAdmissionValidationNeverDispatchCommit(bool invalidCredential)
    {
        var calls = 0;
        using var http = new HttpClient(new NativeClientTests.Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(NativeClientTests.Json("{}"));
        }));
        INativeAdmissionProvider provider = invalidCredential
            ? new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech") : new FinalInvalidAdmission();
        using var client = new NativeClient(http, NativeCodecTests.Scope, provider,
            _ => ValueTask.FromResult(invalidCredential ? "tbk1.\u2603" : "tbk1.fixture-key"), TimeSpan.FromSeconds(2));
        var result = await client.CommitAsync(client.PrepareCommit(NativeCodecTests.CreateCommit()));
        var failure = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
        await Assert.That(failure.Code).IsEqualTo(invalidCredential ? "invalid_credential" : "invalid_admission");
        await Assert.That(failure.MutationOutcome).IsEqualTo(NativeMutationOutcome.Unknown);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedWriteMetadataIsRejectedBeforeDigestAllocation()
    {
        var commit = NativeCodecTests.CreateCommit();
        var write = commit.Writes[0];
        foreach (var invalid in new[]
        {
            write with { SchemaDigest = new string('x', 64) },
            write with { SchemaDigest = new string('a', 1024 * 1024) },
            write with { Collection = "invalid" },
            write with { Observed = default },
            write with { Observed = [new NativeObserved(NativeCodecTests.Replica, "not-hex", 1)] }
        })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
            {
                NativeCodec.EncodeCommit(NativeCodecTests.Scope, commit with { Writes = [invalid] });
                return Task.CompletedTask;
            });
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OversizedJsonStringsAreRejectedWithoutAllocatingTheEncodedInput(bool multibyte)
    {
        var text = multibyte ? new string('é', NativeCodec.MaximumBodyBytes / 2 + 1)
            : new string('a', NativeCodec.MaximumBodyBytes + 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        try { using var document = NativeCodec.Parse(text); throw new InvalidOperationException("Oversized JSON accepted."); }
        catch (InvalidDataException) { }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(1024L * 1024);
    }

    [Test]
    public async Task CompleteConflictResponsesMayExceedMutationObservationBounds()
    {
        var context = Enumerable.Range(1, 1025).Select(index => new NativeObserved(
            $"00000000-0000-0000-0000-{index:x12}", new string('1', 32), 1)).ToImmutableArray();
        var siblings = context.Select(origin => new NativeSibling(new NativeEvent(NativeCodecTests.Database, 0,
            origin.Replica, origin.Incarnation, 1), 1, NativeCodecTests.SchemaDigest, new NativeWriteValue.Tombstone())).ToImmutableArray();
        var point = new NativePoint(NativePointState.Conflict, null, context, siblings);
        using var handler = new NativeClientTests.Handler((_, _) => Task.FromResult(NativeClientTests.Json(NativeCodec.EncodePoint(point))));
        using var client = NativeClientTests.Client(handler);
        var result = await client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document);
        var actual = ((NativeResult<NativePoint>.Success)result).Value;
        await Assert.That(actual.Context.Length).IsEqualTo(1025);
        await Assert.That(actual.Siblings.Length).IsEqualTo(1025);
        var commit = NativeCodecTests.CreateCommit();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
        {
            client.PrepareCommit(commit with { Writes = [commit.Writes[0] with { Observed = context }] });
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task CancelledPreparedAndRestoredCommitsDoNotDecodeTheirImmutableBodiesAgain()
    {
        var calls = 0;
        using var handler = new NativeClientTests.Handler((_, _) => { calls++; throw new InvalidOperationException("No dispatch expected."); });
        using var client = NativeClientTests.Client(handler);
        var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit(new string('a', 400_000)));
        var restored = client.RestoreCommit(new NativeRetainedOperation(prepared.Scope, prepared.ExplicitOrigin, prepared.Identity, prepared.RequestBody));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (var operation in new[] { prepared, restored })
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var task = client.CommitAsync(operation, cancellation.Token);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var failure = ((NativeResult<NativeCommitReceipt>.Failure)await task).Error;
            await Assert.That(allocated).IsLessThan(256L * 1024);
            await Assert.That(failure.Code).IsEqualTo("cancelled");
            await Assert.That(failure.MutationOutcome).IsEqualTo(NativeMutationOutcome.Unknown);
            await Assert.That(failure.Transaction).IsEqualTo(prepared.Identity);
        }
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task PersistenceRemainsBoundToTheOpenedDirectoryAfterPathReplacement()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("terrascale-native-anchor-");
        try
        {
            var original = Path.Combine(root.FullName, "original");
            var moved = Path.Combine(root.FullName, "moved");
            Directory.CreateDirectory(original);
            using var parent = NativeOperationDirectory.Open(original);
            Directory.Move(original, moved);
            Directory.CreateDirectory(original);
            using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected."));
            using var client = NativeClientTests.Client(handler);
            var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
            await parent.SaveAsync(System.Text.Encoding.UTF8.GetBytes(prepared.RequestBody), "operation.json", CancellationToken.None);
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(moved, "operation.json"))).IsEqualTo(prepared.RequestBody);
            await Assert.That(Directory.GetFiles(original).Length).IsEqualTo(0);
            await Assert.That(Directory.GetFiles(moved).Length).IsEqualTo(1);
            await Assert.ThrowsAsync<IOException>(() => parent.SaveAsync([1], "operation.json", CancellationToken.None));
            await Assert.That(Directory.GetFiles(moved).Length).IsEqualTo(1);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task RecoveryLoadRejectsLinksAndChecksTheOpenedFilesPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("terrascale-native-open-");
        try
        {
            using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected."));
            using var client = NativeClientTests.Client(handler);
            var path = Path.Combine(directory.FullName, "operation.json");
            await NativeOperationStore.SaveAsync(client.PrepareCommit(NativeCodecTests.CreateCommit()), path);
            var link = Path.Combine(directory.FullName, "link.json");
            File.CreateSymbolicLink(link, path);
            await Assert.ThrowsAsync<InvalidDataException>(() => NativeOperationStore.LoadAsync(link));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            await Assert.ThrowsAsync<InvalidDataException>(() => NativeOperationStore.LoadAsync(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class FinalInvalidAdmission : INativeAdmissionProvider
    {
        public ValueTask<NativeAdmission> ResolveAsync(NativeScope scope, string apiKey, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NativeAdmission("https://gru.api.terrascale.tech", static () => throw new InvalidDataException("Stale admission.")));
    }


}
