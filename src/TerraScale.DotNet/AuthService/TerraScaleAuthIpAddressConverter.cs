using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

internal sealed class TerraScaleAuthIpAddressConverter : JsonConverter<IPAddress>
{
    public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && IPAddress.TryParse(reader.GetString(), out var address)
            ? address : throw new JsonException("Expected an IP address string.");

    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
