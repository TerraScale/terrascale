using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Management
{
    /// <summary>The REST JSON representation of management contracts.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(TerraBaseManagedDatabase))]
    [JsonSerializable(typeof(TerraBaseDatabasePage))]
    [JsonSerializable(typeof(TerraBaseDatabasePlacement))]
    [JsonSerializable(typeof(TerraBaseIndex))]
    [JsonSerializable(typeof(TerraBaseIndexPage))]
    [JsonSerializable(typeof(TerraBaseCreateIndex))]
    [JsonSerializable(typeof(TerraBaseCollectionStatsPage))]
    [JsonSerializable(typeof(JsonElement))]
    public sealed partial class ManagementJson : JsonSerializerContext;
}
