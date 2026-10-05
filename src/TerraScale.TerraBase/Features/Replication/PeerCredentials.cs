using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TerraScale.TerraBase.Configuration;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>
/// This node's peer certificate and the peer certificate authorities, read from <see cref="PeerTlsFiles"/>
/// and checked at most once a second on TLS handshakes, so certificates rotate
/// without a restart. Every certificate in the authority file is trusted, so a rotation can list the
/// old and the new authority until every node has its new certificate. A rotation that cannot be read
/// yet, or whose certificate does not chain to its authorities yet (files replaced one at a time), keeps
/// the previous credentials and is retried on the next handshake.
/// </summary>
internal sealed partial class PeerCredentials : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private readonly PeerTlsFiles _files;
    private readonly ILogger<PeerCredentials> _logger;
    private readonly TimeProvider _time;
    private readonly Func<(X509Certificate2 Certificate, X509Certificate2Collection Authorities, X509Certificate2Collection Intermediates)> _load;
    private readonly Func<(string, string, string)>? _fingerprints;
    private readonly Lock _reload = new();
    private readonly Lock _ownership = new();
    private Snapshot _current;
    private long _lastCheck;
    private bool _disposed;

    public PeerCredentials(TerraBaseOptions options, ILogger<PeerCredentials> logger, TimeProvider time)
        : this(options, logger, null, time: time)
    {
    }

    internal PeerCredentials(
        TerraBaseOptions options, ILogger<PeerCredentials> logger,
        Func<(X509Certificate2 Certificate, X509Certificate2Collection Authorities, X509Certificate2Collection Intermediates)>? load,
        Func<(string, string, string)>? fingerprints = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _files = options.PeerTls ?? throw new InvalidOperationException("the peer listener is not configured");
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _lastCheck = _time.GetTimestamp();
        _load = load ?? _files.Load;
        _fingerprints = fingerprints;
        // Keep the actual startup pair even if files stay incomplete throughout listener initialization.
        _current = options.TakePeerCredentialSnapshot()
            ?? throw new InvalidOperationException("the peer listener credentials were not validated");
        try
        {
            if (load is not null || fingerprints is not null)
            {
                // Integration hooks exercise initialization before connections can acquire leases.
                Reload(forceReload: true);
            }
        }
        catch
        {
            _current.Dispose();
            throw;
        }
    }

    /// <summary>Leases one pair for the connection; concurrent reloads and checks within one second use the cached pair.</summary>
    public Lease Capture()
    {
        if (_reload.TryEnter())
        {
            try
            {
                var now = _time.GetTimestamp();
                lock (_ownership)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                }

                if (_time.GetElapsedTime(_lastCheck, now) >= CheckInterval)
                {
                    // Start the interval before I/O so failed loads are throttled as well.
                    _lastCheck = now;
                    Reload();
                }
            }
            finally
            {
                _reload.Exit();
            }
        }

        lock (_ownership)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _current.Acquire();
        }
    }

    private void Reload(bool forceReload = false)
    {
        Snapshot? loaded = null;
        try
        {
            if (!forceReload && _current.Fingerprints == Fingerprints())
            {
                return;
            }

            loaded = LoadStable();
            if (loaded is null)
            {
                LogReloadFailed(_logger);
                return;
            }

            Snapshot? replaced = null;
            lock (_ownership)
            {
                if (!_disposed)
                {
                    replaced = _current;
                    _current = loaded;
                    loaded = null;
                }
            }

            if (replaced is not null)
            {
                // Release ownership; connection leases keep an old generation alive until they close.
                replaced.Dispose();
                LogReloaded(_logger);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            LogReloadFailed(_logger);
        }
        finally
        {
            loaded?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_ownership)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _current.Dispose();
    }

    // Fingerprints are read before and after loading, and a
    // load during which a file changed is discarded (null after three tries), so a cached snapshot never
    // pairs old contents with new fingerprints and the next handshake reloads it.
    private Snapshot? LoadStable() => LoadStable(_files, _load, _fingerprints);

    internal static Snapshot? LoadStable(
        PeerTlsFiles files,
        Func<(X509Certificate2 Certificate, X509Certificate2Collection Authorities, X509Certificate2Collection Intermediates)>? load = null,
        Func<(string, string, string)>? fingerprints = null)
    {
        load ??= files.Load;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var before = fingerprints?.Invoke() ?? Fingerprints(files);
                var (certificate, authorities, intermediates) = load();
                var stable = false;
                try
                {
                    if ((fingerprints?.Invoke() ?? Fingerprints(files)) == before)
                    {
                        var snapshot = new Snapshot(before, certificate, authorities, intermediates);
                        stable = true;
                        return snapshot;
                    }
                }
                finally
                {
                    if (!stable)
                    {
                        PeerTlsFiles.Dispose(certificate, authorities, intermediates);
                    }
                }
            }
            catch (Exception error) when (attempt < 2 && error is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                // A staged rotation can fail before loading completes; it still gets the bounded retry.
            }
        }

        return null;
    }

    private (string, string, string) Fingerprints() =>
        _fingerprints?.Invoke() ?? Fingerprints(_files);

    private static (string, string, string) Fingerprints(PeerTlsFiles files) =>
        (Fingerprint(files.Certificate), Fingerprint(files.PrivateKey), Fingerprint(files.Authority));

    private static string Fingerprint(string path)
    {
        return Convert.ToHexString(SHA256.HashData(PeerTlsFiles.ReadPemBytes(path)));
    }

    // Messages name the settings, never the paths.
    [LoggerMessage(LogLevel.Information, "Reloaded peer-certificate, peer-private-key and peer-client-ca")]
    private static partial void LogReloaded(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "peer-certificate, peer-private-key or peer-client-ca changed but cannot be read or do not chain; keeping the previous certificates")]
    private static partial void LogReloadFailed(ILogger logger);

    internal sealed class Snapshot((string, string, string) fingerprints, X509Certificate2 certificate, X509Certificate2Collection authorities, X509Certificate2Collection intermediates) : IDisposable
    {
        private int _references = 1;
        private int _ownerReleased;

        public (string, string, string) Fingerprints { get; } = fingerprints;

        public X509Certificate2 Certificate { get; } = certificate;

        public SslStreamCertificateContext CertificateContext { get; } = Context(certificate, intermediates, authorities);

        private static SslStreamCertificateContext Context(X509Certificate2 certificate, X509Certificate2Collection intermediates, X509Certificate2Collection authorities)
        {
            var chain = new X509Certificate2Collection(intermediates);
            chain.AddRange(authorities);
            return SslStreamCertificateContext.Create(certificate, chain, offline: true);
        }

        public X509ChainPolicy Policy(string usage) => PeerTlsFiles.Policy(authorities, usage);

        public bool IsTrusted(X509Certificate2 certificate, string usage, X509Chain? presented)
        {
            if (presented?.ChainElements.Count > PeerTlsFiles.MaxAuthorities + 2)
            {
                return false;
            }

            var extra = new X509Certificate2Collection();
            if (presented is not null)
            {
                foreach (var element in presented.ChainElements.Cast<X509ChainElement>().Skip(1))
                {
                    extra.Add(element.Certificate);
                }
            }

            return PeerTlsFiles.Chains(certificate, authorities, usage, extra);
        }

        public Lease Acquire() => new(this);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
            {
                Release();
            }
        }

        public void AddReference() => Interlocked.Increment(ref _references);

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                PeerTlsFiles.Dispose(Certificate, authorities, intermediates);
                foreach (var intermediate in CertificateContext.IntermediateCertificates)
                {
                    intermediate.Dispose();
                }
            }
        }
    }

    internal sealed class Lease : IDisposable
    {
        private Snapshot? _snapshot;

        internal Lease(Snapshot snapshot)
        {
            _snapshot = snapshot;
            snapshot.AddReference();
        }

        // Weak connection keys can lose their lease without an explicit close callback.
        ~Lease() => Release();

        public X509Certificate2 Certificate => Snapshot.Certificate;

        private Snapshot Snapshot => Volatile.Read(ref _snapshot) ?? throw new ObjectDisposedException(nameof(Lease));

        public SslStreamCertificateContext CertificateContext => Snapshot.CertificateContext;

        public X509ChainPolicy Policy(string usage) => Snapshot.Policy(usage);

        public bool IsTrusted(X509Certificate2 certificate, string usage, X509Chain? presented = null) => Snapshot.IsTrusted(certificate, usage, presented);

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private void Release() => Interlocked.Exchange(ref _snapshot, null)?.Release();
    }
}
