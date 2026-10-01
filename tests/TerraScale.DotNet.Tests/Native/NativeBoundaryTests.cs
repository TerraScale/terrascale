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
        commit = commit with
        {
            Writes = Enumerable.Range(0, 1024).Select(index => commit.Writes[0] with
            {
                Document = $"018f0000-0000-7000-8000-{index:x12}",
                Value = shared
            }).ToImmutableArray()
        };
        using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected."));
        using var client = NativeClientTests.Client(handler);
        await Assert.That(client.PrepareCommit(commit with { Writes = [commit.Writes[0]] }).RequestBody.Length).IsGreaterThan(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        try { client.PrepareCommit(commit); throw new InvalidOperationException("Oversized commit was accepted."); }
        catch (InvalidDataException exception)
        {
            if (exception.Message != "Native encoding exceeds its byte limit.") throw;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(64L * 1024 * 1024);
    }

    [Test]
    public async Task SharedSmallFieldsStopWireAllocationBeforeMaterializingEveryWrite()
    {
        var shared = new NativeWriteValue.Live(Enumerable.Range(1, 1024)
            .Select(id => new NativeField((uint)id, new NativeValue.Null())).ToImmutableArray());
        var commit = NativeCodecTests.CreateCommit();
        commit = commit with
        {
            Writes = Enumerable.Range(0, 512).Select(index => commit.Writes[0] with
            {
                Document = $"018f0000-0000-7000-8000-{index:x12}",
                Value = shared
            }).ToImmutableArray()
        };
        using var client = NativeClientTests.Client(new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected.")));
        await Assert.That(client.PrepareCommit(commit with { Writes = [commit.Writes[0]] }).RequestBody.Length).IsGreaterThan(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        try { client.PrepareCommit(commit); throw new InvalidOperationException("Oversized commit was accepted."); }
        catch (InvalidDataException exception)
        {
            if (exception.Message != "Native request exceeds the 4 MiB limit.") throw;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(64L * 1024 * 1024);
    }

    [Test]
    public async Task JsonEncodingEnforcesWireLimitWhenTextEscapingExpandsAValidEffect()
    {
        var commit = NativeCodecTests.CreateCommit(new string('é', 400_000));
        commit = commit with
        {
            Writes = [commit.Writes[0], commit.Writes[0] with
        {
            Document = "018f0000-0000-7000-8000-000000000006"
        }]
        };
        using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected."));
        using var client = NativeClientTests.Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
        {
            client.PrepareCommit(commit);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task LiteralUnicodeRetainedBodiesAreValidatedWithoutReplacementSerialization()
    {
        var commit = NativeCodecTests.CreateCommit("marker");
        commit = commit with
        {
            Writes = [commit.Writes[0], commit.Writes[0] with
        {
            Document = "018f0000-0000-7000-8000-000000000006"
        }]
        };
        var small = NativeCodec.EncodeCommit(NativeCodecTests.Scope, commit);
        // Independent TB-DOC-EFFECT-v1 digest for two 400,000-character UTF-8 values.
        const string digest = "aa43ee6d10b4e2564c28483341c731922e7b851d976e8eb1cea389a5b3b96254";
        var body = small.Body.Replace(small.Identity.EffectDigest, digest, StringComparison.Ordinal)
            .Replace("marker", new string('é', 400_000), StringComparison.Ordinal);
        var identity = small.Identity with { EffectDigest = digest };
        await Assert.That(System.Text.Encoding.UTF8.GetByteCount(body)).IsLessThan(NativeCodec.MaximumBodyBytes);
        using var document = NativeCodec.Parse(body);
        var decoded = NativeCodec.DecodeCommit(NativeCodecTests.Scope, document.RootElement);
        await Assert.That(decoded.Writes.Length).IsEqualTo(2);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
        {
            NativeCodec.EncodeCommit(NativeCodecTests.Scope, decoded);
            return Task.CompletedTask;
        });

        using var handler = new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected."));
        using var client = NativeClientTests.Client(handler);
        var prepared = client.RestoreCommit(new NativeRetainedOperation(NativeCodecTests.Scope, "https://gru.api.terrascale.tech", identity, body));
        await Assert.That(prepared.RequestBody).IsEqualTo(body);
        await Assert.That(prepared.Identity).IsEqualTo(identity);
        var directory = Directory.CreateTempSubdirectory("terrascale-native-unicode-");
        try
        {
            var path = Path.Combine(directory.FullName, "operation.json");
            await NativeOperationStore.SaveAsync(prepared, path);
            var retained = await NativeOperationStore.LoadAsync(path);
            await Assert.That(retained.Identity).IsEqualTo(identity);
            await Assert.That(client.RestoreCommit(retained).RequestBody).IsEqualTo(body);
        }
        finally { directory.Delete(recursive: true); }
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task MalformedUnicodePropertyNamesReturnTypedFailures(bool mutation)
    {
        using var handler = new NativeClientTests.Handler((_, _) => Task.FromResult(NativeClientTests.Json("{\"\\uD800\":0}")));
        using var client = NativeClientTests.Client(handler);
        if (mutation)
        {
            var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
            var result = await client.CommitAsync(prepared);
            var error = ((NativeResult<NativeCommitReceipt>.Failure)result).Error;
            await Assert.That(error.Code).IsEqualTo("invalid_response");
            await Assert.That(error.Transaction).IsEqualTo(prepared.Identity);
            await Assert.That(error.MutationOutcome).IsEqualTo(NativeMutationOutcome.Uncertain);
        }
        else
        {
            var result = await client.ReadAsync(NativeCodecTests.Collection, NativeCodecTests.Document);
            await Assert.That(((NativeResult<NativePoint>.Failure)result).Error.Code).IsEqualTo("invalid_response");
        }
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
            _ => ValueTask.FromResult(invalidCredential ? "tbk1.\u2603" : "tbk1.fixture-key"), TimeSpan.FromSeconds(30));
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task OversizedPointsAndQueryPagesStopBeforeAggregateSiblingAllocation(bool queryPage)
    {
        var value = new NativeWriteValue.Live([new NativeField(1, new NativeValue.Bytes(ImmutableArray.Create(new byte[256 * 1024])))]);
        var siblings = Enumerable.Range(0, queryPage ? 1 : 128).Select(index => new NativeSibling(
            new NativeEvent(NativeCodecTests.Database, 0, $"018f0000-0000-7000-8000-{index:x12}", new string('1', 32), 1),
            1, NativeCodecTests.SchemaDigest, value)).ToImmutableArray();
        var context = siblings.Select(sibling => new NativeObserved(sibling.Event.Replica, sibling.Event.Incarnation, 1)).ToImmutableArray();
        var point = new NativePoint(queryPage ? NativePointState.Value : NativePointState.Conflict,
            queryPage ? new string('d', 64) : null, context, siblings);
        var rows = Enumerable.Range(0, 128).Select(index => new NativeQueryRow($"018f0000-0000-7000-8000-{index:x12}", point)).ToImmutableArray();
        var control = queryPage
            ? NativeCodec.EncodeQueryPage(new NativeQueryPage([rows[0]], null))
            : NativeCodec.EncodePoint(point with { Siblings = siblings.Take(2).ToImmutableArray(), Context = context.Take(2).ToImmutableArray() });
        await Assert.That(control.Length).IsGreaterThan(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            if (queryPage) NativeCodec.EncodeQueryPage(new NativeQueryPage(rows, null));
            else NativeCodec.EncodePoint(point);
            throw new InvalidOperationException("Oversized point/page was accepted.");
        }
        catch (InvalidDataException exception)
        {
            if (exception.Message != "Native request exceeds the 4 MiB limit.") throw;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(64L * 1024 * 1024);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OversizedPointContextsStopBeforeBufferingEveryOrigin(bool queryPage)
    {
        var context = Enumerable.Range(0, 250_000).Select(index =>
            new NativeObserved($"018f0000-0000-7000-8000-{index:x12}", new string('1', 32), 1)).ToImmutableArray();
        var point = new NativePoint(NativePointState.Deleted, new string('d', 64), context,
            [new NativeSibling(new NativeEvent(NativeCodecTests.Database, 0, context[0].Replica, context[0].Incarnation, 1),
                1, NativeCodecTests.SchemaDigest, new NativeWriteValue.Tombstone())]);
        var smaller = point with { Context = context.Take(16).ToImmutableArray() };
        var validBody = queryPage
            ? NativeCodec.EncodeQueryPage(new NativeQueryPage([new NativeQueryRow(NativeCodecTests.Document, smaller)], null))
            : NativeCodec.EncodePoint(smaller);
        await Assert.That(validBody.Length).IsGreaterThan(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            if (queryPage) NativeCodec.EncodeQueryPage(new NativeQueryPage([new NativeQueryRow(NativeCodecTests.Document, point)], null));
            else NativeCodec.EncodePoint(point);
            throw new InvalidOperationException("Oversized point context was accepted.");
        }
        catch (InvalidDataException exception)
        {
            // A semantic rejection must not satisfy this budget regression.
            if (exception.Message != "Native request exceeds the 4 MiB limit.") throw;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(64L * 1024 * 1024);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecoveryLoadRejectsOwnerPrivateFilesInWritableParents(bool groupWritable)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("terrascale-native-parent-");
        try
        {
            using var client = NativeClientTests.Client(new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected.")));
            var path = Path.Combine(directory.FullName, "operation.json");
            var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
            await NativeOperationStore.SaveAsync(prepared, path);
            await Assert.That((await NativeOperationStore.LoadAsync(path)).Identity).IsEqualTo(prepared.Identity);
            File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                (groupWritable ? UnixFileMode.GroupWrite : UnixFileMode.OtherWrite));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => NativeOperationStore.LoadAsync(path));
            await Assert.That(error!.Message).IsEqualTo("Native operation directory must prevent changes by other users.");
        }
        finally { directory.Delete(recursive: true); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecoveryRejectsPrivateLeavesBeneathReplaceableAncestors(bool load)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("terrascale-native-hierarchy-");
        try
        {
            var ancestor = Directory.CreateDirectory(Path.Combine(root.FullName, "replaceable"));
            var leaf = Directory.CreateDirectory(Path.Combine(ancestor.FullName, "private"));
            using var client = NativeClientTests.Client(new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected.")));
            var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
            var path = Path.Combine(leaf.FullName, "operation.json");
            if (load) await NativeOperationStore.SaveAsync(prepared, path);
            File.SetUnixFileMode(ancestor.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                if (load) await NativeOperationStore.LoadAsync(path);
                else await NativeOperationStore.SaveAsync(prepared, path);
            });
            await Assert.That(error!.Message).IsEqualTo("Native operation directory must prevent changes by other users.");
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task RecoveryAllowsIntermediateLinksControlledByProtectedDirectories()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("terrascale-native-trusted-link-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real"));
            Directory.CreateDirectory(Path.Combine(real.FullName, "private"));
            Directory.CreateSymbolicLink(Path.Combine(root.FullName, "alias"), "real");
            var path = Path.Combine(root.FullName, "alias", "private", "operation.json");
            using var client = NativeClientTests.Client(new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected.")));
            var prepared = client.PrepareCommit(NativeCodecTests.CreateCommit());
            await NativeOperationStore.SaveAsync(prepared, path);
            await Assert.That((await NativeOperationStore.LoadAsync(path)).Identity).IsEqualTo(prepared.Identity);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task RecoveryReadRemainsBoundToTheValidatedDirectoryAfterPathReplacement()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("terrascale-native-read-anchor-");
        try
        {
            var original = Path.Combine(root.FullName, "original");
            var moved = Path.Combine(root.FullName, "moved");
            Directory.CreateDirectory(original);
            await File.WriteAllTextAsync(Path.Combine(original, "operation.json"), "original");
            using var parent = NativeOperationDirectory.Open(original);
            Directory.Move(original, moved);
            Directory.CreateDirectory(original);
            await File.WriteAllTextAsync(Path.Combine(original, "operation.json"), "replacement");
            using var handle = parent.OpenRead("operation.json");
            using var stream = new FileStream(handle, FileAccess.Read);
            using var reader = new StreamReader(stream);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("original");
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task RecoveryLoadRejectsOwnerPrivateFifosAndDirectories()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("terrascale-native-type-");
        try
        {
            var fifo = Path.Combine(directory.FullName, "operation.fifo");
            var start = new System.Diagnostics.ProcessStartInfo("mkfifo") { UseShellExecute = false };
            start.ArgumentList.Add(fifo);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            await Assert.That(process.ExitCode).IsEqualTo(0);
            File.SetUnixFileMode(fifo, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // A blocking FIFO open cannot be interrupted by an async wait in
            // this process. Run the real SDK load in an independently killable
            // probe process so loss of O_NONBLOCK produces a targeted failure.
            // A dedicated probe avoids starting another TUnit engine under CI.
            var host = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet"));
            var probeAssembly = Path.Combine(AppContext.BaseDirectory, "FifoProbe", "TerraScale.DotNet.FifoProbe.dll");
            var probeStart = new System.Diagnostics.ProcessStartInfo(host)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            probeStart.ArgumentList.Add(probeAssembly);
            probeStart.ArgumentList.Add(fifo);
            using var probe = System.Diagnostics.Process.Start(probeStart)!;
            var stdout = probe.StandardOutput.ReadToEndAsync();
            var stderr = probe.StandardError.ReadToEndAsync();
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await probe.WaitForExitAsync(guard.Token); }
            catch (OperationCanceledException)
            {
                probe.Kill(entireProcessTree: true);
                await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var diagnostics = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                throw new TimeoutException("FIFO recovery probe timed out and was terminated: " + string.Join(Environment.NewLine, diagnostics));
            }
            var report = await stdout + await stderr;
            if (probe.ExitCode != 0 || !report.Contains("FIFO_PROBE_READY", StringComparison.Ordinal))
                throw new InvalidOperationException("FIFO recovery probe failed: " + report);
            var nested = Directory.CreateDirectory(Path.Combine(directory.FullName, "nested"));
            var invalidDirectory = await Assert.ThrowsAsync<InvalidDataException>(() => NativeOperationStore.LoadAsync(nested.FullName));
            await Assert.That(invalidDirectory!.Message).IsEqualTo("Native operation file must be a regular file.");
        }
        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task RecoveryLoadRejectsEmbeddedNulInsteadOfOpeningTheFilenamePrefix()
    {
        var directory = Directory.CreateTempSubdirectory("terrascale-native-nul-");
        try
        {
            using var client = NativeClientTests.Client(new NativeClientTests.Handler((_, _) => throw new InvalidOperationException("No dispatch expected.")));
            var path = Path.Combine(directory.FullName, "operation.json");
            await NativeOperationStore.SaveAsync(client.PrepareCommit(NativeCodecTests.CreateCommit()), path);
            await Assert.ThrowsAsync<ArgumentException>(() => NativeOperationStore.LoadAsync(path + "\0suffix"));
            var retained = await NativeOperationStore.LoadAsync(path);
            await Assert.That(retained.Scope).IsEqualTo(NativeCodecTests.Scope);
        }
        finally { directory.Delete(recursive: true); }
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
