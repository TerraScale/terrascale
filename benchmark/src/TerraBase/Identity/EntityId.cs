using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;

namespace TerraBase;

/// <summary>
/// The stable identity of a TerraBase entity.
/// </summary>
/// <remarks>
/// Text is the canonical 26-character, upper-case ULID form. Native storage and
/// MessagePack use the ULID's canonical 16 bytes in network order. ULIDs are
/// identities only; causal ordering is represented by separate metadata.
/// </remarks>
[JsonConverter(typeof(EntityIdJsonConverter))]
[MessagePackFormatter(typeof(EntityIdMessagePackFormatter))]
public readonly record struct EntityId : IComparable<EntityId>, ISpanParsable<EntityId>
{
    /// <summary>The number of bytes in the native representation.</summary>
    public const int ByteLength = 16;

    /// <summary>The number of characters in the canonical text representation.</summary>
    public const int StringLength = 26;

    private readonly System.Ulid value;
    private readonly bool initialized;

    /// <summary>Rejects parameterless construction without a value.</summary>
    public EntityId() => throw new InvalidOperationException("An entity identifier requires a value.");

    /// <summary>Creates an entity identifier from a Cysharp ULID.</summary>
    /// <param name="value">The ULID value.</param>
    public EntityId(System.Ulid value)
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        if (!value.TryWriteBytes(bytes) || !IsCanonicalBytes(bytes))
        {
            throw new ArgumentException("The ULID has a non-canonical byte representation.", nameof(value));
        }

        this.value = value;
        initialized = true;
    }

    /// <summary>Creates an entity identifier from canonical 16-byte ULID data.</summary>
    /// <param name="bytes">The ULID bytes in network order.</param>
    public EntityId(ReadOnlySpan<byte> bytes)
    {
        if (!IsCanonicalBytes(bytes))
        {
            throw new ArgumentException("An entity identifier requires exactly 16 canonical ULID bytes.", nameof(bytes));
        }

        value = new System.Ulid(bytes);
        initialized = true;
    }

    /// <summary>Creates an entity identifier from canonical ULID text.</summary>
    /// <param name="text">The 26-character, upper-case ULID text.</param>
    public EntityId(string text)
    {
        if (!TryParse(text, out var parsed))
        {
            throw new ArgumentException("An entity identifier must be a canonical ULID.", nameof(text));
        }

        this = parsed;
    }

    /// <summary>The underlying Cysharp ULID.</summary>
    public System.Ulid Value => EnsureInitialized();

    /// <summary>The underlying Cysharp ULID.</summary>
    public System.Ulid Ulid => Value;

    /// <summary>Whether this value is the uninitialized default value.</summary>
    public bool IsEmpty => !initialized;

    /// <summary>Creates a new time-sortable entity identifier.</summary>
    public static EntityId New() => new(System.Ulid.NewUlid());

    /// <summary>Parses canonical ULID text.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="FormatException">The text is not canonical ULID text.</exception>
    public static EntityId Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var result)
            ? result
            : throw new FormatException("An entity identifier must be a canonical ULID.");
    }

    /// <summary>Parses canonical ULID text.</summary>
    public static EntityId Parse(ReadOnlySpan<char> text)
    {
        return TryParse(text, out var result)
            ? result
            : throw new FormatException("An entity identifier must be a canonical ULID.");
    }

    /// <summary>Attempts to parse canonical ULID text.</summary>
    public static bool TryParse(string? text, out EntityId result)
    {
        result = default;
        return text is not null && TryParse(text.AsSpan(), out result);
    }

    /// <summary>Attempts to parse canonical ULID text.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out EntityId result)
    {
        result = default;
        if (!IsCanonicalText(text) || !System.Ulid.TryParse(text, out var parsed))
        {
            return false;
        }

        result = new EntityId(parsed);
        return true;
    }

    /// <summary>Parses canonical 16-byte ULID data.</summary>
    public static EntityId FromBytes(ReadOnlySpan<byte> bytes) => new(bytes);

    /// <summary>Attempts to parse canonical 16-byte ULID data.</summary>
    public static bool TryFromBytes(ReadOnlySpan<byte> bytes, out EntityId result)
    {
        result = default;
        if (!IsCanonicalBytes(bytes))
        {
            return false;
        }

        result = new EntityId(bytes);
        return true;
    }

    /// <summary>Returns whether the text is canonical upper-case ULID text.</summary>
    public static bool IsCanonical(string? text) => text is not null && IsCanonicalText(text.AsSpan());

    /// <summary>Copies the canonical 16-byte representation into a caller-owned buffer.</summary>
    public bool TryWriteBytes(Span<byte> destination)
    {
        EnsureInitialized();
        if (destination.Length < ByteLength)
        {
            return false;
        }

        return value.TryWriteBytes(destination[..ByteLength]);
    }

    /// <summary>Returns a new array containing the canonical 16-byte representation.</summary>
    public byte[] ToByteArray()
    {
        EnsureInitialized();
        return value.ToByteArray();
    }

    /// <inheritdoc />
    public int CompareTo(EntityId other) => EnsureInitialized().CompareTo(other.EnsureInitialized());

    /// <inheritdoc />
    public override string ToString() => EnsureInitialized().ToString();

    /// <inheritdoc />
    public static EntityId Parse(string? s, IFormatProvider? provider) => Parse(s ?? throw new ArgumentNullException(nameof(s)));

    /// <inheritdoc />
    public static bool TryParse(string? s, IFormatProvider? provider, out EntityId result) => TryParse(s, out result);

    /// <inheritdoc />
    public static EntityId Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out EntityId result) => TryParse(s, out result);

    private System.Ulid EnsureInitialized() => initialized
        ? value
        : throw new InvalidOperationException("The entity identifier is uninitialized.");

    private static bool IsCanonicalText(ReadOnlySpan<char> text)
    {
        if (text.Length != StringLength || text[0] > '7')
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'H') and
                not (>= 'J' and <= 'K') and
                not (>= 'M' and <= 'N') and
                not (>= 'P' and <= 'T') and
                not (>= 'V' and <= 'Z'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCanonicalBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == ByteLength && (bytes[0] & 0b1100_0000) == 0;
}

/// <summary>Serializes entity IDs as canonical upper-case ULID strings in JSON.</summary>
public sealed class EntityIdJsonConverter : JsonConverter<EntityId>
{
    /// <inheritdoc />
    public override EntityId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !EntityId.TryParse(reader.GetString(), out var result))
        {
            throw new JsonException("Entity identifiers must be canonical ULID strings.");
        }

        return result;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, EntityId value, JsonSerializerOptions options)
    {
        if (value.IsEmpty)
        {
            throw new JsonException("The default entity identifier cannot be serialized.");
        }

        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>Serializes entity IDs as exact 16-byte MessagePack binary values.</summary>
public sealed class EntityIdMessagePackFormatter : IMessagePackFormatter<EntityId>
{
    /// <summary>The shared formatter instance.</summary>
    public static readonly EntityIdMessagePackFormatter Formatter = new();

    /// <inheritdoc />
    public EntityId Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.NextMessagePackType != MessagePackType.Binary)
        {
            throw new MessagePackSerializationException("Entity identifiers must use MessagePack binary values.");
        }

        var bytes = reader.ReadBytes();
        if (bytes is null || bytes.Value.Length != EntityId.ByteLength)
        {
            throw new MessagePackSerializationException("Entity identifiers must contain exactly 16 bytes.");
        }

        try
        {
            return EntityId.FromBytes(bytes.Value.IsSingleSegment
                ? bytes.Value.FirstSpan
                : bytes.Value.ToArray());
        }
        catch (ArgumentException exception)
        {
            throw new MessagePackSerializationException("Entity identifiers must contain canonical ULID bytes.", exception);
        }
    }

    /// <inheritdoc />
    public void Serialize(ref MessagePackWriter writer, EntityId value, MessagePackSerializerOptions options)
    {
        if (value.IsEmpty)
        {
            throw new MessagePackSerializationException("The default entity identifier cannot be serialized.");
        }

        Span<byte> bytes = stackalloc byte[EntityId.ByteLength];
        if (!value.TryWriteBytes(bytes))
        {
            throw new MessagePackSerializationException("The default entity identifier cannot be serialized.");
        }

        writer.Write(bytes);
    }
}
