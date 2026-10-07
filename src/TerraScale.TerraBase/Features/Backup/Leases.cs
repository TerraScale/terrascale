using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>Expiring leases in a store every member shares: ts-orchestrator's NATS (JetStream KV).</summary>
// anna-full (TS-647): Engine/Cluster's NATS KV leases replace this store; delete it with LeasedShippers.
internal interface ILeaseStore
{
    /// <summary>How long a lease lives unless renewed.</summary>
    TimeSpan Ttl { get; }

    /// <summary>Takes <paramref name="key"/> for <paramref name="holder"/> when no one holds it: its revision, or null when held.</summary>
    Task<ulong?> TryAcquireAsync(string key, string holder, CancellationToken cancellationToken);

    /// <summary>Extends the lease taken or renewed at <paramref name="revision"/>: the new revision, or null when it was lost.</summary>
    Task<ulong?> TryRenewAsync(string key, string holder, ulong revision, CancellationToken cancellationToken);

    /// <summary>Gives the lease taken or renewed at <paramref name="revision"/> back, so another member takes it at once.</summary>
    Task ReleaseAsync(string key, ulong revision, CancellationToken cancellationToken);
}

/// <summary>
/// Leases as keys of the KV bucket <see cref="Bucket"/>, whose <c>max_age</c> is the lease time: a holder rewrites its key
/// at the revision it holds, and a key nobody rewrites expires, so another member can create it. The bucket is created on
/// first use (three replicas, one on a single-server NATS).
/// </summary>
// anna-full (TS-647): Engine/Cluster owns the NATS connection and the lease bucket; delete this with LeasedShippers.
internal sealed class NatsLeaseStore(NatsSettings settings, ILoggerFactory loggers) : ILeaseStore, IAsyncDisposable
{
    public const string Bucket = "terrabase_backup_leases";

    // ts-orchestrator renews the credentials and certificate files before they expire (48 h, renewed at 24 h, TS-651): every
    // connect and reconnect reads them again, and a reconnect refused with an expired JWT keeps retrying until it can.
    // With a client certificate the connection is TLS first: ts-orchestrator's broker bridge terminates mutual TLS before
    // any NATS byte, and its server's INFO then says no TLS. A plain nats-server needs `handshake_first: true` for this.
    private readonly NatsConnection _connection = new(new NatsOpts
    {
        Url = settings.Url,
        Name = "terrabase",
        AuthOpts = new NatsAuthOpts { AuthCredCallback = (_, _) => ValueTask.FromResult(NatsAuthCred.FromCreds(File.ReadAllText(settings.CredentialsFile))) },
        TlsOpts = settings.CertificateFile is null
            ? new NatsTlsOpts()
            : new NatsTlsOpts { Mode = TlsMode.Implicit, ConfigureClientAuthentication = options => ConfigureTls(settings, options) },
        IgnoreAuthErrorAbort = true,
        LoggerFactory = loggers,
    });

    private readonly SemaphoreSlim _opening = new(1, 1);
    private INatsKVStore? _store;

    public TimeSpan Ttl { get; } = TimeSpan.FromSeconds(15);

    public async Task<ulong?> TryAcquireAsync(string key, string holder, CancellationToken cancellationToken)
    {
        var result = await (await StoreAsync(cancellationToken).ConfigureAwait(false)).TryCreateAsync(key, holder, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Value : result.Error is NatsKVCreateException ? null : throw result.Error;
    }

    public async Task<ulong?> TryRenewAsync(string key, string holder, ulong revision, CancellationToken cancellationToken)
    {
        var result = await (await StoreAsync(cancellationToken).ConfigureAwait(false)).TryUpdateAsync(key, holder, revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Value : result.Error is NatsKVWrongLastRevisionException ? null : throw result.Error;
    }

    public async Task ReleaseAsync(string key, ulong revision, CancellationToken cancellationToken) =>
        _ = await (await StoreAsync(cancellationToken).ConfigureAwait(false)).TryDeleteAsync(key, new NatsKVDeleteOpts { Revision = revision }, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        _opening.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    // The client certificate and the CA the servers chain to, as the files hold them now.
    private static ValueTask ConfigureTls(NatsSettings settings, SslClientAuthenticationOptions options)
    {
        var certificate = X509Certificate2.CreateFromPemFile(settings.CertificateFile!, settings.PrivateKeyFile);
        var authorities = new X509Certificate2Collection();
        authorities.ImportFromPemFile(settings.CaFile!);
        options.ClientCertificates = [certificate];
        options.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        options.CertificateChainPolicy.CustomTrustStore.AddRange(authorities);
        return ValueTask.CompletedTask;
    }

    private async Task<INatsKVStore> StoreAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _store) is { } store)
        {
            return store;
        }

        await _opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store is { } opened)
            {
                return opened;
            }

            var context = new NatsKVContext(new NatsJSContext(_connection));
            try
            {
                store = await context.GetStoreAsync(Bucket, cancellationToken).ConfigureAwait(false);
            }
            catch (NatsJSApiException error) when (error.Error.Code == 404)
            {
                var config = new NatsKVConfig(Bucket) { MaxAge = Ttl, History = 1, Storage = NatsKVStorageType.Memory, NumberOfReplicas = 3 };
                try
                {
                    store = await context.CreateOrUpdateStoreAsync(config, cancellationToken).ConfigureAwait(false);
                }
                catch (NatsJSApiException)
                {
                    // A single-server NATS (development) cannot hold three replicas.
                    store = await context.CreateOrUpdateStoreAsync(config with { NumberOfReplicas = 1 }, cancellationToken).ConfigureAwait(false);
                }
            }

            Volatile.Write(ref _store, store);
            return store;
        }
        finally
        {
            _opening.Release();
        }
    }
}

/// <summary>
/// The interim <see cref="IShipperLeases"/> of a cluster: one lease per file in <see cref="ILeaseStore"/>. A member
/// tries a free lease at most every 5 s, renews a held one every 5 s at its revision, and stops shipping when it could not
/// renew for 10 s, 5 s before the lease can expire for the others. The epoch is the revision it took the lease at. Held
/// leases are given back at shutdown, after the last pass shipped, so another member takes over at once.
/// </summary>
/// <remarks>anna-full (TS-647): the database's primary member lease in Engine/Cluster replaces these leases.</remarks>
internal sealed partial class LeasedShippers(ILeaseStore leases, TerraBaseOptions options, TimeProvider time, ILogger<LeasedShippers> logger)
    : IShipperLeases, IAsyncDisposable
{
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(5);

    // A restarted member is a new holder: it never mistakes a lease its previous process held for its own.
    private readonly string _holder = $"{options.NodeName}/{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<string, Lease> _files = new(StringComparer.Ordinal);

    public async ValueTask<ulong?> ShipperEpochAsync(TenantContext? database, CancellationToken cancellationToken)
    {
        // NATS keys take no slashes.
        var key = BackupLayout.File(database).Replace('/', '.');
        var lease = _files.GetOrAdd(key, static _ => new Lease());
        if (lease.Revision is { } revision)
        {
            if (time.GetElapsedTime(lease.Renewed) >= RenewInterval)
            {
                try
                {
                    if (await leases.TryRenewAsync(key, _holder, revision, cancellationToken).ConfigureAwait(false) is { } renewed)
                    {
                        (lease.Revision, lease.Renewed) = (renewed, time.GetTimestamp());
                    }
                    else
                    {
                        LogLost(logger, key);
                        lease.Revision = null;
                        return null;
                    }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    LogFailed(logger, key, error);
                }
            }

            if (time.GetElapsedTime(lease.Renewed) < leases.Ttl - RenewInterval)
            {
                return lease.Epoch;
            }

            LogLost(logger, key);
            lease.Revision = null;
            return null;
        }

        if (lease.Tried is { } tried && time.GetElapsedTime(tried) < RenewInterval)
        {
            return null;
        }

        lease.Tried = time.GetTimestamp();
        try
        {
            if (await leases.TryAcquireAsync(key, _holder, cancellationToken).ConfigureAwait(false) is not { } acquired)
            {
                return null;
            }

            (lease.Revision, lease.Epoch, lease.Renewed) = (acquired, acquired, time.GetTimestamp());
            return acquired;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, key, error);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var (key, lease) in _files)
        {
            if (lease.Revision is not { } revision)
            {
                continue;
            }

            try
            {
                await leases.ReleaseAsync(key, revision, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // It expires on its own.
                LogFailed(logger, key, error);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "This member lost the backup lease of {Key} and stops shipping it")]
    private static partial void LogLost(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The backup lease of {Key} could not be taken, renewed or released; the next pass retries")]
    private static partial void LogFailed(ILogger logger, string key, Exception error);

    // One file's lease, touched only by the pass that ships the file.
    private sealed class Lease
    {
        public ulong? Revision { get; set; }
        public ulong Epoch { get; set; }
        public long Renewed { get; set; }
        public long? Tried { get; set; }
    }
}

/// <summary>A single member ships every file: the interim <see cref="IShipperLeases"/> without NATS (one member, tests).</summary>
// anna-full (TS-647): a single member holds every database's primary lease in Engine/Cluster, so this goes too.
internal sealed class SingleShipper : IShipperLeases
{
    public ValueTask<ulong?> ShipperEpochAsync(TenantContext? database, CancellationToken cancellationToken) => ValueTask.FromResult<ulong?>(1);
}
