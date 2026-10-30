using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Import
{
    /// <summary>The REST JSON shape of the Import API.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 256)]
    [JsonSerializable(typeof(TerraBaseImportJob))]
    [JsonSerializable(typeof(IReadOnlyList<TerraBaseImportJob>))]
    [JsonSerializable(typeof(TerraBaseCreateImport))]
    [JsonSerializable(typeof(TerraBaseUpdateImport))]
    [JsonSerializable(typeof(TerraBaseStartImport))]
    [JsonSerializable(typeof(TerraBaseImportFormat))]
    [JsonSerializable(typeof(TerraBaseImportState))]
    public sealed partial class ImportJson : JsonSerializerContext;
}
