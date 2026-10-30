using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Platform
{
    /// <summary>The REST JSON representation of platform contracts.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(JsonElement))]
    public sealed partial class PlatformJson : JsonSerializerContext;
}
