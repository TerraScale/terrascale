using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using TerraBase.Configuration;

namespace TerraBase.Features.Shapes;

internal readonly record struct ShapePurgeSlot(bool Granted, DateTimeOffset NotBefore);

internal interface IShapePurgeBudget
{
    ValueTask<ShapePurgeSlot> TakeAsync(CancellationToken cancellationToken);

    ValueTask DeferAsync(DateTimeOffset until, CancellationToken cancellationToken);
}

internal sealed class ShapePurgeBudget(NatsSettings? settings, Func<CancellationToken, ValueTask<int>> regions, TimeProvider time)
    : IShapePurgeBudget, IAsyncDisposable
{
    internal const string Bucket = "terrabase_cdn_budget";
    internal const string Key = "purge.6cd3f3639e928077593d9b470765efeb";
    internal static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(12_500);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NatsConnection? _connection;
    private INatsKVStore? _store;

    public async ValueTask<ShapePurgeSlot> TakeAsync(CancellationToken cancellationToken)
    {
        var store = await StoreAsync(cancellationToken).ConfigureAwait(false);
        var spacing = Spacing * Math.Max(1, await regions(cancellationToken).ConfigureAwait(false));
        var now = time.GetUtcNow();
        var entry = await store.TryGetEntryAsync<byte[]>(Key, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (entry.Success)
        {
            var at = Decode(entry.Value.Value);
            if (at > now)
            {
                return new(false, at);
            }

            var updated = await store.TryUpdateAsync(Key, Encode(now + spacing), entry.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
            return updated.Success ? new(true, now) : Lost(updated.Error, now + spacing);
        }

        Missing(entry.Error);
        var created = await store.TryCreateAsync(Key, Encode(now + spacing), cancellationToken: cancellationToken).ConfigureAwait(false);
        return created.Success ? new(true, now) : Lost(created.Error, now + spacing);
    }

    public async ValueTask DeferAsync(DateTimeOffset until, CancellationToken cancellationToken)
    {
        var store = await StoreAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var entry = await store.TryGetEntryAsync<byte[]>(Key, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (entry.Success && Decode(entry.Value.Value) >= until)
            {
                return;
            }

            if (!entry.Success)
            {
                Missing(entry.Error);
            }

            var written = entry.Success
                ? await store.TryUpdateAsync(Key, Encode(until), entry.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await store.TryCreateAsync(Key, Encode(until), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (written.Success)
            {
                return;
            }

            _ = Lost(written.Error, until);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async ValueTask<INatsKVStore> StoreAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _store) is { } ready)
        {
            return ready;
        }

        var identity = settings ?? throw new IOException("the shared CDN purge budget has no NATS role identity");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connection ??= new NatsConnection(Options(identity));
            var store = await new NatsKVContext(new NatsJSContext(_connection)).GetStoreAsync(Bucket, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _store, store);
            return store;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static NatsOpts Options(NatsSettings identity) => new()
    {
        Url = identity.Url,
        Name = "terrabase-cdn-budget",
        ConnectTimeout = TimeSpan.FromSeconds(5),
        RequestTimeout = TimeSpan.FromSeconds(5),
        CommandTimeout = TimeSpan.FromSeconds(5),
        AuthOpts = new NatsAuthOpts
        {
            AuthCredCallback = async (_, cancellationToken) =>
                NatsAuthCred.FromCreds(await File.ReadAllTextAsync(identity.CredentialsFile, cancellationToken).ConfigureAwait(false)),
        },
        TlsOpts = new NatsTlsOpts
        {
            Mode = TlsMode.Implicit,
            ConfigureClientAuthentication = options => Authenticate(options, identity),
        },
    };

    private static ValueTask Authenticate(SslClientAuthenticationOptions options, NatsSettings identity)
    {
        var certificate = X509Certificate2.CreateFromPemFile(identity.Certificate, identity.PrivateKey);
        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(identity.Certificate);
        var authorities = new X509Certificate2Collection();
        authorities.ImportFromPemFile(identity.Authority);
        options.ClientCertificateContext = SslStreamCertificateContext.Create(certificate, [.. chain.Skip(1)], offline: true);
        options.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        options.CertificateChainPolicy.CustomTrustStore.AddRange(authorities);
        return ValueTask.CompletedTask;
    }

    private static void Missing(Exception error)
    {
        if (error is not (NatsKVKeyNotFoundException or NatsKVKeyDeletedException))
        {
            throw new IOException("the shared CDN purge budget could not be read", error);
        }
    }

    private static ShapePurgeSlot Lost(Exception error, DateTimeOffset retry) =>
        error is NatsKVWrongLastRevisionException or NatsKVCreateException
            ? new(false, retry)
            : throw new IOException("the shared CDN purge budget could not be written", error);

    private static DateTimeOffset Decode(byte[]? value) => value is { Length: 8 }
        ? DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(value))
        : throw new IOException("the shared CDN purge budget holds an invalid value");

    private static byte[] Encode(DateTimeOffset at)
    {
        var value = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(value, at.ToUnixTimeMilliseconds());
        return value;
    }
}
