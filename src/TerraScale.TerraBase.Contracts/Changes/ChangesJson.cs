using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Changes
{
    /// <summary>The REST JSON representation of changes contracts.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 256,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(TerraBaseChange))]
    [JsonSerializable(typeof(TerraBaseExportLine))]
    public sealed partial class ChangesJson : JsonSerializerContext;
}
