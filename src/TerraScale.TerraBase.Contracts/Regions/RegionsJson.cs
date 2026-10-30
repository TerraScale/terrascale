using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Regions
{
    /// <summary>The REST JSON representation of regions contracts.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(TerraBaseRegions))]
    [JsonSerializable(typeof(TerraBaseHealth))]
    public sealed partial class RegionsJson : JsonSerializerContext;
}
