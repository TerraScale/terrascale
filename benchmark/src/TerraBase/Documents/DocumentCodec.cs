using System.Buffers.Binary;
using System.Text;

namespace TerraBase;

/// <summary>Bounds enforced before a document codec allocates nested data.</summary>
public sealed record DocumentCodecLimits
{
    /// <summary>The default codec limits.</summary>
    public static DocumentCodecLimits Default { get; } = new();

    /// <summary>The maximum encoded document size.</summary>
    public int MaxBytes { get; init; } = 1 << 20;

    /// <summary>The maximum nested value depth.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>The maximum number of items in one array or object.</summary>
    public int MaxItems { get; init; } = 65_536;

    /// <summary>The maximum UTF-8 byte length of a string or field name.</summary>
    public int MaxStringBytes { get; init; } = 1 << 16;

    internal void Validate()
    {
        if (MaxBytes < 32 || MaxDepth < 1 || MaxItems < 1 || MaxStringBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), "Document codec limits must be positive and usable.");
        }
    }
}

/// <summary>The common operations implemented by one binary document codec.</summary>
public interface IDocumentCodec
{
    /// <summary>Encodes one immutable document value.</summary>
    byte[] Encode(DocumentValue value);

    /// <summary>Decodes one complete binary document value.</summary>
    DocumentValue Decode(ReadOnlySpan<byte> payload);
}

/// <summary>The storage-only binary document codec abstraction.</summary>
public interface IStorageDocumentCodec : IDocumentCodec
{
}

/// <summary>The transport-only binary document codec abstraction.</summary>
public interface ITransportDocumentCodec : IDocumentCodec
{
}

/// <summary>Binary codec for durable storage payloads.</summary>
public sealed class StorageDocumentCodec : IStorageDocumentCodec
{
    private readonly DocumentCodecLimits limits;

    /// <summary>Creates a storage codec with bounded defaults.</summary>
    public StorageDocumentCodec(DocumentCodecLimits? limits = null)
    {
        this.limits = limits ?? DocumentCodecLimits.Default;
        this.limits.Validate();
    }

    /// <inheritdoc />
    public byte[] Encode(DocumentValue value) => BinaryDocumentCodec.Encode(value, BinaryDocumentCodec.StorageMagic, limits);

    /// <inheritdoc />
    public DocumentValue Decode(ReadOnlySpan<byte> payload) =>
        BinaryDocumentCodec.Decode(payload, BinaryDocumentCodec.StorageMagic, limits);
}

/// <summary>Binary codec for transport payloads.</summary>
public sealed class TransportDocumentCodec : ITransportDocumentCodec
{
    private readonly DocumentCodecLimits limits;

    /// <summary>Creates a transport codec with bounded defaults.</summary>
    public TransportDocumentCodec(DocumentCodecLimits? limits = null)
    {
        this.limits = limits ?? DocumentCodecLimits.Default;
        this.limits.Validate();
    }

    /// <inheritdoc />
    public byte[] Encode(DocumentValue value) => BinaryDocumentCodec.Encode(value, BinaryDocumentCodec.TransportMagic, limits);

    /// <inheritdoc />
    public DocumentValue Decode(ReadOnlySpan<byte> payload) =>
        BinaryDocumentCodec.Decode(payload, BinaryDocumentCodec.TransportMagic, limits);
}

/// <summary>Thrown when a binary document payload is malformed or exceeds limits.</summary>
public sealed class DocumentCodecException : Exception
{
    /// <summary>Creates a codec exception.</summary>
    public DocumentCodecException(string message)
        : base(message)
    {
    }
}

internal static class BinaryDocumentCodec
{
    internal static ReadOnlySpan<byte> StorageMagic => "TBS1"u8;
    internal static ReadOnlySpan<byte> TransportMagic => "TBT1"u8;

    private const byte NullTag = 0;
    private const byte BooleanTag = 1;
    private const byte IntegerTag = 2;
    private const byte NumberTag = 3;
    private const byte StringTag = 4;
    private const byte BinaryTag = 5;
    private const byte ArrayTag = 6;
    private const byte ObjectTag = 7;

    internal static byte[] Encode(DocumentValue value, ReadOnlySpan<byte> magic, DocumentCodecLimits limits)
    {
        ArgumentNullException.ThrowIfNull(value);
        var output = new List<byte>(Math.Min(limits.MaxBytes, 256));
        output.AddRange(magic.ToArray());
        output.Add(1);
        WriteValue(output, value, 0, limits);
        if (output.Count > limits.MaxBytes)
        {
            throw new DocumentCodecException("The encoded document exceeds the configured byte limit.");
        }

        return output.ToArray();
    }

    internal static DocumentValue Decode(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> magic, DocumentCodecLimits limits)
    {
        if (payload.Length > limits.MaxBytes)
        {
            throw new DocumentCodecException("The encoded document exceeds the configured byte limit.");
        }

        var reader = new Reader(payload);
        if (!reader.Take(magic.Length).SequenceEqual(magic) || reader.ReadByte() != 1)
        {
            throw new DocumentCodecException("The document codec header is not recognized.");
        }

        var value = ReadValue(ref reader, 0, limits);
        if (!reader.AtEnd)
        {
            throw new DocumentCodecException("The document payload contains trailing bytes.");
        }

        return value;
    }

    private static void WriteValue(List<byte> output, DocumentValue value, int depth, DocumentCodecLimits limits)
    {
        if (depth > limits.MaxDepth)
        {
            throw new DocumentCodecException("The document exceeds the maximum nesting depth.");
        }

        output.Add(value.Kind switch
        {
            DocumentValueKind.Null => NullTag,
            DocumentValueKind.Boolean => BooleanTag,
            DocumentValueKind.Integer => IntegerTag,
            DocumentValueKind.Number => NumberTag,
            DocumentValueKind.String => StringTag,
            DocumentValueKind.Binary => BinaryTag,
            DocumentValueKind.Array => ArrayTag,
            DocumentValueKind.Object => ObjectTag,
            _ => throw new DocumentCodecException("The document contains an unknown value kind."),
        });

        switch (value.Kind)
        {
            case DocumentValueKind.Boolean:
                output.Add(value.BooleanValue ? (byte)1 : (byte)0);
                break;
            case DocumentValueKind.Integer:
                WriteInt64(output, value.IntegerValue);
                break;
            case DocumentValueKind.Number:
                WriteInt64(output, BitConverter.DoubleToInt64Bits(value.NumberValue));
                break;
            case DocumentValueKind.String:
                WriteString(output, value.StringValue, limits);
                break;
            case DocumentValueKind.Binary:
                WriteBytes(output, value.BinaryValue, limits);
                break;
            case DocumentValueKind.Array:
                WriteCount(output, value.ArrayValue.Count, limits);
                foreach (var item in value.ArrayValue)
                {
                    WriteValue(output, item, depth + 1, limits);
                }

                break;
            case DocumentValueKind.Object:
                WriteCount(output, value.ObjectValue.Count, limits);
                foreach (var field in value.ObjectValue.OrderBy(static field => field.Key, StringComparer.Ordinal))
                {
                    WriteString(output, field.Key, limits);
                    WriteValue(output, field.Value, depth + 1, limits);
                }

                break;
        }

        if (output.Count > limits.MaxBytes)
        {
            throw new DocumentCodecException("The encoded document exceeds the configured byte limit.");
        }
    }

    private static DocumentValue ReadValue(ref Reader reader, int depth, DocumentCodecLimits limits)
    {
        if (depth > limits.MaxDepth)
        {
            throw new DocumentCodecException("The document exceeds the maximum nesting depth.");
        }

        return reader.ReadByte() switch
        {
            NullTag => DocumentValue.Null,
            BooleanTag => ReadBoolean(ref reader),
            IntegerTag => DocumentValue.Integer(reader.ReadInt64()),
            NumberTag => ReadNumber(ref reader),
            StringTag => DocumentValue.String(reader.ReadString(limits)),
            BinaryTag => DocumentValue.Binary(reader.ReadBytes(limits)),
            ArrayTag => ReadArray(ref reader, depth, limits),
            ObjectTag => ReadObject(ref reader, depth, limits),
            _ => throw new DocumentCodecException("The document contains an unknown value tag."),
        };
    }

    private static DocumentValue ReadBoolean(ref Reader reader) => reader.ReadByte() switch
    {
        0 => DocumentValue.Boolean(false),
        1 => DocumentValue.Boolean(true),
        _ => throw new DocumentCodecException("The document contains an invalid Boolean value."),
    };

    private static DocumentValue ReadNumber(ref Reader reader)
    {
        var number = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        try
        {
            return DocumentValue.Number(number);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new DocumentCodecException("The document contains a non-finite number.") { Source = exception.Source };
        }
    }

    private static DocumentValue ReadArray(ref Reader reader, int depth, DocumentCodecLimits limits)
    {
        var count = reader.ReadCount(limits);
        var values = new DocumentValue[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = ReadValue(ref reader, depth + 1, limits);
        }

        return DocumentValue.Array(values);
    }

    private static DocumentValue ReadObject(ref Reader reader, int depth, DocumentCodecLimits limits)
    {
        var count = reader.ReadCount(limits);
        var values = new List<KeyValuePair<string, DocumentValue>>(count);
        string? previous = null;
        for (var index = 0; index < count; index++)
        {
            var name = reader.ReadString(limits);
            if (previous is not null && StringComparer.Ordinal.Compare(previous, name) >= 0)
            {
                throw new DocumentCodecException("Object fields must be unique and ordinally sorted.");
            }

            previous = name;
            values.Add(new(name, ReadValue(ref reader, depth + 1, limits)));
        }

        return DocumentValue.Object(values);
    }

    private static void WriteCount(List<byte> output, int count, DocumentCodecLimits limits)
    {
        if (count > limits.MaxItems)
        {
            throw new DocumentCodecException("The document collection exceeds the configured item limit.");
        }

        WriteInt32(output, count);
    }

    private static void WriteString(List<byte> output, string value, DocumentCodecLimits limits)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > limits.MaxStringBytes)
        {
            throw new DocumentCodecException("The document string exceeds the configured byte limit.");
        }

        WriteInt32(output, bytes.Length);
        output.AddRange(bytes);
    }

    private static void WriteBytes(List<byte> output, byte[] value, DocumentCodecLimits limits)
    {
        if (value.Length > limits.MaxBytes)
        {
            throw new DocumentCodecException("The document binary value exceeds the configured byte limit.");
        }

        WriteInt32(output, value.Length);
        output.AddRange(value);
    }

    private static void WriteInt32(List<byte> output, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        output.AddRange(bytes.ToArray());
    }

    private static void WriteInt64(List<byte> output, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        output.AddRange(bytes.ToArray());
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> payload;
        private int position;

        internal Reader(ReadOnlySpan<byte> payload)
        {
            this.payload = payload;
            position = 0;
        }

        internal bool AtEnd => position == payload.Length;

        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || payload.Length - position < count)
            {
                throw new DocumentCodecException("The document payload is truncated.");
            }

            var result = payload.Slice(position, count);
            position += count;
            return result;
        }

        internal byte ReadByte() => Take(1)[0];

        internal long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(sizeof(long)));

        internal int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

        internal int ReadCount(DocumentCodecLimits limits)
        {
            var count = ReadInt32();
            if (count < 0 || count > limits.MaxItems)
            {
                throw new DocumentCodecException("The document collection exceeds the configured item limit.");
            }

            return count;
        }

        internal string ReadString(DocumentCodecLimits limits)
        {
            var length = ReadInt32();
            if (length < 0 || length > limits.MaxStringBytes)
            {
                throw new DocumentCodecException("The document string exceeds the configured byte limit.");
            }

            try
            {
                return new UTF8Encoding(false, true).GetString(Take(length));
            }
            catch (DecoderFallbackException exception)
            {
                throw new DocumentCodecException($"The document contains invalid UTF-8: {exception.Message}");
            }
        }

        internal byte[] ReadBytes(DocumentCodecLimits limits)
        {
            var length = ReadInt32();
            if (length < 0 || length > limits.MaxBytes)
            {
                throw new DocumentCodecException("The document binary value exceeds the configured byte limit.");
            }

            return Take(length).ToArray();
        }
    }
}
