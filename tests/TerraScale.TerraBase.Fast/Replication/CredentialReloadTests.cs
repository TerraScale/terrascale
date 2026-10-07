using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Bogus;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class CredentialReloadTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Concurrent_handshakes_keep_the_cached_pair_and_reload_checks_are_throttled(bool failReload)
    {
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        var clock = new PeerCredentialClock();
        var pause = new PausedRead();
        var pauseReload = false;
        var loads = 0;
        var checks = 0;
        await using var server = await TerraBaseTestServer.StartAsync(original.Apply, services => services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<TerraBaseOptions>();
            var files = options.PeerTls!;
            return new PeerCredentials(options, provider.GetRequiredService<ILogger<PeerCredentials>>(), () =>
            {
                Interlocked.Increment(ref loads);
                if (pauseReload)
                {
                    pause.Hold();
                    if (failReload)
                    {
                        throw new IOException("simulated incomplete credential read");
                    }
                }

                return files.Load();
            }, () =>
            {
                Interlocked.Increment(ref checks);
                return (Fingerprint(files.Certificate), Fingerprint(files.PrivateKey), Fingerprint(files.Authority));
            }, clock);
        }));
        var hello = await HelloAsync(server);
        original.OverwriteWith(rotated, preserveTimestamps: true);
        pauseReload = true;
        clock.Advance();
        var checking = WelcomeAsync(failReload ? original : rotated, server.PeerAddress!, hello);
        try
        {
            await pause.Entered.WaitAsync(TimeSpan.FromSeconds(30));
            // This real connection must finish while another connection still holds the reload gate.
            await WelcomeAsync(original, server.PeerAddress!, hello);
            await Assert.That(Volatile.Read(ref loads)).IsEqualTo(2);
        }
        finally
        {
            pause.Release();
            await checking;
        }

        var loaded = Volatile.Read(ref loads);
        var checkedContents = Volatile.Read(ref checks);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await WelcomeAsync(failReload ? original : rotated, server.PeerAddress!, hello);
        }

        await Assert.That(Volatile.Read(ref loads)).IsEqualTo(loaded);
        await Assert.That(Volatile.Read(ref checks)).IsEqualTo(checkedContents);
        clock.Advance();
        await WelcomeAsync(failReload ? original : rotated, server.PeerAddress!, hello);
        await Assert.That(Volatile.Read(ref loads)).IsEqualTo(loaded + (failReload ? 3 : 0));
        await Assert.That(Volatile.Read(ref checks) > checkedContents).IsTrue();
    }

    [Test]
    public async Task Connection_leases_release_retired_pairs_and_survive_owner_shutdown()
    {
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        var clock = new PeerCredentialClock();
        var pairs = new List<(X509Certificate2 Certificate, X509Certificate2Collection Authorities, X509Certificate2Collection Intermediates)>();
        PeerCredentials? credentials = null;
        PeerCredentials.Lease? held = null;
        try
        {
            await using (var server = await TerraBaseTestServer.StartAsync(original.Apply, services => services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<TerraBaseOptions>();
                return new PeerCredentials(options, provider.GetRequiredService<ILogger<PeerCredentials>>(), () =>
                {
                    var pair = options.PeerTls!.Load();
                    pairs.Add(pair);
                    return pair;
                }, time: clock);
            })))
            {
                credentials = server.Services.GetRequiredService<PeerCredentials>();
                await Assert.That(server.Services.GetRequiredService<TerraBaseOptions>().PeerCredentialSnapshot).IsNull();
                using var oldConnection = GrpcChannel.ForAddress(server.PeerAddress!, new GrpcChannelOptions
                {
                    HttpHandler = original.CreateHandler(),
                    DisposeHttpClient = true,
                });
                var hello = await HelloAsync(server);
                await WelcomeAsync(oldConnection, hello);
                original.OverwriteWith(rotated, preserveTimestamps: true);
                clock.Advance();
                await WelcomeAsync(rotated, server.PeerAddress!, hello);

                await Assert.That(pairs.Count).IsEqualTo(2);
                await Assert.That(pairs[0].Certificate.Handle != IntPtr.Zero).IsTrue();
                await Assert.That(pairs[0].Authorities.Cast<X509Certificate2>().All(authority => authority.Handle != IntPtr.Zero)).IsTrue();
                await WelcomeAsync(oldConnection, hello);
                oldConnection.Dispose();
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Disposed(pairs[0])));
                held = credentials.Capture();
            }

            // The host releases its owner, while this explicit connection lease still owns the active pair.
            await Assert.That(held!.Certificate.Handle != IntPtr.Zero).IsTrue();
            await Assert.That(() => credentials!.Capture()).Throws<ObjectDisposedException>();
            held.Dispose();
            held.Dispose();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Disposed(pairs[1])));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Test]
    [Arguments("peer-certificate")]
    [Arguments("peer-private-key")]
    [Arguments("peer-client-ca")]
    [Arguments("authority-count")]
    public async Task Over_limit_PEM_rotations_keep_the_previous_pair_until_complete(string limit)
    {
        using var original = new PeerTls();
        using var rotated = new PeerTls();
        var clock = new PeerCredentialClock();
        await using var server = await TerraBaseTestServer.StartAsync(original.Apply, services => services.AddSingleton<TimeProvider>(clock));
        var files = server.Services.GetRequiredService<TerraBaseOptions>().PeerTls!;
        if (limit == "authority-count")
        {
            var authority = Encoding.UTF8.GetString(PeerTlsFiles.ReadPemBytes(files.Authority));
            File.WriteAllText(files.Authority, string.Join('\n', Enumerable.Repeat(authority, PeerTlsFiles.MaxAuthorities + 1)));
        }
        else
        {
            var path = limit switch
            {
                "peer-certificate" => files.Certificate,
                "peer-private-key" => files.PrivateKey,
                "peer-client-ca" => files.Authority,
                _ => throw new ArgumentOutOfRangeException(nameof(limit)),
            };
            File.WriteAllBytes(path, new byte[PeerTlsFiles.MaxPemFileBytes + 1]);
        }

        var hello = await HelloAsync(server);
        clock.Advance();
        await WelcomeAsync(original, server.PeerAddress!, hello);
        original.OverwriteWith(rotated, preserveTimestamps: true);
        clock.Advance();
        await WelcomeAsync(rotated, server.PeerAddress!, hello);
    }

    private static bool Disposed((X509Certificate2 Certificate, X509Certificate2Collection Authorities, X509Certificate2Collection Intermediates) pair) =>
        pair.Certificate.Handle == IntPtr.Zero && pair.Authorities.Cast<X509Certificate2>().All(authority => authority.Handle == IntPtr.Zero);

    private static string Fingerprint(string path) => Convert.ToHexString(SHA256.HashData(PeerTlsFiles.ReadPemBytes(path)));

    private static async Task<Hello> HelloAsync(TerraBaseTestServer server)
    {
        var fake = new Faker();
        var database = await server.RegisterAsync(new TenantContext(fake.Random.Guid().ToString("D"), PlanTier.Hobby, fake.Random.Guid().ToString("D"), fake.Random.Guid().ToString("D")));
        return ReplicationProtocol.CreateHello(database, RandomNumberGenerator.GetBytes(16));
    }

    private static async Task WelcomeAsync(PeerTls tls, Uri address, Hello hello)
    {
        using var channel = tls.CreateChannel(address);
        await WelcomeAsync(channel, hello);
    }

    private static async Task WelcomeAsync(GrpcChannel channel, Hello hello)
    {
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = hello });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        await call.RequestStream.CompleteAsync();
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsFalse();
    }

    // Models slow synchronous file I/O with a bounded event barrier, never a timing-based sleep.
    private sealed class PausedRead
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _released;

        public Task Entered => _entered.Task;

        public void Hold()
        {
            lock (_gate)
            {
                _entered.TrySetResult();
                while (!_released)
                {
                    if (!Monitor.Wait(_gate, TimeSpan.FromSeconds(30)))
                    {
                        throw new TimeoutException("the test did not release the credential read");
                    }
                }
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _released = true;
                Monitor.PulseAll(_gate);
            }
        }
    }
}

internal sealed class PeerCredentialClock : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public void Advance() => Interlocked.Add(ref _timestamp, TimeSpan.TicksPerSecond);
}
