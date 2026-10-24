using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Hosting;

namespace TerraBase.Features.Replication;

/// <summary>
/// Log shipping through the object store (FR-50, docs/terrabase/replication/24-log-chunks.md). A batch large enough that one
/// sealed copy in the bucket costs less than sending it to every other placed region goes to the bucket once, and each
/// peer gets only its reference (<c>Batch.chunk</c>) and reads the bytes from the bucket, which charges no egress. A chunk
/// is the batch's LZ4 envelope sealed as <c>TBL1</c> ‖ nonce ‖ AES-256-GCM ciphertext ‖ tag under its own fresh key, which
/// only the peer streams carry, with the object key as associated data. A machine with a bucket says so in its
/// <c>Welcome</c> and reads chunks; one it cannot read refuses the batch as <c>chunk_unread</c>. Without a bucket no chunk
/// is read or sent.
/// </summary>
internal sealed partial class LogChunks
{
    internal const string Prefix = "v1/replication/";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int Overhead = 4 + NonceBytes + TagBytes;

    private readonly IObjectStore? _store;
    private readonly Counter<long> _requests;

    public LogChunks(IServiceProvider services, TerraBaseOptions options, TimeProvider time, IMeterFactory meters, ILogger<LogChunks> logger)
    {
        _store = services.GetService<IObjectStore>();
        (_options, _time, _logger) = (options, time, logger);
        var meter = meters.Create(Telemetry.MeterName);
        _requests = meter.CreateCounter<long>("terrabase.replication.log_chunk_requests", "{request}",
            "Object store requests of log shipping, by operation (put, get, delete, list).");
        _bytes = meter.CreateCounter<long>("terrabase.replication.shipped_bytes", "By",
            "Change group bytes this machine shipped, by path: peer (batches on the peer streams) or store (log chunks put in the object store).");
    }

    /// <summary>Whether this machine reads log chunks: it has the object store.</summary>
    public bool Reads => _store is not null;

    /// <summary>The envelope <paramref name="chunk"/> holds, read from the object store and opened.</summary>
    /// <exception cref="InvalidDataException">The chunk is malformed, missing, unreadable or does not open under its key.</exception>
    public async Task<ByteString> FetchAsync(V1.LogChunk chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (_store is null || !chunk.Key.StartsWith(Prefix, StringComparison.Ordinal) || chunk.Key.Length > 256 || chunk.Seal.Length != 32
            || chunk.Length < Overhead || chunk.Length > ReplicationProtocol.MaxMessageBytes || chunk.Decoded > ReplicationProtocol.MaxApplicationBytes)
        {
            throw new InvalidDataException("a log chunk names a sealed object of this machine's store");
        }

        var sealedBytes = new byte[chunk.Length];
        bool found;
        try
        {
            _requests.Add(1, Operation("get"));
            found = await _store.ReadAsync(chunk.Key, async (stream, token) =>
            {
                if (await stream.ReadAtLeastAsync(sealedBytes, sealedBytes.Length, throwOnEndOfStream: false, token).ConfigureAwait(false) != sealedBytes.Length
                    || await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                {
                    throw new InvalidDataException("a log chunk has the length its reference names");
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not InvalidDataException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException("a log chunk could not be read from the object store", error);
        }

        if (!found || !sealedBytes.AsSpan(0, 4).SequenceEqual("TBL1"u8))
        {
            throw new InvalidDataException("a log chunk is a sealed object in the object store");
        }

        var plain = new byte[sealedBytes.Length - Overhead];
        try
        {
            using var aes = new AesGcm(chunk.Seal.Span, TagBytes);
            aes.Decrypt(sealedBytes.AsSpan(4, NonceBytes), sealedBytes.AsSpan(4 + NonceBytes, plain.Length), sealedBytes.AsSpan(^TagBytes), plain,
                Encoding.UTF8.GetBytes(chunk.Key));
        }
        catch (AuthenticationTagMismatchException error)
        {
            throw new InvalidDataException("a log chunk opens under the key its reference names", error);
        }

        return ApplicationEnvelope.DecodedLength(plain) == chunk.Decoded
            ? UnsafeByteOperations.UnsafeWrap(plain)
            : throw new InvalidDataException("a log chunk decodes to the length its reference names");
    }

    /// <summary>The chunk object of <paramref name="envelope"/> under <paramref name="key"/>, and the fresh key it is sealed with.</summary>
    internal static (byte[] Sealed, byte[] Seal) Seal(string key, ReadOnlySpan<byte> envelope)
    {
        var seal = RandomNumberGenerator.GetBytes(32);
        var sealedBytes = new byte[envelope.Length + Overhead];
        "TBL1"u8.CopyTo(sealedBytes);
        RandomNumberGenerator.Fill(sealedBytes.AsSpan(4, NonceBytes));
        using var aes = new AesGcm(seal, TagBytes);
        aes.Encrypt(sealedBytes.AsSpan(4, NonceBytes), envelope, sealedBytes.AsSpan(4 + NonceBytes, envelope.Length), sealedBytes.AsSpan(^TagBytes),
            Encoding.UTF8.GetBytes(key));
        return (sealedBytes, seal);
    }

    private static KeyValuePair<string, object?> Operation(string name) => new("operation", name);
}
