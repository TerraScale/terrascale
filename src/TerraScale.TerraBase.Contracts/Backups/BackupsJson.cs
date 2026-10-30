using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Backups
{
    /// <summary>The REST JSON representation of backups contracts.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(TerraBaseRestoreWindow))]
    [JsonSerializable(typeof(TerraBasePointInTimePlan))]
    [JsonSerializable(typeof(TerraBasePointInTimeRequest))]
    public sealed partial class BackupsJson : JsonSerializerContext;
}
