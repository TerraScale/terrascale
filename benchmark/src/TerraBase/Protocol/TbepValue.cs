// <copyright file="TbepValue.cs" company="TerraScale">
// TerraBase (SE-14). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol;

/// <summary>
/// The recursive document-body value of the TerraBase Engine Protocol:
/// a tagged array <c>[tag]</c> or <c>[tag, payload]</c> on the wire, with
/// objects carried as name-sorted field lists so identical values encode
/// to identical bytes in every language (PD-2026-08-26-02 D4).
/// </summary>
[MessagePackFormatter(typeof(TbepValueFormatter))]
public sealed class TbepValue : IEquatable<TbepValue>
{
    private TbepValue(byte tag, object? payload)
    {
        this.Tag = tag;
        this.Payload = payload;
    }

    /// <summary>The null value (tag 0).</summary>
    public static TbepValue Null { get; } = new(0, null);

    /// <summary>Builds a boolean value (tag 1).</summary>
    public static TbepValue Of(bool value) => new(1, value);

    /// <summary>Builds an integer value (tag 2).</summary>
    public static TbepValue Of(long value) => new(2, value);

    /// <summary>Builds a number value (tag 3).</summary>
    public static TbepValue Of(double value) => new(3, value);

    /// <summary>Builds a string value (tag 4).</summary>
    public static TbepValue Of(string value) => new(4, value);

    /// <summary>Builds an array value (tag 5).</summary>
    public static TbepValue Of(TbepValue[] value) => new(5, value);

    /// <summary>Builds an object value (tag 6); the fields canonicalize name-sorted on encode.</summary>
    public static TbepValue Of(ObjectField[] value) => new(6, value);

    /// <summary>The wire tag (0..=6).</summary>
    public byte Tag { get; }

    /// <summary>The payload: bool, long, double, string, TbepValue[], or ObjectField[]. Null for tag 0.</summary>
    public object? Payload { get; }

    /// <inheritdoc/>
    public bool Equals(TbepValue? other) =>
        other is not null
        && this.Tag == other.Tag
        && (this.Payload, other.Payload) switch
        {
            (null, null) => true,
            (bool a, bool b) => a == b,
            (long a, long b) => a == b,
            (double a, double b) => a.Equals(b),
            (string a, string b) => a == b,
            (TbepValue[] a, TbepValue[] b) => a.SequenceEqual(b),
            (ObjectField[] a, ObjectField[] b) => a.SequenceEqual(b),
            _ => false,
        };

    /// <inheritdoc/>
    public override bool Equals(object? obj) => this.Equals(obj as TbepValue);

    /// <inheritdoc/>
    public override int GetHashCode() => System.HashCode.Combine(this.Tag, this.Payload);
}

/// <summary>One object field: a name and its value, carried name-sorted on the wire.</summary>
[MessagePackFormatter(typeof(ObjectFieldFormatter))]
public sealed record ObjectField(string Name, TbepValue Value);

/// <summary>The MessagePack formatter of <see cref="TbepValue"/>: positional arrays with nil reserved for nullable fields.</summary>
#pragma warning disable MsgPack009
public sealed class TbepValueFormatter : IMessagePackFormatter<TbepValue?>
{
    /// <summary>The shared singleton formatter.</summary>
    public static readonly TbepValueFormatter Formatter = new();

    /// <inheritdoc/>
    public TbepValue? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        var count = reader.ReadArrayHeader();
        var tag = reader.ReadByte();
        switch (tag)
        {
            case 0:
                if (count != 1)
                {
                    throw TbepWire.FormatException("null carries no payload");
                }

                return TbepValue.Null;
            case 1:
                if (count != 2)
                {
                    throw TbepWire.FormatException("boolean carries exactly one payload");
                }

                return TbepValue.Of(reader.ReadBoolean());
            case 2:
                if (count != 2)
                {
                    throw TbepWire.FormatException("integer carries exactly one payload");
                }

                return TbepValue.Of(reader.ReadInt64());
            case 3:
                if (count != 2)
                {
                    throw TbepWire.FormatException("number carries exactly one payload");
                }

                return TbepValue.Of(reader.ReadDouble());
            case 4:
                if (count != 2)
                {
                    throw TbepWire.FormatException("string carries exactly one payload");
                }

                return TbepValue.Of(reader.ReadString() ?? throw TbepWire.FormatException("nil is not a string"));
            case 5:
                if (count != 2)
                {
                    throw TbepWire.FormatException("array carries exactly one payload");
                }

                var items = options.Resolver!.GetFormatter<TbepValue[]>()!.Deserialize(ref reader, options);
                return TbepValue.Of(items);
            case 6:
                if (count != 2)
                {
                    throw TbepWire.FormatException("object carries exactly one payload");
                }

                var fields = options.Resolver!.GetFormatter<ObjectField[]>()!.Deserialize(ref reader, options);
                // Canonical decode: name-sorted.
                Array.Sort(fields, (left, right) => string.CompareOrdinal(left.Name, right.Name));
                return TbepValue.Of(fields);
            default:
                throw TbepWire.FormatException($"unknown TbepValue tag {tag}");
        }
    }

    /// <inheritdoc/>
    public void Serialize(ref MessagePackWriter writer, TbepValue? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        switch (value.Tag)
        {
            case 0:
                writer.WriteArrayHeader(1);
                writer.Write(value.Tag);
                return;
            case 1:
                writer.WriteArrayHeader(2);
                writer.Write(value.Tag);
                writer.Write((bool)value.Payload!);
                return;
            case 2:
                writer.WriteArrayHeader(2);
                writer.Write(value.Tag);
                writer.Write((long)value.Payload!);
                return;
            case 3:
                writer.WriteArrayHeader(2);
                writer.Write(value.Tag);
                writer.Write((double)value.Payload!);
                return;
            case 4:
                writer.WriteArrayHeader(2);
                writer.Write(value.Tag);
                writer.Write((string)value.Payload!);
                return;
            case 5:
                writer.WriteArrayHeader(2);
                writer.Write(value.Tag);
                options.Resolver!.GetFormatter<TbepValue[]>()!.Serialize(ref writer, (TbepValue[])value.Payload!, options);
                return;
            case 6:
                writer.WriteArrayHeader(2);
                writer.Write(value.Tag);
                var fields = (ObjectField[])value.Payload!;
                // Canonical encode: name-sorted objects.
                var sorted = fields.OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
                options.Resolver!.GetFormatter<ObjectField[]>()!.Serialize(ref writer, sorted, options);
                return;
            default:
                throw TbepWire.FormatException($"unknown TbepValue tag {value.Tag}");
        }
    }
}

/// <summary>The strict formatter of <see cref="ObjectField"/>: a positional [name, value] pair.</summary>
public sealed class ObjectFieldFormatter : IMessagePackFormatter<ObjectField?>
{
    /// <inheritdoc/>
    public ObjectField Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            throw TbepWire.FormatException("nil is not an object field");
        }

        var count = reader.ReadArrayHeader();
        if (count != 2)
        {
            throw TbepWire.FormatException("an object field carries exactly a name and a value");
        }

        var name = reader.ReadString() ?? throw TbepWire.FormatException("nil is not a field name");
        var value = TbepValueFormatter.Formatter.Deserialize(ref reader, options)
            ?? throw TbepWire.FormatException("nil is not an object field value");
        return new ObjectField(name, value);
    }

    /// <inheritdoc/>
    public void Serialize(ref MessagePackWriter writer, ObjectField? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            throw TbepWire.FormatException("nil is not an object field");
        }

        writer.WriteArrayHeader(2);
        writer.Write(value.Name);
        TbepValueFormatter.Formatter.Serialize(ref writer, value.Value, options);
    }
}
