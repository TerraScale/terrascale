using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TerraScale.DotNet.Testing;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Pure startup helper checks and cleanup of the real pinned release process.</summary>
public sealed class TerraBaseTestServerTests
{
    [Test]
    public async Task ListenerAnnouncementsSurviveDiagnosticLogEviction()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-logs-").FullName;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        await using var server = new TerraBaseTestServer(directory, certificate, 0, 0, "unused");
        server.Keep("      Now listening on: http://127.0.0.1:12345");
        server.Keep("      Now listening on: https://[::]:54321");
        for (var i = 0; i < 500; i++)
        {
            server.Keep($"Diagnostic line {i}");
        }

        await Assert.That(server.DiscoverListenerAddresses()).IsTrue();
        await Assert.That(server.ClientAddress).IsEqualTo(new Uri("https://localhost:54321/"));
        var admin = (HttpClient)typeof(TerraBaseTestServer).GetField("admin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        await Assert.That(admin.BaseAddress).IsEqualTo(new Uri("http://127.0.0.1:12345/"));
    }

    [Test]
    public async Task ClientAndAdminListenersBindTheirOwnEphemeralPorts()
    {
        var (server, unavailable) = await TerraBaseTestServer.StartAsync();
        TerraBaseServer.VerifyCiAvailability(server is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
        Skip.Unless(server is not null, unavailable ?? "Pinned TerraBase release unavailable");
        await using var owned = server!;
        var admin = (HttpClient)typeof(TerraBaseTestServer).GetField("admin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        var process = (Process)typeof(TerraBaseTestServer).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        var adminPort = admin.BaseAddress!.Port;
        var clientPort = owned.ClientAddress.Port;
        await Assert.That(adminPort).IsNotEqualTo(clientPort);
        await Assert.That(adminPort).IsGreaterThan(0);
        await Assert.That(clientPort).IsGreaterThan(0);
        await Assert.That(process.StartInfo.ArgumentList).Contains("--admin-port=0");
        await Assert.That(process.StartInfo.ArgumentList).Contains("--client-port=0");
        var tenant = await owned.CreateTenantAsync();
        await Assert.That(tenant.ApiKey).StartsWith("tb_");
    }

    [Test]
    [Arguments("certificate")]
    [Arguments("ports")]
    [Arguments("construction")]
    [Arguments("cleanup")]
    public async Task SetupFailureReleasesOwnedResourcesAndPreservesOriginalError(string stage)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-setup-").FullName;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var failure = new InvalidOperationException("Setup failed");
        try
        {
            Task<TerraBaseTestServer> Start() => TerraBaseTestServer.StartAsync(
                "unused", directory,
                () => stage == "certificate" ? throw failure : certificate,
                () =>
                {
                    if (stage is "ports" or "cleanup")
                    {
                        if (stage == "cleanup")
                        {
                            // Make cleanup fail too; the port failure must remain the caller's exception.
                            Directory.Delete(directory);
                        }

                        throw failure;
                    }

                    // Invalid admin URI fails construction before an HttpClient or process is allocated.
                    return (-1, 1);
                }, CancellationToken.None);

            if (stage == "construction")
            {
                await Assert.ThrowsAsync<UriFormatException>(Start);
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(Start);
                await Assert.That(ReferenceEquals(error, failure)).IsTrue();
            }

            await Assert.That(Directory.Exists(directory)).IsFalse();
            await Assert.That(certificate.Handle == IntPtr.Zero).IsEqualTo(stage != "certificate");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StartupCleanupFailurePreservesTheOriginalErrorAndRecoverableOwner(bool cancelled)
    {
        var (binary, unavailable) = await TerraBaseRelease.Pinned.ResolveAsync();
        TerraBaseServer.VerifyCiAvailability(binary is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
        Skip.Unless(binary is not null, unavailable ?? "Pinned TerraBase release unavailable");
        var directory = Directory.CreateTempSubdirectory("terrabase-startup-").FullName;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Exception startupFailure = cancelled ? new OperationCanceledException("Startup cancelled") : new IOException("Startup failed");
        var cleanupFailure = new Win32Exception(5, "Termination denied");
        TerraBaseTestServer? captured = null;
        try
        {
            var error = await Assert.ThrowsAsync<Exception>(() => TerraBaseTestServer.StartAsync(
                binary!, directory, () => certificate, static () => (0, 0), CancellationToken.None,
                running =>
                {
                    captured = running;
                    running.BeforeKillAsync = () => Task.FromException(cleanupFailure);
                    throw startupFailure;
                }));
            await Assert.That(ReferenceEquals(error, startupFailure)).IsTrue();
            await Assert.That(ReferenceEquals(error!.Data["TerraBaseCleanupException"], cleanupFailure)).IsTrue();
            var owner = error.Data[nameof(TerraBaseTestServer)] as TerraBaseTestServer;
            await Assert.That(ReferenceEquals(owner, captured)).IsTrue();
            await Assert.That(owner).IsNotNull();
            var process = (Process)typeof(TerraBaseTestServer).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            using var observer = Process.GetProcessById(process.Id);
            await Assert.That(observer.HasExited).IsFalse();
            await Assert.That(Directory.Exists(directory)).IsTrue();
            await Assert.That(owner!.Certificate.Handle).IsNotEqualTo(IntPtr.Zero);

            owner.BeforeKillAsync = null;
            await owner.DisposeAsync();
            await Assert.That(observer.HasExited).IsTrue();
            await Assert.That(Directory.Exists(directory)).IsFalse();
            await Assert.That(owner.Certificate.Handle).IsEqualTo(IntPtr.Zero);
        }
        finally
        {
            if (captured is not null)
            {
                captured.BeforeKillAsync = null;
                await captured.DisposeAsync();
            }
            else if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task ProcessExitStillReleasesResources()
    {
        var (server, unavailable) = await TerraBaseTestServer.StartAsync();
        TerraBaseServer.VerifyCiAvailability(server is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
        Skip.Unless(server is not null, unavailable ?? "Pinned TerraBase release unavailable");
        var process = (Process)typeof(TerraBaseTestServer).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var directory = (string)typeof(TerraBaseTestServer).GetField("directory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await server!.DisposeAsync();
            await server.DisposeAsync();

            await Assert.That(Directory.Exists(directory)).IsFalse();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => server!.Http.GetAsync(server.ClientAddress));
            await Assert.That(server!.Certificate.Handle).IsEqualTo(IntPtr.Zero);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                await server!.DisposeAsync();
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task KillFailurePreservesLiveProcessResourcesForRetry(bool concurrent)
    {
        var (server, unavailable) = await TerraBaseTestServer.StartAsync();
        TerraBaseServer.VerifyCiAvailability(server is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
        Skip.Unless(server is not null, unavailable ?? "Pinned TerraBase release unavailable");
        await using var owned = server!;
        var process = (Process)typeof(TerraBaseTestServer).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        var directory = (string)typeof(TerraBaseTestServer).GetField("directory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        // A regression must not orphan TerraBase even if disposal loses its process handle.
        using var cleanupProcess = Process.GetProcessById(process.Id);
        var failure = new Win32Exception(5, "Termination denied");
        var blockedKill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        owned.BeforeKillAsync = () =>
        {
            attempts++;
            return concurrent ? blockedKill.Task : Task.FromException(failure);
        };
        try
        {
            var first = owned.DisposeAsync().AsTask();
            var second = concurrent ? owned.DisposeAsync().AsTask() : first;
            await Assert.That(ReferenceEquals(first, second)).IsTrue();
            if (concurrent)
            {
                blockedKill.SetException(failure);
            }

            var firstError = await Assert.ThrowsAsync<Win32Exception>(() => first);
            var secondError = await Assert.ThrowsAsync<Win32Exception>(() => second);
            await Assert.That(ReferenceEquals(firstError, failure)).IsTrue();
            await Assert.That(ReferenceEquals(firstError, secondError)).IsTrue();
            await Assert.That(attempts).IsEqualTo(1);
            await Assert.That(process.HasExited).IsFalse();
            await Assert.That(process.Handle).IsNotEqualTo(IntPtr.Zero);
            await Assert.That(Directory.Exists(Path.Combine(directory, "data"))).IsTrue();
            await Assert.That(owned.Certificate.Handle).IsNotEqualTo(IntPtr.Zero);
            var tenant = await owned.CreateTenantAsync();
            await Assert.That(tenant.ApiKey).StartsWith("tb_");

            owned.BeforeKillAsync = () =>
            {
                attempts++;
                return Task.CompletedTask;
            };
            var retry = owned.DisposeAsync().AsTask();
            var repeated = owned.DisposeAsync().AsTask();
            await Assert.That(ReferenceEquals(retry, first)).IsFalse();
            await Assert.That(ReferenceEquals(retry, repeated)).IsTrue();
            await Task.WhenAll(retry, repeated);
            await Assert.That(ReferenceEquals(retry, owned.DisposeAsync().AsTask())).IsTrue();
            await Assert.That(attempts).IsEqualTo(2);
            await Assert.That(Directory.Exists(directory)).IsFalse();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => owned.Http.GetAsync(owned.ClientAddress));
            await Assert.That(owned.Certificate.Handle).IsEqualTo(IntPtr.Zero);
        }
        finally
        {
            owned.BeforeKillAsync = null;
            if (!cleanupProcess.HasExited)
            {
                cleanupProcess.Kill(entireProcessTree: true);
                await cleanupProcess.WaitForExitAsync();
            }

            blockedKill.TrySetResult();
            await owned.DisposeAsync();
        }
    }

    [Test]
    public async Task ExitOutputTimeoutRetainsResourcesAndSharesFailureUntilRetry()
    {
        Skip.Unless(OperatingSystem.IsLinux(), "Holding another process's output pipes requires Linux /proc");
        var (server, unavailable) = await TerraBaseTestServer.StartAsync();
        TerraBaseServer.VerifyCiAvailability(server is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
        Skip.Unless(server is not null, unavailable ?? "Pinned TerraBase release unavailable");
        await using var owned = server!;
        var process = (Process)typeof(TerraBaseTestServer).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        var directory = (string)typeof(TerraBaseTestServer).GetField("directory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        var admin = (HttpClient)typeof(TerraBaseTestServer).GetField("admin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        using var cleanupProcess = Process.GetProcessById(process.Id);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // These extra write ends keep the real redirected readers waiting for EOF after TerraBase exits.
        using var stdout = File.OpenHandle($"/proc/{process.Id}/fd/1", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        using var stderr = File.OpenHandle($"/proc/{process.Id}/fd/2", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var exitTimeout = owned.ProcessExitTimeout;
        try
        {
            process.Kill(entireProcessTree: true);
            // This independent observer has no redirected readers, so it confirms OS exit without waiting for EOF.
            await cleanupProcess.WaitForExitAsync(guard.Token);
            owned.ProcessExitTimeout = TimeSpan.FromMilliseconds(100);

            var first = owned.DisposeAsync().AsTask();
            var concurrent = owned.DisposeAsync().AsTask();
            await Assert.That(ReferenceEquals(first, concurrent)).IsTrue();
            var error = await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(guard.Token));
            var sharedError = await Assert.ThrowsAsync<TimeoutException>(() => concurrent.WaitAsync(guard.Token));
            await Assert.That(ReferenceEquals(error, sharedError)).IsTrue();
            await Assert.That(first.IsFaulted).IsTrue();
            await Assert.That(guard.IsCancellationRequested).IsFalse();
            await Assert.That(cleanupProcess.HasExited).IsTrue();
            await Assert.That(process.Handle).IsNotEqualTo(IntPtr.Zero);
            await Assert.That(Directory.Exists(Path.Combine(directory, "data"))).IsTrue();
            await Assert.That(owned.Certificate.Handle).IsNotEqualTo(IntPtr.Zero);
            owned.Http.CancelPendingRequests();
            admin.CancelPendingRequests();

            stdout.Dispose();
            stderr.Dispose();
            owned.ProcessExitTimeout = exitTimeout;
            var retry = owned.DisposeAsync().AsTask();
            var repeated = owned.DisposeAsync().AsTask();
            await Assert.That(ReferenceEquals(retry, first)).IsFalse();
            await Assert.That(ReferenceEquals(retry, repeated)).IsTrue();
            await Task.WhenAll(retry, repeated).WaitAsync(guard.Token);
            await Assert.That(ReferenceEquals(retry, owned.DisposeAsync().AsTask())).IsTrue();
            await Assert.That(Directory.Exists(directory)).IsFalse();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => owned.Http.GetAsync(owned.ClientAddress));
            await Assert.That(owned.Certificate.Handle).IsEqualTo(IntPtr.Zero);
        }
        finally
        {
            stdout.Dispose();
            stderr.Dispose();
            owned.ProcessExitTimeout = exitTimeout;
            if (!cleanupProcess.HasExited)
            {
                cleanupProcess.Kill(entireProcessTree: true);
                await cleanupProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await owned.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task DirectoryDeletionFailureRetriesWithoutReusingDisposedResources()
    {
        var (server, unavailable) = await TerraBaseTestServer.StartAsync();
        TerraBaseServer.VerifyCiAvailability(server is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
        Skip.Unless(server is not null, unavailable ?? "Pinned TerraBase release unavailable");
        await using var owned = server!;
        var process = (Process)typeof(TerraBaseTestServer).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        var directory = (string)typeof(TerraBaseTestServer).GetField("directory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        using var cleanupProcess = Process.GetProcessById(process.Id);
        var failure = new IOException("Directory temporarily unavailable");
        var blockedDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletions = 0;
        var kills = 0;
        owned.BeforeKillAsync = () =>
        {
            kills++;
            return Task.CompletedTask;
        };
        owned.BeforeDeleteDirectoryAsync = () => ++deletions == 1 ? Task.FromException(failure) : blockedDelete.Task;
        try
        {
            var tenant = await owned.CreateTenantAsync();
            await Assert.That(tenant.ApiKey).StartsWith("tb_");
            var first = owned.DisposeAsync().AsTask();
            var error = await Assert.ThrowsAsync<IOException>(() => first);
            await Assert.That(ReferenceEquals(error, failure)).IsTrue();
            await Assert.That(cleanupProcess.HasExited).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(directory, "data"))).IsTrue();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
            {
                _ = process.Handle;
                return Task.CompletedTask;
            });
            await Assert.ThrowsAsync<ObjectDisposedException>(() => owned.Http.GetAsync(owned.ClientAddress));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => owned.CreateApiKeyAsync(tenant.Organization.Id));
            await Assert.That(owned.Certificate.Handle).IsEqualTo(IntPtr.Zero);

            var retry = owned.DisposeAsync().AsTask();
            var concurrent = owned.DisposeAsync().AsTask();
            await Assert.That(ReferenceEquals(retry, first)).IsFalse();
            await Assert.That(ReferenceEquals(retry, concurrent)).IsTrue();
            await Assert.That(retry.IsCompleted).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(directory, "data"))).IsTrue();
            blockedDelete.SetResult();
            await Task.WhenAll(retry, concurrent);
            await Assert.That(Directory.Exists(directory)).IsFalse();
            await Assert.That(ReferenceEquals(retry, owned.DisposeAsync().AsTask())).IsTrue();
            await Assert.That(deletions).IsEqualTo(2);
            await Assert.That(kills).IsEqualTo(1);
        }
        finally
        {
            owned.BeforeKillAsync = null;
            owned.BeforeDeleteDirectoryAsync = null;
            blockedDelete.TrySetResult();
            if (!cleanupProcess.HasExited)
            {
                cleanupProcess.Kill(entireProcessTree: true);
                await cleanupProcess.WaitForExitAsync();
            }

            await owned.DisposeAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RepeatedDisposalReleasesRealProcessResources(bool concurrent)
    {
        var (server, unavailable) = await TerraBaseTestServer.StartAsync();
        TerraBaseServer.VerifyCiAvailability(server is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
        Skip.Unless(server is not null, unavailable ?? "Pinned TerraBase release unavailable");
        await using var owned = server!;
        var directory = (string)typeof(TerraBaseTestServer).GetField("directory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
        if (concurrent)
        {
            await Task.WhenAll(owned.DisposeAsync().AsTask(), owned.DisposeAsync().AsTask());
        }
        else
        {
            await owned.DisposeAsync();
            await owned.DisposeAsync();
        }

        await Assert.That(Directory.Exists(directory)).IsFalse();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owned.Http.GetAsync(owned.ClientAddress));
        await Assert.That(owned.Certificate.Handle).IsEqualTo(IntPtr.Zero);
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(true, false)]
    public async Task CiAvailabilityRejectsOnlyMissingBinariesInCi(bool available, bool isCi)
    {
        const string reason = "Pinned binary unavailable";
        if (!available && isCi)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            {
                TerraBaseServer.VerifyCiAvailability(available, reason, isCi);
                return Task.CompletedTask;
            });
            await Assert.That(error!.Message).Contains(reason);
        }
        else
        {
            TerraBaseServer.VerifyCiAvailability(available, reason, isCi);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RequestTimeoutAndConnectionFailureAreRetried(bool requestTimeout)
    {
        var attempts = 0;
        await TerraBaseTestServer.WaitForHealthAsync(_ => ++attempts == 1
            ? Task.FromException<bool>(requestTimeout
                ? new TaskCanceledException("Request timed out", new TimeoutException())
                : new HttpRequestException("Not listening"))
            : Task.FromResult(true), TimeSpan.FromSeconds(5), CancellationToken.None);
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task CallerCancellationStopsAnInFlightProbeWithoutRetrying()
    {
        using var caller = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => TerraBaseTestServer.WaitForHealthAsync(async token =>
        {
            attempts++;
            await caller.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return false;
        }, TimeSpan.FromSeconds(5), caller.Token));
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task StartupDeadlineBoundsProbesAndRetryDelays(bool pendingProbe)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => TerraBaseTestServer.WaitForHealthAsync(async token =>
        {
            if (pendingProbe)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }

            return false;
        }, TimeSpan.FromMilliseconds(50), guard.Token));
        await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        await Assert.That(guard.IsCancellationRequested).IsFalse();
    }
}
