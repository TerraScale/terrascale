using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

internal static class TerraScaleTimestampJson
{
    internal static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new TimestampConverter());
        options.Converters.Add(new OffsetTimestampConverter());
        return options;
    }

    private sealed class TimestampConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64()).UtcDateTime;

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            if (value.Kind == DateTimeKind.Unspecified)
                throw new JsonException("Timestamp DateTime values must specify UTC or local time.");
            writer.WriteNumberValue(new DateTimeOffset(value).ToUnixTimeMilliseconds());
        }
    }

    private sealed class OffsetTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
    }
}
