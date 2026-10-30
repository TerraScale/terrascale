using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts
{
    /// <summary>Reads and writes TerraBase's Unix-millisecond timestamps.</summary>
    public sealed class UnixMillisecondsConverter : JsonConverter<DateTimeOffset>
    {
        /// <inheritdoc />
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(reader.TokenType == JsonTokenType.String
                ? long.Parse(reader.GetString()!, NumberStyles.None, CultureInfo.InvariantCulture)
                : reader.GetInt64());
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
        }
    }
}
