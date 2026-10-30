using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Schema
{
    /// <summary>The REST JSON representation of schema contracts.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 256,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(TerraBaseCollectionSchema))]
    [JsonSerializable(typeof(TerraBaseDeclaredSchema))]
    [JsonSerializable(typeof(TerraBaseSchemaPreview))]
    public sealed partial class SchemaJson : JsonSerializerContext;
}
