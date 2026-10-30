using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Logs
{
    /// <summary>The REST JSON representation of logs contracts.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 256,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(TerraBaseRequestLogPage))]
    [JsonSerializable(typeof(TerraBaseQueryShapePage))]
    public sealed partial class LogsJson : JsonSerializerContext;
}
