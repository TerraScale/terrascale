using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet.TerraBase;

namespace ApiBench;

// Both SDK and raw reads must match the seed's id, returned version, value state and exact compact document bytes.
internal sealed class ParityDocument(string id, string version, string body)
{
    internal bool Matches(TerraBaseDocument? value) => value is not null
        && string.Equals(value.Id, id, StringComparison.Ordinal)
        && string.Equals(value.Version, version, StringComparison.Ordinal)
        && value.State == "value" && value.Document.ValueKind == JsonValueKind.Object
        && string.Equals(value.Document.GetRawText(), body, StringComparison.Ordinal);

    internal bool Matches(ReadOnlySpan<byte> response)
    {
        try { return Matches(JsonSerializer.Deserialize(response, ParityDocumentJson.Default.TerraBaseDocument)); }
        catch (JsonException) { return false; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TerraBaseDocument))]
internal partial class ParityDocumentJson : JsonSerializerContext
{
}
