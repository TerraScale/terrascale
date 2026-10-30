using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.TerraBase.Contracts
{
    /// <summary>
    /// The REST JSON form of the contract types, which TerraBase uses to serve each RPC through the matching REST route.
    /// Web defaults give camelCase names; snake_case members are pinned. Null members are omitted, as TerraBase rejects
    /// explicit nulls in operations.
    /// </summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 256)]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(TerraBaseCollectionInfo))]
    [JsonSerializable(typeof(TerraBaseCollectionPage))]
    [JsonSerializable(typeof(TerraBaseDocument))]
    [JsonSerializable(typeof(TerraBaseDocumentPage))]
    [JsonSerializable(typeof(TerraBaseQuery))]
    [JsonSerializable(typeof(TerraBaseWriteResult))]
    [JsonSerializable(typeof(TerraBaseTransaction))]
    [JsonSerializable(typeof(TerraBaseTransactionResult))]
    public sealed partial class TerraBaseContractsJson : JsonSerializerContext;
}
