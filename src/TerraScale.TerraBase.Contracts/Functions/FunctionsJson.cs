using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Functions
{
    /// <summary>The REST JSON representation of functions contracts.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 256,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(TerraBaseFunctionInfo))]
    [JsonSerializable(typeof(IReadOnlyList<TerraBaseFunctionInfo>))]
    [JsonSerializable(typeof(IReadOnlyList<TerraBaseFunctionInvocation>))]
    [JsonSerializable(typeof(TerraBaseFunctionStats))]
    [JsonSerializable(typeof(TerraBaseFunctionDeploy))]
    [JsonSerializable(typeof(TerraBaseFunctionSecret))]
    [JsonSerializable(typeof(IReadOnlyList<TerraBaseFunctionSecret>))]
    [JsonSerializable(typeof(TerraBaseFunctionSecretValue))]
    public sealed partial class FunctionsJson : JsonSerializerContext;
}
