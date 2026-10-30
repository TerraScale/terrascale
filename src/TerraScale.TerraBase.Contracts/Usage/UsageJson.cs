using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Usage
{
    /// <summary>The REST JSON representation of usage contracts.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(TerraBaseDatabaseUsage))]
    [JsonSerializable(typeof(TerraBaseProjectUsage))]
    [JsonSerializable(typeof(JsonElement))]
    public sealed partial class UsageJson : JsonSerializerContext;
}
