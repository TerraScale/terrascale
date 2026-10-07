using System.Buffers.Text;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Configuration;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// Members as keys <c>member.&lt;base64url node-name&gt;</c> of the KV bucket <see cref="Bucket"/> in ts-orchestrator's
/// NATS, whose <c>max_age</c> is the TTL. ts-orchestrator provisions the bucket and grants the terrabase role access to
/// it (<c>internal/roleidentity</c>); elsewhere it is created on first use (three replicas, one on a single-server NATS).
/// </summary>
internal sealed class NatsMemberStore(NatsSettings settings, ILoggerFactory loggers) : IMemberStore, IAsyncDisposable
{
    // anna-full (TS-647): the bucket TS-633's interim leases used, the only one ts-orchestrator grants TerraBase; a
    // dedicated members bucket needs a grant there first.
    public const string Bucket = "terrabase_backup_leases";

    private const string Prefix = "member.";

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

    public async Task PutAsync(ClusterMember member, CancellationToken cancellationToken) =>
        _ = await (await StoreAsync(cancellationToken).ConfigureAwait(false))
            .PutAsync(Key(member.Name), JsonSerializer.SerializeToUtf8Bytes(member, ClusterJsonContext.Default.ClusterMember), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    public async Task<ClusterView> ListAsync(CancellationToken cancellationToken)
    {
        var store = await StoreAsync(cancellationToken).ConfigureAwait(false);
        var members = new List<ClusterMember>();
        ulong revision = 0;
        await foreach (var key in store.GetKeysAsync([Prefix + ">"], cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var entry = await store.TryGetEntryAsync<byte[]>(key, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (entry.Success && entry.Value.Value is { } value && JsonSerializer.Deserialize(value, ClusterJsonContext.Default.ClusterMember) is { } member)
            {
                members.Add(member);
                revision = Math.Max(revision, entry.Value.Revision);
            }
        }

        return new ClusterView(revision, [.. members.OrderBy(member => member.Name, StringComparer.Ordinal)]);
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken) =>
        await (await StoreAsync(cancellationToken).ConfigureAwait(false)).DeleteAsync(Key(name), cancellationToken: cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        _opening.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    // NATS keys allow letters, digits and -/_=. only; a node name may also hold colons and pluses.
    private static string Key(string name) => Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(name));

    // The client certificate and the CA the servers chain to, as the files hold them now. The certificate file holds the
    // leaf and its intermediates (ts-orchestrator's host issuer) while the servers trust only the root, so the client
    // presents the whole chain: a bare leaf makes the broker bridge close the connection right after the handshake.
    private static ValueTask ConfigureTls(NatsSettings settings, SslClientAuthenticationOptions options)
    {
        var certificate = X509Certificate2.CreateFromPemFile(settings.CertificateFile!, settings.PrivateKeyFile);
        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(settings.CertificateFile!);
        var authorities = new X509Certificate2Collection();
        authorities.ImportFromPemFile(settings.CaFile!);
        options.ClientCertificateContext = SslStreamCertificateContext.Create(certificate, [.. chain.Skip(1), .. authorities], offline: true);
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
                var config = new NatsKVConfig(Bucket)
                {
                    MaxAge = Ttl,
                    History = 1,
                    Storage = NatsKVStorageType.Memory,
                    NumberOfReplicas = 3,
                    LimitMarkerTTL = Ttl,
                };
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

            // The lease fence assumes a key lives exactly Ttl after its last write.
            if ((await store.GetStatusAsync(cancellationToken).ConfigureAwait(false)).Info.Config.MaxAge != Ttl)
            {
                throw new InvalidOperationException($"The KV bucket {Bucket} must keep keys for {Ttl.TotalSeconds} s");
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
