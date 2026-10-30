using System.Buffers;
using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;

namespace TerraScale.TerraBase.Contracts
{
    /// <summary>Writes a JSON value as a MessagePack binary of its UTF-8 JSON text.</summary>
    public sealed class JsonElementFormatter : IMessagePackFormatter<JsonElement>
    {
        /// <summary>The shared instance.</summary>
        public static JsonElementFormatter Instance { get; } = new();

        /// <inheritdoc />
        public void Serialize(ref MessagePackWriter writer, JsonElement value, MessagePackSerializerOptions options)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var json = new Utf8JsonWriter(buffer))
            {
                value.WriteTo(json);
            }

            writer.Write(buffer.WrittenSpan);
        }

        /// <inheritdoc />
        public JsonElement Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            var bytes = reader.ReadBytes() ?? throw new MessagePackSerializationException("A JSON value is required.");
            var json = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 256 });
            return JsonElement.ParseValue(ref json);
        }
    }

    /// <summary>Writes an optional JSON value; MessagePack nil is absent.</summary>
    public sealed class NullableJsonElementFormatter : IMessagePackFormatter<JsonElement?>
    {
        /// <summary>The shared instance.</summary>
        public static NullableJsonElementFormatter Instance { get; } = new();

        /// <inheritdoc />
        public void Serialize(ref MessagePackWriter writer, JsonElement? value, MessagePackSerializerOptions options)
        {
            if (value is not { } element)
            {
                writer.WriteNil();
                return;
            }

            JsonElementFormatter.Instance.Serialize(ref writer, element, options);
        }

        /// <inheritdoc />
        public JsonElement? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            return reader.TryReadNil() ? null : JsonElementFormatter.Instance.Deserialize(ref reader, options);
        }
    }
}
