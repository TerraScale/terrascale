// <copyright file="TbepWire.cs" company="TerraScale">
// TerraBase (SE-14). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol;

using System.Buffers;
using System.Text;
/// <summary>
/// The TBEP frame envelope in its MessagePack engine-channel profile:
/// a positional array of exactly
/// <c>[version u8, kind u16, correlation u64, causation u64, idempotency str?, digest u64, body bin]</c>.
/// Every decode failure fails closed (PD-2026-08-26-02 D4).
/// </summary>
public static partial class TbepWire
{
    /// <summary>The fail-closed decode failure of a hostile frame.</summary>
    public sealed class WireException(
        string reason,
        WireFailure failure
    ) : Exception(reason)
    {
        /// <summary>The failure class, mirroring the Rust envelope's rows.</summary>
        public WireFailure Failure { get; } = failure;
    }

    /// <summary>The fail-closed failure classes.</summary>
    public enum WireFailure
    {
        /// <summary>The leading version byte is not the wire version.</summary>
        UnsupportedVersion,

        /// <summary>The frame kind is not in the registry.</summary>
        UnknownFrameKind,

        /// <summary>The digest does not cover the body bytes.</summary>
        DigestMismatch,

        /// <summary>The idempotency pairing is wrong for the frame kind.</summary>
        IdempotencyPairing,

        /// <summary>The frame is structurally invalid (truncation, trailing bytes, wrong arity, nil in a required slot, bad types).</summary>
        MalformedFrame,

        /// <summary>The frame exceeds the decode bound.</summary>
        FrameTooLarge,

        /// <summary>The idempotency key is not constructible.</summary>
        InvalidIdempotencyKey,
    }

    /// <summary>Builds the canonical fail-closed decode exception.</summary>
    /// <param name="reason">The machine-readable reason.</param>
    /// <returns>The exception to throw.</returns>
    public static WireException FormatException(string reason) => new(reason, WireFailure.MalformedFrame);

    /// <summary>Builds a typed fail-closed decode exception.</summary>
    /// <param name="reason">The machine-readable reason.</param>
    /// <param name="failure">The failure class.</param>
    /// <returns>The exception to throw.</returns>
    public static WireException FormatException(string reason, WireFailure failure) => new(reason, failure);

    /// <summary>The idempotency-key byte bound.</summary>
    public const int MaxIdempotencyBytes = 128;

    /// <summary>One sealed frame: its identity, its digest, and its body bytes.</summary>
    public sealed class Frame(
        byte version,
        ushort kind,
        ulong correlation,
        ulong causation,
        string? idempotency,
        ulong digest,
        byte[] body
    )
    {
        /// <summary>The wire version the frame carries.</summary>
        public byte Version { get; } = version;

        /// <summary>The frame kind code.</summary>
        public ushort Kind { get; } = kind;

        /// <summary>The frame's correlation identity.</summary>
        public ulong Correlation { get; } = correlation;

        /// <summary>The peer frame this frame answers.</summary>
        public ulong Causation { get; } = causation;

        /// <summary>The idempotency key, present exactly on mutation-family request frames.</summary>
        public string? Idempotency { get; } = idempotency;

        /// <summary>The sealed digest over the body bytes.</summary>
        public ulong Digest { get; } = digest;

        /// <summary>The encoded body array's bytes.</summary>
        public byte[] Body { get; } = body;
    }

    /// <summary>Validates an idempotency key the way the Rust side does: non-empty, bounded, control-free.</summary>
    /// <param name="key">The candidate key.</param>
    /// <exception cref="WireException">The key violates its limits.</exception>
    public static void ValidateIdempotency(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw FormatException("the key is empty", WireFailure.InvalidIdempotencyKey);
        }

        if (Encoding.UTF8.GetByteCount(key) > MaxIdempotencyBytes)
        {
            throw FormatException("the key exceeds its bound", WireFailure.InvalidIdempotencyKey);
        }

        if (key.Any(char.IsControl))
        {
            throw FormatException("the key carries control characters", WireFailure.InvalidIdempotencyKey);
        }
    }

    /// <summary>Computes the FNV-1a 64 digest over the body bytes.</summary>
    /// <param name="bytes">The body bytes.</param>
    /// <returns>The digest.</returns>
    public static ulong DigestOf(ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            ulong hash = 0xcbf2_9ce4_8422_2325;
            foreach (var bite in bytes)
            {
                hash ^= bite;
                hash *= 0x0000_0100_0000_01b3;
            }

            return hash;
        }
    }

    /// <summary>Seals one frame: enforces the idempotency pairing and computes the digest.</summary>
    /// <param name="kind">The frame kind code.</param>
    /// <param name="correlation">The correlation identity.</param>
    /// <param name="causation">The causation identity.</param>
    /// <param name="idempotency">The idempotency key, exactly on mutation-family kinds.</param>
    /// <param name="body">The encoded body bytes.</param>
    /// <returns>The sealed frame.</returns>
    /// <exception cref="WireException">The pairing is wrong.</exception>
    public static Frame Seal(ushort kind, ulong correlation, ulong causation, string? idempotency, byte[] body)
    {
        var carries = TbepRegistry.CarriesIdempotency(kind);
        if (carries != (idempotency is not null))
        {
            throw FormatException(
                carries
                    ? "a mutation-family frame requires its idempotency key"
                    : "only mutation-family frames carry an idempotency key",
                WireFailure.IdempotencyPairing);
        }

        if (idempotency is not null)
        {
            ValidateIdempotency(idempotency);
        }

        return new Frame(TbepRegistry.WireVersion, kind, correlation, causation, idempotency, DigestOf(body), body);
    }

    /// <summary>Encodes one sealed frame to its envelope bytes.</summary>
    /// <param name="frame">The sealed frame.</param>
    /// <returns>The envelope bytes.</returns>
    public static byte[] Encode(Frame frame)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(7);
        writer.Write(frame.Version);
        writer.Write(frame.Kind);
        writer.Write(frame.Correlation);
        writer.Write(frame.Causation);
        if (frame.Idempotency is null)
        {
            writer.WriteNil();
        }
        else
        {
            writer.Write(frame.Idempotency);
        }

        writer.Write(frame.Digest);
        writer.Write(frame.Body);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Decodes one envelope, failing closed on every violation.</summary>
    /// <param name="bytes">The envelope bytes.</param>
    /// <returns>The sealed frame.</returns>
    /// <exception cref="WireException">Any fail-closed row.</exception>
    public static Frame Decode(byte[] bytes)
    {
        if (bytes.LongLength > TbepRegistry.MaxFrameBytes)
        {
            throw FormatException($"the frame exceeds {TbepRegistry.MaxFrameBytes} bytes", WireFailure.FrameTooLarge);
        }

        var reader = new MessagePackReader(bytes);
        if (reader.TryReadNil())
        {
            throw FormatException("nil is not a frame");
        }

        var count = reader.ReadArrayHeader();
        if (count != 7)
        {
            throw FormatException("an envelope carries exactly seven elements");
        }

        var version = reader.ReadByte();
        if (version != TbepRegistry.WireVersion)
        {
            throw FormatException($"unsupported TBEP wire version {version}", WireFailure.UnsupportedVersion);
        }

        var kind = reader.ReadUInt16();
        if (!TbepRegistry.KnowsKind(kind))
        {
            throw FormatException($"unknown frame kind {kind}", WireFailure.UnknownFrameKind);
        }

        var correlation = reader.ReadUInt64();
        var causation = reader.ReadUInt64();
        string? idempotency = reader.TryReadNil() ? null : reader.ReadString();
        if (idempotency is not null)
        {
            ValidateIdempotency(idempotency);
        }

        var carries = TbepRegistry.CarriesIdempotency(kind);
        if (carries != (idempotency is not null))
        {
            throw FormatException(
                carries
                    ? "a mutation-family frame requires its idempotency key"
                    : "only mutation-family frames carry an idempotency key",
                WireFailure.IdempotencyPairing);
        }

        var digest = reader.ReadUInt64();
        var body = reader.ReadBytes()?.ToArray() ?? throw FormatException("the body must ride as msgpack bin");
        if (reader.Consumed != bytes.LongLength)
        {
            throw FormatException("trailing bytes after the envelope");
        }

        if (DigestOf(body) != digest)
        {
            throw FormatException("the content digest does not cover the body", WireFailure.DigestMismatch);
        }

        return new Frame(version, kind, correlation, causation, idempotency, digest, body);
    }

    /// <summary>Writes one frame to a stream: a u32 little-endian length prefix plus the envelope bytes.</summary>
    /// <param name="stream">The output stream.</param>
    /// <param name="frame">The frame to write.</param>
    /// <returns>A task that completes when the frame is written.</returns>
    public static async Task WriteFrame(Stream stream, Frame frame)
    {
        var envelope = Encode(frame);
        if (envelope.Length > TbepRegistry.MaxFrameBytes)
        {
            throw FormatException($"the frame exceeds {TbepRegistry.MaxFrameBytes} bytes", WireFailure.FrameTooLarge);
        }

        var prefix = BitConverter.GetBytes((uint)envelope.Length);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(prefix);
        }

        await stream.WriteAsync(prefix).ConfigureAwait(false);
        await stream.WriteAsync(envelope).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Reads one frame from a stream; a clean end of stream answers <c>null</c>.</summary>
    /// <param name="stream">The input stream.</param>
    /// <returns>The frame read, or <c>null</c> on a clean end of stream.</returns>
    /// <exception cref="WireException">Any fail-closed row.</exception>
    public static async Task<Frame?> ReadFrame(Stream stream)
    {
        var prefix = new byte[4];
        if (!await ReadExact(stream, prefix).ConfigureAwait(false))
        {
            return null;
        }

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(prefix);
        }

        var length = BitConverter.ToUInt32(prefix);
        if (length > TbepRegistry.MaxFrameBytes)
        {
            throw FormatException($"the frame exceeds {TbepRegistry.MaxFrameBytes} bytes", WireFailure.FrameTooLarge);
        }

        var envelope = new byte[length];
        if (length > 0 && !await ReadExact(stream, envelope).ConfigureAwait(false))
        {
            throw FormatException("the stream ended inside a frame");
        }

        return Decode(envelope);
    }

    private static async Task<bool> ReadExact(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }

    /// <summary>The bytes-on-wire of one frame on the stream (prefix plus envelope).</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The stream wire bytes.</returns>
    public static int StreamWireBytes(Frame frame) => Encode(frame).Length + TbepRegistry.StreamPrefixBytes;

    /// <summary>The per-frame envelope overhead: stream wire bytes minus body bytes.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The overhead in bytes.</returns>
    public static int EnvelopeOverhead(Frame frame) => StreamWireBytes(frame) - frame.Body.Length;
}
