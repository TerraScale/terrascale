using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TerraScale.DotNet.Testing;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Release verification and cache publication using file bodies and filesystem faults, never fake HTTP.</summary>
public sealed class TerraBaseReleaseTests
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("not really terrabase");
    private static readonly TerraBaseRelease Release = new("ci-test", Convert.ToHexStringLower(SHA256.HashData(Bytes)));

    [Test]
    public async Task PinnedReleaseNamesItsTagAndDigest()
    {
        await Assert.That(TerraBaseRelease.Pinned.Sha256).Matches("^[0-9a-f]{64}$");
        await Assert.That(TerraBaseRelease.Pinned.DownloadUri.AbsoluteUri).IsEqualTo(
            $"https://git.terrascale.tech/TerraScale/terrabase/releases/download/{TerraBaseRelease.Pinned.Tag}/terrabase-linux-amd64");
    }

    [Test]
    [Arguments("null")]
    [Arguments("empty")]
    [Arguments("short")]
    [Arguments("long")]
    [Arguments("uppercase")]
    [Arguments("nonhex")]
    [Arguments("newline")]
    [Arguments("rooted")]
    [Arguments("traversal")]
    public async Task InvalidDigestIsRejectedBeforeFilesystemOrDownloadActivity(string kind)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-digest-").FullName;
        var cache = Path.Combine(directory, "cache");
        var digest = kind switch
        {
            "null" => null!,
            "empty" => string.Empty,
            "short" => new string('a', 63),
            "long" => new string('a', 65),
            "uppercase" => new string('A', 64),
            "nonhex" => new string('g', 64),
            "newline" => new string('a', 63) + "\n",
            "rooted" => Path.Combine(directory, "escaped"),
            "traversal" => "../escaped",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        try
        {
            foreach (var release in new[] { new TerraBaseRelease("ci-test", digest), Release with { Sha256 = digest } })
            {
                // Even if validation regresses, cancellation prevents contacting the real release host.
                var error = await Assert.ThrowsAsync<ArgumentException>(() => release.ResolveAsync(
                    binary: null, cache, token: "unused", caller.Token));
                await Assert.That(error!.ParamName).IsEqualTo(nameof(TerraBaseRelease.Sha256));
                await Assert.That(error.Message).Contains("exactly 64 lowercase hexadecimal characters");
                await Assert.That(Directory.GetFileSystemEntries(directory)).IsEmpty();

                // Invalid paths prove digest validation precedes even path processing.
                var cacheError = await Assert.ThrowsAsync<ArgumentException>(() => release.ResolveAsync(
                    binary: null, cacheRoot: "\0", token: "unused", caller.Token));
                await Assert.That(cacheError!.ParamName).IsEqualTo(nameof(TerraBaseRelease.Sha256));
                var binaryError = await Assert.ThrowsAsync<ArgumentException>(() => release.ResolveAsync(
                    binary: "\0", cache, token: null, caller.Token));
                await Assert.That(binaryError!.ParamName).IsEqualTo(nameof(TerraBaseRelease.Sha256));
                await Assert.That(Directory.GetFileSystemEntries(directory)).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task CachedBinaryIsUsedOnlyWhenItsDigestMatches()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            Skip.Test("linux-x64 only");
            return;
        }
        var cache = Directory.CreateTempSubdirectory("terrabase-cache-").FullName;
        try
        {
            var cached = Path.Combine(cache, Release.Sha256, "terrabase-linux-amd64");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            await File.WriteAllBytesAsync(cached, Bytes);
            File.SetUnixFileMode(cached, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var (path, unavailable) = await Release.ResolveAsync(binary: null, cache, token: null, CancellationToken.None);
            await Assert.That(path).IsEqualTo(cached);
            await Assert.That(unavailable).IsNull();

            await File.WriteAllTextAsync(cached, "tampered");
            await Assert.That(() => Release.ResolveAsync(binary: null, cache, token: null, CancellationToken.None)).Throws<InvalidOperationException>();
            await Assert.That(() => Release.ResolveAsync(binary: cached, cache, token: null, CancellationToken.None)).Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    public async Task BinaryExecuteAccessIsCheckedAfterDigestValidation(bool explicitBinary, bool otherExecute)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            Skip.Test("linux-x64 only");
            return;
        }
        var cache = Directory.CreateTempSubdirectory("terrabase-cache-").FullName;
        try
        {
            var cached = Path.Combine(cache, Release.Sha256, "terrabase-linux-amd64");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            await File.WriteAllBytesAsync(cached, Bytes);
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
                (otherExecute ? UnixFileMode.OtherExecute : UnixFileMode.OtherRead);
            File.SetUnixFileMode(cached, mode);
            var binary = explicitBinary ? cached : null;

            if (otherExecute && GetEffectiveUserId() == 0)
            {
                // Linux permits root to execute when any execute bit is set.
                var (path, unavailable) = await Release.ResolveAsync(binary, cache, token: null, CancellationToken.None);
                await Assert.That(path).IsEqualTo(cached);
                await Assert.That(unavailable).IsNull();
            }
            else
            {
                var permissionError = await Assert.ThrowsAsync<InvalidOperationException>(() => Release.ResolveAsync(
                    binary, cache, token: null, CancellationToken.None));
                await Assert.That(permissionError!.Message).Contains(cached);
                await Assert.That(permissionError.Message).Contains("chmod u+x");
            }
            await Assert.That(File.GetUnixFileMode(cached)).IsEqualTo(mode);

            await File.WriteAllTextAsync(cached, "tampered");
            var digestError = await Assert.ThrowsAsync<InvalidOperationException>(() => Release.ResolveAsync(
                binary, cache, token: null, CancellationToken.None));
            await Assert.That(digestError!.Message).Contains("not the pinned " + Release.Sha256);
            await Assert.That(File.GetUnixFileMode(cached)).IsEqualTo(mode);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RelativeBinaryAndCachePathsResolveToAbsolutePaths(bool explicitBinary)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            Skip.Test("linux-x64 only");
            return;
        }
        var cache = Directory.CreateTempSubdirectory("terrabase-cache-").FullName;
        try
        {
            var cached = Path.Combine(cache, Release.Sha256, "terrabase-linux-amd64");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            await File.WriteAllBytesAsync(cached, Bytes);
            File.SetUnixFileMode(cached, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var relativeCache = Path.GetRelativePath(Environment.CurrentDirectory, cache);
            var relativeBinary = explicitBinary ? Path.GetRelativePath(Environment.CurrentDirectory, cached) : null;

            var (path, unavailable) = await Release.ResolveAsync(relativeBinary, relativeCache, token: null, CancellationToken.None);
            await Assert.That(path).IsEqualTo(cached);
            await Assert.That(unavailable).IsNull();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments(1L)]
    [Arguments(65537L)]
    public async Task OversizedDownloadLeavesNoPartialBinary(long? declaredLength)
    {
        const int limit = 64 * 1024;
        var cache = Directory.CreateTempSubdirectory("terrabase-cache-").FullName;
        try
        {
            var partial = Path.Combine(cache, "terrabase.partial");
            using var source = new UnseekableStream(new byte[limit + 1]);
            using var content = new StreamContent(source);
            content.Headers.ContentLength = declaredLength;
            await Assert.That(content.Headers.ContentLength).IsEqualTo(declaredLength);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TerraBaseRelease.WriteDownloadAsync(
                content, partial, CancellationToken.None, maxBytes: limit));
            await Assert.That(error!.Message).Contains("byte limit");
            if (declaredLength > limit)
            {
                await Assert.That(source.Position).IsEqualTo(0);
            }
            await Assert.That(Directory.GetFiles(cache)).IsEmpty();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments(65536L)]
    public async Task DownloadAtTheByteLimitPreservesItsBytes(long? declaredLength)
    {
        var bytes = new byte[64 * 1024];
        bytes[^1] = 42;
        var cache = Directory.CreateTempSubdirectory("terrabase-cache-").FullName;
        try
        {
            var partial = Path.Combine(cache, "terrabase.partial");
            using var content = new StreamContent(new UnseekableStream(bytes));
            content.Headers.ContentLength = declaredLength;
            await Assert.That(content.Headers.ContentLength).IsEqualTo(declaredLength);
            await TerraBaseRelease.WriteDownloadAsync(content, partial, CancellationToken.None, maxBytes: bytes.Length);
            await Assert.That((await File.ReadAllBytesAsync(partial)).SequenceEqual(bytes)).IsTrue();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    public async Task CancelledBinaryCopyLeavesNoPartialBinary()
    {
        var cache = Directory.CreateTempSubdirectory("terrabase-cache-").FullName;
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        try
        {
            var partial = Path.Combine(cache, "terrabase.partial");
            using var content = new StreamContent(new UnseekableStream(Bytes));
            await Assert.ThrowsAsync<OperationCanceledException>(() => TerraBaseRelease.WriteDownloadAsync(
                content, partial, caller.Token, maxBytes: Bytes.Length));
            await Assert.That(Directory.GetFiles(cache)).IsEmpty();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    public async Task CancelledDownloadLeavesNoPartialBinary()
    {
        Skip.Unless(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64, "linux-x64 only");
        var cache = Directory.CreateTempSubdirectory("terrabase-cache-").FullName;
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        try
        {
            // The already-cancelled send never contacts the release host.
            await Assert.ThrowsAsync<OperationCanceledException>(() => Release.ResolveAsync(
                binary: null, cache, token: "unused", caller.Token));
            await Assert.That(Directory.GetFiles(cache, "*", SearchOption.AllDirectories)).IsEmpty();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DownloadPublishesVerifiedExecutableAndCleansConcurrentPartials(bool concurrent)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Skip.Test("linux-x64 only");
            return;
        }
        var release = TerraBaseRelease.Pinned;
        var (source, unavailable) = await release.ResolveAsync();
        TerraBaseServer.VerifyCiAvailability(source is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
        Skip.Unless(source is not null, unavailable ?? string.Empty);
        var cache = Directory.CreateTempSubdirectory("terrabase-download-").FullName;
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = concurrent ? 2 : 1;
        var completedBodies = 0;
        try
        {
            async Task WriteBody(string partial, CancellationToken cancellationToken)
            {
                using var content = new StreamContent(File.OpenRead(source!));
                await TerraBaseRelease.WriteDownloadAsync(content, partial, cancellationToken);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (Interlocked.Increment(ref completedBodies) == writers) ready.SetResult();
                // Both writers must create a partial before either can publish the cache entry.
                await ready.Task.WaitAsync(cancellationToken);
            }

            var results = await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => release.ResolveAsync(
                binary: null, cache, token: "file-body", caller.Token, WriteBody)));
            var cached = Path.Combine(cache, release.Sha256, "terrabase-linux-amd64");
            foreach (var (path, reason) in results)
            {
                await Assert.That(path).IsEqualTo(cached);
                await Assert.That(reason).IsNull();
            }
            await Assert.That(completedBodies).IsEqualTo(writers);
            await Assert.That(File.GetUnixFileMode(cached)).IsEqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Assert.That(Directory.GetFiles(cache, "*", SearchOption.AllDirectories)).IsEquivalentTo(new[] { cached });
            await using var published = File.OpenRead(cached);
            await Assert.That(Convert.ToHexStringLower(await SHA256.HashDataAsync(published, caller.Token))).IsEqualTo(release.Sha256);

            // A second resolution verifies the published entry without invoking the body writer.
            var reused = await release.ResolveAsync(binary: null, cache, token: null, caller.Token,
                (_, _) => throw new InvalidOperationException("The cached binary must be reused."));
            await Assert.That(reused.Path).IsEqualTo(cached);
            await Assert.That(reused.Unavailable).IsNull();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    [Arguments("digest", false)]
    [Arguments("io", false)]
    [Arguments("cancel", false)]
    [Arguments("digest", true)]
    public async Task FailedDownloadResolutionPreservesPrimaryAndReportsCleanupFailure(string failure, bool cleanupFails)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Skip.Test("linux-x64 only");
            return;
        }
        var cache = Directory.CreateTempSubdirectory("terrabase-download-").FullName;
        var directory = Path.Combine(cache, TerraBaseRelease.Pinned.Sha256);
        using var caller = new CancellationTokenSource();
        try
        {
            async Task WriteFaultBody(string partial, CancellationToken cancellationToken)
            {
                await File.WriteAllBytesAsync(partial, Bytes, cancellationToken);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (failure == "io") throw new IOException("Download body filesystem fault.");
                if (failure == "cancel")
                {
                    await caller.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            Task Resolve() => TerraBaseRelease.Pinned.ResolveAsync(
                binary: null, cache, token: "file-body", caller.Token, WriteFaultBody,
                beforeDeletePartial: cleanupFails ? partial =>
                {
                    File.Delete(partial);
                    // A directory at the file path makes real deletion fail, including under root.
                    Directory.CreateDirectory(partial);
                } : null);
            Exception? error;
            if (failure == "digest")
            {
                error = await Assert.ThrowsAsync<InvalidOperationException>(Resolve);
                await Assert.That(error!.Message).Contains("not the pinned " + TerraBaseRelease.Pinned.Sha256);
            }
            else if (failure == "io")
            {
                error = await Assert.ThrowsAsync<IOException>(Resolve);
                await Assert.That(error!.Message).Contains("Download body filesystem fault.");
            }
            else
            {
                error = await Assert.ThrowsAsync<OperationCanceledException>(Resolve);
                await Assert.That(((OperationCanceledException)error!).CancellationToken.IsCancellationRequested).IsTrue();
            }
            if (cleanupFails)
            {
                await Assert.That(error!.Data["TerraBaseCleanupException"] is UnauthorizedAccessException).IsTrue();
                await Assert.That(Directory.GetDirectories(directory, "*.partial").Length).IsEqualTo(1);
            }
            else
            {
                await Assert.That(error!.Data["TerraBaseCleanupException"]).IsNull();
                await Assert.That(Directory.GetFiles(cache, "*", SearchOption.AllDirectories)).IsEmpty();
            }
            await Assert.That(File.Exists(Path.Combine(directory, "terrabase-linux-amd64"))).IsFalse();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DownloadExecuteAccessIsCheckedAfterExecutableAssignmentBeforePublication(bool executePolicy)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Skip.Test("linux-x64 only");
            return;
        }
        var release = TerraBaseRelease.Pinned;
        var (source, unavailable) = await release.ResolveAsync();
        TerraBaseServer.VerifyCiAvailability(source is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
        Skip.Unless(source is not null, unavailable ?? string.Empty);
        var cache = Directory.CreateTempSubdirectory("terrabase-execute-").FullName;
        var directory = Path.Combine(cache, release.Sha256);
        var assignedMode = default(UnixFileMode);
        try
        {
            async Task WriteBody(string partial, CancellationToken cancellationToken)
            {
                using var content = new StreamContent(File.OpenRead(source!));
                await TerraBaseRelease.WriteDownloadAsync(content, partial, cancellationToken);
            }

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => release.ResolveAsync(
                binary: null, cache, token: "file-body", CancellationToken.None, WriteBody, partial =>
                {
                    if (OperatingSystem.IsLinux())
                    {
                        assignedMode = File.GetUnixFileMode(partial);
                        if (!executePolicy) File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }, hasExecuteAccess: executePolicy ? partial =>
                {
                    // Pure execute-policy denial keeps real pinned bytes and execute bits intact; no mount changes.
                    if (OperatingSystem.IsLinux()) assignedMode = File.GetUnixFileMode(partial);
                    return false;
                } : null));
            await Assert.That(assignedMode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Assert.That(error!.Message).Contains("not executable by the current effective user");
            await Assert.That(error.Message).Contains("executable filesystem");
            await Assert.That(error.Message).Contains("noexec");
            await Assert.That(error.Data["TerraBaseCleanupException"]).IsNull();
            await Assert.That(Directory.GetFiles(cache, "*", SearchOption.AllDirectories)).IsEmpty();
            await Assert.That(File.Exists(Path.Combine(directory, "terrabase-linux-amd64"))).IsFalse();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    [Arguments("resolve", false)]
    [Arguments("write", false)]
    [Arguments("nested", false)]
    [Arguments("resolve", true)]
    [Arguments("write", true)]
    [Arguments("nested", true)]
    public async Task PartialCleanupFailurePreservesTheOriginalException(string path, bool cancellation)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Skip.Test("linux-x64 only");
            return;
        }
        var cache = Directory.CreateTempSubdirectory("terrabase-cleanup-").FullName;
        using var caller = new CancellationTokenSource();
        Exception primary = cancellation ? new OperationCanceledException("Body cancellation.", null, caller.Token) : new IOException("Body filesystem fault.");
        string? failedPartial = null;
        try
        {
            void BlockDeletion(string partial)
            {
                File.Delete(partial);
                // Deleting a directory with File.Delete fails for root too, without requiring a permissions setup.
                Directory.CreateDirectory(partial);
                failedPartial = partial;
                if (cancellation) caller.Cancel();
            }

            async Task WriteFaultBody(string partial, CancellationToken cancellationToken)
            {
                using var source = new FaultingStream(() => BlockDeletion(partial), primary);
                using var content = new StreamContent(source);
                try
                {
                    await TerraBaseRelease.WriteDownloadAsync(content, partial, cancellationToken);
                }
                finally
                {
                    await Assert.That(source.CanRead).IsFalse();
                }
            }

            async Task Fail()
            {
                if (path == "resolve")
                {
                    await TerraBaseRelease.Pinned.ResolveAsync(binary: null, cache, token: "file-body", caller.Token,
                        (partial, _) =>
                        {
                            BlockDeletion(partial);
                            throw primary;
                        });
                    return;
                }

                if (path == "nested")
                    await TerraBaseRelease.Pinned.ResolveAsync(binary: null, cache, token: "file-body", caller.Token, WriteFaultBody);
                else
                    await WriteFaultBody(Path.Combine(cache, "terrabase.partial"), caller.Token);
            }

            Exception? error = cancellation
                ? await Assert.ThrowsAsync<OperationCanceledException>(Fail)
                : await Assert.ThrowsAsync<IOException>(Fail);
            await Assert.That(ReferenceEquals(error, primary)).IsTrue();
            if (cancellation)
                await Assert.That(((OperationCanceledException)error!).CancellationToken).IsEqualTo(caller.Token);
            var cleanup = (Exception)error!.Data["TerraBaseCleanupException"]!;
            var errors = cleanup is AggregateException aggregate ? aggregate.InnerExceptions.ToArray() : new[] { cleanup };
            await Assert.That(errors.Length).IsEqualTo(path == "nested" ? 2 : 1);
            await Assert.That(errors.All(item => item is UnauthorizedAccessException)).IsTrue();
            await Assert.That(Directory.Exists(failedPartial)).IsTrue();
            await Assert.That(Directory.GetFiles(cache, "*", SearchOption.AllDirectories)).IsEmpty();
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Test]
    public async Task PartialCleanupFailureWithoutAPrimaryExceptionIsReported()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-cleanup-").FullName;
        try
        {
            await Assert.That(() => TerraBaseRelease.DeletePartial(directory)).Throws<UnauthorizedAccessException>();
            await Assert.That(Directory.Exists(directory)).IsTrue();
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Test]
    public async Task UncachedBinaryWithoutATokenIsUnavailable()
    {
        Skip.Unless(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64, "linux-x64 only");
        var cache = Directory.CreateTempSubdirectory("terrabase-cache-").FullName;
        try
        {
            var (path, unavailable) = await Release.ResolveAsync(binary: null, cache, token: null, CancellationToken.None);
            await Assert.That(path).IsNull();
            await Assert.That(unavailable).Contains("ORG_READ_TOKEN");
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    private sealed class FaultingStream(Action onRead, Exception failure) : MemoryStream(Bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            return ValueTask.FromException<int>(failure);
        }
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
