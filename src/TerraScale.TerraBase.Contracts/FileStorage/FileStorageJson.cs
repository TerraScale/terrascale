using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.FileStorage
{
    /// <summary>The REST JSON shape of the FileStorage API.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 256)]
    [JsonSerializable(typeof(TerraBaseFileBucket))]
    [JsonSerializable(typeof(TerraBaseStoredFile))]
    [JsonSerializable(typeof(TerraBaseFileUrl))]
    [JsonSerializable(typeof(TerraBaseFileUpload))]
    [JsonSerializable(typeof(TerraBaseFileBucketRequest))]
    [JsonSerializable(typeof(TerraBaseFileRulesRequest))]
    [JsonSerializable(typeof(TerraBaseFileBucketPage))]
    [JsonSerializable(typeof(TerraBaseStoredFilePage))]
    public sealed partial class FileStorageJson : JsonSerializerContext;
}
